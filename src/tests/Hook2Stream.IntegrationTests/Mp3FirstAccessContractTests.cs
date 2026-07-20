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
        Assert.Contains("read=true", readUrl.GetProperty("url").GetString(), StringComparison.Ordinal);

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
