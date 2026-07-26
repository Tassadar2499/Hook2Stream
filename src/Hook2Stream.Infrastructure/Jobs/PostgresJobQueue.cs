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
    private const int MaxConcurrencyAttempts = 3;

    public async Task<Guid> EnqueueAsync(
        JobEnqueueRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequiredCapability);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HandlerVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PayloadJson);

        var requiredCapability = NormalizeCapability(request.RequiredCapability);
        JobRoutingRegistry.EnsureMatches(request.Type, requiredCapability);
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
            AvailableAt = request.AvailableAt ?? DateTimeOffset.UtcNow
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
        await SyncPipelineStageAsync(
            job,
            PipelineStageState.Queued,
            progressPercent: 0,
            blockerCode: null,
            errorCode: null,
            forceCurrentJob: true,
            cancellationToken);

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

        var expiredLeases = await CloseExpiredLeasesAsync(
            transaction,
            now,
            capabilities,
            cancellationToken);
        foreach (var expired in expiredLeases)
        {
            dbContext.JobEvents.Add(NewEvent(
                expired.JobId,
                expired.Exhausted ? "failed" : "lease_expired",
                new
                {
                    errorCode = "job.lease_expired",
                    exhausted = expired.Exhausted,
                    expiredAt = now
            }));
            if (expired.ProjectId is { } projectId &&
                expired.PipelineRunId is not null &&
                !string.IsNullOrWhiteSpace(expired.PipelineStage))
            {
                dbContext.OutboxMessages.Add(new OutboxMessage
                {
                    WorkspaceId = expired.WorkspaceId,
                    AggregateId = projectId,
                    Destination = "pipeline",
                    MessageType = "pipeline.reconcile",
                    DedupeKey = $"pipeline.reconcile:{projectId:N}:job.lease_expired:{expired.JobId:N}:{expired.AttemptNumber}",
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        projectId,
                        jobId = expired.JobId,
                        attemptNumber = expired.AttemptNumber,
                        reason = "job.lease_expired"
                    })
                });
            }
        }

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
                    OR (state = @running AND lease_expires_at <= @now AND NOT EXISTS (
                        SELECT 1
                        FROM job_attempts AS active_attempt
                        WHERE active_attempt.job_id = jobs.id
                          AND active_attempt.number = jobs.attempt_count
                          AND active_attempt.deleted_at IS NULL
                          AND active_attempt.state = @running
                    ))
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
                error_code = NULL,
                error_message = NULL,
                completed_at = NULL,
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

        var stagesToSynchronize = expiredLeases
            .Where(value => leasedJob is null || value.JobId != leasedJob.Id)
            .ToArray();
        if (stagesToSynchronize.Length > 0)
        {
            var expiredIds = stagesToSynchronize.Select(value => value.JobId).ToArray();
            var expiredJobs = await dbContext.Jobs
                .Where(value => expiredIds.Contains(value.Id))
                .ToDictionaryAsync(value => value.Id, cancellationToken);
            foreach (var expired in stagesToSynchronize)
            {
                if (!expiredJobs.TryGetValue(expired.JobId, out var expiredJob)) continue;
                await SyncPipelineStageAsync(
                    expiredJob,
                    expired.Exhausted ? PipelineStageState.Failed : PipelineStageState.Retrying,
                    expiredJob.ProgressPercent,
                    blockerCode: null,
                    errorCode: "job.lease_expired",
                    forceCurrentJob: false,
                    cancellationToken);
            }
        }

        if (leasedJob is null)
        {
            if (expiredLeases.Count > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }
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
        var trackedJob = await dbContext.Jobs.SingleAsync(value => value.Id == leasedJob.Id, cancellationToken);
        await SyncPipelineStageAsync(
            trackedJob,
            PipelineStageState.Running,
            trackedJob.ProgressPercent,
            blockerCode: null,
            errorCode: null,
            forceCurrentJob: false,
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return leasedJob;
    }

    private async Task<List<ExpiredLease>> CloseExpiredLeasesAsync(
        IDbContextTransaction transaction,
        DateTimeOffset now,
        IReadOnlyCollection<string> capabilities,
        CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            """
            WITH expired AS MATERIALIZED (
                SELECT job.id,
                       job.workspace_id,
                       job.project_id,
                       job.pipeline_run_id,
                       job.pipeline_stage,
                       job.attempt_count,
                       ((SELECT COUNT(*)
                         FROM job_attempts AS failed_attempt
                         WHERE failed_attempt.job_id = job.id
                           AND failed_attempt.deleted_at IS NULL
                           AND failed_attempt.state = @failed) + 1 >= job.max_attempts) AS exhausted
                FROM jobs AS job
                WHERE job.deleted_at IS NULL
                  AND job.required_capability = ANY(@capabilities)
                  AND job.state = @running
                  AND job.lease_expires_at IS NOT NULL
                  AND job.lease_expires_at <= @now
                  AND EXISTS (
                      SELECT 1
                      FROM job_attempts AS current_attempt
                      WHERE current_attempt.job_id = job.id
                        AND current_attempt.number = job.attempt_count
                        AND current_attempt.deleted_at IS NULL
                        AND current_attempt.state = @running
                  )
                ORDER BY job.lease_expires_at, job.created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 100
            ),
            closed_attempts AS (
                UPDATE job_attempts AS attempt
                SET state = @failed,
                    completed_at = @now,
                    error_code = 'job.lease_expired',
                    error_message = 'The worker lease expired before completion.',
                    updated_at = @now,
                    version = attempt.version + 1
                FROM expired
                WHERE attempt.job_id = expired.id
                  AND attempt.number = expired.attempt_count
                  AND attempt.deleted_at IS NULL
                  AND attempt.state = @running
                RETURNING attempt.job_id
            ),
            updated_jobs AS (
                UPDATE jobs AS job
                SET state = CASE WHEN expired.exhausted THEN @failed ELSE @queued END,
                    available_at = CASE WHEN expired.exhausted THEN job.available_at ELSE @now END,
                    completed_at = CASE WHEN expired.exhausted THEN @now ELSE NULL END,
                    progress_stage = CASE WHEN expired.exhausted THEN 'failed' ELSE 'retry_scheduled' END,
                    error_code = 'job.lease_expired',
                    error_message = CASE
                        WHEN expired.exhausted
                            THEN 'The worker lease expired and exhausted its retry budget.'
                        ELSE 'The worker lease expired before completion; a retry was scheduled.'
                    END,
                    lease_owner = NULL,
                    lease_token = NULL,
                    lease_expires_at = NULL,
                    updated_at = @now,
                    version = job.version + 1
                FROM expired
                JOIN closed_attempts ON closed_attempts.job_id = expired.id
                WHERE job.id = expired.id
                RETURNING job.id
            )
            SELECT expired.id,
                   expired.exhausted,
                   expired.workspace_id,
                   expired.project_id,
                   expired.pipeline_run_id,
                   expired.pipeline_stage,
                   expired.attempt_count
            FROM expired
            JOIN updated_jobs ON updated_jobs.id = expired.id
            """;
        AddParameter(command, "queued", (int)JobState.Queued);
        AddParameter(command, "running", (int)JobState.Running);
        AddParameter(command, "failed", (int)JobState.Failed);
        AddParameter(command, "now", now);
        AddParameter(command, "capabilities", capabilities.ToArray());

        var expiredLeases = new List<ExpiredLease>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            expiredLeases.Add(new ExpiredLease(
                reader.GetGuid(0),
                reader.GetBoolean(1),
                reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6)));
        }
        return expiredLeases;
    }

    public Task<bool> HeartbeatAsync(
        Guid jobId,
        string workerId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        int progressPercent,
        string stage,
        CancellationToken cancellationToken)
        => ExecuteWithConcurrencyRetryAsync(
            token => HeartbeatOnceAsync(
                jobId,
                workerId,
                leaseToken,
                leaseDuration,
                progressPercent,
                stage,
                token),
            cancellationToken);

    private async Task<bool> HeartbeatOnceAsync(
        Guid jobId,
        string workerId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        int progressPercent,
        string stage,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var job = await dbContext.Jobs.SingleOrDefaultAsync(
            value => value.Id == jobId &&
                     value.State == JobState.Running &&
                     value.LeaseOwner == workerId &&
                     value.LeaseToken == leaseToken &&
                     value.LeaseExpiresAt > now,
            cancellationToken);

        if (job is null)
        {
            return false;
        }

        job.LeaseExpiresAt = now.Add(leaseDuration);
        if (progressPercent >= 0)
        {
            job.ProgressPercent = Math.Max(job.ProgressPercent, Math.Clamp(progressPercent, 0, 100));
        }

        if (!string.IsNullOrWhiteSpace(stage))
        {
            job.ProgressStage = stage;
            dbContext.JobEvents.Add(NewEvent(job.Id, "progress", new { job.ProgressPercent, stage }));
        }

        await SyncPipelineStageAsync(
            job,
            PipelineStageState.Running,
            job.ProgressPercent,
            blockerCode: null,
            errorCode: null,
            forceCurrentJob: false,
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task CompleteAsync(
        Guid jobId,
        string workerId,
        Guid leaseToken,
        CancellationToken cancellationToken)
        => ExecuteWithConcurrencyRetryAsync(
            token => CompleteOnceAsync(jobId, workerId, leaseToken, token),
            cancellationToken);

    private async Task CompleteOnceAsync(
        Guid jobId,
        string workerId,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var job = await dbContext.Jobs.SingleOrDefaultAsync(
            value => value.Id == jobId &&
                     value.State == JobState.Running &&
                     value.LeaseOwner == workerId &&
                     value.LeaseToken == leaseToken &&
                     value.LeaseExpiresAt > now,
            cancellationToken);

        if (job is null)
        {
            return;
        }

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
        AddFinalRenderReconcile(job, "job.completed");
        await SyncPipelineStageAsync(
            job,
            PipelineStageState.Succeeded,
            progressPercent: 100,
            blockerCode: null,
            errorCode: null,
            forceCurrentJob: false,
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task FailAsync(
        LeasedJob leasedJob,
        string errorCode,
        string safeMessage,
        bool retryable,
        CancellationToken cancellationToken)
        => ExecuteWithConcurrencyRetryAsync(
            token => FailOnceAsync(
                leasedJob,
                errorCode,
                safeMessage,
                retryable,
                token),
            cancellationToken);

    private async Task FailOnceAsync(
        LeasedJob leasedJob,
        string errorCode,
        string safeMessage,
        bool retryable,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var job = await dbContext.Jobs.SingleOrDefaultAsync(
            value => value.Id == leasedJob.Id &&
                     value.State == JobState.Running &&
                     value.LeaseOwner == leasedJob.LeaseOwner &&
                     value.LeaseToken == leasedJob.LeaseToken &&
                     value.LeaseExpiresAt > now,
            cancellationToken);

        if (job is null)
        {
            return;
        }

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
        AddFinalRenderReconcile(job, retry ? "job.retry_scheduled" : "job.failed");
        await SyncPipelineStageAsync(
            job,
            retry ? PipelineStageState.Retrying : PipelineStageState.Failed,
            job.ProgressPercent,
            blockerCode: null,
            errorCode,
            forceCurrentJob: false,
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<TResult> ExecuteWithConcurrencyRetryAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxConcurrencyAttempts; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyAttempts)
            {
                // SaveChanges is transactional, so clearing removes every
                // stale aggregate member plus any uncommitted event/outbox
                // entities before the next attempt re-reads job/stage/run.
                dbContext.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("The concurrency retry loop completed unexpectedly.");
    }

    private async Task ExecuteWithConcurrencyRetryAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await ExecuteWithConcurrencyRetryAsync(
            async token =>
            {
                await operation(token);
                return true;
            },
            cancellationToken);
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

        await ExecuteWithConcurrencyRetryAsync(
            token => DeferOnceAsync(leasedJob, delay, reasonCode, token),
            cancellationToken);
    }

    private async Task DeferOnceAsync(
        LeasedJob leasedJob,
        TimeSpan delay,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var job = await dbContext.Jobs.SingleOrDefaultAsync(
            value => value.Id == leasedJob.Id &&
                     value.State == JobState.Running &&
                     value.LeaseOwner == leasedJob.LeaseOwner &&
                     value.LeaseToken == leasedJob.LeaseToken &&
                     value.LeaseExpiresAt > now,
            cancellationToken);
        if (job is null) return;

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
        AddFinalRenderReconcile(job, "job.deferred");
        await SyncPipelineStageAsync(
            job,
            PipelineStageState.Queued,
            job.ProgressPercent,
            blockerCode: null,
            errorCode: null,
            forceCurrentJob: false,
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task BlockAsync(
        LeasedJob leasedJob,
        string reasonCode,
        string safeMessage,
        CancellationToken cancellationToken)
        => ExecuteWithConcurrencyRetryAsync(
            token => BlockOnceAsync(leasedJob, reasonCode, safeMessage, token),
            cancellationToken);

    private async Task BlockOnceAsync(
        LeasedJob leasedJob,
        string reasonCode,
        string safeMessage,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var job = await dbContext.Jobs.SingleOrDefaultAsync(
            value => value.Id == leasedJob.Id &&
                     value.State == JobState.Running &&
                     value.LeaseOwner == leasedJob.LeaseOwner &&
                     value.LeaseToken == leasedJob.LeaseToken &&
                     value.LeaseExpiresAt > now,
            cancellationToken);
        if (job is null) return;

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
        AddFinalRenderReconcile(job, "job.blocked");
        await SyncPipelineStageAsync(
            job,
            PipelineStageState.WaitingUser,
            job.ProgressPercent,
            blockerCode: reasonCode,
            errorCode: null,
            forceCurrentJob: false,
            cancellationToken);
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

    private async Task SyncPipelineStageAsync(
        Job job,
        PipelineStageState state,
        int progressPercent,
        string? blockerCode,
        string? errorCode,
        bool forceCurrentJob,
        CancellationToken cancellationToken)
    {
        if (job.PipelineRunId is not { } pipelineRunId ||
            string.IsNullOrWhiteSpace(job.PipelineStage) ||
            !Enum.TryParse<WorkflowLane>(job.PipelineStage, ignoreCase: true, out var lane))
        {
            return;
        }

        // Final render is a fan-out/fan-in lane. No individual child or export
        // queue transition can authoritatively make the whole lane terminal;
        // PipelineReconciler aggregates the linked RenderBatch instead.
        if (lane == WorkflowLane.FinalRender)
        {
            return;
        }

        var stage = dbContext.ChangeTracker.Entries<PipelineStage>()
            .Select(value => value.Entity)
            .SingleOrDefault(value => value.PipelineRunId == pipelineRunId && value.Lane == lane)
            ?? await dbContext.PipelineStages
                .Include(value => value.PipelineRun)
                .ThenInclude(value => value.Stages)
                .SingleOrDefaultAsync(
                    value => value.PipelineRunId == pipelineRunId && value.Lane == lane,
                    cancellationToken);
        if (stage is null || !forceCurrentJob && stage.CurrentJobId != job.Id)
        {
            return;
        }

        var progress = Math.Clamp(progressPercent, 0, 100);
        if (stage.State == state &&
            stage.ProgressPercent == progress &&
            string.Equals(stage.BlockerCode, blockerCode, StringComparison.Ordinal) &&
            string.Equals(stage.ErrorCode, errorCode, StringComparison.Ordinal) &&
            stage.CurrentJobId == job.Id)
        {
            return;
        }

        stage.State = state;
        stage.ProgressPercent = progress;
        stage.BlockerCode = blockerCode;
        stage.ErrorCode = errorCode;
        stage.CurrentJobId = job.Id;
        var run = stage.PipelineRun;
        var states = run.Stages.Select(value => value.State).ToArray();
        run.State = states.Any(value => value == PipelineStageState.Failed)
            ? PipelineStageState.Failed
            : states.Any(value => value == PipelineStageState.Degraded)
                ? PipelineStageState.Degraded
                : states.Any(value => value is PipelineStageState.Running or PipelineStageState.Queued or PipelineStageState.Retrying)
                    ? PipelineStageState.Running
                    : states.All(value => value == PipelineStageState.Succeeded)
                        ? PipelineStageState.Succeeded
                        : PipelineStageState.WaitingUser;
        run.CompletedAt = run.State == PipelineStageState.Succeeded
            ? run.CompletedAt ?? DateTimeOffset.UtcNow
            : null;
        run.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void AddFinalRenderReconcile(Job job, string reason)
    {
        if (job.ProjectId is not { } projectId ||
            job.PipelineRunId is null ||
            !string.Equals(job.PipelineStage, nameof(WorkflowLane.FinalRender), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            WorkspaceId = job.WorkspaceId,
            AggregateId = projectId,
            Destination = "pipeline",
            MessageType = "pipeline.reconcile",
            DedupeKey = $"pipeline.reconcile:{projectId:N}:final-render:{job.Id:N}:{job.AttemptCount}:{reason}:{Guid.CreateVersion7():N}",
            PayloadJson = JsonSerializer.Serialize(new { projectId, jobId = job.Id, reason })
        });
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, value));

    internal static string NormalizeCapability(string capability)
        => JobRoutingRegistry.NormalizeCapability(capability);

    private sealed record ExpiredLease(
        Guid JobId,
        bool Exhausted,
        Guid WorkspaceId,
        Guid? ProjectId,
        Guid? PipelineRunId,
        string? PipelineStage,
        int AttemptNumber);

    private static bool IsIdempotencyConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: { } constraintName
        } && constraintName.Contains("idempotency", StringComparison.OrdinalIgnoreCase);
}
