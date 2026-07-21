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
    public async Task Manual_item_edit_preserves_dependency_fingerprint_and_cancels_stale_preview()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var seeded = await SeedCampaign(factory);

        var current = await client.GetAsync($"/api/v1/releases/{seeded.ProjectId}/campaign");
        current.EnsureSuccessStatusCode();
        var editedComposition = Composition(
            CampaignPlanContractValidator.CanonicalSlots(isAlreadyReleased: true)[7].RelativeDay,
            "Keep the release moving");
        using var update = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/releases/{seeded.ProjectId}/campaign/items/{seeded.ItemId}")
        {
            Content = JsonContent.Create(new
            {
                template = seeded.Template,
                hookId = seeded.HookId,
                backgroundAssetId = (Guid?)null,
                text = "Keep the release moving",
                compositionJson = editedComposition
            })
        };
        update.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);

        var response = await client.SendAsync(update);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var saved = payload.GetProperty("items").EnumerateArray()
            .Single(value => value.GetProperty("id").GetGuid() == seeded.ItemId);
        Assert.Equal("Keep the release moving", saved.GetProperty("text").GetString());
        Assert.Equal(seeded.Template, saved.GetProperty("template").GetString());
        Assert.Equal(seeded.HookId, saved.GetProperty("hookId").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var project = await db.Projects.SingleAsync(value => value.Id == seeded.ProjectId);
        var revision = await db.CampaignPlanRevisions.SingleAsync(
            value => value.Id == project.CurrentCampaignPlanRevisionId);
        var preview = await db.Jobs.SingleAsync(value => value.Id == seeded.PreviewJobId);
        Assert.Equal(seeded.DependencyFingerprint, revision.SourceFingerprint);
        Assert.Equal(JobState.Cancelled, preview.State);
        Assert.Equal("preview.revision_superseded", preview.ErrorCode);
    }

    [Fact]
    public async Task Manual_item_edit_rejects_template_or_hook_reassignment()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var seeded = await SeedCampaign(factory);
        var current = await client.GetAsync($"/api/v1/releases/{seeded.ProjectId}/campaign");

        using var update = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/releases/{seeded.ProjectId}/campaign/items/{seeded.ItemId}")
        {
            Content = JsonContent.Create(new
            {
                template = "kinetic-lyrics",
                hookId = seeded.HookId,
                backgroundAssetId = (Guid?)null,
                text = "Mutation",
                compositionJson = Composition(7, "Mutation")
            })
        };
        update.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);

        var response = await client.SendAsync(update);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("campaign.slot_assignment_immutable", problem.GetProperty("code").GetString());
    }

    private static async Task<SeededCampaign> SeedCampaign(Hook2StreamApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
        var projectId = Guid.CreateVersion7();
        var transcriptId = Guid.CreateVersion7();
        var hookIds = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        var hooks = new HookSetRevision
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Number = 1,
            State = RevisionState.Approved,
            TranscriptRevisionId = transcriptId,
            HooksJson = JsonSerializer.Serialize(
                hookIds.Select((id, index) => new HookRequest(
                    id.ToString("N"),
                    new[] { "chorus", "emotional", "energy" }[index],
                    index * 20_000,
                    index * 20_000 + 15_000,
                    $"Hook {index + 1}")),
                StoredJson),
            SourceFingerprint = new string('1', 64)
        };
        var project = new ReleaseProject
        {
            Id = projectId,
            WorkspaceId = workspaceId,
            ProjectLabel = "Released campaign",
            ArtistName = "Test artist",
            TrackTitle = "Test track",
            Language = "en",
            FlowKind = FlowKind.Mp3First,
            Mode = ReleaseMode.Released,
            ReleaseDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            CampaignStartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            State = ProjectState.CampaignReady,
            SetupCompletedAt = DateTimeOffset.UtcNow,
            CurrentHookSetRevisionId = hooks.Id
        };
        var slots = CampaignPlanContractValidator.CanonicalSlots(isAlreadyReleased: true);
        var items = slots.Select((slot, index) => new CampaignItemRequest(
            Guid.CreateVersion7(),
            index + 1,
            slot.TemplateKey,
            slot.HookIndex is { } hookIndex ? hookIds[hookIndex].ToString("N") : string.Empty,
            null,
            $"Campaign item {index + 1}",
            Composition(slot.RelativeDay, $"Campaign item {index + 1}"))).ToArray();
        var dependencyFingerprint = new string('2', 64);
        var campaign = new CampaignPlanRevision
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Number = 1,
            State = RevisionState.ReadyForReview,
            TranscriptRevisionId = transcriptId,
            ArtworkPackRevisionId = Guid.CreateVersion7(),
            HookSetRevisionId = hooks.Id,
            ItemsJson = JsonSerializer.Serialize(items, StoredJson),
            SourceFingerprint = dependencyFingerprint
        };
        project.CurrentCampaignPlanRevisionId = campaign.Id;
        var preview = new Job
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Type = JobType.PreviewRender,
            State = JobState.Queued,
            RequiredCapability = "render",
            PayloadJson = JsonSerializer.Serialize(new
            {
                projectId = project.Id,
                campaignRevisionId = campaign.Id,
                campaignItemId = items[0].Id
            }, StoredJson)
        };
        db.Projects.Add(project);
        db.HookSetRevisions.Add(hooks);
        db.CampaignPlanRevisions.Add(campaign);
        db.Jobs.Add(preview);
        await db.SaveChangesAsync();
        var selected = items[7];
        return new SeededCampaign(
            project.Id,
            selected.Id,
            selected.Template,
            selected.HookId,
            preview.Id,
            dependencyFingerprint);
    }

    private static string Composition(int relativeDay, string text) => JsonSerializer.Serialize(new
    {
        relativeDay,
        headline = text,
        cta = "Listen now",
        durationMilliseconds = 15_000,
        hashtags = new[] { "#hook2stream" },
        copyVariants = new
        {
            neutral = text,
            emotional = $"Feel {text}",
            destinations = new
            {
                tiktok = text,
                youtubeShorts = text,
                instagramReels = text,
                vkClips = text
            }
        }
    }, StoredJson);

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

    private sealed record SeededCampaign(
        Guid ProjectId,
        Guid ItemId,
        string Template,
        string HookId,
        Guid PreviewJobId,
        string DependencyFingerprint);
}
