using Hook2Stream.Application;
using Hook2Stream.Infrastructure.Media;
using Hook2Stream.Domain;

namespace Hook2Stream.UnitTests;

public sealed class MediaInspectionTests
{
    [Fact]
    public async Task Mp3_id3_artist_and_title_are_normalized_from_ffprobe_tags()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hook2stream-media-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "track.mp3");
        try
        {
            await File.WriteAllBytesAsync(path, [0x49, 0x44, 0x33, 0, 0, 0, 0, 0, 0, 0]);
            var runner = new FixtureProbeRunner(
                """
                {
                  "streams": [{ "codec_type": "audio", "codec_name": "mp3" }],
                  "format": {
                    "duration": "181.25",
                    "tags": { "ARTIST": "  Example\u0000 Artist  ", "Title": "  The Track  " }
                  }
                }
                """);

            var result = await MediaInspector.InspectAsync(
                path,
                "ffprobe",
                runner,
                TimeSpan.FromSeconds(1),
                directory,
                CancellationToken.None);

            Assert.Equal("audio/mpeg", result.ContentType);
            Assert.Equal("Example  Artist", result.ArtistName);
            Assert.Equal("The Track", result.TrackTitle);
            Assert.Equal(181_250, result.DurationMilliseconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Id3_suggestions_only_fill_blank_unconfirmed_mp3_first_fields()
    {
        var inspection = new MediaInspection(
            "audio/mpeg", 10, 1_000, null, null, null, "mp3", "ID3 Artist", "ID3 Title");
        var draft = Project(string.Empty, string.Empty, setupCompleted: false);
        var edited = Project("Typed Artist", "Typed Title", setupCompleted: false);
        var confirmed = Project(string.Empty, string.Empty, setupCompleted: true);

        MediaMetadataSuggestions.ApplyMp3FirstDraft(draft, inspection, "filename fallback");
        MediaMetadataSuggestions.ApplyMp3FirstDraft(edited, inspection, "filename fallback");
        MediaMetadataSuggestions.ApplyMp3FirstDraft(confirmed, inspection, "filename fallback");

        Assert.Equal("ID3 Artist", draft.ArtistName);
        Assert.Equal("ID3 Title", draft.TrackTitle);
        Assert.Equal("Typed Artist", edited.ArtistName);
        Assert.Equal("Typed Title", edited.TrackTitle);
        Assert.Empty(confirmed.ArtistName);
        Assert.Empty(confirmed.TrackTitle);

        var withoutTitleTag = inspection with { TrackTitle = null };
        var fallback = Project(string.Empty, string.Empty, setupCompleted: false);
        MediaMetadataSuggestions.ApplyMp3FirstDraft(fallback, withoutTitleTag, "file-name");
        Assert.Equal("file-name", fallback.TrackTitle);
    }

    private static ReleaseProject Project(string artist, string title, bool setupCompleted) => new()
    {
        WorkspaceId = Guid.NewGuid(),
        ProjectLabel = "Draft",
        ArtistName = artist,
        TrackTitle = title,
        FlowKind = FlowKind.Mp3First,
        SetupCompletedAt = setupCompleted ? DateTimeOffset.UtcNow : null
    };

    private sealed class FixtureProbeRunner(string json) : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessExecutionResult(0, json, string.Empty, TimeSpan.Zero));
    }
}
