using System.Text.Json;
using Hook2Stream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Infrastructure.Persistence;

/// <summary>
/// Pauses in-flight project work without deleting creative data. Lease tokens
/// are revoked immediately; restore requeues only jobs paused by archiving and
/// preserves a safety delay before another worker can lease them.
/// </summary>
public static class ProjectArchiveCoordinator
{
    public static async Task PauseAsync(
        Hook2StreamDbContext db,
        ReleaseProject project,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
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
            job.ProgressStage = "archived";
            job.ErrorCode = "project.archived";
            job.ErrorMessage = "The project was archived.";
            job.LeaseOwner = null;
            job.LeaseToken = null;
            job.LeaseExpiresAt = null;
            db.JobEvents.Add(new JobEvent
            {
                JobId = job.Id,
                EventType = "cancelled",
                DataJson = JsonSerializer.Serialize(new { code = "project.archived" })
            });
        }

        foreach (var attempt in attempts)
        {
            attempt.State = JobState.Cancelled;
            attempt.CompletedAt = now;
            attempt.ErrorCode = "project.archived";
        }

        if (jobIds.Length > 0)
        {
            var stages = await db.PipelineStages
                .Include(value => value.PipelineRun)
                .Where(value => value.CurrentJobId != null && jobIds.Contains(value.CurrentJobId.Value))
                .ToListAsync(cancellationToken);
            foreach (var stage in stages)
            {
                stage.State = PipelineStageState.Cancelled;
                stage.ProgressPercent = 0;
                stage.BlockerCode = "project.archived";
                stage.ErrorCode = null;
                stage.PipelineRun.State = PipelineStageState.Cancelled;
                stage.PipelineRun.CompletedAt = now;
            }
        }

        var pendingOutbox = await db.OutboxMessages
            .Where(value => value.AggregateId == project.Id && value.ProcessedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var message in pendingOutbox)
        {
            message.ProcessedAt = now;
            message.LastError = "project.archived";
        }
    }

    public static async Task ResumeAsync(
        Hook2StreamDbContext db,
        ReleaseProject project,
        DateTimeOffset now,
        TimeSpan leaseSafetyDelay,
        CancellationToken cancellationToken)
    {
        var jobs = await db.Jobs
            .Where(value => value.ProjectId == project.Id &&
                            value.State == JobState.Cancelled &&
                            value.ErrorCode == "project.archived")
            .ToListAsync(cancellationToken);
        var jobIds = jobs.Select(value => value.Id).ToArray();
        foreach (var job in jobs)
        {
            var safeAt = (job.CompletedAt ?? now).Add(leaseSafetyDelay);
            job.State = JobState.Queued;
            job.AvailableAt = safeAt > now ? safeAt : now;
            // Keep the monotonic attempt number: JobAttempt has a unique
            // (JobId, Number) key. Archiving must not make the next lease reuse
            // an existing attempt number, but it also must not consume the
            // restored job's final retry budget.
            job.MaxAttempts = Math.Max(job.MaxAttempts, job.AttemptCount + 1);
            job.CompletedAt = null;
            job.ProgressPercent = 0;
            job.ProgressStage = "queued";
            job.ErrorCode = null;
            job.ErrorMessage = null;
            job.LeaseOwner = null;
            job.LeaseToken = null;
            job.LeaseExpiresAt = null;
            db.JobEvents.Add(new JobEvent
            {
                JobId = job.Id,
                EventType = "requeued",
                DataJson = JsonSerializer.Serialize(new { reason = "project.restored", job.AvailableAt })
            });
        }

        if (jobIds.Length == 0) return;

        var stages = await db.PipelineStages
            .Include(value => value.PipelineRun)
            .Where(value => value.CurrentJobId != null && jobIds.Contains(value.CurrentJobId.Value))
            .ToListAsync(cancellationToken);
        foreach (var stage in stages)
        {
            stage.State = PipelineStageState.Queued;
            stage.ProgressPercent = 0;
            stage.BlockerCode = null;
            stage.ErrorCode = null;
            stage.PipelineRun.State = PipelineStageState.Queued;
            stage.PipelineRun.CompletedAt = null;
        }
    }
}
