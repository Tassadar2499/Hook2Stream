using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Providers;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Worker;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.UnitTests;

public sealed class PipelineGateTests
{
    private static readonly DateOnly Today = new(2026, 7, 20);

    [Fact]
    public void Pipeline_reconcile_tracks_outbox_and_project_event_in_one_unit_of_work()
    {
        using var db = new Hook2StreamDbContext(
            new DbContextOptionsBuilder<Hook2StreamDbContext>()
                .UseNpgsql("Host=localhost;Database=hook2stream_test;Username=test;Password=test")
                .Options);
        var project = new ReleaseProject
        {
            WorkspaceId = Guid.NewGuid(),
            ProjectLabel = "Release",
            ArtistName = "Artist",
            TrackTitle = "Track"
        };
        var causationId = Guid.NewGuid();

        PipelineOutbox.Reconcile(db, project, "analysis.completed", causationId);

        var outbox = Assert.Single(db.ChangeTracker.Entries<OutboxMessage>()).Entity;
        var projectEvent = Assert.Single(db.ChangeTracker.Entries<ProjectEvent>()).Entity;
        Assert.Equal("pipeline", outbox.Destination);
        Assert.Equal("analysis.completed", projectEvent.EventType);
        Assert.Contains(causationId.ToString(), projectEvent.DataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(2048, 2048, true)]
    [InlineData(1088, 1920, true)]
    [InlineData(1024, 1024, true)]
    [InlineData(2047, 2048, false)]
    [InlineData(4096, 1024, false)]
    [InlineData(512, 512, false)]
    [InlineData(3824, 1280, true)]
    [InlineData(3840, 1280, false)]
    [InlineData(3840, 1264, false)]
    public void OpenRouter_image_dimensions_follow_the_supported_custom_size_envelope(
        int width,
        int height,
        bool expected)
    {
        Assert.Equal(expected, OpenRouterArtworkProvider.HasSupportedDimensions(width, height));
    }

    [Fact]
    public void Instrumental_release_does_not_require_lyrics_rights()
    {
        var (project, audio, rights) = ValidGateInputs();
        project.IsInstrumental = true;
        project.IsInstrumentalConfirmed = true;
        rights.OwnsLyricsRights = false;

        var result = ArtworkAutomationGate.Evaluate(project, audio, rights, Today);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void Vocal_release_cannot_use_instrumental_confirmation_to_bypass_lyrics_rights()
    {
        var (project, audio, rights) = ValidGateInputs();
        project.IsInstrumental = false;
        project.IsInstrumentalConfirmed = true;
        rights.OwnsLyricsRights = false;

        var result = ArtworkAutomationGate.Evaluate(project, audio, rights, Today);

        Assert.False(result.Allowed);
        Assert.Equal("rights.required", result.BlockerCode);
    }

    [Theory]
    [InlineData(ReleaseMode.Unscheduled, 0)]
    [InlineData(ReleaseMode.Upcoming, -1)]
    [InlineData(ReleaseMode.Upcoming, 0)]
    [InlineData(ReleaseMode.Released, 1)]
    public void Unconfirmed_release_timing_blocks_external_artwork(ReleaseMode mode, int dayOffset)
    {
        var (project, audio, rights) = ValidGateInputs();
        project.Mode = mode;
        project.ReleaseDate = Today.AddDays(dayOffset);

        var result = ArtworkAutomationGate.Evaluate(project, audio, rights, Today);

        Assert.False(result.Allowed);
        Assert.Equal("release.schedule_required", result.BlockerCode);
    }

    [Fact]
    public void Release_published_today_is_ready_for_external_artwork()
    {
        var (project, audio, rights) = ValidGateInputs();
        project.Mode = ReleaseMode.Released;
        project.ReleaseDate = Today;

        var result = ArtworkAutomationGate.Evaluate(project, audio, rights, Today);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void Rights_must_be_bound_to_the_active_audio_hash()
    {
        var (project, audio, rights) = ValidGateInputs();
        rights.AudioFingerprint = new string('b', 64);

        var result = ArtworkAutomationGate.Evaluate(project, audio, rights, Today);

        Assert.False(result.Allowed);
        Assert.Equal("rights.required", result.BlockerCode);
    }

    [Fact]
    public void Legacy_artwork_consent_does_not_authorize_external_audio_processing()
    {
        var (project, audio, rights) = ValidGateInputs();
        rights.AllowsExternalAiProcessing = false;
        rights.AllowsExternalAiArtwork = true;

        var result = ArtworkAutomationGate.Evaluate(project, audio, rights, Today);

        Assert.False(result.Allowed);
        Assert.Equal("rights.external_ai_processing_required", result.BlockerCode);
    }

    private static (ReleaseProject Project, MediaAsset Audio, RightsAttestation Rights) ValidGateInputs()
    {
        var project = new ReleaseProject
        {
            WorkspaceId = Guid.NewGuid(),
            ProjectLabel = "Release",
            ArtistName = "Artist",
            TrackTitle = "Track",
            SetupCompletedAt = DateTimeOffset.UtcNow,
            Mode = ReleaseMode.Upcoming,
            ReleaseDate = Today.AddDays(1)
        };
        var audio = new MediaAsset
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Kind = AssetKind.Audio,
            OriginalFileName = "track.mp3",
            DeclaredContentType = "audio/mpeg",
            DeclaredBytes = 1_000,
            ObjectKey = "audio/source",
            Sha256 = new string('a', 64)
        };
        var rights = new RightsAttestation
        {
            ProjectId = project.Id,
            ActorSubject = "test",
            PolicyVersion = "v1",
            OwnsAudioRights = true,
            OwnsLyricsRights = true,
            AllowsExternalAiArtwork = true,
            AllowsExternalAiProcessing = true,
            AudioAssetId = audio.Id,
            AudioFingerprint = audio.Sha256
        };
        return (project, audio, rights);
    }
}
