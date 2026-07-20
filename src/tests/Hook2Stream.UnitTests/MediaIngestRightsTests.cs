using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Media;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Worker;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.UnitTests;

public sealed class MediaIngestRightsTests
{
    [Fact]
    public async Task Uploaded_visual_waits_for_visual_rights_before_processing()
    {
        await using var db = Database();
        var asset = Asset(AssetKind.Visual);
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync();
        var processor = new RecordingProcessor();
        var handler = new MediaIngestJobHandler(processor, db);

        var exception = await Assert.ThrowsAsync<JobBlockedException>(
            () => handler.ProcessAsync(Job(asset), default));

        Assert.Equal("rights.visual_required", exception.ReasonCode);
        Assert.False(processor.Called);
    }

    [Fact]
    public async Task Mp3_ingest_is_not_blocked_before_rights_attestation()
    {
        await using var db = Database();
        var asset = Asset(AssetKind.Audio);
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync();
        var processor = new RecordingProcessor();
        var handler = new MediaIngestJobHandler(processor, db);

        await handler.ProcessAsync(Job(asset), default);

        Assert.True(processor.Called);
    }

    [Fact]
    public async Task Validated_mp3_hash_binds_only_the_pending_external_ai_attestation()
    {
        await using var db = Database();
        var asset = Asset(AssetKind.Audio);
        asset.Sha256 = new string('a', 64);
        var rights = new RightsAttestation
        {
            ProjectId = asset.ProjectId,
            ActorSubject = "test",
            PolicyVersion = "external-ai-zdr-v1",
            OwnsAudioRights = true,
            OwnsLyricsRights = true,
            AllowsExternalAiProcessing = true,
            AudioAssetId = asset.Id
        };
        db.AddRange(asset, rights);
        await db.SaveChangesAsync();

        await MediaIngestProcessor.BindPendingExternalAiConsentAsync(db, asset, default);
        await db.SaveChangesAsync();

        Assert.Equal(asset.Sha256, rights.AudioFingerprint);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task Validated_mp3_hash_never_expands_or_rebinds_consent(
        bool allowsExternalAi,
        string? existingFingerprint)
    {
        await using var db = Database();
        var asset = Asset(AssetKind.Audio);
        asset.Sha256 = new string('a', 64);
        var rights = new RightsAttestation
        {
            ProjectId = asset.ProjectId,
            ActorSubject = "test",
            PolicyVersion = "external-ai-zdr-v1",
            OwnsAudioRights = true,
            OwnsLyricsRights = true,
            AllowsExternalAiProcessing = allowsExternalAi,
            AudioAssetId = asset.Id,
            AudioFingerprint = existingFingerprint
        };
        db.AddRange(asset, rights);
        await db.SaveChangesAsync();

        await MediaIngestProcessor.BindPendingExternalAiConsentAsync(db, asset, default);

        Assert.Equal(existingFingerprint, rights.AudioFingerprint);
    }

    private static Hook2StreamDbContext Database() => new(
        new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"media-ingest-rights-{Guid.NewGuid():N}")
            .Options);

    private static MediaAsset Asset(AssetKind kind) => new()
    {
        WorkspaceId = Guid.CreateVersion7(),
        ProjectId = Guid.CreateVersion7(),
        Kind = kind,
        Origin = AssetOrigin.Uploaded,
        State = AssetState.Uploaded,
        OriginalFileName = kind == AssetKind.Audio ? "track.mp3" : "loop.mp4",
        DeclaredContentType = kind == AssetKind.Audio ? "audio/mpeg" : "video/mp4",
        DeclaredBytes = 1024,
        ObjectKey = $"tests/{Guid.CreateVersion7():N}"
    };

    private static LeasedJob Job(MediaAsset asset) => new(
        Guid.CreateVersion7(),
        asset.WorkspaceId,
        null,
        asset.Id,
        JobType.MediaIngest,
        "{}",
        1,
        3,
        "media",
        "v1",
        null,
        1,
        "test-worker",
        DateTimeOffset.UtcNow.AddMinutes(1),
        Guid.CreateVersion7());

    private sealed class RecordingProcessor : IMediaIngestProcessor
    {
        public bool Called { get; private set; }

        public Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }
}
