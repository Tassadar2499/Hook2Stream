using System.Text.Json;
using Hook2Stream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Infrastructure.Persistence;

/// <summary>
/// Applies the database-side deletion fence before any storage purge is made
/// visible to workers. Clearing lease tokens makes late handler commits fail
/// their fencing check.
/// </summary>
public static class ProjectDeletionCoordinator
{
    public static async Task FenceAsync(
        Hook2StreamDbContext db,
        ReleaseProject project,
        DateTimeOffset now,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        project.DeletedAt ??= now;
        project.IsArchived = true;
        project.State = ProjectState.Archived;
        project.StateBeforeArchive = null;

        var assets = await db.MediaAssets
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var asset in assets)
        {
            asset.DeletedAt ??= now;
            asset.State = AssetState.Deleted;
            asset.IsActive = false;
        }

        // Prevent refresh/sign/complete endpoints from extending an upload
        // after deletion. The delayed storage purge runs only after every URL
        // issued before this fence has expired.
        var uploads = await db.UploadSessions
            .Where(value => value.ProjectId == project.Id)
            .ToListAsync(cancellationToken);
        foreach (var upload in uploads.Where(value =>
                     value.State is UploadState.Initiated or UploadState.Uploading))
        {
            upload.State = UploadState.Expired;
            upload.AbortedAt ??= now;
        }

        var jobs = await db.Jobs
            .Where(value => value.ProjectId == project.Id &&
                            (value.State == JobState.Queued || value.State == JobState.Running))
            .ToListAsync(cancellationToken);
        var jobIds = jobs.Select(value => value.Id).ToArray();
        var attempts = await db.JobAttempts
            .Where(value => jobIds.Contains(value.JobId) && value.State == JobState.Running)
            .ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            job.State = JobState.Cancelled;
            job.CompletedAt = now;
            job.ProgressStage = "cancelled";
            job.ErrorCode = reasonCode;
            job.ErrorMessage = "The project was deleted.";
            job.LeaseOwner = null;
            job.LeaseToken = null;
            job.LeaseExpiresAt = null;
            db.JobEvents.Add(new JobEvent
            {
                JobId = job.Id,
                EventType = "cancelled",
                DataJson = JsonSerializer.Serialize(new { code = reasonCode })
            });
        }
        foreach (var attempt in attempts)
        {
            attempt.State = JobState.Cancelled;
            attempt.CompletedAt = now;
            attempt.ErrorCode = reasonCode;
        }

        var runs = await db.PipelineRuns
            .Include(value => value.Stages)
            .Where(value => value.ProjectId == project.Id &&
                            value.State != PipelineStageState.Succeeded &&
                            value.State != PipelineStageState.Cancelled)
            .ToListAsync(cancellationToken);
        foreach (var run in runs)
        {
            run.State = PipelineStageState.Cancelled;
            run.CompletedAt = now;
            foreach (var stage in run.Stages.Where(value => value.State != PipelineStageState.Succeeded))
            {
                stage.State = PipelineStageState.Cancelled;
                stage.ProgressPercent = 0;
                stage.BlockerCode = reasonCode;
                stage.CurrentJobId = null;
                stage.CurrentRenderBatchId = null;
            }
        }

        var batches = await db.RenderBatches
            .Where(value => value.ProjectId == project.Id &&
                            (value.State == RenderBatchState.Queued || value.State == RenderBatchState.Running))
            .ToListAsync(cancellationToken);
        foreach (var batch in batches)
        {
            batch.State = RenderBatchState.Cancelled;
            batch.CompletedAt = now;
        }

        var pendingOutbox = await db.OutboxMessages
            .Where(value => value.AggregateId == project.Id && value.ProcessedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var message in pendingOutbox)
        {
            message.ProcessedAt = now;
            message.LastError = reasonCode;
        }
    }
}
