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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ready_asset_retry_does_not_reprocess_or_regress_the_asset(bool isActive)
    {
        await using var db = Database();
        var asset = Asset(AssetKind.Audio);
        asset.State = AssetState.Ready;
        asset.IsActive = isActive;
        asset.Sha256 = new string('a', 64);
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync();
        var processor = new RecordingProcessor();
        var handler = new MediaIngestJobHandler(processor, db);

        await handler.ProcessAsync(Job(asset), default);

        Assert.False(processor.Called);
        var persisted = await db.MediaAssets.AsNoTracking().SingleAsync(value => value.Id == asset.Id);
        Assert.Equal(AssetState.Ready, persisted.State);
        Assert.Equal(isActive, persisted.IsActive);
        Assert.Equal(asset.Sha256, persisted.Sha256);
    }

    [Fact]
    public async Task Mp3_finalization_reloads_project_after_a_concurrent_setup_edit()
    {
        var options = DatabaseOptions();
        await using var db = new Hook2StreamDbContext(options);
        var project = Project();
        var asset = Asset(AssetKind.Audio);
        asset.ProjectId = project.Id;
        asset.WorkspaceId = project.WorkspaceId;
        var leased = Job(asset, project.Id);
        db.AddRange(project, asset, PersistedJob(leased));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var processor = new ConcurrentSetupProcessor(
            db,
            options,
            project.Id,
            asset.Id);
        var handler = new MediaIngestJobHandler(processor, db);

        await handler.ProcessAsync(leased, default);

        Assert.True(processor.Called);
        db.ChangeTracker.Clear();
        var persistedProject = await db.Projects.SingleAsync(value => value.Id == project.Id);
        var persistedAsset = await db.MediaAssets.SingleAsync(value => value.Id == asset.Id);
        Assert.Equal("Saved concurrently", persistedProject.TrackTitle);
        Assert.Equal(ProjectState.Analyzing, persistedProject.State);
        Assert.Equal(AssetState.Ready, persistedAsset.State);
        Assert.True(persistedAsset.IsActive);
        Assert.Equal(new string('b', 64), persistedAsset.Sha256);
        Assert.Single(await db.OutboxMessages.ToListAsync());
        Assert.Single(await db.ProjectEvents.ToListAsync());

        var downstreamJob = new Job
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Type = JobType.AudioAnalysis,
            State = JobState.Queued,
            PayloadJson = "{}"
        };
        db.Jobs.Add(downstreamJob);
        await db.SaveChangesAsync();

        await handler.ProcessAsync(leased, default);

        db.ChangeTracker.Clear();
        Assert.Equal(1, processor.CallCount);
        Assert.Single(await db.OutboxMessages.ToListAsync());
        Assert.Single(await db.ProjectEvents.ToListAsync());
        Assert.Equal(
            JobState.Queued,
            (await db.Jobs.SingleAsync(value => value.Id == downstreamJob.Id)).State);
    }

    [Fact]
    public async Task Finalizing_ready_cover_reconciles_mp3_first_pipeline_once()
    {
        await using var db = Database();
        var project = Project();
        var cover = Asset(AssetKind.Cover);
        cover.ProjectId = project.Id;
        cover.WorkspaceId = project.WorkspaceId;
        cover.State = AssetState.Ready;
        cover.IsActive = true;
        cover.Sha256 = new string('c', 64);
        var leased = Job(cover, project.Id);
        db.AddRange(project, cover, PersistedJob(leased));
        await db.SaveChangesAsync();
        var processor = new RecordingProcessor();
        var handler = new MediaIngestJobHandler(processor, db);

        await handler.ProcessAsync(leased, default);
        await handler.ProcessAsync(leased, default);

        Assert.False(processor.Called);
        Assert.Single(await db.OutboxMessages.ToListAsync());
        Assert.Single(await db.ProjectEvents.ToListAsync());
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

    private static Hook2StreamDbContext Database() => new(DatabaseOptions());

    private static DbContextOptions<Hook2StreamDbContext> DatabaseOptions() =>
        new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"media-ingest-rights-{Guid.NewGuid():N}")
            .Options;

    private static ReleaseProject Project() => new()
    {
        WorkspaceId = Guid.CreateVersion7(),
        ProjectLabel = "Race test",
        ArtistName = "Artist",
        TrackTitle = "Original",
        FlowKind = FlowKind.Mp3First
    };

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

    private static LeasedJob Job(MediaAsset asset, Guid? projectId = null) => new(
        Guid.CreateVersion7(),
        asset.WorkspaceId,
        projectId,
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

    private static Job PersistedJob(LeasedJob leased) => new()
    {
        Id = leased.Id,
        WorkspaceId = leased.WorkspaceId,
        ProjectId = leased.ProjectId,
        AssetId = leased.AssetId,
        Type = leased.Type,
        State = JobState.Running,
        PayloadJson = leased.PayloadJson,
        AttemptCount = leased.AttemptNumber,
        MaxAttempts = leased.MaxAttempts,
        RequiredCapability = leased.RequiredCapability,
        HandlerVersion = leased.HandlerVersion,
        PayloadSchemaVersion = leased.PayloadSchemaVersion,
        LeaseOwner = leased.LeaseOwner,
        LeaseToken = leased.LeaseToken,
        LeaseExpiresAt = leased.LeaseExpiresAt
    };

    private sealed class RecordingProcessor : IMediaIngestProcessor
    {
        public bool Called { get; private set; }

        public Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    private sealed class ConcurrentSetupProcessor(
        Hook2StreamDbContext db,
        DbContextOptions<Hook2StreamDbContext> options,
        Guid projectId,
        Guid assetId) : IMediaIngestProcessor
    {
        public int CallCount { get; private set; }
        public bool Called => CallCount > 0;

        public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
        {
            CallCount++;
            var asset = await db.MediaAssets.SingleAsync(
                value => value.Id == assetId,
                cancellationToken);
            _ = await db.Projects.SingleAsync(
                value => value.Id == projectId,
                cancellationToken);
            asset.State = AssetState.Ready;
            asset.IsActive = true;
            asset.Sha256 = new string('b', 64);
            await db.SaveChangesAsync(cancellationToken);

            await using var concurrentDb = new Hook2StreamDbContext(options);
            var concurrentlyEdited = await concurrentDb.Projects.SingleAsync(
                value => value.Id == projectId,
                cancellationToken);
            concurrentlyEdited.TrackTitle = "Saved concurrently";
            await concurrentDb.SaveChangesAsync(cancellationToken);
        }
    }
}
