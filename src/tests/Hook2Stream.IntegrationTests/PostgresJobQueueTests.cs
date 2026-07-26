using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Jobs;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Hook2Stream.IntegrationTests;

public sealed class PostgresJobQueueTests
{
    [Theory]
    [InlineData("heartbeat", JobState.Running, PipelineStageState.Running, PipelineStageState.Running, "progress")]
    [InlineData("complete", JobState.Succeeded, PipelineStageState.Succeeded, PipelineStageState.Succeeded, "succeeded")]
    [InlineData("fail", JobState.Failed, PipelineStageState.Failed, PipelineStageState.Failed, "failed")]
    [InlineData("defer", JobState.Queued, PipelineStageState.Queued, PipelineStageState.Running, "deferred")]
    [InlineData("block", JobState.Cancelled, PipelineStageState.WaitingUser, PipelineStageState.WaitingUser, "waiting_user")]
    public async Task Lease_transition_rebuilds_aggregate_after_concurrency_conflict(
        string transition,
        JobState expectedJobState,
        PipelineStageState expectedStageState,
        PipelineStageState expectedRunState,
        string expectedEventType)
    {
        var interceptor = new InjectedConcurrencyInterceptor();
        await using var dbContext = CreateDbContext(interceptor);
        var (job, leasedJob) = await SeedPipelineJobAsync(dbContext);
        interceptor.Arm(failures: 1);
        var queue = new PostgresJobQueue(dbContext);

        switch (transition)
        {
            case "heartbeat":
                Assert.True(await queue.HeartbeatAsync(
                    job.Id,
                    leasedJob.LeaseOwner,
                    leasedJob.LeaseToken,
                    TimeSpan.FromMinutes(2),
                    50,
                    "working",
                    CancellationToken.None));
                break;
            case "complete":
                await queue.CompleteAsync(
                    job.Id,
                    leasedJob.LeaseOwner,
                    leasedJob.LeaseToken,
                    CancellationToken.None);
                break;
            case "fail":
                await queue.FailAsync(
                    leasedJob,
                    "provider.failure",
                    "The provider failed.",
                    retryable: false,
                    CancellationToken.None);
                break;
            case "defer":
                await queue.DeferAsync(
                    leasedJob,
                    TimeSpan.FromSeconds(5),
                    "dependency.wait",
                    CancellationToken.None);
                break;
            case "block":
                await queue.BlockAsync(
                    leasedJob,
                    "consent.required",
                    "Consent is required.",
                    CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition));
        }

        dbContext.ChangeTracker.Clear();
        var persistedJob = await dbContext.Jobs.SingleAsync(value => value.Id == job.Id);
        var persistedStage = await dbContext.PipelineStages.SingleAsync();
        var persistedRun = await dbContext.PipelineRuns.SingleAsync();
        var events = await dbContext.JobEvents.ToListAsync();

        Assert.Equal(2, interceptor.SaveAttempts);
        Assert.Equal(expectedJobState, persistedJob.State);
        Assert.Equal(expectedStageState, persistedStage.State);
        Assert.Equal(expectedRunState, persistedRun.State);
        Assert.Single(events);
        Assert.Equal(expectedEventType, events[0].EventType);
    }

    [Fact]
    public async Task Lease_transition_has_bounded_concurrency_retry_budget()
    {
        var interceptor = new InjectedConcurrencyInterceptor();
        await using var dbContext = CreateDbContext(interceptor);
        var (job, leasedJob) = await SeedPipelineJobAsync(dbContext);
        interceptor.Arm(failures: int.MaxValue);
        var queue = new PostgresJobQueue(dbContext);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            queue.CompleteAsync(
                job.Id,
                leasedJob.LeaseOwner,
                leasedJob.LeaseToken,
                CancellationToken.None));

        Assert.Equal(3, interceptor.SaveAttempts);
    }

    [Fact]
    public async Task Enqueue_preserves_pipeline_metadata_and_is_idempotent()
    {
        await using var dbContext = CreateDbContext();
        var queue = new PostgresJobQueue(dbContext);
        var pipelineRunId = Guid.NewGuid();
        var request = new JobEnqueueRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            JobType.Transcription,
            "{\"revisionId\":\"00000000-0000-0000-0000-000000000001\"}",
            "transcription:project:1",
            RequiredCapability: " Control ",
            HandlerVersion: "openrouter-stt-v1",
            InputFingerprint: "sha256:audio",
            PayloadSchemaVersion: 2,
            PipelineRunId: pipelineRunId,
            PipelineStage: "transcript");

        var first = await queue.EnqueueAsync(request, CancellationToken.None);
        var second = await queue.EnqueueAsync(request, CancellationToken.None);

        Assert.Equal(first, second);
        var job = await dbContext.Jobs.SingleAsync();
        Assert.Equal(JobRoutingRegistry.Control, job.RequiredCapability);
        Assert.Equal("openrouter-stt-v1", job.HandlerVersion);
        Assert.Equal("sha256:audio", job.InputFingerprint);
        Assert.Equal(2, job.PayloadSchemaVersion);
        Assert.Equal(pipelineRunId, job.PipelineRunId);
        Assert.Equal("transcript", job.PipelineStage);
        Assert.Single(await dbContext.JobEvents.ToListAsync());
    }

    [Fact]
    public async Task Lease_token_fences_heartbeat_and_completion()
    {
        await using var dbContext = CreateDbContext();
        var token = Guid.NewGuid();
        var job = new Job
        {
            WorkspaceId = Guid.NewGuid(),
            Type = JobType.MediaIngest,
            PayloadJson = "{}",
            RequiredCapability = "media",
            State = JobState.Running,
            AttemptCount = 1,
            LeaseOwner = "worker-a",
            LeaseToken = token,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        dbContext.Jobs.Add(job);
        dbContext.JobAttempts.Add(new JobAttempt
        {
            JobId = job.Id,
            Number = 1,
            WorkerId = "worker-a",
            State = JobState.Running,
            StartedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();
        var queue = new PostgresJobQueue(dbContext);

        var staleHeartbeat = await queue.HeartbeatAsync(
            job.Id,
            "worker-a",
            Guid.NewGuid(),
            TimeSpan.FromMinutes(2),
            50,
            "working",
            CancellationToken.None);
        await queue.CompleteAsync(job.Id, "worker-a", Guid.NewGuid(), CancellationToken.None);

        Assert.False(staleHeartbeat);
        Assert.Equal(JobState.Running, job.State);

        var validHeartbeat = await queue.HeartbeatAsync(
            job.Id,
            "worker-a",
            token,
            TimeSpan.FromMinutes(2),
            50,
            "working",
            CancellationToken.None);
        await queue.CompleteAsync(job.Id, "worker-a", token, CancellationToken.None);

        Assert.True(validHeartbeat);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Null(job.LeaseToken);
        Assert.Equal(100, job.ProgressPercent);
    }

    [Fact]
    public async Task Expired_lease_fences_queue_and_handler_commits()
    {
        await using var dbContext = CreateDbContext();
        var token = Guid.NewGuid();
        var job = new Job
        {
            WorkspaceId = Guid.NewGuid(),
            Type = JobType.MediaIngest,
            PayloadJson = "{}",
            RequiredCapability = JobRoutingRegistry.Media,
            State = JobState.Running,
            AttemptCount = 1,
            LeaseOwner = "worker-a",
            LeaseToken = token,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        };
        dbContext.Jobs.Add(job);
        dbContext.JobAttempts.Add(new JobAttempt
        {
            JobId = job.Id,
            Number = 1,
            WorkerId = "worker-a",
            State = JobState.Running,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await dbContext.SaveChangesAsync();
        var queue = new PostgresJobQueue(dbContext);
        var leased = new LeasedJob(
            job.Id,
            job.WorkspaceId,
            null,
            null,
            job.Type,
            job.PayloadJson,
            1,
            job.MaxAttempts,
            job.RequiredCapability,
            job.HandlerVersion,
            null,
            job.PayloadSchemaVersion,
            "worker-a",
            job.LeaseExpiresAt.Value,
            token);

        var heartbeat = await queue.HeartbeatAsync(
            job.Id,
            "worker-a",
            token,
            TimeSpan.FromMinutes(1),
            50,
            "working",
            CancellationToken.None);
        await queue.CompleteAsync(job.Id, "worker-a", token, CancellationToken.None);

        Assert.False(heartbeat);
        Assert.Equal(JobState.Running, job.State);
        await Assert.ThrowsAsync<JobLeaseFenceException>(() =>
            JobLeaseFence.CommitAsync(dbContext, leased, CancellationToken.None));
    }

    [Fact]
    public async Task Block_stops_polling_and_records_waiting_user_attempt()
    {
        await using var dbContext = CreateDbContext();
        var leaseToken = Guid.NewGuid();
        var job = new Job
        {
            WorkspaceId = Guid.NewGuid(),
            Type = JobType.FinalRender,
            PayloadJson = "{}",
            RequiredCapability = "render",
            State = JobState.Running,
            AttemptCount = 1,
            LeaseOwner = "worker-a",
            LeaseToken = leaseToken,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        dbContext.Jobs.Add(job);
        dbContext.JobAttempts.Add(new JobAttempt
        {
            JobId = job.Id,
            Number = 1,
            WorkerId = "worker-a",
            State = JobState.Running,
            StartedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();
        var queue = new PostgresJobQueue(dbContext);
        var leased = new LeasedJob(
            job.Id,
            job.WorkspaceId,
            null,
            null,
            job.Type,
            job.PayloadJson,
            1,
            job.MaxAttempts,
            job.RequiredCapability,
            job.HandlerVersion,
            null,
            1,
            "worker-a",
            job.LeaseExpiresAt!.Value,
            leaseToken);

        await queue.BlockAsync(
            leased,
            "rights.required",
            "Confirm rights before rendering.",
            CancellationToken.None);

        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Equal("waiting_user", job.ProgressStage);
        Assert.Equal("rights.required", job.ErrorCode);
        Assert.Null(job.LeaseToken);
        var attempt = await dbContext.JobAttempts.SingleAsync();
        Assert.Equal(JobState.Cancelled, attempt.State);
        Assert.NotNull(attempt.CompletedAt);
        Assert.Equal("rights.required", attempt.ErrorCode);
        Assert.Contains(await dbContext.JobEvents.ToListAsync(), value => value.EventType == "waiting_user");
    }

    [Theory]
    [InlineData("bad capability")]
    [InlineData("gpu/transcription")]
    [InlineData("")]
    public async Task Enqueue_rejects_unsafe_capabilities(string capability)
    {
        await using var dbContext = CreateDbContext();
        var queue = new PostgresJobQueue(dbContext);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => queue.EnqueueAsync(
            new JobEnqueueRequest(
                Guid.NewGuid(),
                null,
                null,
                JobType.AudioAnalysis,
                "{}",
                null,
                RequiredCapability: capability),
            CancellationToken.None));
    }

    [Fact]
    public async Task Enqueue_rejects_a_safe_but_incorrect_job_route()
    {
        await using var dbContext = CreateDbContext();
        var queue = new PostgresJobQueue(dbContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(
            new JobEnqueueRequest(
                Guid.NewGuid(),
                null,
                null,
                JobType.Transcription,
                "{}",
                null,
                RequiredCapability: JobRoutingRegistry.Analysis),
            CancellationToken.None));
    }

    private static async Task<(Job Job, LeasedJob LeasedJob)> SeedPipelineJobAsync(
        Hook2StreamDbContext dbContext)
    {
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var job = new Job
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Type = JobType.Transcription,
            PayloadJson = "{}",
            RequiredCapability = JobRoutingRegistry.Control,
            State = JobState.Running,
            AttemptCount = 1,
            LeaseOwner = "worker-a",
            LeaseToken = leaseToken,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            PipelineStage = WorkflowLane.Transcript.ToString(),
            ProgressStage = "processing"
        };
        var run = new PipelineRun
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Number = 1,
            State = PipelineStageState.Running,
            Trigger = "test"
        };
        var stage = new PipelineStage
        {
            PipelineRun = run,
            PipelineRunId = run.Id,
            Lane = WorkflowLane.Transcript,
            State = PipelineStageState.Running,
            CurrentJobId = job.Id
        };
        run.Stages.Add(stage);
        job.PipelineRunId = run.Id;
        dbContext.Jobs.Add(job);
        dbContext.JobAttempts.Add(new JobAttempt
        {
            JobId = job.Id,
            Number = 1,
            WorkerId = "worker-a",
            State = JobState.Running,
            StartedAt = DateTimeOffset.UtcNow
        });
        dbContext.PipelineRuns.Add(run);
        await dbContext.SaveChangesAsync();

        return (
            job,
            new LeasedJob(
                job.Id,
                workspaceId,
                projectId,
                null,
                job.Type,
                job.PayloadJson,
                1,
                job.MaxAttempts,
                job.RequiredCapability,
                job.HandlerVersion,
                null,
                job.PayloadSchemaVersion,
                "worker-a",
                job.LeaseExpiresAt!.Value,
                leaseToken));
    }

    private static Hook2StreamDbContext CreateDbContext(
        SaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"job-queue-tests-{Guid.NewGuid():N}");
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new Hook2StreamDbContext(options.Options);
    }

    private sealed class InjectedConcurrencyInterceptor : SaveChangesInterceptor
    {
        private int _remainingFailures;
        private bool _armed;

        public int SaveAttempts { get; private set; }

        public void Arm(int failures)
        {
            _remainingFailures = failures;
            SaveAttempts = 0;
            _armed = true;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_armed)
            {
                return new ValueTask<InterceptionResult<int>>(result);
            }

            SaveAttempts++;
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                throw new DbUpdateConcurrencyException("Injected optimistic concurrency conflict.");
            }

            return new ValueTask<InterceptionResult<int>>(result);
        }
    }
}
