using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Worker;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.UnitTests;

public sealed class FinalRenderProjectionTests
{
    [Theory]
    [InlineData(JobState.Cancelled, "waiting_user", "rights.visual_required", PipelineStageState.WaitingUser, "rights.visual_required", null)]
    [InlineData(JobState.Failed, "failed", "export.bundle_failed", PipelineStageState.Failed, null, "export.bundle_failed")]
    public async Task Current_run_projection_derives_export_terminal_and_blocking_states(
        JobState exportState,
        string progressStage,
        string jobErrorCode,
        PipelineStageState expectedState,
        string? expectedBlocker,
        string? expectedError)
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"final-render-projection-{Guid.NewGuid():N}")
            .Options;
        await using var db = new Hook2StreamDbContext(options);
        var workspaceId = Guid.CreateVersion7();
        var project = new ReleaseProject
        {
            WorkspaceId = workspaceId,
            ProjectLabel = "Final render projection",
            ArtistName = "Artist",
            TrackTitle = "Track",
            FlowKind = FlowKind.Mp3First
        };
        var currentRun = new PipelineRun
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Project = project,
            Number = 2,
            State = PipelineStageState.Running
        };
        currentRun.Stages.Add(new PipelineStage
        {
            PipelineRun = currentRun,
            PipelineRunId = currentRun.Id,
            Lane = WorkflowLane.FinalRender,
            State = PipelineStageState.Running
        });
        var oldRun = new PipelineRun
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Project = project,
            Number = 1,
            State = PipelineStageState.Running
        };
        var entitlement = new Entitlement
        {
            WorkspaceId = workspaceId,
            CheckoutId = Guid.CreateVersion7(),
            ProjectId = project.Id,
            ProductCode = "release_pack",
            State = EntitlementState.Active,
            IncludedItemCount = 18,
            RemainingContentRerenders = 18,
            ProviderPeriodKey = "one-time"
        };
        var export = new Job
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            PipelineRunId = currentRun.Id,
            PipelineStage = "finalRender",
            Type = JobType.ExportBundle,
            RequiredCapability = JobRoutingRegistry.Export,
            HandlerVersion = "export-v1",
            PayloadJson = "{}",
            State = exportState,
            ProgressStage = progressStage,
            ErrorCode = jobErrorCode,
            ProgressPercent = 100
        };
        var currentBatch = new RenderBatch
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            PipelineRunId = currentRun.Id,
            EntitlementId = entitlement.Id,
            State = RenderBatchState.Running,
            JobIdsJson = JsonSerializer.Serialize(new[] { export.Id }),
            IdempotencyKey = "current-batch",
            RequestHash = new string('a', 64),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var unrelatedNewerBatch = new RenderBatch
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            PipelineRunId = oldRun.Id,
            EntitlementId = entitlement.Id,
            State = RenderBatchState.Running,
            JobIdsJson = "[]",
            IdempotencyKey = "old-run-newer-batch",
            RequestHash = new string('b', 64),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(project, currentRun, oldRun, entitlement, export, currentBatch, unrelatedNewerBatch);
        await db.SaveChangesAsync();

        await PipelineReconciler.EnsureFinalRenderAsync(
            db,
            project,
            currentRun,
            CancellationToken.None);

        var stage = Assert.Single(currentRun.Stages);
        Assert.Equal(expectedState, stage.State);
        Assert.Equal(expectedBlocker, stage.BlockerCode);
        Assert.Equal(expectedError, stage.ErrorCode);
        Assert.Equal(export.Id, stage.CurrentJobId);
        Assert.Equal(currentBatch.Id, stage.CurrentRenderBatchId);
    }
}
