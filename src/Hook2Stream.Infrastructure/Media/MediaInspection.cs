using System.Globalization;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;

namespace Hook2Stream.Infrastructure.Media;

public sealed record MediaInspection(
    string ContentType,
    long SizeBytes,
    long? DurationMilliseconds,
    int? Width,
    int? Height,
    string? VideoCodec,
    string? AudioCodec,
    string? ArtistName,
    string? TrackTitle);

public sealed class MediaRejectedException(string code, string safeMessage) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
}

public static class MediaMetadataSuggestions
{
    public static void ApplyMp3FirstDraft(
        ReleaseProject project,
        MediaInspection inspection,
        string? fallbackTrackTitle = null)
    {
        if (project.FlowKind != FlowKind.Mp3First || project.SetupCompletedAt is not null) return;
        if (string.IsNullOrWhiteSpace(project.ArtistName) &&
            !string.IsNullOrWhiteSpace(inspection.ArtistName))
        {
            project.ArtistName = inspection.ArtistName;
        }

        if (string.IsNullOrWhiteSpace(project.TrackTitle))
        {
            project.TrackTitle = !string.IsNullOrWhiteSpace(inspection.TrackTitle)
                ? inspection.TrackTitle
                : NormalizeFallback(fallbackTrackTitle);
        }
    }

    private static string NormalizeFallback(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = new string(value
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray())
            .Trim();
        return normalized[..Math.Min(normalized.Length, 160)];
    }
}

public static class MediaInspector
{
    public static async Task<MediaInspection> InspectAsync(
        string path,
        string ffprobePath,
        IProcessRunner processRunner,
        TimeSpan timeout,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var contentType = await DetectContentTypeAsync(path, cancellationToken);
        var result = await processRunner.RunAsync(
            ffprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
            timeout,
            workingDirectory,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new MediaRejectedException(
                "media.probe_failed",
                "The file could not be decoded. Upload an uncorrupted supported media file.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var streams = document.RootElement.TryGetProperty("streams", out var streamsElement)
                ? streamsElement.EnumerateArray().ToArray()
                : [];

            var video = streams.FirstOrDefault(stream =>
                stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video");
            var audio = streams.FirstOrDefault(stream =>
                stream.TryGetProperty("codec_type", out var type) && type.GetString() == "audio");

            var hasFormat = document.RootElement.TryGetProperty("format", out var format);
            long? durationMilliseconds = null;
            if (hasFormat &&
                format.TryGetProperty("duration", out var durationElement) &&
                TryGetDouble(durationElement, out var duration))
            {
                durationMilliseconds = (long)Math.Round(duration * 1000, MidpointRounding.AwayFromZero);
            }

            return new MediaInspection(
                contentType,
                new FileInfo(path).Length,
                durationMilliseconds,
                GetInt32(video, "width"),
                GetInt32(video, "height"),
                GetString(video, "codec_name"),
                GetString(audio, "codec_name"),
                hasFormat ? GetNormalizedTag(format, "artist") : null,
                hasFormat ? GetNormalizedTag(format, "title") : null);
        }
        catch (JsonException)
        {
            throw new MediaRejectedException(
                "media.invalid_probe_output",
                "The file metadata could not be read.");
        }
    }

    private static async Task<string> DetectContentTypeAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[16];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            header.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(header, cancellationToken);

        if (read >= 12 &&
            header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46)
        {
            if (header[8] == 0x57 && header[9] == 0x41 && header[10] == 0x56 && header[11] == 0x45)
            {
                return "audio/wav";
            }

            if (header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
            {
                return "image/webp";
            }
        }

        if (read >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return "image/png";
        }

        if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (read >= 3 &&
            header[0] == 0x49 && header[1] == 0x44 && header[2] == 0x33 ||
            read >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
        {
            return "audio/mpeg";
        }

        if (read >= 12 &&
            header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70)
        {
            return header[8] == 0x71 && header[9] == 0x74
                ? "video/quicktime"
                : "video/mp4";
        }

        if (read >= 4 &&
            header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
        {
            return "video/webm";
        }

        throw new MediaRejectedException(
            "media.magic_bytes_unsupported",
            "The file content does not match a supported media format.");
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value)
            ? value.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : null;

    private static string? GetNormalizedTag(JsonElement format, string name)
    {
        if (!format.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tag = tags.EnumerateObject().FirstOrDefault(value =>
            string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase));
        if (tag.Value.ValueKind != JsonValueKind.String) return null;
        var raw = tag.Value.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = new string(raw
            .Select(value => char.IsControl(value) ? ' ' : value)
            .ToArray())
            .Trim();
        return normalized.Length == 0
            ? null
            : normalized[..Math.Min(normalized.Length, 160)];
    }

    private static bool TryGetDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }

        return double.TryParse(
            element.GetString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }
}
