using Hook2Stream.Application;
using Hook2Stream.Domain;

namespace Hook2Stream.UnitTests;

public sealed class ReleaseRulesTests
{
    private static readonly DateOnly Today = new(2026, 7, 16);

    [Fact]
    public void Upcoming_release_requires_a_future_date()
    {
        var request = CreateRequest(
            ReleaseMode.Upcoming,
            releaseDate: Today,
            campaignStartDate: null);

        var errors = ReleaseRules.Validate(request, Today).ToDictionary();

        Assert.Contains("releaseDate", errors.Keys);
    }

    [Fact]
    public void Released_track_requires_campaign_start()
    {
        var request = CreateRequest(
            ReleaseMode.Released,
            releaseDate: Today.AddDays(-2),
            campaignStartDate: null);

        var errors = ReleaseRules.Validate(request, Today).ToDictionary();

        Assert.Contains("campaignStartDate", errors.Keys);
    }

    [Fact]
    public void Instrumental_release_does_not_accept_lyrics()
    {
        var request = CreateRequest(
            ReleaseMode.Upcoming,
            Today.AddDays(10),
            null) with
        {
            IsInstrumental = true,
            LyricsText = "Invented lyrics"
        };

        var errors = ReleaseRules.Validate(request, Today).ToDictionary();

        Assert.Contains("lyricsText", errors.Keys);
    }

    [Fact]
    public void Archive_and_restore_preserve_the_previous_workflow_state()
    {
        var project = new ReleaseProject
        {
            WorkspaceId = Guid.CreateVersion7(),
            ProjectLabel = "Release 01",
            ArtistName = "Artist",
            TrackTitle = "Track",
            State = ProjectState.PreviewReady
        };

        project.Archive();
        Assert.True(project.IsArchived);
        Assert.Equal(ProjectState.Archived, project.State);
        Assert.Equal(ProjectState.PreviewReady, project.StateBeforeArchive);

        project.Restore();
        Assert.False(project.IsArchived);
        Assert.Equal(ProjectState.PreviewReady, project.State);
        Assert.Null(project.StateBeforeArchive);
    }

    private static CreateReleaseRequest CreateRequest(
        ReleaseMode mode,
        DateOnly? releaseDate,
        DateOnly? campaignStartDate) =>
        new(
            "Release 01",
            "Artist",
            "Track",
            "en",
            null,
            "Real lyrics",
            false,
            mode,
            releaseDate,
            campaignStartDate);
}
