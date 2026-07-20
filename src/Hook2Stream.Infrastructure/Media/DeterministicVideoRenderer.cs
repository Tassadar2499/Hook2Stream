using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure.Providers;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Media;

public sealed class DeterministicVideoRenderer(
    IObjectStorage storage,
    IProcessRunner processRunner,
    IOptions<MediaToolsOptions> mediaOptions,
    IOptions<PipelineProviderOptions> providerOptions,
    TimeProvider timeProvider) : IVideoRenderer
{
    public async Task<ProviderResult<VideoRenderResult>> RenderAsync(
        VideoRenderRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var invalid = Validate(request);
        if (invalid is not null)
        {
            return Failed(request, startedAt, ProviderFailureKind.UserInput, "render.composition_invalid", invalid);
        }

        var workDirectory = Path.Combine(
            Path.GetTempPath(),
            "hook2stream-render",
            request.Context.OperationId.ToString("N"),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var audioPath = Path.Combine(workDirectory, "audio-source");
        var visualPath = Path.Combine(workDirectory, "visual-source");
        var headlinePath = Path.Combine(workDirectory, "headline.txt");
        var captionPath = Path.Combine(workDirectory, "caption.txt");
        var ctaPath = Path.Combine(workDirectory, "cta.txt");
        var outputPath = Path.Combine(workDirectory, "render.mp4");
        var posterPath = Path.Combine(workDirectory, "poster.jpg");
        var videoKey = $"{request.Context.StagingPrefix}/video.mp4";
        var posterKey = $"{request.Context.StagingPrefix}/poster.jpg";
        var videoUploaded = false;
        var posterUploaded = false;

        try
        {
            var composition = request.Composition;
            var visual = composition.Background ?? composition.Cover;
            await storage.DownloadAsync(composition.Audio.ObjectKey, audioPath, cancellationToken);
            await storage.DownloadAsync(visual.ObjectKey, visualPath, cancellationToken);
            await File.WriteAllTextAsync(headlinePath, composition.Headline, Encoding.UTF8, cancellationToken);
            await File.WriteAllTextAsync(captionPath, composition.Caption, Encoding.UTF8, cancellationToken);
            await File.WriteAllTextAsync(ctaPath, composition.CallToAction, Encoding.UTF8, cancellationToken);

            var arguments = BuildRenderArguments(
                request,
                visual.ContentType,
                visualPath,
                audioPath,
                headlinePath,
                captionPath,
                ctaPath,
                outputPath);
            var render = await processRunner.RunAsync(
                mediaOptions.Value.FfmpegPath,
                arguments,
                TimeSpan.FromSeconds(providerOptions.Value.VideoRendering.TimeoutSeconds),
                workDirectory,
                cancellationToken);
            if (render.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                return Failed(
                    request,
                    startedAt,
                    ProviderFailureKind.Transient,
                    "render.ffmpeg_failed",
                    "The video could not be rendered. Try again.");
            }

            var poster = await processRunner.RunAsync(
                mediaOptions.Value.FfmpegPath,
                [
                    "-y", "-v", "error", "-ss", "0.1", "-i", outputPath,
                    "-map", "0:v:0", "-frames:v", "1", "-q:v", "2", posterPath
                ],
                TimeSpan.FromSeconds(providerOptions.Value.VideoRendering.TimeoutSeconds),
                workDirectory,
                cancellationToken);
            if (poster.ExitCode != 0 || !File.Exists(posterPath) || new FileInfo(posterPath).Length == 0)
            {
                return Failed(
                    request,
                    startedAt,
                    ProviderFailureKind.Transient,
                    "render.poster_failed",
                    "The video poster could not be rendered. Try again.");
            }

            await storage.UploadAsync(videoKey, outputPath, "video/mp4", cancellationToken);
            videoUploaded = true;
            await storage.UploadAsync(posterKey, posterPath, "image/jpeg", cancellationToken);
            posterUploaded = true;

            var duration = request.Composition.DurationMilliseconds;
            var video = await ManifestAsync(
                request,
                "video",
                videoKey,
                outputPath,
                "video/mp4",
                duration,
                request.Profile.Width,
                request.Profile.Height,
                cancellationToken);
            var posterManifest = await ManifestAsync(
                request,
                "poster",
                posterKey,
                posterPath,
                "image/jpeg",
                null,
                request.Profile.Width,
                request.Profile.Height,
                cancellationToken);
            var result = new VideoRenderResult(video, posterManifest, [video, posterManifest]);
            return ProviderResult<VideoRenderResult>.Succeeded(
                result,
                Provenance(request, startedAt, timeProvider.GetUtcNow()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            if (posterUploaded) await TryDeleteObjectAsync(posterKey, cancellationToken);
            if (videoUploaded) await TryDeleteObjectAsync(videoKey, cancellationToken);
            return Failed(
                request,
                startedAt,
                ProviderFailureKind.Transient,
                "render.processing_failed",
                "The video could not be rendered. Try again.");
        }
        finally
        {
            TryDelete(workDirectory);
        }
    }

    internal static IReadOnlyList<string> BuildRenderArguments(
        VideoRenderRequest request,
        string visualContentType,
        string visualPath,
        string audioPath,
        string headlinePath,
        string captionPath,
        string ctaPath,
        string outputPath)
    {
        var composition = request.Composition;
        var profile = request.Profile;
        var durationSeconds = composition.DurationMilliseconds / 1000d;
        var hookStartSeconds = composition.HookStartMilliseconds / 1000d;
        var duration = durationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var hookStart = hookStartSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var videoVisual = visualContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

        var visualFilter = composition.Fit == "fit"
            ? $"scale={profile.Width}:{profile.Height}:force_original_aspect_ratio=decrease," +
              $"pad={profile.Width}:{profile.Height}:(ow-iw)/2:(oh-ih)/2:color=black"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"scale={profile.Width}:{profile.Height}:force_original_aspect_ratio=increase," +
                $"crop={profile.Width}:{profile.Height}:(iw-ow)*{composition.FocalPointX:0.####}:(ih-oh)*{composition.FocalPointY:0.####}");
        if (videoVisual)
        {
            visualFilter += $",fps={profile.FramesPerSecond}";
        }
        else
        {
            visualFilter += string.Create(
                CultureInfo.InvariantCulture,
                $",zoompan=z='min(zoom+0.0007,1.08)':" +
                $"x='(iw-iw/zoom)*{composition.FocalPointX:0.####}':" +
                $"y='(ih-ih/zoom)*{composition.FocalPointY:0.####}':" +
                $"d=1:s={profile.Width}x{profile.Height}:fps={profile.FramesPerSecond}");
        }

        visualFilter += composition.Opening switch
        {
            "fade" => ",fade=t=in:st=0:d=0.6",
            "punch" => ",eq=contrast=1.08:saturation=1.25,fade=t=in:st=0:d=0.12",
            "reveal" => ",fade=t=in:st=0:d=0.28",
            _ => string.Empty
        };

        var headlineSize = Math.Max(30, profile.Width * 58 / 1080);
        var captionSize = Math.Max(20, profile.Width * 34 / 1080);
        var ctaSize = Math.Max(22, profile.Width * 38 / 1080);
        var watermarkSize = Math.Max(20, profile.Width * 36 / 1080);
        var textTop = composition.TextLayout switch
        {
            "lowerThird" => "h*0.61",
            "stacked" => "h*0.42",
            _ => "h*0.34"
        };
        var boxTop = composition.TextLayout switch
        {
            "lowerThird" => "ih*0.58",
            "stacked" => "ih*0.39",
            _ => "ih*0.31"
        };
        var primary = Color(composition.PrimaryColor, "121212");
        var secondary = Color(composition.SecondaryColor, "FFFFFF");
        visualFilter +=
            $",drawbox=x=iw*0.065:y={boxTop}:w=iw*0.87:h=ih*0.28:color=0x{primary}@0.68:t=fill" +
            $",drawtext=font='Sans':expansion=none:textfile='{EscapeFilterPath(headlinePath)}':fontcolor=0x{secondary}:fontsize={headlineSize}:x=w*0.1:y={textTop}" +
            $",drawtext=font='Sans':expansion=none:textfile='{EscapeFilterPath(captionPath)}':fontcolor=white:fontsize={captionSize}:x=w*0.1:y={textTop}+h*0.07" +
            $",drawtext=font='Sans':expansion=none:textfile='{EscapeFilterPath(ctaPath)}':fontcolor=0x{secondary}:fontsize={ctaSize}:x=w*0.1:y={textTop}+h*0.17";
        if (profile.Watermarked)
        {
            visualFilter +=
                $",drawbox=x=0:y=ih-ih*0.065:w=iw:h=ih*0.065:color=black@0.68:t=fill" +
                $",drawtext=font='Sans':text='HOOK2STREAM PREVIEW':fontcolor=white:fontsize={watermarkSize}:x=(w-text_w)/2:y=h-h*0.043";
        }

        visualFilter += $",trim=duration={duration},setpts=PTS-STARTPTS,format=yuv420p[v]";
        var audioFilter =
            $"[1:a]atrim=start={hookStart}:duration={duration},asetpts=PTS-STARTPTS," +
            $"aresample=async=1:first_pts=0,apad=pad_dur={duration},atrim=duration={duration}[a]";
        var filterComplex = $"[0:v]{visualFilter};{audioFilter}";

        var arguments = new List<string> { "-y", "-v", "error" };
        if (videoVisual)
        {
            arguments.AddRange(["-stream_loop", "-1", "-i", visualPath]);
        }
        else
        {
            arguments.AddRange(["-loop", "1", "-framerate", profile.FramesPerSecond.ToString(CultureInfo.InvariantCulture), "-i", visualPath]);
        }

        arguments.AddRange([
            "-i", audioPath,
            "-filter_complex", filterComplex,
            "-map", "[v]", "-map", "[a]",
            "-t", duration,
            "-r", profile.FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-profile:v", "high", "-level:v", "4.1",
            "-preset", "veryfast", "-crf", profile.Watermarked ? "28" : "20",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000",
            "-movflags", "+faststart", outputPath
        ]);
        return arguments;
    }

    private static string? Validate(VideoRenderRequest request)
    {
        var composition = request.Composition;
        var profile = request.Profile;
        if (!Valid(composition.Audio) || !Valid(composition.Cover) ||
            composition.Background is not null && !Valid(composition.Background))
        {
            return "The video composition references an unavailable media asset.";
        }

        if (composition.HookStartMilliseconds < 0 ||
            composition.HookEndMilliseconds <= composition.HookStartMilliseconds ||
            composition.DurationMilliseconds != composition.HookEndMilliseconds - composition.HookStartMilliseconds ||
            composition.DurationMilliseconds is < 1_000 or > 60_000 ||
            profile.Width <= 0 || profile.Height <= 0 || profile.FramesPerSecond is < 1 or > 60 ||
            profile.VideoCodec != "h264" || profile.AudioCodec != "aac")
        {
            return "The video composition has invalid timing or output settings.";
        }

        return null;
    }

    private static bool Valid(ProviderObjectReference source) =>
        source.AssetId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(source.ObjectKey) &&
        source.SizeBytes > 0 &&
        !string.IsNullOrWhiteSpace(source.Sha256) &&
        source.Sha256.Length == 64 &&
        source.Sha256.All(Uri.IsHexDigit);

    private static string Color(string value, string fallback) =>
        value.Length == 7 && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0
            ? value[1..].ToUpperInvariant()
            : fallback;

    private static string EscapeFilterPath(string path) =>
        path.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    private static async Task<ProviderArtifactManifest> ManifestAsync(
        VideoRenderRequest request,
        string role,
        string objectKey,
        string path,
        string contentType,
        long? durationMilliseconds,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var hash = await Sha256Async(path, cancellationToken);
        var idSource = SHA256.HashData(Encoding.UTF8.GetBytes($"{request.Context.OperationId:N}:{role}:{hash}"));
        return new ProviderArtifactManifest(
            new Guid(idSource.AsSpan(0, 16)),
            role,
            objectKey,
            hash,
            contentType,
            new FileInfo(path).Length,
            Materialized: true,
            durationMilliseconds,
            width,
            height);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
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

    private ProviderResult<VideoRenderResult> Failed(
        VideoRenderRequest request,
        DateTimeOffset startedAt,
        ProviderFailureKind kind,
        string code,
        string message) =>
        ProviderResult<VideoRenderResult>.Failed(
            new ProviderFailure(kind, code, message),
            Provenance(request, startedAt, timeProvider.GetUtcNow()));

    private static ProviderProvenance Provenance(
        VideoRenderRequest request,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt) =>
        new(
            "hook2stream.deterministic",
            "ffmpeg-motion-templates",
            "deterministic-render-v1",
            request.Context.OperationId.ToString("N"),
            request.Context.InputHash,
            request.Context.ParameterHash,
            startedAt,
            completedAt);

    private async Task TryDeleteObjectAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            await storage.DeleteAsync(objectKey, cancellationToken);
        }
        catch
        {
            // Staging cleanup is best effort and must not replace the safe provider failure.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Temporary render data is removed by the host lifecycle.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary render data is removed by the host lifecycle.
        }
    }
}
