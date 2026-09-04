using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hook2Stream.IntegrationTests;

public sealed class PreviewRetryTests
{
    [Fact]
    public async Task Terminal_preview_failure_exposes_and_idempotently_executes_manual_retry()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var seeded = await SeedFailedPreview(factory);

        var release = await client.GetAsync($"/api/v1/releases/{seeded.ProjectId}");
        release.EnsureSuccessStatusCode();
        var workflowResponse = await client.GetAsync($"/api/v1/releases/{seeded.ProjectId}/workflow");
        workflowResponse.EnsureSuccessStatusCode();
        var workflow = await workflowResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("retryPreview", workflow.GetProperty("nextAction").GetString());

        using var firstRequest = RetryRequest(
            seeded.ProjectId,
            seeded.JobId,
            release.Headers.ETag!.Tag,
            "retry-preview-once");
        var first = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var accepted = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(seeded.JobId, accepted.GetProperty("jobId").GetGuid());

        // A network retry may carry the old project ETag. The idempotency record
        // is resolved first and must return the original operation.
        using var repeatedRequest = RetryRequest(
            seeded.ProjectId,
            seeded.JobId,
            release.Headers.ETag!.Tag,
            "retry-preview-once");
        var repeated = await client.SendAsync(repeatedRequest);
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var job = await db.Jobs.SingleAsync(value => value.Id == seeded.JobId);
        var run = await db.PipelineRuns
            .Include(value => value.Stages)
            .SingleAsync(value => value.ProjectId == seeded.ProjectId);
        var stage = run.Stages.Single(value => value.Lane == WorkflowLane.Preview);
        Assert.Equal(JobState.Queued, job.State);
        Assert.Equal(6, job.MaxAttempts);
        Assert.Null(job.ErrorCode);
        Assert.Null(job.CompletedAt);
        Assert.Equal(PipelineStageState.Queued, stage.State);
        Assert.Equal(seeded.JobId, stage.CurrentJobId);
        Assert.Single(await db.ApiIdempotencyRecords
            .Where(value => value.Scope == "preview.retry")
            .ToListAsync());
        Assert.Single(await db.JobEvents
            .Where(value => value.JobId == seeded.JobId && value.EventType == "requeued")
            .ToListAsync());
    }

    private static HttpRequestMessage RetryRequest(
        Guid projectId,
        Guid jobId,
        string etag,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/releases/{projectId}/preview/retries")
        {
            Content = JsonContent.Create(new { failedJobId = jobId })
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static async Task<SeededPreview> SeedFailedPreview(Hook2StreamApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
        var project = new ReleaseProject
        {
            WorkspaceId = workspaceId,
            ProjectLabel = "Preview retry",
            ArtistName = "Test artist",
            TrackTitle = "Test track",
            Language = "en",
            FlowKind = FlowKind.Mp3First,
            Mode = ReleaseMode.Unscheduled,
            SetupCompletedAt = DateTimeOffset.UtcNow
        };
        var campaign = new CampaignPlanRevision
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Number = 1,
            State = RevisionState.ReadyForReview,
            TranscriptRevisionId = Guid.CreateVersion7(),
            ArtworkPackRevisionId = Guid.CreateVersion7(),
            HookSetRevisionId = Guid.CreateVersion7(),
            ItemsJson = "[]",
            SourceFingerprint = new string('c', 64)
        };
        project.CurrentCampaignPlanRevisionId = campaign.Id;
        var job = new Job
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Type = JobType.PreviewRender,
            RequiredCapability = "render",
            HandlerVersion = "deterministic-render-v1",
            PayloadJson = JsonSerializer.Serialize(new
            {
                projectId = project.Id,
                campaignRevisionId = campaign.Id,
                campaignItemId = Guid.CreateVersion7()
            }),
            State = JobState.Failed,
            AttemptCount = 3,
            MaxAttempts = 3,
            ProgressPercent = 65,
            ProgressStage = "rendering",
            ErrorCode = "job.database_contract_invalid",
            ErrorMessage = "Processing failed and requires attention.",
            CompletedAt = DateTimeOffset.UtcNow
        };
        var run = new PipelineRun
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Project = project,
            Number = 1,
            State = PipelineStageState.Failed,
            Trigger = "audio-upload"
        };
        run.Stages = Enum.GetValues<WorkflowLane>()
            .Select(lane => new PipelineStage
            {
                PipelineRun = run,
                PipelineRunId = run.Id,
                Lane = lane,
                State = lane switch
                {
                    WorkflowLane.Preview => PipelineStageState.Failed,
                    WorkflowLane.FinalRender => PipelineStageState.WaitingUser,
                    _ => PipelineStageState.Succeeded
                },
                ProgressPercent = lane == WorkflowLane.Preview ? 65 : 100,
                ErrorCode = lane == WorkflowLane.Preview
                    ? "job.database_contract_invalid"
                    : null,
                BlockerCode = lane == WorkflowLane.FinalRender
                    ? "purchase.required"
                    : null,
                CurrentJobId = lane == WorkflowLane.Preview ? job.Id : null
            })
            .ToList();
        db.Projects.Add(project);
        db.CampaignPlanRevisions.Add(campaign);
        db.Jobs.Add(job);
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync();
        return new SeededPreview(project.Id, job.Id);
    }

    private static async Task Onboard(HttpClient client)
    {
        var response = await client.PutAsJsonAsync("/api/v1/account/onboarding", new
        {
            workspaceName = "Preview retry tests",
            acceptTerms = true,
            acceptPrivacy = true,
            termsVersion = "2026-09-04",
            privacyVersion = "2026-09-04",
            displayName = "Test artist"
        });
        response.EnsureSuccessStatusCode();
    }

    private sealed record SeededPreview(Guid ProjectId, Guid JobId);
}
