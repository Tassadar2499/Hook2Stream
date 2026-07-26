using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Infrastructure.Pipeline;
using Hook2Stream.Infrastructure.Providers;
using Hook2Stream.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class ArtworkBackgroundReferenceTests
{
    [Fact]
    public async Task Background_provider_receives_thumbnail_while_canonical_cover_fences_the_result()
    {
        var databaseName = $"artwork-background-reference-{Guid.NewGuid():N}";
        var databaseRoot = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase(databaseName, databaseRoot)
            .Options;
        var workspaceId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var audioId = Guid.CreateVersion7();
        var packId = Guid.CreateVersion7();
        var coverId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var leaseToken = Guid.CreateVersion7();
        const string audioSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string canonicalSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string changedCanonicalSha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string thumbnailSha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        const string canonicalObjectKey = "workspaces/test/approved-cover.png";
        const string thumbnailObjectKey = "workspaces/test/approved-cover.thumbnail.webp";
        const long thumbnailBytes = 42_321;

        await using var db = new Hook2StreamDbContext(options);
        var project = new ReleaseProject
        {
            Id = projectId,
            WorkspaceId = workspaceId,
            ProjectLabel = "Background reference",
            ArtistName = "Artist",
            TrackTitle = "Track",
            FlowKind = FlowKind.Mp3First,
            SetupCompletedAt = DateTimeOffset.UtcNow,
            Mode = ReleaseMode.Upcoming,
            ReleaseDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7),
            CurrentArtworkPackRevisionId = packId
        };
        var audio = new MediaAsset
        {
            Id = audioId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Kind = AssetKind.Audio,
            Origin = AssetOrigin.Uploaded,
            Purpose = AssetPurpose.AudioMaster,
            State = AssetState.Ready,
            OriginalFileName = "track.mp3",
            DeclaredContentType = "audio/mpeg",
            DetectedContentType = "audio/mpeg",
            DeclaredBytes = 1_024,
            ActualBytes = 1_024,
            ObjectKey = "workspaces/test/track.mp3",
            IsActive = true,
            Sha256 = audioSha256
        };
        var cover = new MediaAsset
        {
            Id = coverId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Kind = AssetKind.Cover,
            Origin = AssetOrigin.Generated,
            Purpose = AssetPurpose.ApprovedCover,
            State = AssetState.Ready,
            OriginalFileName = "approved-cover.png",
            DeclaredContentType = "image/png",
            DetectedContentType = "image/png",
            DeclaredBytes = 12_000_000,
            ActualBytes = 12_000_000,
            ObjectKey = canonicalObjectKey,
            Sha256 = canonicalSha256,
            Width = 2048,
            Height = 2048
        };
        cover.Derivatives.Add(new MediaDerivative
        {
            AssetId = cover.Id,
            Asset = cover,
            Kind = DerivativeKind.Thumbnail,
            ProcessorVersion = "generated-preview-v1",
            ObjectKey = thumbnailObjectKey,
            ContentType = "image/webp",
            Bytes = thumbnailBytes,
            Sha256 = thumbnailSha256,
            Width = 384,
            Height = 384
        });
        // The watermarked proxy is present but must never be selected as the
        // external provider reference.
        cover.Derivatives.Add(new MediaDerivative
        {
            AssetId = cover.Id,
            Asset = cover,
            Kind = DerivativeKind.ImageProxy,
            ProcessorVersion = "generated-preview-v1",
            ObjectKey = "workspaces/test/approved-cover.preview.webp",
            ContentType = "image/webp",
            Bytes = 250_000,
            Sha256 = new string('e', 64)
        });
        var pack = new ArtworkPackRevision
        {
            Id = packId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Number = 1,
            OperationNumber = 1,
            State = RevisionState.Approved,
            Prompt = "Approved artwork",
            CandidateAssetIdsJson = JsonSerializer.Serialize(new[] { coverId }),
            SelectedAssetId = coverId,
            SourceFingerprint = canonicalSha256,
            ApprovedAt = DateTimeOffset.UtcNow
        };
        var payload = JsonSerializer.Serialize(new
        {
            projectId,
            artworkPackRevisionId = packId,
            mode = "backgrounds",
            count = 3
        });
        var job = new Job
        {
            Id = jobId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            AssetId = coverId,
            Type = JobType.ArtworkGeneration,
            State = JobState.Running,
            RequiredCapability = JobRoutingRegistry.Control,
            HandlerVersion = "openrouter-image-v1",
            InputFingerprint = $"cover:{coverId:N}:v1",
            PayloadJson = payload,
            AttemptCount = 1,
            LeaseOwner = "worker-1",
            LeaseToken = leaseToken,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
        };
        db.AddRange(
            project,
            audio,
            cover,
            pack,
            job,
            new BrandKit { WorkspaceId = workspaceId },
            new RightsAttestation
            {
                ProjectId = projectId,
                ActorSubject = "owner",
                PolicyVersion = "external-ai-zdr-v1",
                OwnsAudioRights = true,
                OwnsLyricsRights = true,
                OwnsVisualRights = true,
                AllowsExternalAiProcessing = true,
                AudioAssetId = audioId,
                AudioFingerprint = audioSha256,
                AcceptedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();

        var provider = new CapturingArtworkProvider(async () =>
        {
            await using var concurrent = new Hook2StreamDbContext(options);
            var currentCover = await concurrent.MediaAssets.SingleAsync(value => value.Id == coverId);
            currentCover.Sha256 = changedCanonicalSha256;
            await concurrent.SaveChangesAsync();
        });
        var handler = new ArtworkGenerationJobHandler(
            db,
            provider,
            new UnusedArtifactStore(),
            new UnusedObjectStorage(),
            new UnusedProcessRunner(),
            Options.Create(new MediaToolsOptions()),
            TimeProvider.System,
            new ExistingCoverComposer(cover),
            new NoopInvocationWriter());
        var leasedJob = new LeasedJob(
            job.Id,
            workspaceId,
            projectId,
            coverId,
            JobType.ArtworkGeneration,
            payload,
            1,
            3,
            JobRoutingRegistry.Control,
            job.HandlerVersion,
            job.InputFingerprint,
            1,
            "worker-1",
            job.LeaseExpiresAt!.Value,
            leaseToken);

        var exception = await Assert.ThrowsAsync<JobHandlerException>(
            () => handler.ProcessAsync(leasedJob, CancellationToken.None));

        Assert.Equal("artwork.revision_stale", exception.Code);
        Assert.NotNull(provider.Request);
        Assert.NotNull(provider.Request.ReferenceImage);
        var reference = provider.Request.ReferenceImage!;
        Assert.Equal(coverId, reference.AssetId);
        Assert.Equal(thumbnailObjectKey, reference.ObjectKey);
        Assert.Equal(thumbnailSha256, reference.Sha256);
        Assert.Equal("image/webp", reference.ContentType);
        Assert.Equal(thumbnailBytes, reference.SizeBytes);
        Assert.Equal(384, reference.Width);
        Assert.Equal(384, reference.Height);
        Assert.NotEqual(canonicalObjectKey, reference.ObjectKey);

        await using var verify = new Hook2StreamDbContext(options);
        Assert.Equal(
            changedCanonicalSha256,
            await verify.MediaAssets.Where(value => value.Id == coverId).Select(value => value.Sha256).SingleAsync());
        Assert.Empty(JsonSerializer.Deserialize<Guid[]>(
            (await verify.ArtworkPackRevisions.SingleAsync(value => value.Id == packId)).BackgroundAssetIdsJson)!);
    }

    private sealed class CapturingArtworkProvider(Func<Task> afterCapture) : IArtworkProvider
    {
        public ArtworkGenerationRequest? Request { get; private set; }

        public async Task<ProviderResult<ArtworkGenerationResult>> GenerateAsync(
            ArtworkGenerationRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            await afterCapture();
            return await new FixtureArtworkProvider(TimeProvider.System).GenerateAsync(request, cancellationToken);
        }
    }

    private sealed class ExistingCoverComposer(MediaAsset cover) : ICleanCoverComposer
    {
        public Task<MediaAsset> EnsureAsync(
            ReleaseProject project,
            ArtworkPackRevision artworkPack,
            CancellationToken cancellationToken,
            string? artistNameSnapshot = null,
            string? trackTitleSnapshot = null) => Task.FromResult(cover);
    }

    private sealed class NoopInvocationWriter : IAiProviderInvocationWriter
    {
        public Task RecordAsync(
            LeasedJob job,
            string stage,
            ProviderExecutionContext context,
            ProviderProvenance provenance,
            ProviderFailure? failure,
            string? status,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UnusedArtifactStore : IPipelineArtifactStore
    {
        public Task<PromotedArtifact> PromoteAsync(
            ProviderArtifactManifest manifest,
            string canonicalObjectKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PromotedArtifact> StoreLocalAsync(
            string sourcePath,
            string canonicalObjectKey,
            string contentType,
            long? durationMilliseconds,
            int? width,
            int? height,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedProcessRunner : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            string workingDirectory,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedObjectStorage : IObjectStorage
    {
        public Task EnsureBucketAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Uri> CreateUploadUrlAsync(string objectKey, string contentType, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Uri> CreateReadUrlAsync(string objectKey, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MultipartUpload> CreateMultipartUploadAsync(string objectKey, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Uri> CreateMultipartPartUploadUrlAsync(string objectKey, string uploadId, int partNumber, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteMultipartUploadAsync(string objectKey, string uploadId, IReadOnlyList<MultipartPart> parts, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortMultipartUploadAsync(string objectKey, string uploadId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DownloadAsync(string objectKey, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UploadAsync(string objectKey, string sourcePath, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteProjectObjectsAsync(ProjectStorageScope scope, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAssetObjectsAsync(AssetStorageScope scope, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
