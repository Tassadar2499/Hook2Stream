using Hook2Stream.Application;

namespace Hook2Stream.UnitTests;

public sealed class BrandKitRulesTests
{
    [Fact]
    public void Valid_brand_kit_passes()
    {
        var request = new UpdateBrandKitRequest(
            "NEЯСЫТЬ",
            "#111827",
            "#F9FAFB",
            "#F97316",
            "Oswald",
            "Inter",
            "Listen now",
            "https://example.com/song",
            "No fake virality claims.",
            false);

        var result = BrandKitRules.Validate(request);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Unsupported_font_insecure_link_and_low_contrast_are_rejected()
    {
        var request = new UpdateBrandKitRequest(
            "Artist",
            "#777777",
            "#7A7A7A",
            "#F97316",
            "DownloadedFont",
            "Inter",
            "Listen",
            "http://example.com",
            null,
            false);

        var result = BrandKitRules.Validate(request).ToDictionary();

        Assert.Contains("headingFont", result.Keys);
        Assert.Contains("smartLink", result.Keys);
        Assert.Contains("primaryColor", result.Keys);
    }
}
