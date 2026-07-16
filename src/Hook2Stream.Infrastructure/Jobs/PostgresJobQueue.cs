using System.Data;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Hook2Stream.Infrastructure.Jobs;

public sealed class PostgresJobQueue(Hook2StreamDbContext dbContext) : IJobQueue
{
    public async Task<Guid> EnqueueAsync(
        Guid workspaceId,
        Guid? projectId,
        Guid? assetId,
        JobType type,
        string payloadJson,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existingId = await dbContext.Jobs
                .Where(job => job.IdempotencyKey == idempotencyKey)
                .Select(job => (Guid?)job.Id)
                .SingleOrDefaultAsync(cancellationToken);

            if (existingId is not null)
            {
                return existingId.Value;
            }
        }

        var job = new Job
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            AssetId = assetId,
            Type = type,
            PayloadJson = payloadJson,
            IdempotencyKey = idempotencyKey,
            State = JobState.Queued,
            AvailableAt = DateTimeOffset.UtcNow
        };

        dbContext.Jobs.Add(job);
        dbContext.JobEvents.Add(NewEvent(job.Id, "queued", new { job.Type, job.AvailableAt }));
        await dbContext.SaveChangesAsync(cancellationToken);
        return job.Id;
    }

    public async Task<LeasedJob?> TryLeaseAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            token => TryLeaseOnceAsync(workerId, leaseDuration, token),
            cancellationToken);
    }

    private async Task<LeasedJob?> TryLeaseOnceAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseExpiresAt = now.Add(leaseDuration);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            """
            WITH candidate AS (
                SELECT id
                FROM jobs
                WHERE deleted_at IS NULL
                  AND (
                    (state = @queued AND available_at <= @now)
                    OR (state = @running AND lease_expires_at <= @now)
                  )
                ORDER BY available_at, created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE jobs AS job
            SET state = @running,
                lease_owner = @worker_id,
                lease_expires_at = @lease_expires_at,
                attempt_count = job.attempt_count + 1,
                progress_stage = 'starting',
                updated_at = @now,
                version = job.version + 1
            FROM candidate
            WHERE job.id = candidate.id
            RETURNING job.id,
                      job.workspace_id,
                      job.project_id,
                      job.asset_id,
                      job.type,
                      job.payload_json,
                      job.attempt_count,
                      job.max_attempts,
                      job.lease_owner,
                      job.lease_expires_at
            """;
        AddParameter(command, "queued", (int)JobState.Queued);
        AddParameter(command, "running", (int)JobState.Running);
        AddParameter(command, "now", now);
        AddParameter(command, "worker_id", workerId);
        AddParameter(command, "lease_expires_at", leaseExpiresAt);

        LeasedJob? leasedJob = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                leasedJob = new LeasedJob(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    (JobType)reader.GetInt32(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetString(8),
                    reader.GetFieldValue<DateTimeOffset>(9));
            }
        }

        if (leasedJob is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        dbContext.JobAttempts.Add(new JobAttempt
        {
            JobId = leasedJob.Id,
            Number = leasedJob.AttemptNumber,
            WorkerId = workerId,
            State = JobState.Running,
            StartedAt = now
        });
        dbContext.JobEvents.Add(NewEvent(
            leasedJob.Id,
            "running",
            new { leasedJob.AttemptNumber, workerId, leaseExpiresAt }));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return leasedJob;
    }

    public async Task<bool> HeartbeatAsync(
        Guid jobId,
        string workerId,
        TimeSpan leaseDuration,
        int progressPercent,
        string stage,
        CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.SingleOrDefaultAsync(
            value => value.Id == jobId &&
                     value.State == JobState.Running &&
                     value.LeaseOwner == workerId,
            cancellationToken);

        if (job is null)
        {
            return false;
        }

        job.LeaseExpiresAt = DateTimeOffset.UtcNow.Add(leaseDuration);
        if (progressPercent >= 0)
        {
            job.ProgressPercent = Math.Max(job.ProgressPercent, Math.Clamp(progressPercent, 0, 100));
        }

        if (!string.IsNullOrWhiteSpace(stage))
        {
            job.ProgressStage = stage;
            dbContext.JobEvents.Add(NewEvent(job.Id, "progress", new { job.ProgressPercent, stage }));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task CompleteAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.SingleOrDefaultAsync(
            value => value.Id == jobId &&
                     value.State == JobState.Running &&
                     value.LeaseOwner == workerId,
            cancellationToken);

        if (job is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        job.State = JobState.Succeeded;
        job.ProgressPercent = 100;
        job.ProgressStage = "completed";
        job.CompletedAt = now;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;

        var attempt = await dbContext.JobAttempts
            .SingleAsync(
                value => value.JobId == job.Id && value.Number == job.AttemptCount,
                cancellationToken);
        attempt.State = JobState.Succeeded;
        attempt.CompletedAt = now;
        dbContext.JobEvents.Add(NewEvent(job.Id, "succeeded", new { completedAt = now }));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task FailAsync(
        LeasedJob leasedJob,
        string errorCode,
        string safeMessage,
        bool retryable,
        CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.SingleOrDefaultAsync(
            value => value.Id == leasedJob.Id &&
                     value.State == JobState.Running &&
                     value.LeaseOwner == leasedJob.LeaseOwner,
            cancellationToken);

        if (job is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var retry = retryable && job.AttemptCount < job.MaxAttempts;
        job.State = retry ? JobState.Queued : JobState.Failed;
        job.AvailableAt = retry
            ? now.Add(JobRetrySchedule.ForAttempt(job.AttemptCount))
            : job.AvailableAt;
        job.ErrorCode = errorCode;
        job.ErrorMessage = safeMessage;
        job.ProgressStage = retry ? "retry_scheduled" : "failed";
        job.CompletedAt = retry ? null : now;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;

        var attempt = await dbContext.JobAttempts.SingleAsync(
            value => value.JobId == job.Id && value.Number == job.AttemptCount,
            cancellationToken);
        attempt.State = JobState.Failed;
        attempt.CompletedAt = now;
        attempt.ErrorCode = errorCode;
        attempt.ErrorMessage = safeMessage;

        dbContext.JobEvents.Add(NewEvent(
            job.Id,
            retry ? "retry_scheduled" : "failed",
            new
            {
                errorCode,
                message = safeMessage,
                retryAt = retry ? (DateTimeOffset?)job.AvailableAt : null
            }));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AppendEventAsync(
        Guid jobId,
        string eventType,
        object data,
        CancellationToken cancellationToken)
    {
        dbContext.JobEvents.Add(NewEvent(jobId, eventType, data));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static JobEvent NewEvent(Guid jobId, string eventType, object data) =>
        new()
        {
            JobId = jobId,
            EventType = eventType,
            DataJson = JsonSerializer.Serialize(data)
        };

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, value));
}
