using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Hook2Stream.IntegrationTests;

public sealed class AccountAndReleaseTests
{
    [Fact]
    public async Task Onboarding_requires_legal_acceptance_and_is_idempotent()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();

        var rejected = await client.PutAsJsonAsync(
            "/api/v1/account/onboarding",
            Onboarding(acceptTerms: false));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        var first = await client.PutAsJsonAsync(
            "/api/v1/account/onboarding",
            Onboarding());
        first.EnsureSuccessStatusCode();
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();

        var second = await client.PutAsJsonAsync(
            "/api/v1/account/onboarding",
            Onboarding());
        second.EnsureSuccessStatusCode();
        var secondJson = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            firstJson.GetProperty("workspaceId").GetGuid(),
            secondJson.GetProperty("workspaceId").GetGuid());
    }

    [Fact]
    public async Task Release_update_requires_the_current_etag()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);

        var create = await client.PostAsJsonAsync("/api/v1/releases", ValidRelease());
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = created.GetProperty("id").GetGuid();

        using var update = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/releases/{projectId}")
        {
            Content = JsonContent.Create(ValidRelease() with { TrackTitle = "New title" })
        };
        update.Headers.TryAddWithoutValidation("If-Match", "\"999\"");
        var response = await client.SendAsync(update);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("concurrency.etag_mismatch", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Project_ids_do_not_cross_workspace_boundaries()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", "artist-a");
        await Onboard(client, "Artist A");

        var create = await client.PostAsJsonAsync("/api/v1/releases", ValidRelease());
        create.EnsureSuccessStatusCode();
        var project = await create.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetGuid();

        client.DefaultRequestHeaders.Remove("X-Test-Subject");
        client.DefaultRequestHeaders.Add("X-Test-Subject", "artist-b");
        await Onboard(client, "Artist B");

        var foreignRequest = await client.GetAsync($"/api/v1/releases/{projectId}");

        Assert.Equal(HttpStatusCode.NotFound, foreignRequest.StatusCode);
        var problem = await foreignRequest.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("resource.not_found", problem.GetProperty("code").GetString());
    }

    private static async Task Onboard(HttpClient client, string workspace = "Test workspace")
    {
        var response = await client.PutAsJsonAsync(
            "/api/v1/account/onboarding",
            Onboarding(workspaceName: workspace));
        response.EnsureSuccessStatusCode();
    }

    private static OnboardingRequest Onboarding(
        string workspaceName = "Test workspace",
        bool acceptTerms = true) =>
        new(
            workspaceName,
            acceptTerms,
            true,
            "draft-2026-07-16",
            "draft-2026-07-16",
            "Test artist");

    private static ReleaseRequest ValidRelease() =>
        new(
            "Release 01",
            "Test artist",
            "Test song",
            "en",
            null,
            "We have a chorus",
            false,
            "upcoming",
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14),
            null);

    private sealed record OnboardingRequest(
        string WorkspaceName,
        bool AcceptTerms,
        bool AcceptPrivacy,
        string TermsVersion,
        string PrivacyVersion,
        string DisplayName);

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
