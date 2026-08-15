using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hook2Stream.IntegrationTests;

public sealed class MediaContentEndpointTests
{
    [Fact]
    public async Task Generated_non_preview_content_without_a_proxy_fails_closed()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = CreateClient(factory, "media-no-proxy");
        await Onboard(client);
        var seed = await SeedGeneratedAsset(factory);

        var response = await client.GetAsync(ContentUrl(seed));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemCode(response, "asset.preview_unavailable");
    }

    [Fact]
    public async Task Generated_content_serves_the_protected_derivative_with_its_mime_type()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = CreateClient(factory, "media-proxy");
        await Onboard(client);
        var seed = await SeedGeneratedAsset(factory, includeDerivative: true);
        await Store(factory, seed.SourceObjectKey, "unprotected-original"u8.ToArray());
        await Store(factory, seed.DerivativeObjectKey!, "protected-preview"u8.ToArray());

        var response = await client.GetAsync(ContentUrl(seed));

        response.EnsureSuccessStatusCode();
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("protected-preview", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Active_but_expired_entitlement_cannot_download_generated_content()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = CreateClient(factory, "media-expired");
        await Onboard(client);
        var seed = await SeedGeneratedAsset(
            factory,
            entitlementState: EntitlementState.Active,
            validUntil: DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await client.GetAsync(DownloadUrl(seed));

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        await AssertProblemCode(response, "download.entitlement_required");
    }

    [Fact]
    public async Task Active_valid_entitlement_can_download_generated_content()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = CreateClient(factory, "media-entitled");
        await Onboard(client);
        var seed = await SeedGeneratedAsset(
            factory,
            entitlementState: EntitlementState.Active,
            validUntil: DateTimeOffset.UtcNow.AddHours(1));
        await Store(factory, seed.SourceObjectKey, "licensed-render"u8.ToArray());

        var response = await client.GetAsync(DownloadUrl(seed));

        response.EnsureSuccessStatusCode();
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("licensed-render", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Revoked_entitlement_cannot_download_generated_content()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = CreateClient(factory, "media-revoked");
        await Onboard(client);
        var seed = await SeedGeneratedAsset(
            factory,
            entitlementState: EntitlementState.Revoked,
            revokedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await client.GetAsync(DownloadUrl(seed));

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        await AssertProblemCode(response, "download.entitlement_required");
    }

    [Fact]
    public async Task Soft_deleted_asset_is_not_served()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = CreateClient(factory, "media-deleted");
        await Onboard(client);
        var seed = await SeedGeneratedAsset(factory, deletedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await client.GetAsync(ContentUrl(seed));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertProblemCode(response, "asset.not_found");
    }

    [Fact]
    public async Task Asset_from_another_workspace_is_not_served()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var owner = CreateClient(factory, "media-owner");
        await Onboard(owner);
        var seed = await SeedGeneratedAsset(factory, includeDerivative: true);

        using var stranger = CreateClient(factory, "media-stranger");
        await Onboard(stranger);

        var response = await stranger.GetAsync(ContentUrl(seed));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertProblemCode(response, "asset.not_found");
    }

    private static HttpClient CreateClient(Hook2StreamApiFactory factory, string subject)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        return client;
    }

    private static async Task Onboard(HttpClient client)
    {
        var response = await client.PutAsJsonAsync("/api/v1/account/onboarding", new
        {
            workspaceName = "Media content endpoint tests",
            acceptTerms = true,
            acceptPrivacy = true,
            termsVersion = "draft-2026-07-16",
            privacyVersion = "draft-2026-07-16",
            displayName = "Media endpoint tester"
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<MediaSeed> SeedGeneratedAsset(
        Hook2StreamApiFactory factory,
        bool includeDerivative = false,
        EntitlementState entitlementState = EntitlementState.Active,
        DateTimeOffset? validUntil = null,
        DateTimeOffset? revokedAt = null,
        DateTimeOffset? deletedAt = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var workspace = await db.Workspaces.SingleAsync();
        var project = new ReleaseProject
        {
            WorkspaceId = workspace.Id,
            ProjectLabel = "Media security regression",
            ArtistName = "Integration Artist",
            TrackTitle = "Integration Track"
        };
        var entitlement = new Entitlement
        {
            WorkspaceId = workspace.Id,
            CheckoutId = Guid.CreateVersion7(),
            ProjectId = project.Id,
            ProductCode = "release_pack",
            State = entitlementState,
            IncludedItemCount = 1,
            RemainingContentRerenders = 1,
            ProviderPeriodKey = "one-time",
            ValidUntil = validUntil,
            RevokedAt = revokedAt
        };
        var batch = new RenderBatch
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            EntitlementId = entitlement.Id,
            State = RenderBatchState.Succeeded,
            ItemIdsJson = "[]",
            JobIdsJson = "[]",
            IdempotencyKey = $"media-{Guid.NewGuid():N}",
            RequestHash = new string('a', 64),
            CompletedAt = DateTimeOffset.UtcNow
        };
        var sourceObjectKey = $"tests/media/{Guid.NewGuid():N}/source.mp4";
        var asset = new MediaAsset
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Kind = AssetKind.Visual,
            Origin = AssetOrigin.Generated,
            Purpose = AssetPurpose.CampaignVideo,
            State = AssetState.Ready,
            OriginalFileName = "licensed-render.mp4",
            DeclaredContentType = "video/mp4",
            DetectedContentType = "video/mp4",
            DeclaredBytes = 17,
            ActualBytes = 17,
            ObjectKey = sourceObjectKey,
            IsActive = true,
            RenderBatchId = batch.Id,
            DeletedAt = deletedAt
        };
        string? derivativeObjectKey = null;
        if (includeDerivative)
        {
            derivativeObjectKey = $"tests/media/{Guid.NewGuid():N}/proxy.webp";
            asset.Derivatives.Add(new MediaDerivative
            {
                Kind = DerivativeKind.ImageProxy,
                ProcessorVersion = "integration-test-v1",
                ObjectKey = derivativeObjectKey,
                ContentType = "image/webp",
                Bytes = 17
            });
        }

        db.AddRange(project, entitlement, batch, asset);
        await db.SaveChangesAsync();
        return new MediaSeed(project.Id, asset.Id, sourceObjectKey, derivativeObjectKey);
    }

    private static async Task Store(Hook2StreamApiFactory factory, string objectKey, byte[] content)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, content);
            var storage = factory.Services.GetRequiredService<IObjectStorage>();
            await storage.UploadAsync(objectKey, path, "application/octet-stream", CancellationToken.None);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task AssertProblemCode(HttpResponseMessage response, string expected)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected, problem.GetProperty("code").GetString());
    }

    private static string ContentUrl(MediaSeed seed) =>
        $"/api/v1/releases/{seed.ProjectId}/assets/{seed.AssetId}/content";

    private static string DownloadUrl(MediaSeed seed) =>
        $"/api/v1/releases/{seed.ProjectId}/downloads/{seed.AssetId}";

    private sealed record MediaSeed(
        Guid ProjectId,
        Guid AssetId,
        string SourceObjectKey,
        string? DerivativeObjectKey);
}
