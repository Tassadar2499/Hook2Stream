using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hook2Stream.IntegrationTests;

public sealed class Mp3FirstAccessContractTests
{
    [Fact]
    public async Task Workflow_snapshot_exposes_every_lane_and_is_tenant_safe()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", "workflow-owner");
        await Onboard(client, "Workflow owner");
        var quick = await QuickUpload(client, "workflow-lanes");
        quick.EnsureSuccessStatusCode();
        var body = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = body.GetProperty("project").GetProperty("id").GetGuid();

        var response = await client.GetAsync($"/api/v1/releases/{projectId}/workflow");
        response.EnsureSuccessStatusCode();
        Assert.NotNull(response.Headers.ETag);
        var workflow = await response.Content.ReadFromJsonAsync<JsonElement>();
        var lanes = workflow.GetProperty("lanes")
            .EnumerateArray()
            .Select(value => value.GetProperty("lane").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(
            new HashSet<string>(
            [
                "audio",
                "analysis",
                "transcript",
                "artwork",
                "hooks",
                "campaign",
                "preview",
                "finalRender"
            ], StringComparer.Ordinal).SetEquals(lanes));

        client.DefaultRequestHeaders.Remove("X-Test-Subject");
        client.DefaultRequestHeaders.Add("X-Test-Subject", "workflow-stranger");
        await Onboard(client, "Workflow stranger");
        var foreign = await client.GetAsync($"/api/v1/releases/{projectId}/workflow");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Workflow_snapshot_uses_persisted_stages_and_exposes_final_render_action()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client, "Persisted workflow");
        var quick = await QuickUpload(client, "workflow-persisted");
        var body = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = body.GetProperty("project").GetProperty("id").GetGuid();
        Guid batchId;
        long expectedWorkflowVersion;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var run = await db.PipelineRuns.Include(value => value.Stages)
                .SingleAsync(value => value.ProjectId == projectId);
            foreach (var stage in run.Stages)
            {
                stage.State = PipelineStageState.Succeeded;
                stage.ProgressPercent = 100;
                stage.BlockerCode = null;
                stage.ErrorCode = null;
            }
            run.State = PipelineStageState.Succeeded;
            run.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            var entitlement = new Entitlement
            {
                WorkspaceId = run.WorkspaceId,
                CheckoutId = Guid.CreateVersion7(),
                ProjectId = projectId,
                ProductCode = "release_pack",
                State = EntitlementState.Active,
                IncludedItemCount = 18,
                RemainingContentRerenders = 18,
                ProviderPeriodKey = "one-time"
            };
            var batch = new RenderBatch
            {
                WorkspaceId = run.WorkspaceId,
                ProjectId = projectId,
                PipelineRunId = run.Id,
                EntitlementId = entitlement.Id,
                State = RenderBatchState.Succeeded,
                ItemIdsJson = "[]",
                JobIdsJson = "[]",
                IdempotencyKey = "workflow-persisted",
                RequestHash = new string('a', 64),
                CompletedAt = DateTimeOffset.UtcNow
            };
            batchId = batch.Id;
            run.Stages.Single(value => value.Lane == WorkflowLane.FinalRender).CurrentRenderBatchId = batch.Id;
            // A failed historical job would have made the previous computed
            // workflow report failure. Persisted PipelineStage is authoritative.
            db.Jobs.Add(new Job
            {
                WorkspaceId = run.WorkspaceId,
                ProjectId = projectId,
                Type = JobType.FinalRender,
                State = JobState.Failed,
                RequiredCapability = "render",
                PayloadJson = "{}",
                ErrorCode = "historical.failure"
            });
            db.AddRange(entitlement, batch);
            await db.SaveChangesAsync();
            expectedWorkflowVersion = run.Version;
        }

        var response = await client.GetAsync($"/api/v1/releases/{projectId}/workflow");

        response.EnsureSuccessStatusCode();
        var workflow = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedWorkflowVersion, workflow.GetProperty("workflowVersion").GetInt64());
        Assert.Equal($"\"{expectedWorkflowVersion}\"", response.Headers.ETag!.Tag);
        Assert.Equal("downloadExport", workflow.GetProperty("nextAction").GetString());
        Assert.Equal(batchId, workflow.GetProperty("currentRenderBatchId").GetGuid());
        var final = workflow.GetProperty("lanes").EnumerateArray()
            .Single(value => value.GetProperty("lane").GetString() == "finalRender");
        Assert.Equal("succeeded", final.GetProperty("state").GetString());
        Assert.Equal(100, final.GetProperty("progressPercent").GetInt32());
    }

    [Fact]
    public async Task Final_render_stage_maps_to_unambiguous_next_actions()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client, "Final actions");
        var quick = await QuickUpload(client, "workflow-final-actions");
        var body = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = body.GetProperty("project").GetProperty("id").GetGuid();
        var cases = new[]
        {
            (PipelineStageState.WaitingUser, "purchase.required", "purchaseRender"),
            (PipelineStageState.WaitingUser, "render.start_required", "startFinalRender"),
            (PipelineStageState.Queued, (string?)null, "waitForFinalRender"),
            (PipelineStageState.Running, (string?)null, "waitForFinalRender"),
            (PipelineStageState.Succeeded, (string?)null, "downloadExport"),
            (PipelineStageState.Degraded, "render.partial_failure", "retryFailedRenders"),
            (PipelineStageState.Failed, (string?)null, "retryFailedRenders")
        };

        foreach (var (state, blocker, expectedAction) in cases)
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
                var run = await db.PipelineRuns.Include(value => value.Stages)
                    .SingleAsync(value => value.ProjectId == projectId);
                foreach (var stage in run.Stages)
                {
                    stage.State = PipelineStageState.Succeeded;
                    stage.ProgressPercent = 100;
                    stage.BlockerCode = null;
                    stage.ErrorCode = null;
                }
                var final = run.Stages.Single(value => value.Lane == WorkflowLane.FinalRender);
                final.State = state;
                final.ProgressPercent = state == PipelineStageState.Succeeded ? 100 : 0;
                final.BlockerCode = blocker;
                run.State = state;
                run.UpdatedAt = DateTimeOffset.UtcNow.AddTicks(run.Version + 1);
                await db.SaveChangesAsync();
            }

            var response = await client.GetAsync($"/api/v1/releases/{projectId}/workflow");
            response.EnsureSuccessStatusCode();
            var workflow = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(expectedAction, workflow.GetProperty("nextAction").GetString());
        }
    }

    [Theory]
    [InlineData("rights.required")]
    [InlineData("rights.visual_required")]
    [InlineData("rights.external_ai_processing_required")]
    [InlineData("rights.stale")]
    public async Task Every_rights_blocker_maps_to_confirm_rights(string blockerCode)
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client, $"Rights blocker {blockerCode}");
        var quick = await QuickUpload(client, $"workflow-{blockerCode.Replace('.', '-')}");
        var body = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = body.GetProperty("project").GetProperty("id").GetGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var run = await db.PipelineRuns.Include(value => value.Stages)
                .SingleAsync(value => value.ProjectId == projectId);
            foreach (var stage in run.Stages)
            {
                stage.State = PipelineStageState.Succeeded;
                stage.ProgressPercent = 100;
                stage.BlockerCode = null;
                stage.ErrorCode = null;
            }
            var campaign = run.Stages.Single(value => value.Lane == WorkflowLane.Campaign);
            campaign.State = PipelineStageState.WaitingUser;
            campaign.ProgressPercent = 0;
            campaign.BlockerCode = blockerCode;
            run.State = PipelineStageState.WaitingUser;
            run.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/v1/releases/{projectId}/workflow");

        response.EnsureSuccessStatusCode();
        var workflow = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("confirmRights", workflow.GetProperty("nextAction").GetString());
    }

    [Fact]
    public async Task Read_url_is_issued_only_for_a_ready_owned_asset()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", "asset-owner");
        await Onboard(client, "Asset owner");
        var quick = await QuickUpload(client, "asset-view-url");
        quick.EnsureSuccessStatusCode();
        var body = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = body.GetProperty("project").GetProperty("id").GetGuid();
        var assetId = body.GetProperty("upload").GetProperty("assetId").GetGuid();

        var pending = await client.GetAsync($"/api/v1/releases/{projectId}/assets/{assetId}/view-url");
        Assert.Equal(HttpStatusCode.NotFound, pending.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var asset = await db.MediaAssets.SingleAsync(value => value.Id == assetId);
            asset.State = AssetState.Ready;
            asset.IsActive = true;
            await db.SaveChangesAsync();
        }

        var owned = await client.GetAsync($"/api/v1/releases/{projectId}/assets/{assetId}/view-url");
        owned.EnsureSuccessStatusCode();
        var readUrl = await owned.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(assetId, readUrl.GetProperty("assetId").GetGuid());
        Assert.Equal($"/api/v1/releases/{projectId}/assets/{assetId}/content", readUrl.GetProperty("url").GetString());

        client.DefaultRequestHeaders.Remove("X-Test-Subject");
        client.DefaultRequestHeaders.Add("X-Test-Subject", "asset-stranger");
        await Onboard(client, "Asset stranger");
        var foreign = await client.GetAsync($"/api/v1/releases/{projectId}/assets/{assetId}/view-url");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private static async Task<HttpResponseMessage> QuickUpload(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/releases/audio-uploads")
        {
            Content = JsonContent.Create(new
            {
                fileName = "contract.mp3",
                contentType = "audio/mpeg",
                sizeBytes = 4_000_000,
                confirmsContentRights = true,
                allowsExternalAiProcessing = true
            })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task Onboard(HttpClient client, string workspaceName)
    {
        var response = await client.PutAsJsonAsync("/api/v1/account/onboarding", new
        {
            workspaceName,
            acceptTerms = true,
            acceptPrivacy = true,
            termsVersion = "draft-2026-07-16",
            privacyVersion = "draft-2026-07-16",
            displayName = workspaceName
        });
        response.EnsureSuccessStatusCode();
    }
}
