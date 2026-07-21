using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Worker;

public sealed record AssetCleanupPayload(
    Guid ProjectId,
    Guid? DeletionId = null,
    Guid? UploadSessionId = null,
    Guid? AssetId = null,
    DateTimeOffset? NotBefore = null);

/// <summary>
/// Idempotently removes object-storage content and scrubs creative data while
/// retaining only billing-safe and operational deletion evidence.
/// </summary>
public sealed class AssetCleanupJobHandler(
    Hook2StreamDbContext db,
    IObjectStorage storage,
    TimeProvider timeProvider,
    IOptions<OperationalPolicyOptions> policyOptions) : IJobHandler
{
    public JobType Type => JobType.AssetCleanup;
    public string Capability => JobRoutingRegistry.GetRequiredCapability(Type);

    public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        var payload = PipelineHandlerData.Payload<AssetCleanupPayload>(job);
        if (job.ProjectId != payload.ProjectId)
        {
            throw new JobHandlerException(
                "cleanup.scope_invalid",
                "The cleanup request does not match its project scope.",
                retryable: false);
        }

        var now = timeProvider.GetUtcNow();
        if (payload.NotBefore is { } notBefore && notBefore > now)
        {
            throw new JobDeferredException(
                "cleanup.upload_url_fence",
                "Cleanup is waiting for previously issued upload URLs to expire.",
                Min(notBefore - now, TimeSpan.FromMinutes(15)));
        }

        if (payload.UploadSessionId is { } uploadSessionId)
        {
            await CleanupUploadAsync(job, payload.ProjectId, uploadSessionId, cancellationToken);
            return;
        }

        if (payload.AssetId is { } assetId)
        {
            await CleanupAssetAsync(job, payload.ProjectId, assetId, cancellationToken);
            return;
        }

        await CleanupProjectAsync(job, payload, cancellationToken);
    }

    private async Task CleanupUploadAsync(
        LeasedJob job,
        Guid projectId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await db.UploadSessions
            .IgnoreQueryFilters()
            .Include(value => value.Asset)
            .SingleOrDefaultAsync(
                value => value.Id == sessionId &&
                         value.ProjectId == projectId &&
                         value.WorkspaceId == job.WorkspaceId,
                cancellationToken);
        if (session is null)
        {
            return;
        }

        if (session.IsMultipart && !string.IsNullOrWhiteSpace(session.MultipartUploadId))
        {
            await storage.AbortMultipartUploadAsync(
                session.ObjectKey,
                session.MultipartUploadId,
                cancellationToken);
        }
        // CompleteMultipart can succeed before its DB commit fails. Abort then
        // delete the key in both modes so that completed-but-uncommitted data is
        // not stranded.
        await storage.DeleteAsync(session.ObjectKey, cancellationToken);

        var now = timeProvider.GetUtcNow();
        ScrubSession(session, now);
        await ScrubAssetAsync(session.Asset, now, cancellationToken);
        await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
    }

    private async Task CleanupAssetAsync(
        LeasedJob job,
        Guid projectId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var asset = await db.MediaAssets
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                value => value.Id == assetId &&
                         value.ProjectId == projectId &&
                         value.WorkspaceId == job.WorkspaceId,
                cancellationToken);
        if (asset is null)
        {
            return;
        }

        var sessions = await db.UploadSessions.IgnoreQueryFilters()
            .Where(value => value.AssetId == asset.Id)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            if (session.IsMultipart && !string.IsNullOrWhiteSpace(session.MultipartUploadId))
            {
                await storage.AbortMultipartUploadAsync(
                    session.ObjectKey,
                    session.MultipartUploadId,
                    cancellationToken);
            }
        }

        await DeleteAssetObjectsAsync(asset, cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var session in sessions) ScrubSession(session, now);
        await ScrubAssetAsync(asset, now, cancellationToken);
        await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
    }

    private async Task CleanupProjectAsync(
        LeasedJob job,
        AssetCleanupPayload payload,
        CancellationToken cancellationToken)
    {
        if (payload.DeletionId is not { } deletionId)
        {
            throw new JobHandlerException(
                "cleanup.deletion_proof_missing",
                "Project cleanup requires a matching deletion record.",
                retryable: false);
        }

        var tombstoneQuery = db.Set<ProjectDeletionTombstone>()
            .Where(value => value.ProjectId == payload.ProjectId &&
                            value.WorkspaceId == job.WorkspaceId &&
                            value.Id == deletionId);

        var tombstone = await tombstoneQuery.SingleOrDefaultAsync(cancellationToken);
        if (tombstone is null)
        {
            throw new JobHandlerException(
                "cleanup.deletion_proof_missing",
                "Project cleanup requires a matching deletion record.",
                retryable: false);
        }

        if (tombstone.ContentPurgedAt is not null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var safeAt = tombstone.RequestedAt.AddMinutes(policyOptions.Value.DeletionFenceMinutes);
        if (safeAt > now)
        {
            throw new JobDeferredException(
                "cleanup.deletion_fence",
                "Project cleanup is waiting for the worker lease safety window.",
                safeAt - now);
        }

        var project = await db.Projects
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                value => value.Id == payload.ProjectId && value.WorkspaceId == job.WorkspaceId,
                cancellationToken);
        if (project is null || project.DeletedAt is null)
        {
            throw new JobHandlerException(
                "cleanup.project_not_deleted",
                "Project cleanup is allowed only after the project deletion fence is committed.",
                retryable: false);
        }

        // Multipart parts are not returned by ListObjectsV2 and must be aborted
        // explicitly before deleting each known project prefix.
        var activeUploads = await db.UploadSessions
            .IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id &&
                            value.WorkspaceId == project.WorkspaceId &&
                            value.MultipartUploadId != null &&
                            (value.State == UploadState.Initiated ||
                             value.State == UploadState.Uploading ||
                             value.State == UploadState.Expired))
            .ToListAsync(cancellationToken);
        foreach (var upload in activeUploads)
        {
            await storage.AbortMultipartUploadAsync(
                upload.ObjectKey,
                upload.MultipartUploadId!,
                cancellationToken);
        }

        await storage.DeleteProjectObjectsAsync(
            new ProjectStorageScope(project.WorkspaceId, project.Id),
            cancellationToken);

        now = timeProvider.GetUtcNow();
        await ScrubProjectRowsAsync(project, job.Id, now, cancellationToken);
        tombstone.State = "purged";
        tombstone.ContentPurgedAt = now;
        tombstone.LastError = null;

        db.AuditEvents.Add(new AuditEvent
        {
            WorkspaceId = project.WorkspaceId,
            ActorSubject = "system:retention",
            Action = "project.content_purged",
            ResourceType = "release_project",
            ResourceId = project.Id,
            DataJson = "{}"
        });
        await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
    }

    private async Task ScrubProjectRowsAsync(
        ReleaseProject project,
        Guid currentJobId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        project.ProjectLabel = "[deleted]";
        project.ArtistName = "[deleted]";
        project.TrackTitle = "[deleted]";
        project.Language = "und";
        project.InternalNotes = null;
        project.LyricsText = null;
        project.IsArchived = true;
        project.State = ProjectState.Archived;
        project.StateBeforeArchive = null;
        project.ReleaseDate = null;
        project.CampaignStartDate = null;
        project.CurrentTranscriptRevisionId = null;
        project.CurrentArtworkPackRevisionId = null;
        project.CurrentHookSetRevisionId = null;
        project.CurrentCampaignPlanRevisionId = null;
        project.DeletedAt ??= now;

        var assets = await db.MediaAssets.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var asset in assets)
        {
            await ScrubAssetAsync(asset, now, cancellationToken);
        }

        var sessions = await db.UploadSessions.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions) ScrubSession(session, now);

        var rights = await db.RightsAttestations.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var value in rights)
        {
            value.ActorSubject = "[deleted]";
            value.AudioAssetId = null;
            value.AudioFingerprint = null;
            value.DeletedAt ??= now;
        }

        var analyses = await db.TrackAnalysisRevisions.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var value in analyses)
        {
            value.AnalysisJson = "{}";
            value.ProcessorVersionsJson = "{}";
            value.SourceFingerprint = string.Empty;
            value.DeletedAt ??= now;
        }

        var transcripts = await db.TranscriptRevisions.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var value in transcripts)
        {
            value.PhrasesJson = "[]";
            value.SourceFingerprint = string.Empty;
            value.ApprovedBySubject = null;
            value.DeletedAt ??= now;
        }

        var artwork = await db.ArtworkPackRevisions.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var value in artwork)
        {
            value.Prompt = string.Empty;
            value.CandidateAssetIdsJson = "[]";
            value.BackgroundAssetIdsJson = "[]";
            value.CompositionJson = "{}";
            value.SourceFingerprint = string.Empty;
            value.SelectedAssetId = null;
            value.ApprovedBySubject = null;
            value.DeletedAt ??= now;
        }

        var hooks = await db.HookSetRevisions.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var value in hooks)
        {
            value.HooksJson = "[]";
            value.SourceFingerprint = string.Empty;
            value.DeletedAt ??= now;
        }

        var campaigns = await db.CampaignPlanRevisions.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var value in campaigns)
        {
            value.ItemsJson = "[]";
            value.SourceFingerprint = string.Empty;
            value.DeletedAt ??= now;
        }

        var runs = await db.PipelineRuns.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        var runIds = runs.Select(value => value.Id).ToArray();
        var stages = await db.PipelineStages.IgnoreQueryFilters()
            .Where(value => runIds.Contains(value.PipelineRunId))
            .ToListAsync(cancellationToken);
        foreach (var value in stages)
        {
            value.CurrentRenderBatchId = null;
            value.DeletedAt ??= now;
        }
        foreach (var value in runs)
        {
            value.InputFingerprint = null;
            value.DeletedAt ??= now;
        }

        var projectEvents = await db.ProjectEvents.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var value in projectEvents)
        {
            value.DataJson = "{}";
            value.DeletedAt ??= now;
        }

        var invocations = await db.AiProviderInvocations.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var value in invocations) value.DeletedAt ??= now;

        var jobs = await db.Jobs.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        var jobIds = jobs.Select(value => value.Id).ToArray();
        foreach (var value in jobs)
        {
            // The active cleanup lease must remain retryable until the queue
            // records its terminal state. Its payload contains only deletion
            // scope identifiers and is safe to retain as audit evidence.
            if (value.Id == currentJobId) continue;
            value.PayloadJson = "{}";
            value.InputFingerprint = null;
            if (value.State is JobState.Queued or JobState.Running)
            {
                value.State = JobState.Cancelled;
                value.CompletedAt = now;
                value.ErrorCode = "project.deleted";
                value.ErrorMessage = "The project was deleted.";
                value.LeaseOwner = null;
                value.LeaseToken = null;
                value.LeaseExpiresAt = null;
            }

            value.DeletedAt ??= now;
        }

        var attempts = await db.JobAttempts.IgnoreQueryFilters()
            .Where(value => jobIds.Contains(value.JobId))
            .ToListAsync(cancellationToken);
        foreach (var value in attempts)
        {
            value.ErrorMessage = null;
            if (value.JobId != currentJobId) value.DeletedAt ??= now;
        }

        var jobEvents = await db.JobEvents.IgnoreQueryFilters()
            .Where(value => jobIds.Contains(value.JobId))
            .ToListAsync(cancellationToken);
        foreach (var value in jobEvents)
        {
            value.DataJson = "{}";
            if (value.JobId != currentJobId) value.DeletedAt ??= now;
        }

        var outbox = await db.OutboxMessages.IgnoreQueryFilters()
            .Where(value => value.AggregateId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var value in outbox)
        {
            value.PayloadJson = "{}";
            value.ProcessedAt ??= now;
            value.LastError = null;
            value.DeletedAt ??= now;
        }

        var checkouts = await db.BillingCheckouts.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var value in checkouts)
        {
            value.ItemIdsJson = "[]";
            value.ArtistNameSnapshot = null;
            value.TrackTitleSnapshot = null;
            value.AudioFingerprintSnapshot = null;
            value.AudioAssetIdSnapshot = null;
            value.ArtworkCompositionHash = null;
        }

        var entitlements = await db.Entitlements.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var value in entitlements)
        {
            value.ItemIdsJson = "[]";
            value.ArtistNameSnapshot = null;
            value.TrackTitleSnapshot = null;
            value.AudioFingerprintSnapshot = null;
            value.AudioAssetIdSnapshot = null;
            value.ArtworkCompositionHash = null;
        }

        var batches = await db.RenderBatches.IgnoreQueryFilters()
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var value in batches)
        {
            value.ItemIdsJson = "[]";
            value.JobIdsJson = "[]";
            value.PipelineRunId = null;
            value.DeletedAt ??= now;
        }

        var audit = await db.AuditEvents.IgnoreQueryFilters()
            .Where(value => value.ResourceId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var value in audit) value.DataJson = "{}";
    }

    private async Task ScrubAssetAsync(
        MediaAsset asset,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var derivatives = await db.MediaDerivatives.IgnoreQueryFilters()
            .Where(value => value.AssetId == asset.Id)
            .ToListAsync(cancellationToken);
        foreach (var derivative in derivatives)
        {
            derivative.ObjectKey = $"deleted/derivatives/{derivative.Id:N}";
            derivative.Sha256 = null;
            derivative.DeletedAt ??= now;
        }

        asset.OriginalFileName = "[deleted]";
        asset.DeclaredContentType = "application/octet-stream";
        asset.DetectedContentType = null;
        asset.DeclaredBytes = 0;
        asset.ActualBytes = null;
        asset.ObjectKey = $"deleted/assets/{asset.Id:N}";
        asset.Sha256 = null;
        asset.DurationMilliseconds = null;
        asset.Width = null;
        asset.Height = null;
        asset.VideoCodec = null;
        asset.AudioCodec = null;
        asset.ProvenanceJson = null;
        asset.State = AssetState.Deleted;
        asset.IsActive = false;
        asset.FailureCode = "content.deleted";
        asset.FailureMessage = null;
        asset.DeletedAt ??= now;
    }

    private async Task DeleteAssetObjectsAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        await storage.DeleteAssetObjectsAsync(
            new AssetStorageScope(asset.WorkspaceId, asset.ProjectId, asset.Id),
            cancellationToken);
        await storage.DeleteAsync(asset.ObjectKey, cancellationToken);
        var derivativeKeys = await db.MediaDerivatives.IgnoreQueryFilters()
            .Where(value => value.AssetId == asset.Id)
            .Select(value => value.ObjectKey)
            .ToListAsync(cancellationToken);
        foreach (var key in derivativeKeys)
        {
            await storage.DeleteAsync(key, cancellationToken);
        }
    }

    private static void ScrubSession(UploadSession session, DateTimeOffset now)
    {
        session.State = session.State == UploadState.Completed
            ? UploadState.Completed
            : UploadState.Expired;
        session.ObjectKey = $"deleted/uploads/{session.Id:N}";
        session.MultipartUploadId = null;
        session.AbortedAt ??= now;
        session.DeletedAt ??= now;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
}
