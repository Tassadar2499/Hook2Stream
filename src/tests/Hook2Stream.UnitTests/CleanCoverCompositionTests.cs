using Hook2Stream.Worker;

namespace Hook2Stream.UnitTests;

public sealed class CleanCoverCompositionTests
{
    [Fact]
    public void Parse_adds_defaults_to_legacy_compositions()
    {
        var composition = CleanCoverComposer.CoverComposition.Parse(
            """{"cropX":0.25,"palette":["#010203","#aabbcc","#DDEEFF"],"showArtist":false}""");

        Assert.Equal(.25, composition.CropX);
        Assert.Equal("0x010203", composition.BackgroundColor);
        Assert.Equal("0xaabbcc", composition.ForegroundColor);
        Assert.Equal("0xddeeff", composition.AccentColor);
        Assert.Equal("Sans", composition.FontFamily);
        Assert.Equal(112, composition.ArtistFontSize);
        Assert.Equal(188, composition.TitleFontSize);
        Assert.Equal(0, composition.TextX);
        Assert.Equal(1, composition.TextY);
        Assert.False(composition.ShowArtist);
        Assert.True(composition.ShowTitle);
    }

    [Fact]
    public void Parse_allowlists_fonts_clamps_controls_and_preserves_palette_slots()
    {
        var composition = CleanCoverComposer.CoverComposition.Parse(
            """
            {
              "cropX": -4,
              "cropY": "not-a-number",
              "cropScale": 7,
              "palette": ["red", "#ABCDEF", "#123456"],
              "fontFamily": "Sans':text='unsafe",
              "artistFontSize": 109.5,
              "titleFontSize": 900,
              "textX": 4,
              "textY": -2,
              "showTitle": "yes"
            }
            """);

        Assert.Equal(0, composition.CropX);
        Assert.Equal(.5, composition.CropY);
        Assert.Equal(2, composition.CropScale);
        Assert.Equal("0x121212", composition.BackgroundColor);
        Assert.Equal("0xabcdef", composition.ForegroundColor);
        Assert.Equal("0x123456", composition.AccentColor);
        Assert.Equal("Sans", composition.FontFamily);
        Assert.Equal(110, composition.ArtistFontSize);
        Assert.Equal(360, composition.TitleFontSize);
        Assert.Equal(1, composition.TextX);
        Assert.Equal(0, composition.TextY);
        Assert.True(composition.ShowTitle);
    }

    [Theory]
    [InlineData("serif", "Serif")]
    [InlineData("SERIF", "Serif")]
    [InlineData("monospace", "Monospace")]
    [InlineData("sans", "Sans")]
    public void Parse_maps_only_supported_font_families(string input, string expected)
    {
        var composition = CleanCoverComposer.CoverComposition.Parse(
            $$"""{"fontFamily":"{{input}}"}""");

        Assert.Equal(expected, composition.FontFamily);
    }

    [Fact]
    public void Typography_filters_honor_validated_controls_and_safe_layout()
    {
        var composition = CleanCoverComposer.CoverComposition.Parse(
            """
            {
              "palette": ["#102030", "#f0e0d0", "#a0b0c0"],
              "fontFamily": "monospace",
              "artistFontSize": 100,
              "titleFontSize": 200,
              "textX": 0.75,
              "textY": 0.25
            }
            """);

        var filters = CleanCoverComposer.BuildTypographyFilters(
            composition,
            "/tmp/artist:name's.txt",
            "/tmp/title.txt");

        Assert.Equal(3, filters.Count);
        Assert.Equal("drawbox=x=0:y=693:w=iw:h=468:color=0x102030@0.58:t=fill", filters[0]);
        Assert.Contains(
            "drawtext=font='Monospace':textfile='/tmp/artist\\:name\\'s.txt':fontcolor=0xa0b0c0:" +
            "fontsize=100:x=180+(w-text_w-360)*0.75:y=753:fix_bounds=1",
            filters);
        Assert.Contains(
            "drawtext=font='Monospace':textfile='/tmp/title.txt':fontcolor=0xf0e0d0:" +
            "fontsize=200:x=180+(w-text_w-360)*0.75:y=901:fix_bounds=1",
            filters);
    }

    [Fact]
    public void Typography_filters_are_omitted_when_both_text_layers_are_hidden()
    {
        var composition = CleanCoverComposer.CoverComposition.Parse(
            """{"showArtist":false,"showTitle":false}""");

        Assert.Empty(CleanCoverComposer.BuildTypographyFilters(composition, "artist", "title"));
    }
}
