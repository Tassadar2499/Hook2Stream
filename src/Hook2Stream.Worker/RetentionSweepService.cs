using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
                StringComparer.OrdinalIgnoreCase))
        {
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
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        var policy = policyOptions.Value;
        var now = timeProvider.GetUtcNow();

        var cleanupRequests = new List<CleanupRequest>();

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

        await db.SaveChangesAsync(cancellationToken);

        foreach (var request in cleanupRequests
                     .DistinctBy(value => value.IdempotencyKey))
        {
            var existing = await db.Jobs.IgnoreQueryFilters().SingleOrDefaultAsync(
                value => value.IdempotencyKey == request.IdempotencyKey,
                cancellationToken);
            if (existing is null)
            {
                await queue.EnqueueAsync(
                    new JobEnqueueRequest(
                        request.WorkspaceId,
                        request.ProjectId,
                        request.AssetId,
                        JobType.AssetCleanup,
                        JsonSerializer.Serialize(request.Payload, PipelineHandlerData.Json),
                        request.IdempotencyKey,
                        RequiredCapability: JobRoutingRegistry.Control,
                        AvailableAt: request.AvailableAt),
                    cancellationToken);
            }
            else if (existing.State is JobState.Failed or JobState.Cancelled)
            {
                existing.DeletedAt = null;
                existing.State = JobState.Queued;
                existing.AvailableAt = request.AvailableAt is { } availableAt && availableAt > now
                    ? availableAt
                    : now;
                existing.MaxAttempts = Math.Max(existing.MaxAttempts, existing.AttemptCount + 1);
                existing.CompletedAt = null;
                existing.ErrorCode = null;
                existing.ErrorMessage = null;
                existing.LeaseOwner = null;
                existing.LeaseToken = null;
                existing.LeaseExpiresAt = null;
                db.JobEvents.Add(new JobEvent
                {
                    JobId = existing.Id,
                    EventType = "requeued",
                    DataJson = "{\"reason\":\"retention.deadline\"}"
                });
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        if (expiredUploads.Count > 0 || expiredIdempotency.Count > 0 ||
            expiredAuthSessions.Count > 0 || expiredLoginStates.Count > 0 ||
            cleanupRequests.Count > 0)
        {
            logger.LogInformation(
                "Retention sweep expired {UploadCount} uploads, {IdempotencyCount} idempotency records, {SessionCount} sessions and {LoginStateCount} OAuth states; {CleanupCount} cleanup jobs are ensured.",
                expiredUploads.Count,
                expiredIdempotency.Count,
                expiredAuthSessions.Count,
                expiredLoginStates.Count,
                cleanupRequests.Count);
        }
    }

    private static async Task QueueUnpaidProjectRetentionAsync(
        Hook2StreamDbContext db,
        ICollection<CleanupRequest> cleanupRequests,
        DateTimeOffset now,
        OperationalPolicyOptions policy,
        CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-policy.UnpaidProjectDays);
        var candidates = await db.Projects
            .Where(project => project.CreatedAt <= cutoff &&
                              !db.Entitlements.Any(entitlement =>
                                  entitlement.ProjectId == project.Id &&
                                  (entitlement.State == EntitlementState.Active ||
                                   entitlement.State == EntitlementState.Exhausted)) &&
                              !db.BillingCheckouts.Any(checkout =>
                                  checkout.ProjectId == project.Id &&
                                  checkout.State == CheckoutState.Pending))
            .OrderBy(value => value.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0) return;

        var candidateIds = candidates.Select(value => value.Id).ToArray();
        var existing = await db.ProjectDeletionTombstones
            .Where(value => candidateIds.Contains(value.ProjectId))
            .Select(value => value.ProjectId)
            .ToListAsync(cancellationToken);
        var existingIds = existing.ToHashSet();

        foreach (var project in candidates.Where(value => !existingIds.Contains(value.Id)))
        {
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
}
