using Hook2Stream.Application;
using Hook2Stream.Infrastructure.Media;

namespace Hook2Stream.UnitTests;

public sealed class DeterministicMediaProviderTests
{
    [Fact]
    public void PcmAnalyzer_detects_click_track_tempo_and_builds_complete_sections()
    {
        const int sampleRate = 8_000;
        var samples = new short[sampleRate * 20];
        for (var beat = 0; beat < 40; beat++)
        {
            var start = beat * sampleRate / 2;
            for (var index = start; index < Math.Min(samples.Length, start + sampleRate / 40); index++)
            {
                samples[index] = index % 2 == 0 ? short.MaxValue : short.MinValue;
            }
        }

        var result = DeterministicPcmAnalyzer.Analyze(samples, sampleRate);

        Assert.InRange(result.BeatsPerMinute, 115, 125);
        Assert.InRange(result.BeatMilliseconds.Count, 38, 41);
        Assert.Equal(20_000, result.DurationMilliseconds);
        Assert.Equal(5, result.Sections.Count);
        Assert.Equal(0, result.Sections[0].StartMilliseconds);
        Assert.Equal(result.DurationMilliseconds, result.Sections[^1].EndMilliseconds);
        Assert.Equal(result.DurationMilliseconds, result.EnergyCurve[^1].AtMilliseconds);
    }

    [Fact]
    public void PcmAnalyzer_reports_silence_without_inventing_beats()
    {
        var result = DeterministicPcmAnalyzer.Analyze(new short[16_000], 8_000);

        Assert.Equal(0, result.BeatsPerMinute);
        Assert.Empty(result.BeatMilliseconds);
        Assert.All(result.EnergyCurve, point => Assert.Equal(0, point.Energy));
    }

    [Theory]
    [InlineData("image/png", "-loop", true)]
    [InlineData("video/mp4", "-stream_loop", false)]
    public void Renderer_builds_looped_exact_audio_composition(
        string visualContentType,
        string expectedLoopArgument,
        bool expectsZoom)
    {
        var reference = new ProviderObjectReference(
            Guid.NewGuid(),
            "objects/source",
            new string('a', 64),
            "audio/mpeg",
            1_000,
            180_000);
        var visual = reference with
        {
            AssetId = Guid.NewGuid(),
            ObjectKey = "objects/visual",
            ContentType = visualContentType,
            Width = 2_048,
            Height = 2_048
        };
        var request = new VideoRenderRequest(
            new ProviderExecutionContext(Guid.NewGuid(), new string('b', 64), new string('c', 64), "staging/test"),
            new VideoCompositionSpec(
                Guid.NewGuid(),
                "kinetic-lyrics",
                reference,
                visual,
                null,
                "Headline",
                "Caption",
                "#121212",
                "#fffaf2",
                0.5,
                0.5,
                5_000,
                20_000,
                "fill",
                "fade",
                "center",
                "Listen now",
                15_000,
                new string('d', 64)),
            new VideoRenderProfile(540, 960, 30, "h264", "aac", Watermarked: true));

        var arguments = DeterministicVideoRenderer.BuildRenderArguments(
            request,
            visualContentType,
            "/tmp/visual",
            "/tmp/audio",
            "/tmp/headline.txt",
            "/tmp/caption.txt",
            "/tmp/cta.txt",
            "/tmp/output.mp4");
        var argumentList = arguments.ToList();
        var filter = argumentList[argumentList.IndexOf("-filter_complex") + 1];

        Assert.Contains(expectedLoopArgument, arguments);
        Assert.Equal(expectsZoom, filter.Contains("zoompan", StringComparison.Ordinal));
        Assert.Contains("atrim=start=5:duration=15", filter, StringComparison.Ordinal);
        Assert.Contains("apad=pad_dur=15", filter, StringComparison.Ordinal);
        Assert.Contains("HOOK2STREAM PREVIEW", filter, StringComparison.Ordinal);
        Assert.Contains("-c:v", arguments);
        Assert.Contains("libx264", arguments);
        Assert.Contains("aac", arguments);
    }
}
