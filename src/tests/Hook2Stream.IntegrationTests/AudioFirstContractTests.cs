using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hook2Stream.IntegrationTests;

public sealed class AudioFirstContractTests
{
    [Fact]
    public async Task Advanced_release_is_audio_first_and_accepts_a_wav_master()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);

        var create = await client.PostAsJsonAsync("/api/v1/releases", Release("EN"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var release = await create.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = release.GetProperty("id").GetGuid();
        Assert.Equal("mp3First", release.GetProperty("flowKind").GetString());
        Assert.Equal("en", release.GetProperty("language").GetString());

        var workflowResponse = await client.GetAsync($"/api/v1/releases/{projectId}/workflow");
        workflowResponse.EnsureSuccessStatusCode();
        var workflow = await workflowResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mp3First", workflow.GetProperty("flowKind").GetString());
        Assert.Equal("uploadAudio", workflow.GetProperty("nextAction").GetString());

        var upload = await client.PostAsJsonAsync(
            $"/api/v1/releases/{projectId}/uploads",
            new
            {
                kind = "audio",
                fileName = "lossless-master.wav",
                contentType = "audio/wav",
                sizeBytes = 44_100,
                replacesAssetId = (Guid?)null
            });

        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var project = await db.Projects
            .Include(value => value.PipelineRuns)
            .ThenInclude(value => value.Stages)
            .SingleAsync(value => value.Id == projectId);
        var asset = await db.MediaAssets.SingleAsync(value => value.ProjectId == projectId);
        Assert.Equal(FlowKind.Mp3First, project.FlowKind);
        Assert.Single(project.PipelineRuns);
        Assert.Equal(Enum.GetValues<WorkflowLane>().Length, project.PipelineRuns[0].Stages.Count);
        Assert.Equal("audio/wav", asset.DeclaredContentType);
        Assert.Equal(AssetKind.Audio, asset.Kind);
    }

    [Fact]
    public async Task Generic_update_rejects_audio_first_but_keeps_legacy_compatibility()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);

        var create = await client.PostAsJsonAsync("/api/v1/releases", Release());
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var audioFirstId = created.GetProperty("id").GetGuid();
        using var rejectedUpdate = Put(
            $"/api/v1/releases/{audioFirstId}",
            Release() with { TrackTitle = "Must use setup" },
            create.Headers.ETag!.Tag);

        var rejected = await client.SendAsync(rejectedUpdate);

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("release.flow_endpoint_mismatch", problem.GetProperty("code").GetString());

        Guid legacyId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
            var legacy = new ReleaseProject
            {
                WorkspaceId = workspaceId,
                ProjectLabel = "Imported legacy release",
                ArtistName = "Legacy artist",
                TrackTitle = "Legacy title",
                Language = "en",
                FlowKind = FlowKind.Legacy,
                Mode = ReleaseMode.Unscheduled
            };
            legacyId = legacy.Id;
            db.Projects.Add(legacy);
            await db.SaveChangesAsync();
        }

        var current = await client.GetAsync($"/api/v1/releases/{legacyId}");
        current.EnsureSuccessStatusCode();
        using var compatibleUpdate = Put(
            $"/api/v1/releases/{legacyId}",
            Release() with
            {
                ProjectLabel = "Updated legacy release",
                TrackTitle = "Updated title"
            },
            current.Headers.ETag!.Tag);
        var compatible = await client.SendAsync(compatibleUpdate);

        Assert.Equal(HttpStatusCode.OK, compatible.StatusCode);
        var compatibleJson = await compatible.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("legacy", compatibleJson.GetProperty("flowKind").GetString());
        Assert.Equal("Updated title", compatibleJson.GetProperty("trackTitle").GetString());
    }

    private static HttpRequestMessage Put(string path, ReleaseRequest body, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return request;
    }

    private static ReleaseRequest Release(string language = "en") => new(
        "Advanced release",
        "Test artist",
        "Test track",
        language,
        null,
        "A chorus",
        false,
        "upcoming",
        DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14),
        null);

    private static async Task Onboard(HttpClient client)
    {
        var response = await client.PutAsJsonAsync("/api/v1/account/onboarding", new
        {
            workspaceName = "Audio-first contract tests",
            acceptTerms = true,
            acceptPrivacy = true,
            termsVersion = "2026-09-04",
            privacyVersion = "2026-09-04",
            displayName = "Test artist"
        });
        response.EnsureSuccessStatusCode();
    }

    private sealed record ReleaseRequest(
        string ProjectLabel,
        string ArtistName,
        string TrackTitle,
        string Language,
        string? InternalNotes,
        string? LyricsText,
        bool IsInstrumental,
        string Mode,
        DateOnly? ReleaseDate,
        DateOnly? CampaignStartDate);
}
