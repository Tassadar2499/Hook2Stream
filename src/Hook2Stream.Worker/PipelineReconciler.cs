using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Worker;

public sealed class PipelineReconciler(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    ILogger<PipelineReconciler> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);
    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await ReconcileBatchAsync(stoppingToken);
                if (count == 0)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(_options.OutboxPollMilliseconds),
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Pipeline reconciliation failed. Retrying after {DelaySeconds} seconds.",
                    _options.QueueErrorDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(_options.QueueErrorDelaySeconds), stoppingToken);
            }
        }
    }

    private async Task<int> ReconcileBatchAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < _options.OutboxBatchSize &&
               await ReconcileOneAsync(cancellationToken))
        {
            count++;
        }

        return count;
    }

    private async Task<bool> ReconcileOneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async token =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var message = await db.OutboxMessages
                    .FromSqlRaw(
                        """
                        SELECT *
                        FROM outbox_messages
                        WHERE deleted_at IS NULL
                          AND processed_at IS NULL
                          AND destination = 'pipeline'
                        ORDER BY created_at
                        FOR UPDATE SKIP LOCKED
                        LIMIT 1
                        """)
                    .SingleOrDefaultAsync(token);
                if (message is null)
                {
                    await transaction.RollbackAsync(token);
                    return false;
                }

                try
                {
                    if (message.AggregateId is not { } projectId)
                    {
                        throw new InvalidDataException("A pipeline reconcile message requires an aggregate id.");
                    }

                    await AcquireProjectLockAsync(db, projectId, token);
                    await ReconcileProjectAsync(
                        scope.ServiceProvider,
                        db,
                        message.WorkspaceId,
                        projectId,
                        token);
                    message.AttemptCount++;
                    message.ProcessedAt = DateTimeOffset.UtcNow;
                    message.LastError = null;
                    await db.SaveChangesAsync(token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    message.AttemptCount++;
                    message.LastError = "pipeline.reconcile_failed";
                    if (message.AttemptCount >= _options.OutboxMaxAttempts)
                    {
                        message.ProcessedAt = DateTimeOffset.UtcNow;
                        db.AuditEvents.Add(new AuditEvent
                        {
                            WorkspaceId = message.WorkspaceId,
                            Action = "pipeline.reconcile_dead_lettered",
                            ResourceType = "release_project",
                            ResourceId = message.AggregateId,
                            DataJson = JsonSerializer.Serialize(new { errorCode = message.LastError })
                        });
                    }

                    await db.SaveChangesAsync(token);
                    logger.LogWarning(
                        exception,
                        "Pipeline reconcile message {MessageId} failed on attempt {AttemptCount}.",
                        message.Id,
                        message.AttemptCount);
                }

                await transaction.CommitAsync(token);
                return true;
            },
            cancellationToken);
    }

    private static async Task AcquireProjectLockAsync(
        Hook2StreamDbContext db,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var key = BitConverter.ToInt64(SHA256.HashData(projectId.ToByteArray()), 0);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(@project_key)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "project_key";
        parameter.Value = key;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ReconcileProjectAsync(
        IServiceProvider services,
        Hook2StreamDbContext db,
        Guid workspaceId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .Include(value => value.Assets)
            .SingleOrDefaultAsync(
                value => value.Id == projectId && value.WorkspaceId == workspaceId,
                cancellationToken);
        if (project is null || project.FlowKind != FlowKind.Mp3First || project.IsArchived)
        {
            return;
        }

        var run = await db.PipelineRuns
            .Include(value => value.Stages)
            .Where(value => value.ProjectId == project.Id)
            .OrderByDescending(value => value.Number)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            run = new PipelineRun
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Project = project,
                Number = 1,
                State = PipelineStageState.NotStarted,
                Trigger = "reconcile"
            };
            run.Stages = Enum.GetValues<WorkflowLane>()
                .Select(lane => new PipelineStage
                {
                    PipelineRun = run,
                    PipelineRunId = run.Id,
                    Lane = lane
                })
                .ToList();
            db.PipelineRuns.Add(run);
        }

        var audio = project.Assets
            .Where(value => value.Kind == AssetKind.Audio && value.IsActive && value.State == AssetState.Ready)
            .OrderByDescending(value => value.Revision)
            .FirstOrDefault();
        if (audio is null || string.IsNullOrWhiteSpace(audio.Sha256))
        {
            SetStage(run, WorkflowLane.Audio, PipelineStageState.WaitingUser, "audio.upload_required");
            AggregateRun(run);
            return;
        }

        SetStage(run, WorkflowLane.Audio, PipelineStageState.Succeeded);
        await EnsureFinalRenderAsync(db, project, run, cancellationToken);
        run.InputFingerprint = audio.Sha256;
        var queue = services.GetRequiredService<IJobQueue>();
        var analysis = await db.TrackAnalysisRevisions
            .Where(value => value.ProjectId == project.Id && value.SourceFingerprint == audio.Sha256)
            .OrderByDescending(value => value.Number)
            .FirstOrDefaultAsync(cancellationToken);
        if (analysis is null)
        {
            var number = await db.TrackAnalysisRevisions
                .Where(value => value.ProjectId == project.Id)
                .Select(value => value.Number)
                .DefaultIfEmpty()
                .MaxAsync(cancellationToken) + 1;
            analysis = new TrackAnalysisRevision
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                SourceAssetId = audio.Id,
                Number = number,
                State = RevisionState.Processing,
                SourceFingerprint = audio.Sha256
            };
            db.TrackAnalysisRevisions.Add(analysis);
            var jobId = await queue.EnqueueAsync(new JobEnqueueRequest(
                project.WorkspaceId,
                project.Id,
                audio.Id,
                JobType.AudioAnalysis,
                JsonSerializer.Serialize(new
                {
                    projectId = project.Id,
                    assetId = audio.Id,
                    analysisRevisionId = analysis.Id
                }, StoredJson),
                $"audio-analysis:{audio.Id:N}:r{audio.Revision}:{audio.Sha256}",
                JobRoutingRegistry.Analysis,
                "deterministic-audio-v1",
                audio.Sha256,
                PipelineRunId: run.Id,
                PipelineStage: "analysis"), cancellationToken);
            SetStage(run, WorkflowLane.Analysis, PipelineStageState.Queued, currentJobId: jobId);
            project.State = ProjectState.Analyzing;
        }

        // Deterministic analysis and OpenRouter transcription are independent
        // consumers of the immutable audio master and may start concurrently.
        await EnsureTranscriptAsync(queue, db, project, audio, run, cancellationToken);

        if (analysis.State != RevisionState.Approved)
        {
            var job = await LatestJobAsync(db, project.Id, JobType.AudioAnalysis, audio.Sha256, cancellationToken);
            SetStage(
                run,
                WorkflowLane.Analysis,
                ToStageState(job?.State, analysis.State),
                errorCode: job?.ErrorCode,
                currentJobId: job?.Id);
            AggregateRun(run);
            return;
        }

        SetStage(run, WorkflowLane.Analysis, PipelineStageState.Succeeded);
        await EnsureInitialArtworkAsync(queue, db, project, audio, analysis, run, cancellationToken);
        await EnsureHooksAsync(db, project, audio, run, cancellationToken);
        var currentRights = await db.RightsAttestations.SingleOrDefaultAsync(
            value => value.ProjectId == project.Id,
            cancellationToken);
        var contentRights = ContentRightsGate.Evaluate(project, audio, currentRights);
        if (contentRights.Allowed)
        {
            await EnsureCampaignAsync(queue, db, project, audio, run, cancellationToken);
            await EnsurePreviewAsync(queue, db, project, audio, run, cancellationToken);
        }
        else
        {
            SetStage(run, WorkflowLane.Campaign, PipelineStageState.WaitingUser, contentRights.BlockerCode);
            SetStage(run, WorkflowLane.Preview, PipelineStageState.WaitingUser, contentRights.BlockerCode);
        }

        AggregateRun(run);
    }

    internal static async Task EnsureFinalRenderAsync(
        Hook2StreamDbContext db,
        ReleaseProject project,
        PipelineRun run,
        CancellationToken cancellationToken)
    {
        var batches = await db.RenderBatches
            .Where(value => value.ProjectId == project.Id &&
                            value.WorkspaceId == project.WorkspaceId &&
                            value.PipelineRunId == run.Id)
            .OrderByDescending(value => value.CreatedAt)
            .ToListAsync(cancellationToken);
        var batch = batches.FirstOrDefault(value => value.State is RenderBatchState.Queued or RenderBatchState.Running)
                    ?? batches.FirstOrDefault();
        if (batch is null)
        {
            var now = DateTimeOffset.UtcNow;
            var hasEntitlement = await db.Entitlements.AsNoTracking().AnyAsync(
                value => value.ProjectId == project.Id &&
                         value.WorkspaceId == project.WorkspaceId &&
                         value.State == EntitlementState.Active &&
                         value.RevokedAt == null &&
                         (value.ValidUntil == null || value.ValidUntil > now),
                cancellationToken);
            SetStage(
                run,
                WorkflowLane.FinalRender,
                PipelineStageState.WaitingUser,
                hasEntitlement ? "render.start_required" : "purchase.required");
            SetCurrentRenderBatch(run, null);
            return;
        }

        SetCurrentRenderBatch(run, batch.Id);

        var entitlement = await db.Entitlements.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == batch.EntitlementId && value.WorkspaceId == project.WorkspaceId,
            cancellationToken);
        if (entitlement is null || entitlement.State == EntitlementState.Revoked || entitlement.RevokedAt is not null)
        {
            SetStage(
                run,
                WorkflowLane.FinalRender,
                PipelineStageState.Cancelled,
                "entitlement.revoked",
                currentRenderBatchId: batch.Id);
            return;
        }

        var jobIds = Deserialize<List<Guid>>(batch.JobIdsJson) ?? [];
        var jobs = await db.Jobs.AsNoTracking()
            .Where(value => jobIds.Contains(value.Id))
            .ToListAsync(cancellationToken);
        var exportJob = jobs.FirstOrDefault(value => value.Type == JobType.ExportBundle);
        var currentJob = jobs
            .OrderBy(value => value.Type == JobType.ExportBundle ? 1 : 0)
            .FirstOrDefault(value => value.State is JobState.Running or JobState.Queued)
            ?? jobs.FirstOrDefault(value => value.Type == JobType.ExportBundle)
            ?? jobs.OrderByDescending(value => value.UpdatedAt).FirstOrDefault();
        var progress = jobs.Count == 0
            ? 0
            : (int)Math.Round(jobs.Average(value => value.ProgressPercent));
        var errorCode = jobs
            .Where(value => !string.IsNullOrWhiteSpace(value.ErrorCode))
            .OrderByDescending(value => value.UpdatedAt)
            .Select(value => value.ErrorCode)
            .FirstOrDefault();

        if (batch.State is RenderBatchState.Queued or RenderBatchState.Running && exportJob is not null)
        {
            if (exportJob.State == JobState.Cancelled &&
                string.Equals(exportJob.ProgressStage, "waiting_user", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(exportJob.ErrorCode))
            {
                SetStage(
                    run,
                    WorkflowLane.FinalRender,
                    PipelineStageState.WaitingUser,
                    exportJob.ErrorCode,
                    currentJobId: exportJob.Id,
                    progressPercent: progress,
                    currentRenderBatchId: batch.Id);
                return;
            }

            if (exportJob.State == JobState.Failed)
            {
                SetStage(
                    run,
                    WorkflowLane.FinalRender,
                    PipelineStageState.Failed,
                    errorCode: exportJob.ErrorCode ?? "export.failed",
                    currentJobId: exportJob.Id,
                    progressPercent: progress,
                    currentRenderBatchId: batch.Id);
                return;
            }

            if (exportJob.State == JobState.Cancelled)
            {
                SetStage(
                    run,
                    WorkflowLane.FinalRender,
                    PipelineStageState.Cancelled,
                    "render.export_cancelled",
                    exportJob.ErrorCode,
                    exportJob.Id,
                    progress,
                    batch.Id);
                return;
            }
        }

        switch (batch.State)
        {
            case RenderBatchState.Queued:
                SetStage(run, WorkflowLane.FinalRender, PipelineStageState.Queued,
                    errorCode: errorCode, currentJobId: currentJob?.Id, progressPercent: progress,
                    currentRenderBatchId: batch.Id);
                break;
            case RenderBatchState.Running:
                SetStage(run, WorkflowLane.FinalRender, PipelineStageState.Running,
                    errorCode: errorCode, currentJobId: currentJob?.Id, progressPercent: progress,
                    currentRenderBatchId: batch.Id);
                break;
            case RenderBatchState.Succeeded:
                SetStage(run, WorkflowLane.FinalRender, PipelineStageState.Succeeded,
                    currentJobId: currentJob?.Id, progressPercent: 100,
                    currentRenderBatchId: batch.Id);
                break;
            case RenderBatchState.PartiallySucceeded:
                SetStage(run, WorkflowLane.FinalRender, PipelineStageState.Degraded,
                    "render.partial_failure", errorCode, currentJob?.Id, 100, batch.Id);
                break;
            case RenderBatchState.Failed:
                SetStage(run, WorkflowLane.FinalRender, PipelineStageState.Failed,
                    errorCode: errorCode ?? "render.batch_failed", currentJobId: currentJob?.Id, progressPercent: 100,
                    currentRenderBatchId: batch.Id);
                break;
            case RenderBatchState.Cancelled:
                SetStage(run, WorkflowLane.FinalRender, PipelineStageState.Cancelled,
                    "render.batch_cancelled", errorCode, currentJob?.Id, progress, batch.Id);
                break;
        }

    }

    private static async Task EnsureTranscriptAsync(
        IJobQueue queue,
        Hook2StreamDbContext db,
        ReleaseProject project,
        MediaAsset audio,
        PipelineRun run,
        CancellationToken cancellationToken)
    {
        var current = project.CurrentTranscriptRevisionId is { } currentId
            ? await db.TranscriptRevisions.SingleOrDefaultAsync(value => value.Id == currentId, cancellationToken)
            : null;
        if (project.IsInstrumental && project.IsInstrumentalConfirmed)
        {
            await InstrumentalTranscriptCoordinator.EnsureAsync(
                db,
                project,
                audio,
                "system:pipeline",
                DateTimeOffset.UtcNow,
                cancellationToken);
            SetStage(run, WorkflowLane.Transcript, PipelineStageState.Succeeded);
            return;
        }

        if (current is not null && current.SourceFingerprint == audio.Sha256)
        {
            if (current.State == RevisionState.Processing)
            {
                var consent = await ExternalAiProcessingAsync(db, project, audio, cancellationToken);
                if (!consent.Allowed)
                {
                    SetStage(
                        run,
                        WorkflowLane.Transcript,
                        PipelineStageState.WaitingUser,
                        consent.BlockerCode ?? "rights.external_ai_processing_required");
                    return;
                }
            }

            var job = current.State == RevisionState.Processing
                ? await LatestJobAsync(db, project.Id, JobType.Transcription, audio.Sha256!, cancellationToken)
                : null;
            SetStage(
                run,
                WorkflowLane.Transcript,
                current.State == RevisionState.Approved
                    ? PipelineStageState.Succeeded
                    : current.State == RevisionState.ReadyForReview
                        ? PipelineStageState.WaitingUser
                        : ToStageState(job?.State, current.State),
                current.State == RevisionState.ReadyForReview ? "transcript.review_required" : null,
                job?.ErrorCode,
                job?.Id);
            return;
        }

        var externalAi = await ExternalAiProcessingAsync(db, project, audio, cancellationToken);
        if (!externalAi.Allowed)
        {
            SetStage(
                run,
                WorkflowLane.Transcript,
                PipelineStageState.WaitingUser,
                externalAi.BlockerCode ?? "rights.external_ai_processing_required");
            return;
        }

        var number = await db.TranscriptRevisions
            .Where(value => value.ProjectId == project.Id)
            .Select(value => value.Number)
            .DefaultIfEmpty()
            .MaxAsync(cancellationToken) + 1;
        var revision = new TranscriptRevision
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Number = number,
            Source = TranscriptSource.Automatic,
            State = RevisionState.Processing,
            Language = project.Language,
            SourceFingerprint = audio.Sha256!,
            SupersedesRevisionId = current?.Id
        };
        if (current is not null) current.State = RevisionState.Superseded;
        db.TranscriptRevisions.Add(revision);
        project.CurrentTranscriptRevisionId = revision.Id;
        var jobId = await queue.EnqueueAsync(new JobEnqueueRequest(
            project.WorkspaceId,
            project.Id,
            audio.Id,
            JobType.Transcription,
            JsonSerializer.Serialize(new
            {
                projectId = project.Id,
                assetId = audio.Id,
                transcriptRevisionId = revision.Id
            }, StoredJson),
            $"transcription:{revision.Id:N}:{audio.Sha256}",
            JobRoutingRegistry.Control,
            "openrouter-stt-v1",
            audio.Sha256,
            PipelineRunId: run.Id,
            PipelineStage: "transcript"), cancellationToken);
        SetStage(run, WorkflowLane.Transcript, PipelineStageState.Queued, currentJobId: jobId);
    }

    private static async Task EnsureInitialArtworkAsync(
        IJobQueue queue,
        Hook2StreamDbContext db,
        ReleaseProject project,
        MediaAsset audio,
        TrackAnalysisRevision analysis,
        PipelineRun run,
        CancellationToken cancellationToken)
    {
        var current = project.CurrentArtworkPackRevisionId is { } currentId
            ? await db.ArtworkPackRevisions.SingleOrDefaultAsync(value => value.Id == currentId, cancellationToken)
            : null;
        if (current is not null)
        {
            if (current.State == RevisionState.Approved)
            {
                var backgroundIds = Deserialize<List<Guid>>(current.BackgroundAssetIdsJson) ?? [];
                var readyBackgrounds = await db.MediaAssets.CountAsync(
                    value => backgroundIds.Contains(value.Id) &&
                             value.ProjectId == project.Id &&
                             value.State == AssetState.Ready &&
                             value.Purpose == AssetPurpose.CampaignBackground,
                    cancellationToken);
                if (backgroundIds.Count == 3 && readyBackgrounds == 3)
                {
                    SetStage(run, WorkflowLane.Artwork, PipelineStageState.Succeeded);
                    return;
                }

                var backgroundJob = await db.Jobs
                    .Where(value => value.ProjectId == project.Id &&
                                    value.Type == JobType.ArtworkGeneration &&
                                    value.AssetId == current.SelectedAssetId)
                    .OrderByDescending(value => value.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (backgroundJob?.State is JobState.Queued or JobState.Running)
                {
                    var consent = await ExternalAiProcessingAsync(db, project, audio, cancellationToken);
                    if (!consent.Allowed)
                    {
                        SetStage(
                            run,
                            WorkflowLane.Artwork,
                            PipelineStageState.WaitingUser,
                            consent.BlockerCode ?? "rights.external_ai_processing_required",
                            currentJobId: backgroundJob.Id);
                        return;
                    }
                }

                SetStage(
                    run,
                    WorkflowLane.Artwork,
                    backgroundJob is null
                        ? PipelineStageState.WaitingUser
                        : ToStageState(backgroundJob.State, null),
                    backgroundJob is null ? "artwork.backgrounds_required" : null,
                    backgroundJob?.ErrorCode,
                    backgroundJob?.Id);
                return;
            }

            if (current.State == RevisionState.Processing)
            {
                var consent = await ExternalAiProcessingAsync(db, project, audio, cancellationToken);
                if (!consent.Allowed)
                {
                    SetStage(
                        run,
                        WorkflowLane.Artwork,
                        PipelineStageState.WaitingUser,
                        consent.BlockerCode ?? "rights.external_ai_processing_required");
                    return;
                }
            }

            SetStage(
                run,
                WorkflowLane.Artwork,
                current.State == RevisionState.ReadyForReview
                        ? PipelineStageState.WaitingUser
                        : ToStageState(null, current.State),
                current.State == RevisionState.ReadyForReview ? "artwork.review_required" : null);
            return;
        }

        var rights = await db.RightsAttestations.SingleOrDefaultAsync(
            value => value.ProjectId == project.Id,
            cancellationToken);
        var gate = ArtworkAutomationGate.Evaluate(
            project,
            audio,
            rights,
            DateOnly.FromDateTime(DateTime.UtcNow));
        if (!gate.Allowed)
        {
            SetStage(run, WorkflowLane.Artwork, PipelineStageState.WaitingUser, gate.BlockerCode);
            return;
        }

        if (await db.ArtworkPackRevisions.AnyAsync(value => value.ProjectId == project.Id, cancellationToken))
        {
            // Never spend another artwork operation implicitly.
            SetStage(run, WorkflowLane.Artwork, PipelineStageState.WaitingUser, "artwork.generation_required");
            return;
        }

        var sourceFingerprint = Hash($"{audio.Sha256}:{analysis.Id:N}:{project.ArtistName}:{project.TrackTitle}");
        var revision = new ArtworkPackRevision
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Number = 1,
            OperationNumber = 1,
            State = RevisionState.Processing,
            Prompt = "Text-free artwork reflecting the track mood and energy.",
            SourceFingerprint = sourceFingerprint
        };
        db.ArtworkPackRevisions.Add(revision);
        project.CurrentArtworkPackRevisionId = revision.Id;
        var jobId = await queue.EnqueueAsync(new JobEnqueueRequest(
            project.WorkspaceId,
            project.Id,
            null,
            JobType.ArtworkGeneration,
            JsonSerializer.Serialize(new
            {
                projectId = project.Id,
                artworkPackRevisionId = revision.Id,
                prompt = revision.Prompt,
                style = "track-derived"
            }, StoredJson),
            $"artwork:auto:{revision.Id:N}:{sourceFingerprint}",
            JobRoutingRegistry.Control,
            "openrouter-image-v1",
            sourceFingerprint,
            PipelineRunId: run.Id,
            PipelineStage: "artwork"), cancellationToken);
        SetStage(run, WorkflowLane.Artwork, PipelineStageState.Queued, currentJobId: jobId);
    }

    private static async Task EnsureHooksAsync(
        Hook2StreamDbContext db,
        ReleaseProject project,
        MediaAsset audio,
        PipelineRun run,
        CancellationToken cancellationToken)
    {
        if (project.CurrentTranscriptRevisionId is not { } transcriptId)
        {
            SetStage(run, WorkflowLane.Hooks, PipelineStageState.NotStarted, "transcript.approval_required");
            return;
        }

        var transcript = await db.TranscriptRevisions.SingleAsync(value => value.Id == transcriptId, cancellationToken);
        if (transcript.State != RevisionState.Approved)
        {
            SetStage(run, WorkflowLane.Hooks, PipelineStageState.NotStarted, "transcript.approval_required");
            return;
        }

        var current = project.CurrentHookSetRevisionId is { } currentId
            ? await db.HookSetRevisions.SingleOrDefaultAsync(value => value.Id == currentId, cancellationToken)
            : null;
        if (current is { State: RevisionState.Approved } && current.TranscriptRevisionId == transcript.Id)
        {
            SetStage(run, WorkflowLane.Hooks, PipelineStageState.Succeeded);
            return;
        }

        if (current is not null) current.State = RevisionState.Superseded;
        var number = await db.HookSetRevisions
            .Where(value => value.ProjectId == project.Id)
            .Select(value => value.Number)
            .DefaultIfEmpty()
            .MaxAsync(cancellationToken) + 1;
        var hooks = BuildHooks(transcript.PhrasesJson, audio.DurationMilliseconds ?? 180_000);
        var revision = new HookSetRevision
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Number = number,
            State = RevisionState.Approved,
            TranscriptRevisionId = transcript.Id,
            HooksJson = JsonSerializer.Serialize(hooks, StoredJson),
            SourceFingerprint = $"transcript:{transcript.Id:N}:v{transcript.Version}"
        };
        db.HookSetRevisions.Add(revision);
        project.CurrentHookSetRevisionId = revision.Id;
        SetStage(run, WorkflowLane.Hooks, PipelineStageState.Succeeded);
    }

    private static async Task EnsureCampaignAsync(
        IJobQueue queue,
        Hook2StreamDbContext db,
        ReleaseProject project,
        MediaAsset audio,
        PipelineRun run,
        CancellationToken cancellationToken)
    {
        if (project.CurrentTranscriptRevisionId is not { } transcriptId ||
            project.CurrentArtworkPackRevisionId is not { } artworkId ||
            project.CurrentHookSetRevisionId is not { } hookId)
        {
            SetStage(run, WorkflowLane.Campaign, PipelineStageState.NotStarted, "campaign.dependencies_required");
            return;
        }

        var transcript = await db.TranscriptRevisions.SingleAsync(value => value.Id == transcriptId, cancellationToken);
        var artwork = await db.ArtworkPackRevisions.SingleAsync(value => value.Id == artworkId, cancellationToken);
        var hooks = await db.HookSetRevisions.SingleAsync(value => value.Id == hookId, cancellationToken);
        var brand = await db.BrandKits.SingleAsync(
            value => value.WorkspaceId == project.WorkspaceId,
            cancellationToken);
        project.BrandKitVersion = brand.Version;
        var backgroundIds = Deserialize<List<Guid>>(artwork.BackgroundAssetIdsJson) ?? [];
        var readyBackgrounds = await db.MediaAssets.CountAsync(
            value => backgroundIds.Contains(value.Id) &&
                     value.ProjectId == project.Id &&
                     value.State == AssetState.Ready &&
                     value.Purpose == AssetPurpose.CampaignBackground,
            cancellationToken);
        if (transcript.State != RevisionState.Approved || artwork.State != RevisionState.Approved ||
            hooks.State != RevisionState.Approved || backgroundIds.Count != 3 || readyBackgrounds != 3)
        {
            SetStage(run, WorkflowLane.Campaign, PipelineStageState.NotStarted, "campaign.dependencies_required");
            return;
        }

        var current = project.CurrentCampaignPlanRevisionId is { } currentId
            ? await db.CampaignPlanRevisions.SingleOrDefaultAsync(value => value.Id == currentId, cancellationToken)
            : null;
        var fingerprint = Hash(
            $"{transcript.Id:N}:{transcript.Version}:{artwork.Id:N}:{artwork.Version}:{hooks.Id:N}:{hooks.Version}:" +
            $"{project.ArtistName}:{project.TrackTitle}:{project.Language}:{project.Mode}:{project.ReleaseDate:O}:{project.CampaignStartDate:O}:" +
            $"{project.IsInstrumental}:{project.IsInstrumentalConfirmed}:brand:{brand.Version}");
        if (current is not null && current.SourceFingerprint == fingerprint)
        {
            if (current.State == RevisionState.Processing)
            {
                var consent = await ExternalAiProcessingAsync(db, project, audio, cancellationToken);
                if (!consent.Allowed)
                {
                    SetStage(
                        run,
                        WorkflowLane.Campaign,
                        PipelineStageState.WaitingUser,
                        consent.BlockerCode ?? "rights.external_ai_processing_required");
                    return;
                }
            }

            SetStage(
                run,
                WorkflowLane.Campaign,
                current.State is RevisionState.ReadyForReview or RevisionState.Approved
                    ? PipelineStageState.Succeeded
                    : ToStageState(null, current.State));
            return;
        }

        var externalAi = await ExternalAiProcessingAsync(db, project, audio, cancellationToken);
        if (!externalAi.Allowed)
        {
            SetStage(
                run,
                WorkflowLane.Campaign,
                PipelineStageState.WaitingUser,
                externalAi.BlockerCode ?? "rights.external_ai_processing_required");
            return;
        }

        if (current is not null) current.State = RevisionState.Superseded;
        var number = await db.CampaignPlanRevisions
            .Where(value => value.ProjectId == project.Id)
            .Select(value => value.Number)
            .DefaultIfEmpty()
            .MaxAsync(cancellationToken) + 1;
        var revision = new CampaignPlanRevision
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Number = number,
            State = RevisionState.Processing,
            TranscriptRevisionId = transcript.Id,
            ArtworkPackRevisionId = artwork.Id,
            HookSetRevisionId = hooks.Id,
            SourceFingerprint = fingerprint
        };
        db.CampaignPlanRevisions.Add(revision);
        project.CurrentCampaignPlanRevisionId = revision.Id;
        var jobId = await queue.EnqueueAsync(new JobEnqueueRequest(
            project.WorkspaceId,
            project.Id,
            null,
            JobType.CampaignGeneration,
            JsonSerializer.Serialize(new
            {
                projectId = project.Id,
                campaignRevisionId = revision.Id,
                brandKitVersion = brand.Version
            }, StoredJson),
            $"campaign:{revision.Id:N}:{fingerprint}",
            JobRoutingRegistry.Control,
            "openrouter-campaign-v1",
            fingerprint,
            PipelineRunId: run.Id,
            PipelineStage: "campaign"), cancellationToken);
        SetStage(run, WorkflowLane.Campaign, PipelineStageState.Queued, currentJobId: jobId);
    }

    private static async Task EnsurePreviewAsync(
        IJobQueue queue,
        Hook2StreamDbContext db,
        ReleaseProject project,
        MediaAsset audio,
        PipelineRun run,
        CancellationToken cancellationToken)
    {
        if (project.CurrentCampaignPlanRevisionId is not { } campaignId)
        {
            SetStage(run, WorkflowLane.Preview, PipelineStageState.NotStarted, "campaign.required");
            return;
        }

        var campaign = await db.CampaignPlanRevisions.SingleAsync(value => value.Id == campaignId, cancellationToken);
        if (campaign.State is not (RevisionState.ReadyForReview or RevisionState.Approved))
        {
            SetStage(run, WorkflowLane.Preview, PipelineStageState.NotStarted, "campaign.required");
            return;
        }

        // A successful preview consumes the one project-level allowance. Active
        // work reserves it only for the exact immutable campaign revision.
        var successful = await db.Jobs
            .Where(value => value.ProjectId == project.Id &&
                            value.Type == JobType.PreviewRender &&
                            value.State == JobState.Succeeded)
            .OrderBy(value => value.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var active = await db.Jobs
            .Where(value => value.ProjectId == project.Id &&
                            value.Type == JobType.PreviewRender &&
                            (value.State == JobState.Queued || value.State == JobState.Running))
            .OrderByDescending(value => value.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var activeCampaignId = active is null ? null : PayloadGuid(active.PayloadJson, "campaignRevisionId");
        if (active is not null && activeCampaignId != campaign.Id)
        {
            await CancelJobAsync(db, active, "preview.revision_superseded", cancellationToken);
            active = null;
        }

        if (successful is not null)
        {
            var stale = PayloadGuid(successful.PayloadJson, "campaignRevisionId") != campaign.Id;
            SetStage(
                run,
                WorkflowLane.Preview,
                stale ? PipelineStageState.Stale : PipelineStageState.Succeeded,
                stale ? "preview.allowance_consumed" : null,
                errorCode: successful.ErrorCode,
                currentJobId: successful.Id);
            return;
        }

        if (active is not null)
        {
            SetStage(
                run,
                WorkflowLane.Preview,
                ToStageState(active.State, null),
                errorCode: active.ErrorCode,
                currentJobId: active.Id);
            return;
        }

        var failedCandidates = await db.Jobs
            .Where(value => value.ProjectId == project.Id &&
                            value.Type == JobType.PreviewRender &&
                            value.State == JobState.Failed)
            .OrderByDescending(value => value.CreatedAt)
            .ToListAsync(cancellationToken);
        var failed = failedCandidates.FirstOrDefault(value =>
            PayloadGuid(value.PayloadJson, "campaignRevisionId") == campaign.Id);
        if (failed is not null)
        {
            SetStage(
                run,
                WorkflowLane.Preview,
                PipelineStageState.Failed,
                errorCode: failed.ErrorCode,
                currentJobId: failed.Id);
            return;
        }

        var items = Deserialize<List<CampaignItemRequest>>(campaign.ItemsJson) ?? [];
        if (items.Count != 18)
        {
            SetStage(run, WorkflowLane.Preview, PipelineStageState.NotStarted, "campaign.incomplete");
            return;
        }

        var hookRevision = await db.HookSetRevisions.SingleAsync(
            value => value.Id == campaign.HookSetRevisionId,
            cancellationToken);
        var hooks = Deserialize<List<HookRequest>>(hookRevision.HooksJson) ?? [];
        var instrumental = project.IsInstrumental && project.IsInstrumentalConfirmed;
        var preferredTemplate = instrumental ? "visual-loop-a" : "kinetic-lyrics";
        var preferredHook = instrumental ? "energy" : "chorus";
        var item = items
            .Where(IsPreviewEligible)
            .OrderBy(value =>
                string.Equals(value.Template, preferredTemplate, StringComparison.OrdinalIgnoreCase) &&
                hooks.Any(hook => hook.Id == value.HookId &&
                                  hook.Kind.Contains(preferredHook, StringComparison.OrdinalIgnoreCase))
                    ? 0
                    : 1)
            .ThenBy(value => value.Slot)
            .FirstOrDefault();
        if (item is null)
        {
            SetStage(run, WorkflowLane.Preview, PipelineStageState.WaitingUser, "preview.no_eligible_item");
            return;
        }
        var jobId = await queue.EnqueueAsync(new JobEnqueueRequest(
            project.WorkspaceId,
            project.Id,
            audio.Id,
            JobType.PreviewRender,
            JsonSerializer.Serialize(new
            {
                projectId = project.Id,
                campaignRevisionId = campaign.Id,
                campaignItemId = item.Id,
                audioAssetId = audio.Id,
                audioFingerprint = audio.Sha256
            }, StoredJson),
            $"preview:{campaign.Id:N}",
            JobRoutingRegistry.Render,
            "deterministic-render-v1",
            campaign.Id.ToString("N"),
            PipelineRunId: run.Id,
            PipelineStage: "preview"), cancellationToken);
        SetStage(run, WorkflowLane.Preview, PipelineStageState.Queued, currentJobId: jobId);
    }

    private static bool IsPreviewEligible(CampaignItemRequest item)
    {
        try
        {
            using var document = JsonDocument.Parse(item.CompositionJson);
            var root = document.RootElement;
            if (root.TryGetProperty("isReady", out var ready) && ready.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (root.TryGetProperty("blockingWarning", out var blocking) &&
                blocking.ValueKind == JsonValueKind.True)
            {
                return false;
            }

            return !root.TryGetProperty("blockingWarnings", out var warnings) ||
                   warnings.ValueKind != JsonValueKind.Array ||
                   warnings.GetArrayLength() == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task CancelJobAsync(
        Hook2StreamDbContext db,
        Job job,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        job.State = JobState.Cancelled;
        job.CompletedAt = now;
        job.ProgressStage = "cancelled";
        job.ErrorCode = reasonCode;
        job.ErrorMessage = "The queued preview no longer matches the current campaign revision.";
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.LeaseToken = null;
        var activeAttempts = await db.JobAttempts
            .Where(value => value.JobId == job.Id && value.State == JobState.Running)
            .ToListAsync(cancellationToken);
        foreach (var attempt in activeAttempts)
        {
            attempt.State = JobState.Cancelled;
            attempt.CompletedAt = now;
            attempt.ErrorCode = reasonCode;
        }

        db.JobEvents.Add(new JobEvent
        {
            JobId = job.Id,
            EventType = "cancelled",
            DataJson = JsonSerializer.Serialize(new { reasonCode })
        });
    }

    private static Guid? PayloadGuid(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value) &&
                   value.TryGetGuid(out var parsed)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<HookRequest> BuildHooks(string phrasesJson, long duration)
    {
        var phrases = Deserialize<List<TranscriptPhraseRequest>>(phrasesJson) ?? [];
        string[] kinds = ["chorus", "emotional", "energy"];
        var hooks = new List<HookRequest>(3);
        for (var index = 0; index < 3; index++)
        {
            var phrase = phrases.Count == 0 ? null : phrases[Math.Min(index, phrases.Count - 1)];
            var proposedStart = phrase?.StartMilliseconds ?? duration * index / 3;
            var start = Math.Clamp(proposedStart, 0, Math.Max(0, duration - 10_000));
            var end = Math.Min(duration, start + 15_000);
            if (end - start < 10_000)
            {
                start = Math.Max(0, end - 10_000);
            }

            hooks.Add(new HookRequest(
                Guid.CreateVersion7().ToString("N"),
                kinds[index],
                start,
                end,
                phrase?.Text ?? $"{kinds[index]} hook"));
        }

        return hooks;
    }

    private static void SetStage(
        PipelineRun run,
        WorkflowLane lane,
        PipelineStageState state,
        string? blockerCode = null,
        string? errorCode = null,
        Guid? currentJobId = null,
        int? progressPercent = null,
        Guid? currentRenderBatchId = null)
    {
        var stage = run.Stages.SingleOrDefault(value => value.Lane == lane);
        if (stage is null)
        {
            stage = new PipelineStage
            {
                PipelineRun = run,
                PipelineRunId = run.Id,
                Lane = lane
            };
            run.Stages.Add(stage);
        }

        var progress = progressPercent is { } provided
            ? Math.Clamp(provided, 0, 100)
            : state == PipelineStageState.Succeeded
                ? 100
                : state is PipelineStageState.NotStarted or PipelineStageState.WaitingUser
                    ? 0
                    : stage.ProgressPercent;
        var changed = stage.State != state ||
                      stage.ProgressPercent != progress ||
                      !string.Equals(stage.BlockerCode, blockerCode, StringComparison.Ordinal) ||
                      !string.Equals(stage.ErrorCode, errorCode, StringComparison.Ordinal) ||
                      stage.CurrentJobId != currentJobId ||
                      stage.CurrentRenderBatchId != currentRenderBatchId;
        stage.State = state;
        stage.ProgressPercent = progress;
        stage.BlockerCode = blockerCode;
        stage.ErrorCode = errorCode;
        stage.CurrentJobId = currentJobId;
        stage.CurrentRenderBatchId = currentRenderBatchId;
        if (changed)
        {
            // PipelineRun.Version is the workflow snapshot version exposed by
            // the API, so any child-stage mutation must also touch the run.
            run.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static void SetCurrentRenderBatch(PipelineRun run, Guid? renderBatchId)
    {
        var stage = run.Stages.Single(value => value.Lane == WorkflowLane.FinalRender);
        if (stage.CurrentRenderBatchId == renderBatchId) return;
        stage.CurrentRenderBatchId = renderBatchId;
        run.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void AggregateRun(PipelineRun run)
    {
        var states = run.Stages.Select(value => value.State).ToArray();
        var state = states.Any(value => value == PipelineStageState.Failed)
            ? PipelineStageState.Failed
            : states.Any(value => value == PipelineStageState.Degraded)
                ? PipelineStageState.Degraded
                : states.Any(value => value is PipelineStageState.Running or PipelineStageState.Queued or PipelineStageState.Retrying)
                    ? PipelineStageState.Running
                    : states.All(value => value == PipelineStageState.Succeeded)
                        ? PipelineStageState.Succeeded
                        : PipelineStageState.WaitingUser;
        if (run.State != state)
        {
            run.State = state;
            run.UpdatedAt = DateTimeOffset.UtcNow;
        }

        run.CompletedAt = state == PipelineStageState.Succeeded
            ? run.CompletedAt ?? DateTimeOffset.UtcNow
            : null;
    }

    private static PipelineStageState ToStageState(JobState? job, RevisionState? revision) =>
        job switch
        {
            JobState.Queued => PipelineStageState.Queued,
            JobState.Running => PipelineStageState.Running,
            JobState.Succeeded => PipelineStageState.Succeeded,
            JobState.Failed => PipelineStageState.Failed,
            JobState.Cancelled => PipelineStageState.Cancelled,
            _ => revision switch
            {
                RevisionState.Processing => PipelineStageState.Running,
                RevisionState.ReadyForReview => PipelineStageState.WaitingUser,
                RevisionState.Approved => PipelineStageState.Succeeded,
                RevisionState.Failed => PipelineStageState.Failed,
                RevisionState.Superseded => PipelineStageState.Stale,
                _ => PipelineStageState.NotStarted
            }
        };

    private static async Task<ExternalAiProcessingDecision> ExternalAiProcessingAsync(
        Hook2StreamDbContext db,
        ReleaseProject project,
        MediaAsset audio,
        CancellationToken cancellationToken)
    {
        var rights = await db.RightsAttestations.AsNoTracking().SingleOrDefaultAsync(
            value => value.ProjectId == project.Id,
            cancellationToken);
        return ExternalAiProcessingGate.Evaluate(project, audio, rights);
    }

    private static Task<Job?> LatestJobAsync(
        Hook2StreamDbContext db,
        Guid projectId,
        JobType type,
        string fingerprint,
        CancellationToken cancellationToken) =>
        db.Jobs.Where(value => value.ProjectId == projectId &&
                               value.Type == type &&
                               value.InputFingerprint == fingerprint)
            .OrderByDescending(value => value.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, StoredJson);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public static class PipelineOutbox
{
    public static string CreateReconcileDedupeKey(
        Guid projectId,
        string reason,
        Guid? causationId = null) =>
        $"pipeline.reconcile:{projectId:N}:{reason}:{causationId?.ToString("N") ?? Guid.CreateVersion7().ToString("N")}";

    public static void Reconcile(
        Hook2StreamDbContext db,
        ReleaseProject project,
        string reason,
        Guid? causationId = null)
    {
        db.OutboxMessages.Add(new OutboxMessage
        {
            WorkspaceId = project.WorkspaceId,
            AggregateId = project.Id,
            Destination = "pipeline",
            MessageType = "pipeline.reconcile",
            DedupeKey = CreateReconcileDedupeKey(project.Id, reason, causationId),
            PayloadJson = JsonSerializer.Serialize(new { projectId = project.Id, reason })
        });
        db.ProjectEvents.Add(new ProjectEvent
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            EventType = reason,
            DataJson = JsonSerializer.Serialize(new
            {
                projectId = project.Id,
                causationId
            })
        });
    }
}
