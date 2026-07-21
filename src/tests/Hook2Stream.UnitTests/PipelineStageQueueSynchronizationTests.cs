using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Jobs;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.UnitTests;

public sealed class PipelineStageQueueSynchronizationTests
{
    [Fact]
    public async Task Enqueue_heartbeat_and_completion_update_authoritative_stage()
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"pipeline-stage-queue-{Guid.NewGuid():N}")
            .Options;
        await using var db = new Hook2StreamDbContext(options);
        var workspaceId = Guid.CreateVersion7();
        var project = new ReleaseProject
        {
            WorkspaceId = workspaceId,
            ProjectLabel = "Queue stage",
            ArtistName = "Artist",
            TrackTitle = "Track",
            FlowKind = FlowKind.Mp3First
        };
        var run = new PipelineRun
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Project = project,
            Number = 1,
            State = PipelineStageState.WaitingUser
        };
        run.Stages = Enum.GetValues<WorkflowLane>().Select(lane => new PipelineStage
        {
            PipelineRun = run,
            PipelineRunId = run.Id,
            Lane = lane,
            State = PipelineStageState.NotStarted
        }).ToList();
        db.AddRange(project, run);
        await db.SaveChangesAsync();
        var initialRunVersion = run.Version;
        var queue = new PostgresJobQueue(db);

        var jobId = await queue.EnqueueAsync(new JobEnqueueRequest(
            workspaceId,
            project.Id,
            null,
            JobType.AudioAnalysis,
            "{}",
            "queue-stage-sync",
            JobRoutingRegistry.Analysis,
            "test-v1",
            new string('a', 64),
            PipelineRunId: run.Id,
            PipelineStage: "analysis"), CancellationToken.None);

        var analysis = run.Stages.Single(value => value.Lane == WorkflowLane.Analysis);
        Assert.Equal(PipelineStageState.Queued, analysis.State);
        Assert.Equal(jobId, analysis.CurrentJobId);
        Assert.True(run.Version > initialRunVersion);

        var leaseToken = Guid.CreateVersion7();
        var job = await db.Jobs.SingleAsync(value => value.Id == jobId);
        job.State = JobState.Running;
        job.AttemptCount = 1;
        job.LeaseOwner = "worker";
        job.LeaseToken = leaseToken;
        job.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        db.JobAttempts.Add(new JobAttempt
        {
            JobId = job.Id,
            Number = 1,
            WorkerId = "worker",
            State = JobState.Running,
            StartedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var renewed = await queue.HeartbeatAsync(
            job.Id,
            "worker",
            leaseToken,
            TimeSpan.FromMinutes(1),
            25,
            "analysis",
            CancellationToken.None);

        Assert.True(renewed);
        Assert.Equal(PipelineStageState.Running, analysis.State);
        Assert.Equal(25, analysis.ProgressPercent);

        await queue.CompleteAsync(job.Id, "worker", leaseToken, CancellationToken.None);

        Assert.Equal(PipelineStageState.Succeeded, analysis.State);
        Assert.Equal(100, analysis.ProgressPercent);
        Assert.Equal(job.Id, analysis.CurrentJobId);
    }

    [Fact]
    public async Task Completing_one_final_render_child_keeps_the_fan_out_lane_nonterminal()
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"pipeline-final-render-fanout-{Guid.NewGuid():N}")
            .Options;
        await using var db = new Hook2StreamDbContext(options);
        var workspaceId = Guid.CreateVersion7();
        var project = new ReleaseProject
        {
            WorkspaceId = workspaceId,
            ProjectLabel = "Final render fan-out",
            ArtistName = "Artist",
            TrackTitle = "Track",
            FlowKind = FlowKind.Mp3First
        };
        var run = new PipelineRun
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Project = project,
            Number = 1,
            State = PipelineStageState.Running
        };
        run.Stages = Enum.GetValues<WorkflowLane>().Select(lane => new PipelineStage
        {
            PipelineRun = run,
            PipelineRunId = run.Id,
            Lane = lane,
            State = lane == WorkflowLane.FinalRender
                ? PipelineStageState.Running
                : PipelineStageState.Succeeded,
            ProgressPercent = lane == WorkflowLane.FinalRender ? 10 : 100
        }).ToList();
        var leaseToken = Guid.CreateVersion7();
        var firstChild = new Job
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            PipelineRunId = run.Id,
            PipelineStage = "finalRender",
            Type = JobType.FinalRender,
            RequiredCapability = JobRoutingRegistry.Render,
            HandlerVersion = "test-v1",
            PayloadJson = "{}",
            State = JobState.Running,
            AttemptCount = 1,
            LeaseOwner = "render-worker",
            LeaseToken = leaseToken,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        var secondChild = new Job
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            PipelineRunId = run.Id,
            PipelineStage = "finalRender",
            Type = JobType.FinalRender,
            RequiredCapability = JobRoutingRegistry.Render,
            HandlerVersion = "test-v1",
            PayloadJson = "{}",
            State = JobState.Queued
        };
        var finalStage = run.Stages.Single(value => value.Lane == WorkflowLane.FinalRender);
        finalStage.CurrentJobId = firstChild.Id;
        db.AddRange(project, run, firstChild, secondChild);
        db.JobAttempts.Add(new JobAttempt
        {
            JobId = firstChild.Id,
            Number = 1,
            WorkerId = "render-worker",
            State = JobState.Running,
            StartedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var queue = new PostgresJobQueue(db);

        await queue.CompleteAsync(
            firstChild.Id,
            "render-worker",
            leaseToken,
            CancellationToken.None);

        Assert.Equal(JobState.Succeeded, firstChild.State);
        Assert.Equal(JobState.Queued, secondChild.State);
        Assert.Equal(PipelineStageState.Running, finalStage.State);
        Assert.NotEqual(PipelineStageState.Succeeded, finalStage.State);
        Assert.Contains(
            await db.OutboxMessages.ToListAsync(),
            value => value.AggregateId == project.Id &&
                     value.Destination == "pipeline" &&
                     value.MessageType == "pipeline.reconcile");
    }
}
