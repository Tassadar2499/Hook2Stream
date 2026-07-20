using Hook2Stream.Application;
using Hook2Stream.Domain;

namespace Hook2Stream.UnitTests;

public sealed class Mp3FirstRulesTests
{
    private static readonly DateOnly Today = new(2026, 7, 20);

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("RU")]
    public void Setup_accepts_languages_with_an_automatic_quality_baseline(string language)
    {
        var errors = ReleaseRules.ValidateSetup(Setup(language), Today);

        Assert.True(errors.IsValid);
    }

    [Fact]
    public void Setup_rejects_language_without_an_automatic_quality_baseline()
    {
        var errors = ReleaseRules.ValidateSetup(Setup("es"), Today).ToDictionary();

        Assert.Contains("language", errors.Keys);
    }

    [Fact]
    public void Instrumental_mode_requires_explicit_user_confirmation()
    {
        var request = Setup("en") with
        {
            IsInstrumental = true,
            IsInstrumentalConfirmed = false
        };

        var errors = ReleaseRules.ValidateSetup(request, Today).ToDictionary();

        Assert.Contains("isInstrumentalConfirmed", errors.Keys);
    }

    [Fact]
    public void Unscheduled_setup_does_not_accept_release_dates()
    {
        var request = Setup("en") with { ReleaseDate = Today.AddDays(10) };

        var errors = ReleaseRules.ValidateSetup(request, Today).ToDictionary();

        Assert.Contains("releaseDate", errors.Keys);
    }

    private static SetupReleaseRequest Setup(string language) => new(
        "Release 01",
        "Artist",
        "Track",
        language,
        ReleaseMode.Unscheduled,
        null,
        null,
        false,
        false,
        null);
}
