using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hook2Stream.IntegrationTests;

public sealed class CampaignManualEditTests
{
    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Released_post_release_cta_item_can_be_saved_manually()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        Guid projectId;
        Guid itemId;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
            var hooks = new HookSetRevision
            {
                WorkspaceId = workspaceId,
                ProjectId = Guid.CreateVersion7(),
                Number = 1,
                State = RevisionState.Approved,
                TranscriptRevisionId = Guid.CreateVersion7(),
                HooksJson = JsonSerializer.Serialize(
                    new[] { new HookRequest("chorus", "chorus", 0, 15_000, "Chorus") },
                    StoredJson),
                SourceFingerprint = new string('1', 64)
            };
            var project = new ReleaseProject
            {
                Id = hooks.ProjectId,
                WorkspaceId = workspaceId,
                ProjectLabel = "Released campaign",
                ArtistName = "Test artist",
                TrackTitle = "Test track",
                FlowKind = FlowKind.Mp3First,
                Mode = ReleaseMode.Released,
                CampaignStartDate = DateOnly.FromDateTime(DateTime.UtcNow),
                State = ProjectState.CampaignReady,
                SetupCompletedAt = DateTimeOffset.UtcNow,
                CurrentHookSetRevisionId = hooks.Id
            };
            var items = Enumerable.Range(1, 18)
                .Select(slot => new CampaignItemRequest(
                    Guid.CreateVersion7(),
                    slot,
                    "kinetic-lyrics",
                    "chorus",
                    null,
                    $"Campaign item {slot}",
                    "{}"))
                .ToArray();
            var campaign = new CampaignPlanRevision
            {
                WorkspaceId = workspaceId,
                ProjectId = project.Id,
                Number = 1,
                State = RevisionState.ReadyForReview,
                TranscriptRevisionId = hooks.TranscriptRevisionId,
                ArtworkPackRevisionId = Guid.CreateVersion7(),
                HookSetRevisionId = hooks.Id,
                ItemsJson = JsonSerializer.Serialize(items, StoredJson),
                SourceFingerprint = new string('2', 64)
            };
            project.CurrentCampaignPlanRevisionId = campaign.Id;
            projectId = project.Id;
            itemId = items[7].Id;
            db.Projects.Add(project);
            db.HookSetRevisions.Add(hooks);
            db.CampaignPlanRevisions.Add(campaign);
            await db.SaveChangesAsync();
        }

        var current = await client.GetAsync($"/api/v1/releases/{projectId}/campaign");
        current.EnsureSuccessStatusCode();
        using var update = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/releases/{projectId}/campaign/items/{itemId}")
        {
            Content = JsonContent.Create(new
            {
                template = "post-release-cta",
                hookId = "chorus",
                backgroundAssetId = (Guid?)null,
                text = "Keep the release moving",
                compositionJson = "{}"
            })
        };
        update.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);

        var response = await client.SendAsync(update);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var saved = payload.GetProperty("items").EnumerateArray()
            .Single(value => value.GetProperty("id").GetGuid() == itemId);
        Assert.Equal("post-release-cta", saved.GetProperty("template").GetString());
    }

    private static async Task Onboard(HttpClient client)
    {
        var response = await client.PutAsJsonAsync("/api/v1/account/onboarding", new
        {
            workspaceName = "Campaign manual edit tests",
            acceptTerms = true,
            acceptPrivacy = true,
            termsVersion = "draft-2026-07-16",
            privacyVersion = "draft-2026-07-16",
            displayName = "Campaign editor"
        });
        response.EnsureSuccessStatusCode();
    }
}
