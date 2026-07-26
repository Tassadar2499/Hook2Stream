using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Pipeline;

public sealed record PromotedArtifact(
    string ObjectKey,
    string ContentType,
    long SizeBytes,
    string Sha256,
    long? DurationMilliseconds,
    int? Width,
    int? Height);

public interface IPipelineArtifactStore
{
    Task<PromotedArtifact> PromoteAsync(
        ProviderArtifactManifest manifest,
        string canonicalObjectKey,
        CancellationToken cancellationToken);

    Task<PromotedArtifact> StoreLocalAsync(
        string sourcePath,
        string canonicalObjectKey,
        string contentType,
        long? durationMilliseconds,
        int? width,
        int? height,
        CancellationToken cancellationToken);
}

public sealed class PipelineArtifactStore(
    IObjectStorage storage,
    IProcessRunner processRunner,
    IOptions<MediaToolsOptions> mediaOptions,
    ILogger<PipelineArtifactStore>? logger = null) : IPipelineArtifactStore
{
    private static readonly EventId StagingCleanupFailedEvent =
        new(1001, "PipelineArtifactStagingCleanupFailed");

    public async Task<PromotedArtifact> PromoteAsync(
        ProviderArtifactManifest manifest,
        string canonicalObjectKey,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(canonicalObjectKey);
        var workDirectory = Path.Combine(Path.GetTempPath(), "hook2stream-artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var localPath = Path.Combine(workDirectory, "artifact");

        try
        {
            if (manifest.Materialized)
            {
                var staged = await storage.HeadAsync(manifest.ObjectKey, cancellationToken)
                    ?? throw new PipelineArtifactException(
                        "artifact.staging_missing",
                        "The provider result was not found in staging.");
                if (staged.SizeBytes != manifest.SizeBytes)
                {
                    throw new PipelineArtifactException(
                        "artifact.size_mismatch",
                        "The staged provider result did not match its manifest.");
                }

                await storage.DownloadAsync(manifest.ObjectKey, localPath, cancellationToken);
                var actualHash = await ComputeSha256Async(localPath, cancellationToken);
                if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PipelineArtifactException(
                        "artifact.hash_mismatch",
                        "The staged provider result did not match its manifest.");
                }
            }
            else if (manifest.ContentType == "image/png" && manifest.Width is > 0 && manifest.Height is > 0)
            {
                await FixturePng.WriteAsync(
                    localPath,
                    manifest.Width.Value,
                    manifest.Height.Value,
                    manifest.Sha256,
                    cancellationToken);
            }
            else
            {
                throw new PipelineArtifactException(
                    "artifact.not_materialized",
                    "The provider returned metadata without a materialized artifact.");
            }

            ValidateSignature(localPath, manifest.ContentType);
            var result = await StoreLocalAsync(
                localPath,
                canonicalObjectKey,
                manifest.ContentType,
                manifest.DurationMilliseconds,
                manifest.Width,
                manifest.Height,
                cancellationToken);
            if (manifest.Materialized)
            {
                try
                {
                    await storage.DeleteAsync(manifest.ObjectKey, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    logger?.LogWarning(
                        StagingCleanupFailedEvent,
                        "Provider staging cleanup failed after canonical artifact promotion; cleanup remains best effort.");
                }
            }

            return result;
        }
        finally
        {
            TryDelete(workDirectory);
        }
    }

    public async Task<PromotedArtifact> StoreLocalAsync(
        string sourcePath,
        string canonicalObjectKey,
        string contentType,
        long? durationMilliseconds,
        int? width,
        int? height,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(canonicalObjectKey);
        ValidateSignature(sourcePath, contentType);
        var file = new FileInfo(sourcePath);
        if (!file.Exists || file.Length == 0)
        {
            throw new PipelineArtifactException("artifact.empty", "The generated artifact was empty.");
        }

        if (contentType == "video/mp4")
        {
            await ValidateVideoAsync(
                sourcePath,
                durationMilliseconds,
                width,
                height,
                cancellationToken);
        }
        else if (contentType is "image/png" or "image/jpeg")
        {
            await ValidateImageAsync(sourcePath, width, height, cancellationToken);
        }

        var hash = await ComputeSha256Async(sourcePath, cancellationToken);
        var existing = await storage.HeadAsync(canonicalObjectKey, cancellationToken);
        if (existing is null)
        {
            await storage.UploadAsync(canonicalObjectKey, sourcePath, contentType, cancellationToken);
        }
        else
        {
            var workDirectory = Path.Combine(Path.GetTempPath(), "hook2stream-artifacts", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            var existingPath = Path.Combine(workDirectory, "existing");
            try
            {
                await storage.DownloadAsync(canonicalObjectKey, existingPath, cancellationToken);
                var existingHash = await ComputeSha256Async(existingPath, cancellationToken);
                if (!string.Equals(existingHash, hash, StringComparison.Ordinal))
                {
                    throw new PipelineArtifactException(
                        "artifact.canonical_conflict",
                        "An immutable artifact already exists at the canonical key.");
                }
            }
            finally
            {
                TryDelete(workDirectory);
            }
        }

        var committed = await storage.HeadAsync(canonicalObjectKey, cancellationToken)
            ?? throw new PipelineArtifactException(
                "artifact.promotion_failed",
                "The generated artifact could not be committed.");
        if (committed.SizeBytes != file.Length)
        {
            throw new PipelineArtifactException(
                "artifact.promotion_size_mismatch",
                "The committed artifact did not match the generated file.");
        }

        return new PromotedArtifact(
            canonicalObjectKey,
            contentType,
            file.Length,
            hash,
            durationMilliseconds,
            width,
            height);
    }

    private async Task ValidateVideoAsync(
        string path,
        long? expectedDurationMilliseconds,
        int? expectedWidth,
        int? expectedHeight,
        CancellationToken cancellationToken)
    {
        var workDirectory = Path.GetDirectoryName(path) ?? Path.GetTempPath();
        var probe = await processRunner.RunAsync(
            mediaOptions.Value.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
            TimeSpan.FromSeconds(mediaOptions.Value.ProcessTimeoutSeconds),
            workDirectory,
            cancellationToken);
        if (probe.ExitCode != 0)
        {
            throw new PipelineArtifactException(
                "artifact.video_probe_failed",
                "The rendered video could not be decoded.");
        }

        try
        {
            using var document = JsonDocument.Parse(probe.StandardOutput);
            var streams = document.RootElement.TryGetProperty("streams", out var streamNode) &&
                          streamNode.ValueKind == JsonValueKind.Array
                ? streamNode.EnumerateArray().ToArray()
                : [];
            var video = streams.FirstOrDefault(value => String(value, "codec_type") == "video");
            var audio = streams.FirstOrDefault(value => String(value, "codec_type") == "audio");
            var width = Integer(video, "width");
            var height = Integer(video, "height");
            if (video.ValueKind != JsonValueKind.Object || audio.ValueKind != JsonValueKind.Object ||
                String(video, "codec_name") != "h264" || String(audio, "codec_name") != "aac" ||
                width is null or <= 0 || height is null or <= 0 ||
                width > 3840 || height > 3840 ||
                expectedWidth is not null && width != expectedWidth ||
                expectedHeight is not null && height != expectedHeight)
            {
                throw InvalidVideo();
            }

            var formatDuration = document.RootElement.TryGetProperty("format", out var format)
                ? Seconds(format, "duration")
                : null;
            var videoDuration = Seconds(video, "duration") ?? formatDuration;
            var audioDuration = Seconds(audio, "duration") ?? formatDuration;
            if (formatDuration is null or <= 0 || videoDuration is null or <= 0 || audioDuration is null or <= 0 ||
                Math.Abs(videoDuration.Value - audioDuration.Value) > 1.0 ||
                expectedDurationMilliseconds is { } expected &&
                Math.Abs(formatDuration.Value * 1000 - expected) > 1_500)
            {
                throw new PipelineArtifactException(
                    "artifact.video_timing_invalid",
                    "The rendered video duration or audio synchronization is invalid.");
            }
        }
        catch (JsonException)
        {
            throw InvalidVideo();
        }
    }

    private async Task ValidateImageAsync(
        string path,
        int? expectedWidth,
        int? expectedHeight,
        CancellationToken cancellationToken)
    {
        var workDirectory = Path.GetDirectoryName(path) ?? Path.GetTempPath();
        var probe = await processRunner.RunAsync(
            mediaOptions.Value.FfprobePath,
            [
                "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", path
            ],
            TimeSpan.FromSeconds(mediaOptions.Value.ProcessTimeoutSeconds),
            workDirectory,
            cancellationToken);
        var expected = expectedWidth is > 0 && expectedHeight is > 0
            ? $"{expectedWidth}x{expectedHeight}"
            : null;
        if (probe.ExitCode != 0 || string.IsNullOrWhiteSpace(probe.StandardOutput) ||
            expected is not null && !string.Equals(probe.StandardOutput.Trim(), expected, StringComparison.Ordinal))
        {
            throw new PipelineArtifactException(
                "artifact.image_dimensions_invalid",
                "The generated image dimensions do not match the requested profile.");
        }
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Integer(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : null;

    private static double? Seconds(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numeric)) return numeric;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static PipelineArtifactException InvalidVideo() => new(
        "artifact.video_profile_invalid",
        "The rendered video does not match the required H.264/AAC profile and dimensions.");

    private static void ValidateObjectKey(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey) ||
            objectKey.StartsWith('/') ||
            objectKey.Contains("..", StringComparison.Ordinal) ||
            objectKey.Contains('\\'))
        {
            throw new PipelineArtifactException(
                "artifact.object_key_invalid",
                "The generated artifact key was invalid.");
        }
    }

    private static void ValidateSignature(string path, string contentType)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header);
        var valid = contentType switch
        {
            "image/png" => read >= 8 && header[..8].SequenceEqual(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "image/jpeg" => read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            "video/mp4" => read >= 8 && header[4] == (byte)'f' && header[5] == (byte)'t' &&
                           header[6] == (byte)'y' && header[7] == (byte)'p',
            "application/zip" => read >= 4 && header[0] == (byte)'P' && header[1] == (byte)'K',
            _ => false
        };
        if (!valid)
        {
            throw new PipelineArtifactException(
                "artifact.content_invalid",
                "The generated artifact content did not match its declared type.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Staging cleanup remains best effort; canonical data is immutable.
        }
        catch (UnauthorizedAccessException)
        {
            // The container lifecycle can remove an inaccessible temporary directory.
        }
    }
}

public sealed class PipelineArtifactException(string code, string safeMessage) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
}

internal static class FixturePng
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static async Task WriteAsync(
        string path,
        int width,
        int height,
        string seed,
        CancellationToken cancellationToken)
    {
        if (width is < 1 or > 4_096 || height is < 1 or > 4_096)
        {
            throw new PipelineArtifactException(
                "artifact.fixture_dimensions_invalid",
                "Fixture image dimensions are outside the supported range.");
        }

        var color = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await output.WriteAsync(Signature, cancellationToken);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        await WriteChunkAsync(output, "IHDR", ihdr, cancellationToken);

        await using var compressed = new MemoryStream();
        await using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var x = 0; x < width; x++)
            {
                row[1 + x * 3] = color[0];
                row[2 + x * 3] = color[1];
                row[3 + x * 3] = color[2];
            }

            for (var y = 0; y < height; y++)
            {
                await zlib.WriteAsync(row, cancellationToken);
            }
        }

        await WriteChunkAsync(output, "IDAT", compressed.ToArray(), cancellationToken);
        await WriteChunkAsync(output, "IEND", [], cancellationToken);
    }

    private static async Task WriteChunkAsync(
        Stream output,
        string type,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        await output.WriteAsync(length, cancellationToken);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        await output.WriteAsync(typeBytes, cancellationToken);
        await output.WriteAsync(data, cancellationToken);

        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, Crc32(crcInput));
        await output.WriteAsync(crcBytes, cancellationToken);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }
}
