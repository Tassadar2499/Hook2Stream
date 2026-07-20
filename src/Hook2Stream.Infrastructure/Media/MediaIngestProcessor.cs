using System.Security.Cryptography;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Media;

public sealed class MediaIngestProcessor(
    Hook2StreamDbContext dbContext,
    IObjectStorage objectStorage,
    IJobQueue jobQueue,
    IProcessRunner processRunner,
    IOptions<MediaToolsOptions> options) : IMediaIngestProcessor
{
    private readonly MediaToolsOptions _options = options.Value;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        if (job.AssetId is null)
        {
            throw new MediaRejectedException("job.asset_missing", "The ingest job has no media asset.");
        }

        var asset = await dbContext.MediaAssets
            .Include(value => value.Derivatives)
            .SingleOrDefaultAsync(value => value.Id == job.AssetId, cancellationToken)
            ?? throw new MediaRejectedException("asset.not_found", "The media asset no longer exists.");

        var workRoot = string.IsNullOrWhiteSpace(_options.WorkRoot)
            ? Path.Combine(Path.GetTempPath(), "hook2stream-media")
            : _options.WorkRoot;
        var workDirectory = Path.Combine(workRoot, $"{job.Id:N}-{job.AttemptNumber}");

        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, recursive: true);
        }

        Directory.CreateDirectory(workDirectory);
        var originalPath = Path.Combine(workDirectory, "original");

        try
        {
            asset.State = AssetState.Processing;
            await JobLeaseFence.CommitAsync(dbContext, job, cancellationToken);
            await Heartbeat(job, 5, "downloading", cancellationToken);

            await objectStorage.DownloadAsync(asset.ObjectKey, originalPath, cancellationToken);
            await Heartbeat(job, 20, "validating", cancellationToken);

            var inspection = await MediaInspector.InspectAsync(
                originalPath,
                _options.FfprobePath,
                processRunner,
                TimeSpan.FromSeconds(_options.ProcessTimeoutSeconds),
                workDirectory,
                cancellationToken);
            Validate(asset, inspection);

            asset.ActualBytes = inspection.SizeBytes;
            asset.DetectedContentType = inspection.ContentType;
            asset.DurationMilliseconds = inspection.DurationMilliseconds;
            asset.Width = inspection.Width;
            asset.Height = inspection.Height;
            asset.VideoCodec = inspection.VideoCodec;
            asset.AudioCodec = inspection.AudioCodec;
            asset.Sha256 = await ComputeSha256Async(originalPath, cancellationToken);
            if (asset.Kind == AssetKind.Audio)
            {
                await BindPendingExternalAiConsentAsync(dbContext, asset, cancellationToken);
                await ApplyMp3FirstMetadataSuggestionsAsync(asset, inspection, cancellationToken);
            }

            await Heartbeat(job, 35, "normalizing", cancellationToken);
            var outputs = await CreateDerivativesAsync(asset, originalPath, workDirectory, cancellationToken);

            var progress = 55;
            foreach (var output in outputs)
            {
                await objectStorage.UploadAsync(
                    output.ObjectKey,
                    output.LocalPath,
                    output.ContentType,
                    cancellationToken);
                progress += Math.Max(1, 35 / outputs.Count);
                await Heartbeat(job, Math.Min(progress, 90), "uploading_derivatives", cancellationToken);
            }

            foreach (var derivative in asset.Derivatives)
            {
                derivative.DeletedAt = DateTimeOffset.UtcNow;
            }

            foreach (var output in outputs)
            {
                var fileInfo = new FileInfo(output.LocalPath);
                dbContext.MediaDerivatives.Add(new MediaDerivative
                {
                    AssetId = asset.Id,
                    Kind = output.Kind,
                    ProcessorVersion = _options.ProcessorVersion,
                    ObjectKey = output.ObjectKey,
                    ContentType = output.ContentType,
                    Bytes = fileInfo.Length,
                    Sha256 = await ComputeSha256Async(output.LocalPath, cancellationToken),
                    DurationMilliseconds = output.DurationMilliseconds,
                    Width = output.Width,
                    Height = output.Height
                });
            }

            await ActivateRevisionAsync(asset, cancellationToken);
            asset.State = AssetState.Ready;
            asset.IsActive = true;
            asset.FailureCode = null;
            asset.FailureMessage = null;

            dbContext.AuditEvents.Add(new AuditEvent
            {
                WorkspaceId = asset.WorkspaceId,
                Action = "asset.ingested",
                ResourceType = "media_asset",
                ResourceId = asset.Id,
                DataJson = JsonSerializer.Serialize(new
                {
                    asset.Kind,
                    asset.Revision,
                    asset.DetectedContentType,
                    asset.ActualBytes,
                    derivativeCount = outputs.Count
                })
            });
            await JobLeaseFence.CommitAsync(dbContext, job, cancellationToken);
            await Heartbeat(job, 98, "finalizing", cancellationToken);
        }
        catch (MediaRejectedException exception)
        {
            asset.State = AssetState.Rejected;
            asset.IsActive = false;
            asset.FailureCode = exception.Code;
            asset.FailureMessage = exception.SafeMessage;
            await JobLeaseFence.CommitAsync(dbContext, job, cancellationToken);
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDirectory))
                {
                    Directory.Delete(workDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Cleanup is retried by the host/container lifecycle.
            }
        }
    }

    private async Task<List<DerivativeOutput>> CreateDerivativesAsync(
        MediaAsset asset,
        string originalPath,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        return asset.Kind switch
        {
            AssetKind.Audio => await CreateAudioDerivativesAsync(asset, originalPath, workDirectory, cancellationToken),
            AssetKind.Cover or AssetKind.BrandCharacter =>
                await CreateImageDerivativesAsync(asset, originalPath, workDirectory, cancellationToken),
            AssetKind.Visual when MediaPolicy.IsImageContentType(asset.DetectedContentType!) =>
                await CreateImageDerivativesAsync(asset, originalPath, workDirectory, cancellationToken),
            AssetKind.Visual =>
                await CreateVideoDerivativesAsync(asset, originalPath, workDirectory, cancellationToken),
            _ => throw new MediaRejectedException("asset.kind_unsupported", "The asset role is not supported.")
        };
    }

    private async Task<List<DerivativeOutput>> CreateAudioDerivativesAsync(
        MediaAsset asset,
        string input,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var analysis = Path.Combine(workingDirectory, "analysis.wav");
        var preview = Path.Combine(workingDirectory, "preview.m4a");

        await RunFfmpegAsync(
            ["-y", "-v", "error", "-i", input, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", analysis],
            workingDirectory,
            cancellationToken);
        await RunFfmpegAsync(
            ["-y", "-v", "error", "-i", input, "-vn", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", preview],
            workingDirectory,
            cancellationToken);

        return
        [
            Output(asset, DerivativeKind.AudioAnalysisWave, analysis, "audio/wav", asset.DurationMilliseconds),
            Output(asset, DerivativeKind.AudioPreview, preview, "audio/mp4", asset.DurationMilliseconds)
        ];
    }

    private async Task<List<DerivativeOutput>> CreateImageDerivativesAsync(
        MediaAsset asset,
        string input,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var proxy = Path.Combine(workingDirectory, "proxy.webp");
        var thumbnail = Path.Combine(workingDirectory, "thumbnail.webp");

        await RunFfmpegAsync(
            [
                "-y", "-v", "error", "-i", input,
                "-vf", "scale=w='min(2048,iw)':h=-2",
                "-frames:v", "1", "-c:v", "libwebp", "-q:v", "80", proxy
            ],
            workingDirectory,
            cancellationToken);
        await RunFfmpegAsync(
            [
                "-y", "-v", "error", "-i", input,
                "-vf", "scale=w='min(512,iw)':h=-2",
                "-frames:v", "1", "-c:v", "libwebp", "-q:v", "75", thumbnail
            ],
            workingDirectory,
            cancellationToken);

        return
        [
            Output(asset, DerivativeKind.ImageProxy, proxy, "image/webp"),
            Output(asset, DerivativeKind.Thumbnail, thumbnail, "image/webp")
        ];
    }

    private async Task<List<DerivativeOutput>> CreateVideoDerivativesAsync(
        MediaAsset asset,
        string input,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var proxy = Path.Combine(workingDirectory, "proxy.mp4");
        var thumbnail = Path.Combine(workingDirectory, "thumbnail.webp");

        await RunFfmpegAsync(
            [
                "-y", "-v", "error", "-i", input, "-an",
                "-vf", "scale=w='min(1080,iw)':h=-2:force_original_aspect_ratio=decrease,fps=30,format=yuv420p",
                "-c:v", "libx264", "-profile:v", "high", "-level:v", "4.1",
                "-preset", "medium", "-crf", "21", "-movflags", "+faststart", proxy
            ],
            workingDirectory,
            cancellationToken);
        await RunFfmpegAsync(
            [
                "-y", "-v", "error", "-i", input, "-frames:v", "1",
                "-vf", "scale=w='min(512,iw)':h=-2",
                "-c:v", "libwebp", "-q:v", "75", thumbnail
            ],
            workingDirectory,
            cancellationToken);

        return
        [
            Output(asset, DerivativeKind.VideoProxy, proxy, "video/mp4", asset.DurationMilliseconds),
            Output(asset, DerivativeKind.Thumbnail, thumbnail, "image/webp")
        ];
    }

    private async Task RunFfmpegAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            _options.FfmpegPath,
            arguments,
            TimeSpan.FromSeconds(_options.ProcessTimeoutSeconds),
            workingDirectory,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg exited with code {result.ExitCode}: {Truncate(result.StandardError, 500)}");
        }
    }

    private async Task ActivateRevisionAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        IQueryable<MediaAsset> previousAssets = dbContext.MediaAssets.Where(value =>
            value.ProjectId == asset.ProjectId &&
            value.Id != asset.Id &&
            value.IsActive);

        if (asset.Kind == AssetKind.Visual)
        {
            previousAssets = asset.SupersedesAssetId is null
                ? previousAssets.Where(_ => false)
                : previousAssets.Where(value => value.Id == asset.SupersedesAssetId);
        }
        else
        {
            previousAssets = previousAssets.Where(value => value.Kind == asset.Kind);
        }

        foreach (var previous in await previousAssets.ToListAsync(cancellationToken))
        {
            previous.IsActive = false;
        }
    }

    private async Task ApplyMp3FirstMetadataSuggestionsAsync(
        MediaAsset asset,
        MediaInspection inspection,
        CancellationToken cancellationToken)
    {
        var project = await dbContext.Projects.SingleOrDefaultAsync(
            value => value.Id == asset.ProjectId && value.WorkspaceId == asset.WorkspaceId,
            cancellationToken);
        if (project is not null)
        {
            MediaMetadataSuggestions.ApplyMp3FirstDraft(
                project,
                inspection,
                Path.GetFileNameWithoutExtension(asset.OriginalFileName));
        }
    }

    internal static async Task BindPendingExternalAiConsentAsync(
        Hook2StreamDbContext dbContext,
        MediaAsset asset,
        CancellationToken cancellationToken)
    {
        if (asset.Kind != AssetKind.Audio || string.IsNullOrWhiteSpace(asset.Sha256)) return;
        var attestation = await dbContext.RightsAttestations.SingleOrDefaultAsync(
            value => value.ProjectId == asset.ProjectId,
            cancellationToken);
        if (attestation is null ||
            attestation.AudioAssetId != asset.Id ||
            !attestation.AllowsExternalAiProcessing ||
            !string.IsNullOrWhiteSpace(attestation.AudioFingerprint))
        {
            return;
        }

        // The attestation and validated asset hash are committed in the same EF
        // unit of work. The shared concurrency token prevents a concurrent revoke
        // from being overwritten; a retry re-reads the now-revoked attestation.
        attestation.AudioFingerprint = asset.Sha256;
    }

    private async Task Heartbeat(
        LeasedJob job,
        int progress,
        string stage,
        CancellationToken cancellationToken)
    {
        var renewed = await jobQueue.HeartbeatAsync(
            job.Id,
            job.LeaseOwner,
            job.LeaseToken,
            LeaseDuration,
            progress,
            stage,
            cancellationToken);

        if (!renewed)
        {
            throw new InvalidOperationException("The media job lease was lost.");
        }
    }

    private static void Validate(MediaAsset asset, MediaInspection inspection)
    {
        if (inspection.SizeBytes != asset.DeclaredBytes)
        {
            throw new MediaRejectedException(
                "media.size_mismatch",
                "The uploaded file size does not match the reserved upload.");
        }

        switch (asset.Kind)
        {
            case AssetKind.Audio:
                if (!MediaPolicy.IsAudioContentType(inspection.ContentType) || inspection.AudioCodec is null)
                {
                    RejectType();
                }

                if (inspection.DurationMilliseconds is null ||
                    inspection.DurationMilliseconds > MediaPolicy.MaxAudioDurationSeconds * 1000L)
                {
                    throw new MediaRejectedException("audio.duration_invalid", "Audio must be no longer than 10 minutes.");
                }

                break;
            case AssetKind.Cover:
            case AssetKind.BrandCharacter:
                ValidateImage(inspection);
                break;
            case AssetKind.Visual when MediaPolicy.IsImageContentType(inspection.ContentType):
                ValidateImage(inspection);
                break;
            case AssetKind.Visual:
                if (!MediaPolicy.IsVideoContentType(inspection.ContentType) || inspection.VideoCodec is null)
                {
                    RejectType();
                }

                if (inspection.DurationMilliseconds is null ||
                    inspection.DurationMilliseconds > MediaPolicy.MaxVideoDurationSeconds * 1000L)
                {
                    throw new MediaRejectedException("video.duration_invalid", "Visual video must be no longer than 60 seconds.");
                }

                if (inspection.Width > MediaPolicy.MaxDimension || inspection.Height > MediaPolicy.MaxDimension)
                {
                    throw new MediaRejectedException("video.dimensions_invalid", "Visual video must not exceed 4K dimensions.");
                }

                break;
            default:
                RejectType();
                break;
        }

        return;

        static void ValidateImage(MediaInspection inspection)
        {
            if (!MediaPolicy.IsImageContentType(inspection.ContentType) ||
                inspection.Width is null ||
                inspection.Height is null ||
                inspection.Width <= 0 ||
                inspection.Height <= 0)
            {
                RejectType();
            }
        }

        static void RejectType() =>
            throw new MediaRejectedException(
                "media.content_type_mismatch",
                "The uploaded bytes do not match the selected asset role.");
    }

    private DerivativeOutput Output(
        MediaAsset asset,
        DerivativeKind kind,
        string localPath,
        string contentType,
        long? durationMilliseconds = null) =>
        new(
            kind,
            localPath,
            ObjectKeyFactory.Derivative(
                asset.WorkspaceId,
                asset.ProjectId,
                asset.Id,
                asset.Revision,
                _options.ProcessorVersion,
                kind),
            contentType,
            durationMilliseconds,
            null,
            null);

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed record DerivativeOutput(
        DerivativeKind Kind,
        string LocalPath,
        string ObjectKey,
        string ContentType,
        long? DurationMilliseconds,
        int? Width,
        int? Height);
}
