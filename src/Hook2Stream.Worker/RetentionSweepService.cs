using System.Data;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Worker;

/// <summary>
/// A control-pool scheduler that turns retention deadlines into idempotent
/// cleanup jobs. Storage deletion remains in AssetCleanupJobHandler so lease
/// fencing and retries apply to every destructive operation.
/// </summary>
public sealed class RetentionSweepService(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> workerOptions,
    IOptions<OperationalPolicyOptions> policyOptions,
    TimeProvider timeProvider,
    ILogger<RetentionSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!workerOptions.Value.Capabilities.Contains(
                JobRoutingRegistry.Control,
                StringComparer.OrdinalIgnoreCase) ||
            !policyOptions.Value.RetentionSweepEnabled)
        {
            logger.LogInformation(
                "Retention sweep is disabled for this worker (enabled: {Enabled}).",
                policyOptions.Value.RetentionSweepEnabled);
            return;
        }

        var interval = TimeSpan.FromMinutes(policyOptions.Value.RetentionSweepMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The retention sweep failed; it will be retried.");
            }

            await Task.Delay(interval, timeProvider, stoppingToken);
        }
    }

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        var policy = policyOptions.Value;
        if (!policy.RetentionSweepEnabled)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        await using var strategyScope = scopeFactory.CreateAsyncScope();
        var strategyDb =
            strategyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(
            async token =>
            {
                // A failed attempt can leave tracked entities in an unknown
                // state. Give every execution-strategy retry a fresh scope and
                // DbContext so the whole transaction is replayed atomically.
                await using var attemptScope = scopeFactory.CreateAsyncScope();
                var attemptDb =
                    attemptScope.ServiceProvider
                        .GetRequiredService<Hook2StreamDbContext>();
                return await SweepOnceAsync(
                    attemptDb,
                    now,
                    policy,
                    token);
            },
            cancellationToken);

        if (result.HasWork)
        {
            logger.LogInformation(
                "Retention sweep expired {UploadCount} uploads, {IdempotencyCount} idempotency records, {SessionCount} sessions and {LoginStateCount} OAuth states; {CleanupCount} durable cleanup deliveries are ensured.",
                result.ExpiredUploadCount,
                result.ExpiredIdempotencyCount,
                result.ExpiredAuthSessionCount,
                result.ExpiredLoginStateCount,
                result.CleanupRequestCount);
        }
    }

    private static async Task<SweepResult> SweepOnceAsync(
        Hook2StreamDbContext db,
        DateTimeOffset now,
        OperationalPolicyOptions policy,
        CancellationToken cancellationToken)
    {
        var cleanupRequests = new List<CleanupRequest>();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            : null;

        var expiredUploads = await db.UploadSessions
            .Include(value => value.Asset)
            .Where(value =>
                (value.State == UploadState.Initiated ||
                 value.State == UploadState.Uploading ||
                 value.State == UploadState.Expired) &&
                value.ExpiresAt <= now)
            .Take(500)
            .ToListAsync(cancellationToken);
        foreach (var session in expiredUploads)
        {
            session.State = UploadState.Expired;
            session.Asset.State = AssetState.Rejected;
            session.Asset.IsActive = false;
            session.Asset.FailureCode = "upload.session_expired";
            session.Asset.FailureMessage = "The upload session expired before it was completed.";
            cleanupRequests.Add(new CleanupRequest(
                session.WorkspaceId,
                session.ProjectId,
                session.AssetId,
                new AssetCleanupPayload(session.ProjectId, UploadSessionId: session.Id),
                $"retention:upload:{session.Id:N}"));
        }

        var expiredIdempotency = await db.ApiIdempotencyRecords
            .Where(value => value.ExpiresAt <= now)
            .Take(1_000)
            .ToListAsync(cancellationToken);
        db.ApiIdempotencyRecords.RemoveRange(expiredIdempotency);

        var expiredAuthSessions = await db.Set<AuthSession>()
            .Where(value => value.ExpiresAt <= now ||
                            value.RevokedAt != null && value.RevokedAt <= now.AddDays(-1))
            .Take(1_000)
            .ToListAsync(cancellationToken);
        db.RemoveRange(expiredAuthSessions);
        var expiredLoginStates = await db.Set<OAuthLoginState>()
            .Where(value => value.ExpiresAt <= now ||
                            value.ConsumedAt != null && value.ConsumedAt <= now.AddDays(-1))
            .Take(1_000)
            .ToListAsync(cancellationToken);
        db.RemoveRange(expiredLoginStates);

        await QueueUnpaidProjectRetentionAsync(db, cleanupRequests, now, policy, cancellationToken);
        await QueueAssetRetentionAsync(db, cleanupRequests, now, policy, cancellationToken);

        var deletionFenceCutoff = now.AddMinutes(-policy.DeletionFenceMinutes);
        var dueTombstones = await db.ProjectDeletionTombstones
            .Where(value => value.ContentPurgedAt == null &&
                            value.RequestedAt <= deletionFenceCutoff &&
                            (value.State == "queued" ||
                             value.State == "failed"))
            .Take(200)
            .ToListAsync(cancellationToken);
        foreach (var tombstone in dueTombstones)
        {
            tombstone.State = "queued";
            cleanupRequests.Add(new CleanupRequest(
                tombstone.WorkspaceId,
                tombstone.ProjectId,
                null,
                new AssetCleanupPayload(tombstone.ProjectId, DeletionId: tombstone.Id),
                $"retention:project:{tombstone.Id:N}",
                now));
        }

        await QueueFencedAssetRecoveryAsync(db, cleanupRequests, now, cancellationToken);
        await EnsureCleanupDeliveriesAsync(db, cleanupRequests, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new SweepResult(
            expiredUploads.Count,
            expiredIdempotency.Count,
            expiredAuthSessions.Count,
            expiredLoginStates.Count,
            cleanupRequests.Count);
    }

    private static async Task QueueUnpaidProjectRetentionAsync(
        Hook2StreamDbContext db,
        ICollection<CleanupRequest> cleanupRequests,
        DateTimeOffset now,
        OperationalPolicyOptions policy,
        CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-policy.UnpaidProjectDays);
        var candidateIds = await db.Projects
            .Where(project => project.LastActivityAt <= cutoff &&
                              !db.Entitlements.Any(entitlement =>
                                  entitlement.ProjectId == project.Id &&
                                  (entitlement.State == EntitlementState.Active ||
                                   entitlement.State == EntitlementState.Exhausted)) &&
                              !db.BillingCheckouts.Any(checkout =>
                                  checkout.ProjectId == project.Id &&
                                  checkout.State == CheckoutState.Pending) &&
                              !db.Jobs.Any(job =>
                                  job.ProjectId == project.Id &&
                                  job.Type != JobType.AssetCleanup &&
                                  (job.State == JobState.Queued ||
                                   job.State == JobState.Running)))
            .OrderBy(value => value.LastActivityAt)
            .Select(value => value.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (candidateIds.Count == 0) return;

        var existing = await db.ProjectDeletionTombstones
            .Where(value => candidateIds.Contains(value.ProjectId))
            .Select(value => value.ProjectId)
            .ToListAsync(cancellationToken);
        var existingIds = existing.ToHashSet();

        foreach (var projectId in candidateIds.Where(value => !existingIds.Contains(value)))
        {
            var project = await LockProjectAsync(db, projectId, cancellationToken);
            if (project is null ||
                !await IsUnpaidProjectEligibleAsync(db, project, cutoff, cancellationToken))
            {
                continue;
            }

            await ProjectDeletionCoordinator.FenceAsync(
                db,
                project,
                now,
                "project.retention_expired",
                cancellationToken);
            var tombstone = new ProjectDeletionTombstone
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                ActorSubject = "system:retention",
                RequestedAt = now,
                PurgeDueAt = now.AddMinutes(policy.DeletionFenceMinutes),
                State = "queued",
                PolicyVersion = "retention-v1"
            };
            db.ProjectDeletionTombstones.Add(tombstone);
            cleanupRequests.Add(new CleanupRequest(
                project.WorkspaceId,
                project.Id,
                null,
                new AssetCleanupPayload(project.Id, DeletionId: tombstone.Id),
                $"retention:project:{tombstone.Id:N}",
                now.AddMinutes(policy.DeletionFenceMinutes)));
        }
    }

    private static async Task<ReleaseProject?> LockProjectAsync(
        Hook2StreamDbContext db,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.Projects
                .FromSqlInterpolated(
                    $"""
                     SELECT *
                     FROM release_projects
                     WHERE id = {projectId}
                       AND deleted_at IS NULL
                     FOR UPDATE SKIP LOCKED
                     """)
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await db.Projects.SingleOrDefaultAsync(
            value => value.Id == projectId,
            cancellationToken);
    }

    private static async Task<bool> IsUnpaidProjectEligibleAsync(
        Hook2StreamDbContext db,
        ReleaseProject project,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        if (project.LastActivityAt > cutoff)
        {
            return false;
        }

        var protectedByEntitlement = await db.Entitlements.AnyAsync(
            value => value.ProjectId == project.Id &&
                     (value.State == EntitlementState.Active ||
                      value.State == EntitlementState.Exhausted),
            cancellationToken);
        var protectedByCheckout = await db.BillingCheckouts.AnyAsync(
            value => value.ProjectId == project.Id &&
                     value.State == CheckoutState.Pending,
            cancellationToken);
        var protectedByWork = await db.Jobs.AnyAsync(
            value => value.ProjectId == project.Id &&
                     value.Type != JobType.AssetCleanup &&
                     (value.State == JobState.Queued ||
                      value.State == JobState.Running),
            cancellationToken);
        return !protectedByEntitlement && !protectedByCheckout && !protectedByWork;
    }

    private static async Task QueueAssetRetentionAsync(
        Hook2StreamDbContext db,
        ICollection<CleanupRequest> cleanupRequests,
        DateTimeOffset now,
        OperationalPolicyOptions policy,
        CancellationToken cancellationToken)
    {
        var paidProjectAnchors = await db.Entitlements
            .Where(value => value.ProjectId != null &&
                            (value.State == EntitlementState.Active ||
                             value.State == EntitlementState.Exhausted))
            .GroupBy(value => value.ProjectId!.Value)
            .Select(group => new
            {
                ProjectId = group.Key,
                PaidAt = group.Max(value => value.CreatedAt)
            })
            .ToDictionaryAsync(value => value.ProjectId, value => value.PaidAt, cancellationToken);
        var sourceCutoff = now.AddDays(-policy.PaidSourceDays);
        var outputCutoff = now.AddDays(-policy.PaidOutputDays);
        var artworkCutoff = now.AddDays(-policy.SupersededArtworkDays);

        var protectedArtworkIds = await db.ArtworkPackRevisions
            .Where(value => value.State == RevisionState.Approved && value.SelectedAssetId != null)
            .Select(value => value.SelectedAssetId!.Value)
            .ToListAsync(cancellationToken);
        var activeCampaigns = await db.CampaignPlanRevisions
            .Where(value => value.State == RevisionState.Approved || value.State == RevisionState.ReadyForReview)
            .Select(value => value.ItemsJson)
            .ToListAsync(cancellationToken);
        foreach (var itemsJson in activeCampaigns)
        {
            var items = PipelineHandlerData.Deserialize<List<CampaignItemRequest>>(itemsJson) ?? [];
            protectedArtworkIds.AddRange(items
                .Where(value => value.BackgroundAssetId.HasValue)
                .Select(value => value.BackgroundAssetId!.Value));
        }

        var protectedIds = protectedArtworkIds.ToHashSet();
        var activeProjectIds = (await db.Jobs
                .Where(value => value.ProjectId != null &&
                                value.Type != JobType.AssetCleanup &&
                                (value.State == JobState.Queued || value.State == JobState.Running))
                .Select(value => value.ProjectId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var candidates = await db.MediaAssets
            .Where(asset => asset.DeletedAt == null &&
                            ((db.Entitlements.Any(entitlement =>
                                  entitlement.ProjectId == asset.ProjectId &&
                                  (entitlement.State == EntitlementState.Active ||
                                   entitlement.State == EntitlementState.Exhausted)) &&
                              !db.Entitlements.Any(entitlement =>
                                  entitlement.ProjectId == asset.ProjectId &&
                                  (entitlement.State == EntitlementState.Active ||
                                   entitlement.State == EntitlementState.Exhausted) &&
                                  entitlement.CreatedAt > sourceCutoff) &&
                              asset.CreatedAt <= sourceCutoff &&
                              (asset.Purpose == AssetPurpose.Source ||
                               asset.Purpose == AssetPurpose.AudioMaster ||
                               asset.Purpose == AssetPurpose.PreviewVideo)) ||
                             (db.Entitlements.Any(entitlement =>
                                  entitlement.ProjectId == asset.ProjectId &&
                                  (entitlement.State == EntitlementState.Active ||
                                   entitlement.State == EntitlementState.Exhausted)) &&
                              !db.Entitlements.Any(entitlement =>
                                  entitlement.ProjectId == asset.ProjectId &&
                                  (entitlement.State == EntitlementState.Active ||
                                   entitlement.State == EntitlementState.Exhausted) &&
                                  entitlement.CreatedAt > outputCutoff) &&
                              asset.CreatedAt <= outputCutoff &&
                              (asset.Purpose == AssetPurpose.CampaignVideo ||
                               asset.Purpose == AssetPurpose.ExportBundle ||
                               asset.Purpose == AssetPurpose.CleanCover ||
                               asset.Purpose == AssetPurpose.ApprovedCover)) ||
                             (asset.CreatedAt <= artworkCutoff &&
                              (asset.Purpose == AssetPurpose.CoverCandidate ||
                               asset.Purpose == AssetPurpose.CampaignBackground))))
            .OrderBy(value => value.CreatedAt)
            .Take(500)
            .ToListAsync(cancellationToken);

        foreach (var asset in candidates)
        {
            if (activeProjectIds.Contains(asset.ProjectId)) continue;

            paidProjectAnchors.TryGetValue(asset.ProjectId, out var entitlementCreatedAt);
            if (!IsAssetPastRetention(
                    asset,
                    entitlementCreatedAt == default ? null : entitlementCreatedAt,
                    protectedIds.Contains(asset.Id),
                    now,
                    policy)) continue;

            await AssetDeletionCoordinator.FenceAsync(
                db,
                asset,
                now,
                "asset.retention_expired",
                cancellationToken);
            var cleanupAvailableAt = now.AddMinutes(policy.DeletionFenceMinutes);
            cleanupRequests.Add(new CleanupRequest(
                asset.WorkspaceId,
                asset.ProjectId,
                asset.Id,
                new AssetCleanupPayload(
                    asset.ProjectId,
                    AssetId: asset.Id,
                    NotBefore: cleanupAvailableAt),
                $"retention:asset:{asset.Id:N}",
                cleanupAvailableAt));
        }
    }

    private static async Task QueueFencedAssetRecoveryAsync(
        Hook2StreamDbContext db,
        ICollection<CleanupRequest> cleanupRequests,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var fencedAssets = await db.MediaAssets
            .IgnoreQueryFilters()
            .Where(asset =>
                asset.DeletedAt != null &&
                asset.State == AssetState.Deleted &&
                asset.OriginalFileName != "[deleted]" &&
                !db.ProjectDeletionTombstones.Any(tombstone =>
                    tombstone.ProjectId == asset.ProjectId &&
                    tombstone.ContentPurgedAt == null))
            .OrderBy(value => value.DeletedAt)
            .Take(500)
            .ToListAsync(cancellationToken);

        foreach (var asset in fencedAssets)
        {
            cleanupRequests.Add(new CleanupRequest(
                asset.WorkspaceId,
                asset.ProjectId,
                asset.Id,
                new AssetCleanupPayload(
                    asset.ProjectId,
                    AssetId: asset.Id,
                    NotBefore: now),
                $"retention:asset:{asset.Id:N}",
                now));
        }
    }

    private static async Task EnsureCleanupDeliveriesAsync(
        Hook2StreamDbContext db,
        IEnumerable<CleanupRequest> cleanupRequests,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var requests = cleanupRequests
            .DistinctBy(value => value.IdempotencyKey)
            .ToArray();
        if (requests.Length == 0)
        {
            return;
        }

        var dedupeKeys = requests.Select(value => value.IdempotencyKey).ToArray();
        var jobKeys = dedupeKeys
            .Concat(dedupeKeys.Select(value => $"outbox:{value}"))
            .ToArray();
        var assetIds = requests
            .Where(value => value.AssetId is not null)
            .Select(value => value.AssetId!.Value)
            .Distinct()
            .ToArray();
        var existingJobs = await db.Jobs
            .IgnoreQueryFilters()
            .Where(value =>
                value.Type == JobType.AssetCleanup &&
                ((value.IdempotencyKey != null && jobKeys.Contains(value.IdempotencyKey)) ||
                 (value.AssetId != null && assetIds.Contains(value.AssetId.Value))))
            .ToListAsync(cancellationToken);
        var existingOutbox = await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(value => dedupeKeys.Contains(value.DedupeKey))
            .ToListAsync(cancellationToken);

        foreach (var request in requests)
        {
            var outboxJobKey = $"outbox:{request.IdempotencyKey}";
            var existingJob = existingJobs.FirstOrDefault(value =>
                value.IdempotencyKey == request.IdempotencyKey ||
                value.IdempotencyKey == outboxJobKey ||
                request.AssetId is { } assetId && value.AssetId == assetId);
            if (existingJob is not null)
            {
                if (existingJob.State is not (JobState.Queued or JobState.Running))
                {
                    existingJob.DeletedAt = null;
                    existingJob.State = JobState.Queued;
                    existingJob.AvailableAt =
                        request.AvailableAt is { } availableAt && availableAt > now
                            ? availableAt
                            : now;
                    existingJob.MaxAttempts = Math.Max(
                        existingJob.MaxAttempts,
                        existingJob.AttemptCount + 1);
                    existingJob.CompletedAt = null;
                    existingJob.ErrorCode = null;
                    existingJob.ErrorMessage = null;
                    existingJob.LeaseOwner = null;
                    existingJob.LeaseToken = null;
                    existingJob.LeaseExpiresAt = null;
                    db.JobEvents.Add(new JobEvent
                    {
                        JobId = existingJob.Id,
                        EventType = "requeued",
                        DataJson = "{\"reason\":\"retention.recovery\"}"
                    });
                }

                continue;
            }

            var enqueueRequest = new JobEnqueueRequest(
                request.WorkspaceId,
                request.ProjectId,
                request.AssetId,
                JobType.AssetCleanup,
                JsonSerializer.Serialize(request.Payload, PipelineHandlerData.Json),
                request.IdempotencyKey,
                RequiredCapability: JobRoutingRegistry.Control,
                AvailableAt: request.AvailableAt);
            var message = existingOutbox.FirstOrDefault(
                value => value.DedupeKey == request.IdempotencyKey);
            if (message is null)
            {
                db.OutboxMessages.Add(new OutboxMessage
                {
                    WorkspaceId = request.WorkspaceId,
                    AggregateId = request.ProjectId,
                    Destination = "job",
                    MessageType = "job.asset_cleanup",
                    DedupeKey = request.IdempotencyKey,
                    PayloadJson = JsonSerializer.Serialize(
                        enqueueRequest,
                        PipelineHandlerData.Json)
                });
                continue;
            }

            if (message.ProcessedAt is null && message.DeletedAt is null)
            {
                continue;
            }

            // A processed delivery without its deduplicated job indicates a
            // historical or manually repaired partial state. Reopening the
            // same outbox row preserves the unique dedupe key.
            message.DeletedAt = null;
            message.ProcessedAt = null;
            message.AttemptCount = 0;
            message.LastError = null;
            message.PayloadJson = JsonSerializer.Serialize(
                enqueueRequest,
                PipelineHandlerData.Json);
        }
    }

    internal static bool IsAssetPastRetention(
        MediaAsset asset,
        DateTimeOffset? latestPaidAt,
        bool isProtectedArtwork,
        DateTimeOffset now,
        OperationalPolicyOptions policy)
    {
        var paidAnchor = latestPaidAt is { } paidAt && paidAt > asset.CreatedAt
            ? paidAt
            : asset.CreatedAt;
        var sourceExpired = latestPaidAt is not null &&
                            paidAnchor <= now.AddDays(-policy.PaidSourceDays) &&
                            asset.Purpose is AssetPurpose.Source or
                                AssetPurpose.AudioMaster or
                                AssetPurpose.PreviewVideo;
        var outputExpired = latestPaidAt is not null &&
                            paidAnchor <= now.AddDays(-policy.PaidOutputDays) &&
                            asset.Purpose is AssetPurpose.CampaignVideo or
                                AssetPurpose.ExportBundle or
                                AssetPurpose.CleanCover or
                                AssetPurpose.ApprovedCover;
        var unselectedArtworkExpired = !isProtectedArtwork &&
                                       asset.CreatedAt <= now.AddDays(-policy.SupersededArtworkDays) &&
                                       asset.Purpose is AssetPurpose.CoverCandidate or
                                           AssetPurpose.CampaignBackground;
        return sourceExpired || outputExpired || unselectedArtworkExpired;
    }

    private sealed record CleanupRequest(
        Guid WorkspaceId,
        Guid ProjectId,
        Guid? AssetId,
        AssetCleanupPayload Payload,
        string IdempotencyKey,
        DateTimeOffset? AvailableAt = null);

    private sealed record SweepResult(
        int ExpiredUploadCount,
        int ExpiredIdempotencyCount,
        int ExpiredAuthSessionCount,
        int ExpiredLoginStateCount,
        int CleanupRequestCount)
    {
        public bool HasWork =>
            ExpiredUploadCount > 0 ||
            ExpiredIdempotencyCount > 0 ||
            ExpiredAuthSessionCount > 0 ||
            ExpiredLoginStateCount > 0 ||
            CleanupRequestCount > 0;
    }
}
