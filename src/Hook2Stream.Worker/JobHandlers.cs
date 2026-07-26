using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Media;
using Hook2Stream.Infrastructure.Jobs;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Worker;

public interface IJobHandler
{
    JobType Type { get; }
    string Capability { get; }
    Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken);
}

public sealed class MediaIngestJobHandler(
    IMediaIngestProcessor processor,
    Hook2Stream.Infrastructure.Persistence.Hook2StreamDbContext db) : IJobHandler
{
    private const int MaxFinalizationAttempts = 3;

    public JobType Type => JobType.MediaIngest;
    public string Capability => JobRoutingRegistry.GetRequiredCapability(Type);

    public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        MediaAsset? candidateAsset = null;
        if (job.AssetId is { } candidateAssetId)
        {
            candidateAsset = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(
                value => value.Id == candidateAssetId && value.WorkspaceId == job.WorkspaceId,
                cancellationToken);
        }

        var isAlreadyIngested = candidateAsset is
        {
            State: AssetState.Ready,
            Sha256.Length: > 0
        };
        if (!isAlreadyIngested &&
            candidateAsset is
            {
                Origin: AssetOrigin.Uploaded,
                Kind: AssetKind.Cover or AssetKind.Visual
            })
        {
            var ownsVisualRights = await db.RightsAttestations.AsNoTracking()
                .Where(value => value.ProjectId == candidateAsset.ProjectId)
                .Select(value => value.OwnsVisualRights)
                .SingleOrDefaultAsync(cancellationToken);
            if (!ownsVisualRights)
            {
                throw new JobBlockedException(
                    "rights.visual_required",
                    "Visual processing is paused until rights to the uploaded cover or video are confirmed.");
            }
        }

        try
        {
            if (!isAlreadyIngested)
            {
                await processor.ProcessAsync(job, cancellationToken);
            }

            // Media ingest persists the ready asset before pipeline
            // finalization. A concurrent setup edit may therefore advance the
            // project concurrency token while the processor still has the old
            // project in this scoped context. Always finalize from a fresh
            // snapshot; a worker retry must also treat Ready (including a
            // Ready asset superseded by a newer upload) as idempotent and
            // never put the asset back into Processing.
            await FinalizeMp3FirstAsync(job, cancellationToken);
        }
        catch (MediaRejectedException exception)
        {
            throw new JobHandlerException(exception.Code, exception.SafeMessage, retryable: false, exception);
        }
    }

    private async Task FinalizeMp3FirstAsync(
        LeasedJob job,
        CancellationToken cancellationToken)
    {
        if (job.ProjectId is not { } projectId) return;

        for (var attempt = 1; attempt <= MaxFinalizationAttempts; attempt++)
        {
            db.ChangeTracker.Clear();
            var project = await db.Projects.SingleOrDefaultAsync(
                value => value.Id == projectId,
                cancellationToken);
            if (project?.FlowKind != FlowKind.Mp3First) return;

            try
            {
                var markerKey = PipelineOutbox.CreateReconcileDedupeKey(
                    project.Id,
                    "audio.ingested",
                    job.Id);
                if (await db.OutboxMessages
                        .IgnoreQueryFilters()
                        .AnyAsync(value => value.DedupeKey == markerKey, cancellationToken))
                {
                    return;
                }

                if (job.AssetId is not { } assetId) return;
                var targetAsset = await db.MediaAssets.SingleOrDefaultAsync(
                    value => value.Id == assetId && value.ProjectId == project.Id,
                    cancellationToken);
                if (targetAsset is not
                    {
                        State: AssetState.Ready,
                        IsActive: true,
                        Sha256.Length: > 0
                    })
                {
                    // A newer upload already won activation. Finishing this
                    // stale job must not invalidate dependants for the current
                    // media revision or move the project backwards.
                    return;
                }

                if (targetAsset.Kind == AssetKind.Audio)
                {
                    await InvalidateAudioDependantsAsync(project, targetAsset, cancellationToken);
                }

                PipelineOutbox.Reconcile(db, project, "audio.ingested", job.Id);
                await JobLeaseFence.CommitAsync(db, job, cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxFinalizationAttempts)
            {
                // Rebuild invalidations and the outbox event from the winning
                // project revision. The lease fence keeps each attempt atomic.
            }
        }
    }

    private async Task InvalidateAudioDependantsAsync(
        ReleaseProject project,
        MediaAsset audio,
        CancellationToken cancellationToken)
    {
        if (project.CurrentTranscriptRevisionId is { } transcriptId)
        {
            var transcript = await db.TranscriptRevisions.SingleAsync(value => value.Id == transcriptId, cancellationToken);
            if (!string.Equals(transcript.SourceFingerprint, audio.Sha256, StringComparison.Ordinal))
            {
                transcript.State = RevisionState.Superseded;
                project.CurrentTranscriptRevisionId = null;
            }
        }

        if (project.CurrentArtworkPackRevisionId is { } artworkId)
        {
            var artwork = await db.ArtworkPackRevisions.SingleAsync(value => value.Id == artworkId, cancellationToken);
            await ArtworkCreditLedger.ReleaseReservationAsync(
                db,
                project.WorkspaceId,
                artwork.Id,
                cancellationToken);
            if (artwork.State != RevisionState.Failed)
                artwork.State = RevisionState.Superseded;
            project.CurrentArtworkPackRevisionId = null;
        }

        if (project.CurrentHookSetRevisionId is { } hookId)
        {
            var hooks = await db.HookSetRevisions.SingleAsync(value => value.Id == hookId, cancellationToken);
            hooks.State = RevisionState.Superseded;
            project.CurrentHookSetRevisionId = null;
        }

        if (project.CurrentCampaignPlanRevisionId is { } campaignId)
        {
            var campaign = await db.CampaignPlanRevisions.SingleAsync(value => value.Id == campaignId, cancellationToken);
            campaign.State = RevisionState.Superseded;
            project.CurrentCampaignPlanRevisionId = null;
        }

        var generatedDependants = await db.MediaAssets
            .Where(value => value.ProjectId == project.Id &&
                            value.Origin == AssetOrigin.Generated &&
                            value.IsActive)
            .ToListAsync(cancellationToken);
        foreach (var dependant in generatedDependants)
        {
            dependant.IsActive = false;
        }

        var staleAutomationJobs = await db.Jobs
            .Where(value => value.ProjectId == project.Id &&
                            (value.State == JobState.Queued || value.State == JobState.Running) &&
                            (value.Type == JobType.AudioAnalysis ||
                             value.Type == JobType.Transcription ||
                             value.Type == JobType.ArtworkGeneration ||
                             value.Type == JobType.CampaignGeneration ||
                             value.Type == JobType.PreviewRender))
            .ToListAsync(cancellationToken);
        foreach (var staleJob in staleAutomationJobs)
        {
            staleJob.State = JobState.Cancelled;
            staleJob.ErrorCode = "audio.replaced";
            staleJob.ErrorMessage = "The job was cancelled because the release audio was replaced.";
            staleJob.CompletedAt = DateTimeOffset.UtcNow;
            staleJob.LeaseOwner = null;
            staleJob.LeaseToken = null;
            staleJob.LeaseExpiresAt = null;
            db.JobEvents.Add(new JobEvent
            {
                JobId = staleJob.Id,
                EventType = "cancelled",
                DataJson = "{\"code\":\"audio.replaced\"}"
            });
        }

        project.State = ProjectState.Analyzing;
    }
}

public sealed class JobHandlerException(
    string code,
    string safeMessage,
    bool retryable,
    Exception? innerException = null) : Exception(safeMessage, innerException)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
    public bool Retryable { get; } = retryable;
}

public sealed class JobDeferredException(
    string reasonCode,
    string safeMessage,
    TimeSpan delay) : Exception(safeMessage)
{
    public string ReasonCode { get; } = reasonCode;
    public string SafeMessage { get; } = safeMessage;
    public TimeSpan Delay { get; } = delay;
}

/// <summary>
/// Stops polling for a dependency that only a user action can satisfy. The
/// command endpoint that changes that dependency explicitly resumes the same
/// immutable job.
/// </summary>
public sealed class JobBlockedException(
    string reasonCode,
    string safeMessage) : Exception(safeMessage)
{
    public string ReasonCode { get; } = reasonCode;
    public string SafeMessage { get; } = safeMessage;
}
