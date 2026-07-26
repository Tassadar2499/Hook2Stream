using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hook2Stream.IntegrationTests;

public sealed class ArtworkBackgroundRetryTests
{
    [Fact]
    public async Task Failed_exact_background_job_enqueues_one_deterministic_retry()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var seed = await SeedApprovedArtwork(factory);
        var legacyFingerprint = $"cover:{seed.CoverId:N}:v1";
        Assert.Equal($"cover:{seed.CoverId:N}:v2", seed.Fingerprint);
        var failedJobId = await AddBackgroundJob(
            factory,
            seed,
            JobState.Failed,
            "failed",
            legacyFingerprint);
        await AddNonMatchingActiveJobs(factory, seed);

        using var first = await ApproveCover(client, seed);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal($"\"{seed.PackVersion}\"", first.Headers.ETag?.Tag);
        var firstLocation = first.Headers.Location?.ToString();
        Assert.NotNull(firstLocation);
        var retryJobId = JobId(firstLocation!);

        using var replay = await ApproveCover(client, seed);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstLocation, replay.Headers.Location?.ToString());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var retryKey = $"artwork-backgrounds:{seed.PackId:N}:retry:{failedJobId:N}";
        var retry = await db.Jobs.SingleAsync(value => value.Id == retryJobId);
        Assert.Equal(retryKey, retry.IdempotencyKey);
        Assert.Equal(seed.ProjectId, retry.ProjectId);
        Assert.Equal(JobType.ArtworkGeneration, retry.Type);
        Assert.Equal(seed.CoverId, retry.AssetId);
        Assert.Equal(seed.Fingerprint, retry.InputFingerprint);
        Assert.Equal(JobState.Queued, retry.State);
        using (var payload = JsonDocument.Parse(retry.PayloadJson))
        {
            Assert.Equal(seed.PackId, payload.RootElement.GetProperty("artworkPackRevisionId").GetGuid());
            Assert.Equal("backgrounds", payload.RootElement.GetProperty("mode").GetString());
            Assert.Equal(failedJobId, payload.RootElement.GetProperty("retryOfJobId").GetGuid());
        }

        Assert.Equal(1, await db.Jobs.CountAsync(value => value.IdempotencyKey == retryKey));
        Assert.Equal(JobState.Failed, (await db.Jobs.SingleAsync(value => value.Id == failedJobId)).State);
        var pack = await db.ArtworkPackRevisions.SingleAsync(value => value.Id == seed.PackId);
        Assert.Equal(RevisionState.Approved, pack.State);
        Assert.Equal("[]", pack.BackgroundAssetIdsJson);
        Assert.Equal(seed.PackVersion, pack.Version);
    }

    [Theory]
    [InlineData(JobState.Queued)]
    [InlineData(JobState.Running)]
    public async Task Active_exact_background_job_is_returned_without_a_duplicate(JobState state)
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var seed = await SeedApprovedArtwork(factory);
        var existingJobId = await AddBackgroundJob(factory, seed, state, state.ToString().ToLowerInvariant());

        using var first = await ApproveCover(client, seed);
        using var replay = await ApproveCover(client, seed);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal($"/api/v1/jobs/{existingJobId}", first.Headers.Location?.ToString());
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Single(await db.Jobs.Where(value => value.ProjectId == seed.ProjectId).ToListAsync());
        Assert.Equal(state, (await db.Jobs.SingleAsync()).State);
    }

    [Theory]
    [InlineData(JobState.Cancelled, "waiting_user")]
    [InlineData(JobState.Succeeded, "succeeded")]
    public async Task Non_failed_terminal_background_job_is_preserved_when_artifacts_are_missing(
        JobState state,
        string progressStage)
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var seed = await SeedApprovedArtwork(factory);
        var existingJobId = await AddBackgroundJob(factory, seed, state, progressStage);

        using var response = await ApproveCover(client, seed);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"/api/v1/jobs/{existingJobId}", response.Headers.Location?.ToString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var job = await db.Jobs.SingleAsync();
        Assert.Equal(existingJobId, job.Id);
        Assert.Equal(state, job.State);
        Assert.Equal(progressStage, job.ProgressStage);
        Assert.Equal("[]", (await db.ArtworkPackRevisions.SingleAsync()).BackgroundAssetIdsJson);
    }

    [Fact]
    public async Task Failed_background_job_is_not_retried_when_backgrounds_already_exist()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var backgroundId = Guid.CreateVersion7();
        var seed = await SeedApprovedArtwork(factory, [backgroundId]);
        var failedJobId = await AddBackgroundJob(factory, seed, JobState.Failed, "failed");

        using var response = await ApproveCover(client, seed);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"/api/v1/jobs/{failedJobId}", response.Headers.Location?.ToString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Single(await db.Jobs.ToListAsync());
    }

    private static async Task<ArtworkSeed> SeedApprovedArtwork(
        Hook2StreamApiFactory factory,
        IReadOnlyList<Guid>? backgroundIds = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var workspace = await db.Workspaces.SingleAsync();
        var project = new ReleaseProject
        {
            WorkspaceId = workspace.Id,
            ProjectLabel = "Background retry",
            ArtistName = "Retry artist",
            TrackTitle = "Retry track",
            FlowKind = FlowKind.Mp3First,
            Mode = ReleaseMode.Upcoming,
            ReleaseDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
            IsInstrumental = true,
            IsInstrumentalConfirmed = true,
            SetupCompletedAt = DateTimeOffset.UtcNow
        };
        var audio = new MediaAsset
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Kind = AssetKind.Audio,
            Origin = AssetOrigin.Uploaded,
            Purpose = AssetPurpose.AudioMaster,
            State = AssetState.Ready,
            IsActive = true,
            OriginalFileName = "retry.mp3",
            DeclaredContentType = "audio/mpeg",
            DeclaredBytes = 1024,
            ActualBytes = 1024,
            ObjectKey = $"tests/{project.Id:N}/retry.mp3",
            Sha256 = new string('a', 64),
            DurationMilliseconds = 180_000
        };
        var cover = new MediaAsset
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Kind = AssetKind.Cover,
            Origin = AssetOrigin.Generated,
            Purpose = AssetPurpose.ApprovedCover,
            State = AssetState.Ready,
            IsActive = true,
            OriginalFileName = "retry-cover.png",
            DeclaredContentType = "image/png",
            DeclaredBytes = 2048,
            ActualBytes = 2048,
            ObjectKey = $"tests/{project.Id:N}/retry-cover.png",
            Sha256 = new string('b', 64),
            Width = 2048,
            Height = 2048,
            // Legacy approval enqueued the background job with v1 and then
            // changed Purpose, which persisted this asset as v2.
            Version = 2
        };
        var pack = new ArtworkPackRevision
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Number = 1,
            OperationNumber = 1,
            State = RevisionState.Approved,
            Prompt = "Approved retry cover",
            CandidateAssetIdsJson = JsonSerializer.Serialize(new[] { cover.Id }),
            BackgroundAssetIdsJson = JsonSerializer.Serialize(backgroundIds ?? []),
            SelectedAssetId = cover.Id,
            CompositionJson = "{}",
            SourceFingerprint = "request:background-retry",
            ApprovedBySubject = "user-a",
            ApprovedAt = DateTimeOffset.UtcNow
        };
        project.CurrentArtworkPackRevisionId = pack.Id;
        var rights = new RightsAttestation
        {
            ProjectId = project.Id,
            ActorSubject = "user-a",
            PolicyVersion = "external-ai-zdr-v1",
            OwnsAudioRights = true,
            OwnsLyricsRights = false,
            OwnsVisualRights = true,
            AllowsExternalAiArtwork = true,
            AllowsExternalAiProcessing = true,
            AudioAssetId = audio.Id,
            AudioFingerprint = audio.Sha256,
            SyntheticContentStatus = SyntheticContentStatus.None,
            AcceptedAt = DateTimeOffset.UtcNow
        };
        db.Projects.Add(project);
        db.MediaAssets.AddRange(audio, cover);
        db.ArtworkPackRevisions.Add(pack);
        db.RightsAttestations.Add(rights);
        await db.SaveChangesAsync();
        return new ArtworkSeed(
            workspace.Id,
            project.Id,
            pack.Id,
            cover.Id,
            pack.Version,
            $"cover:{cover.Id:N}:v{cover.Version}");
    }

    private static async Task<Guid> AddBackgroundJob(
        Hook2StreamApiFactory factory,
        ArtworkSeed seed,
        JobState state,
        string progressStage,
        string? inputFingerprint = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var job = BackgroundJob(
            seed,
            seed.ProjectId,
            JobType.ArtworkGeneration,
            seed.CoverId,
            inputFingerprint ?? seed.Fingerprint,
            seed.PackId,
            "backgrounds",
            state,
            progressStage,
            $"background-exact-{Guid.CreateVersion7():N}");
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private static async Task AddNonMatchingActiveJobs(Hook2StreamApiFactory factory, ArtworkSeed seed)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        db.Jobs.AddRange(
            BackgroundJob(seed, Guid.CreateVersion7(), JobType.ArtworkGeneration, seed.CoverId, seed.Fingerprint, seed.PackId, "backgrounds", JobState.Queued, "queued", "background-wrong-project"),
            BackgroundJob(seed, seed.ProjectId, JobType.CampaignGeneration, seed.CoverId, seed.Fingerprint, seed.PackId, "backgrounds", JobState.Queued, "queued", "background-wrong-type"),
            BackgroundJob(seed, seed.ProjectId, JobType.ArtworkGeneration, Guid.CreateVersion7(), seed.Fingerprint, seed.PackId, "backgrounds", JobState.Queued, "queued", "background-wrong-asset"),
            BackgroundJob(seed, seed.ProjectId, JobType.ArtworkGeneration, seed.CoverId, $"{seed.Fingerprint}-stale", seed.PackId, "backgrounds", JobState.Queued, "queued", "background-wrong-fingerprint"),
            BackgroundJob(seed, seed.ProjectId, JobType.ArtworkGeneration, seed.CoverId, seed.Fingerprint, Guid.CreateVersion7(), "backgrounds", JobState.Queued, "queued", "background-wrong-pack"),
            BackgroundJob(seed, seed.ProjectId, JobType.ArtworkGeneration, seed.CoverId, seed.Fingerprint, seed.PackId, "covers", JobState.Queued, "queued", "background-wrong-mode"));
        await db.SaveChangesAsync();
    }

    private static Job BackgroundJob(
        ArtworkSeed seed,
        Guid projectId,
        JobType type,
        Guid assetId,
        string inputFingerprint,
        Guid packId,
        string mode,
        JobState state,
        string progressStage,
        string idempotencyKey) => new()
        {
            WorkspaceId = seed.WorkspaceId,
            ProjectId = projectId,
            AssetId = assetId,
            Type = type,
            RequiredCapability = JobRoutingRegistry.Control,
            HandlerVersion = "openrouter-image-v1",
            InputFingerprint = inputFingerprint,
            IdempotencyKey = idempotencyKey,
            PayloadJson = JsonSerializer.Serialize(new
            {
                projectId,
                artworkPackRevisionId = packId,
                mode,
                count = 3
            }),
            State = state,
            ProgressStage = progressStage,
            ErrorCode = state == JobState.Cancelled ? "rights.external_ai_processing_required" : null,
            CompletedAt = state is JobState.Failed or JobState.Cancelled or JobState.Succeeded
                ? DateTimeOffset.UtcNow
                : null
        };

    private static async Task<HttpResponseMessage> ApproveCover(HttpClient client, ArtworkSeed seed)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/releases/{seed.ProjectId}/artwork/cover-approval")
        {
            Content = JsonContent.Create(new { revisionId = seed.PackId })
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{seed.PackVersion}\"");
        return await client.SendAsync(request);
    }

    private static Guid JobId(string location) =>
        Guid.Parse(location[(location.LastIndexOf('/') + 1)..]);

    private static async Task Onboard(HttpClient client)
    {
        var response = await client.PutAsJsonAsync("/api/v1/account/onboarding", new
        {
            workspaceName = "Artwork background retry tests",
            acceptTerms = true,
            acceptPrivacy = true,
            termsVersion = "draft-2026-07-16",
            privacyVersion = "draft-2026-07-16",
            displayName = "Retry artist"
        });
        response.EnsureSuccessStatusCode();
    }

    private sealed record ArtworkSeed(
        Guid WorkspaceId,
        Guid ProjectId,
        Guid PackId,
        Guid CoverId,
        long PackVersion,
        string Fingerprint);
}
