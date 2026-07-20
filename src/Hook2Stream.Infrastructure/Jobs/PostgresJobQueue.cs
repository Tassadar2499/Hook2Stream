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
        JobEnqueueRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequiredCapability);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HandlerVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PayloadJson);

        var requiredCapability = NormalizeCapability(request.RequiredCapability);
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? null
            : request.IdempotencyKey.Trim();

        if (idempotencyKey is not null)
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
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            AssetId = request.AssetId,
            Type = request.Type,
            PipelineRunId = request.PipelineRunId,
            PipelineStage = request.PipelineStage,
            RequiredCapability = requiredCapability,
            HandlerVersion = request.HandlerVersion.Trim(),
            InputFingerprint = string.IsNullOrWhiteSpace(request.InputFingerprint)
                ? null
                : request.InputFingerprint.Trim(),
            PayloadSchemaVersion = request.PayloadSchemaVersion,
            PayloadJson = request.PayloadJson,
            IdempotencyKey = idempotencyKey,
            State = JobState.Queued,
            AvailableAt = DateTimeOffset.UtcNow
        };

        dbContext.Jobs.Add(job);
        dbContext.JobEvents.Add(NewEvent(
            job.Id,
            "queued",
            new
            {
                job.Type,
                job.RequiredCapability,
                job.HandlerVersion,
                job.PayloadSchemaVersion,
                job.PipelineRunId,
                job.PipelineStage,
                job.AvailableAt
            }));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            idempotencyKey is not null && IsIdempotencyConflict(exception))
        {
            // A concurrent request committed the same logical command after the
            // initial lookup. Reset the failed unit of work and return its job.
            dbContext.ChangeTracker.Clear();
            return await dbContext.Jobs
                .Where(value => value.IdempotencyKey == idempotencyKey)
                .Select(value => value.Id)
                .SingleAsync(cancellationToken);
        }

        return job.Id;
    }

    public async Task<LeasedJob?> TryLeaseAsync(
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyCollection<string> capabilities,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(capabilities);

        var normalizedCapabilities = capabilities
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeCapability)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedCapabilities.Length == 0)
        {
            throw new ArgumentException("At least one worker capability is required.", nameof(capabilities));
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            token => TryLeaseOnceAsync(workerId, leaseDuration, normalizedCapabilities, token),
            cancellationToken);
    }

    private async Task<LeasedJob?> TryLeaseOnceAsync(
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyCollection<string> capabilities,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseExpiresAt = now.Add(leaseDuration);
        var leaseToken = Guid.NewGuid();
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
                  AND required_capability = ANY(@capabilities)
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
                lease_token = @lease_token,
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
                      job.required_capability,
                      job.handler_version,
                      job.input_fingerprint,
                      job.payload_schema_version,
                      job.lease_owner,
                      job.lease_expires_at,
                      job.lease_token
            """;
        AddParameter(command, "queued", (int)JobState.Queued);
        AddParameter(command, "running", (int)JobState.Running);
        AddParameter(command, "now", now);
        AddParameter(command, "worker_id", workerId);
        AddParameter(command, "lease_expires_at", leaseExpiresAt);
        AddParameter(command, "lease_token", leaseToken);
        AddParameter(command, "capabilities", capabilities.ToArray());

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
                    reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.GetInt32(11),
                    reader.GetString(12),
                    reader.GetFieldValue<DateTimeOffset>(13),
                    reader.GetGuid(14));
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
            new
            {
                leasedJob.AttemptNumber,
                leasedJob.RequiredCapability,
                leasedJob.HandlerVersion,
                workerId,
                leaseExpiresAt
            }));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return leasedJob;
    }

    public async Task<bool> HeartbeatAsync(
        Guid jobId,
        string workerId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        int progressPercent,
        string stage,
        CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.SingleOrDefaultAsync(
            value => value.Id == jobId &&
                     value.State == JobState.Running &&
                     value.LeaseOwner == workerId &&
                     value.LeaseToken == leaseToken,
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

    public async Task CompleteAsync(
        Guid jobId,
        string workerId,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.SingleOrDefaultAsync(
            value => value.Id == jobId &&
                     value.State == JobState.Running &&
                     value.LeaseOwner == workerId &&
                     value.LeaseToken == leaseToken,
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
        job.LeaseToken = null;

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
                     value.LeaseOwner == leasedJob.LeaseOwner &&
                     value.LeaseToken == leasedJob.LeaseToken,
            cancellationToken);

        if (job is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var priorFailedAttempts = await dbContext.JobAttempts.CountAsync(
            value => value.JobId == job.Id && value.State == JobState.Failed,
            cancellationToken);
        var retry = retryable && priorFailedAttempts + 1 < job.MaxAttempts;
        job.State = retry ? JobState.Queued : JobState.Failed;
        job.AvailableAt = retry
            ? now.Add(JobRetrySchedule.ForAttempt(priorFailedAttempts + 1))
            : job.AvailableAt;
        job.ErrorCode = errorCode;
        job.ErrorMessage = safeMessage;
        job.ProgressStage = retry ? "retry_scheduled" : "failed";
        job.CompletedAt = retry ? null : now;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.LeaseToken = null;

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

    public async Task DeferAsync(
        LeasedJob leasedJob,
        TimeSpan delay,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero || delay > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        var job = await dbContext.Jobs.SingleOrDefaultAsync(
            value => value.Id == leasedJob.Id &&
                     value.State == JobState.Running &&
                     value.LeaseOwner == leasedJob.LeaseOwner &&
                     value.LeaseToken == leasedJob.LeaseToken,
            cancellationToken);
        if (job is null) return;

        var now = DateTimeOffset.UtcNow;
        job.State = JobState.Queued;
        job.AvailableAt = now.Add(delay);
        job.ProgressStage = "dependency_wait";
        job.ErrorCode = null;
        job.ErrorMessage = null;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.LeaseToken = null;
        var attempt = await dbContext.JobAttempts.SingleAsync(
            value => value.JobId == job.Id && value.Number == leasedJob.AttemptNumber,
            cancellationToken);
        attempt.State = JobState.Cancelled;
        attempt.CompletedAt = now;
        attempt.ErrorCode = reasonCode;
        dbContext.JobEvents.Add(NewEvent(job.Id, "deferred", new
        {
            reasonCode,
            availableAt = job.AvailableAt
        }));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task BlockAsync(
        LeasedJob leasedJob,
        string reasonCode,
        string safeMessage,
        CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.SingleOrDefaultAsync(
            value => value.Id == leasedJob.Id &&
                     value.State == JobState.Running &&
                     value.LeaseOwner == leasedJob.LeaseOwner &&
                     value.LeaseToken == leasedJob.LeaseToken,
            cancellationToken);
        if (job is null) return;

        var now = DateTimeOffset.UtcNow;
        job.State = JobState.Cancelled;
        job.ProgressStage = "waiting_user";
        job.ErrorCode = reasonCode;
        job.ErrorMessage = safeMessage;
        job.CompletedAt = now;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.LeaseToken = null;
        var attempt = await dbContext.JobAttempts.SingleAsync(
            value => value.JobId == job.Id && value.Number == leasedJob.AttemptNumber,
            cancellationToken);
        attempt.State = JobState.Cancelled;
        attempt.CompletedAt = now;
        attempt.ErrorCode = reasonCode;
        attempt.ErrorMessage = safeMessage;
        dbContext.JobEvents.Add(NewEvent(job.Id, "waiting_user", new
        {
            reasonCode,
            message = safeMessage
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

    internal static string NormalizeCapability(string capability)
    {
        var normalized = capability.Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > 64 ||
            normalized.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not '-' and not '_'))
        {
            throw new ArgumentException(
                "Worker capabilities may contain only ASCII letters, digits, '-' and '_'.",
                nameof(capability));
        }

        return normalized;
    }

    private static bool IsIdempotencyConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: { } constraintName
        } && constraintName.Contains("idempotency", StringComparison.OrdinalIgnoreCase);
}
