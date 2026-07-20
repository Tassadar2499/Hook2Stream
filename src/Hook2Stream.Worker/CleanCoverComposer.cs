using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Worker;

public interface ICleanCoverComposer
{
    Task<MediaAsset> EnsureAsync(
        ReleaseProject project,
        ArtworkPackRevision artworkPack,
        CancellationToken cancellationToken,
        string? artistNameSnapshot = null,
        string? trackTitleSnapshot = null);
}

/// <summary>
/// Materializes the approved text-free source plus the user's local crop and
/// typography choices. Artist/title text never leaves this process.
/// </summary>
public sealed class CleanCoverComposer(
    Hook2StreamDbContext db,
    IObjectStorage storage,
    IPipelineArtifactStore artifacts,
    IProcessRunner processRunner,
    IOptions<MediaToolsOptions> mediaOptions) : ICleanCoverComposer
{
    private const int OutputSize = 3000;
    private const int SafeMargin = 180;
    private const int TextGap = 48;
    private const int TextBoxPadding = 60;

    public async Task<MediaAsset> EnsureAsync(
        ReleaseProject project,
        ArtworkPackRevision artworkPack,
        CancellationToken cancellationToken,
        string? artistNameSnapshot = null,
        string? trackTitleSnapshot = null)
    {
        if (artworkPack.ProjectId != project.Id || artworkPack.ApprovedAt is null ||
            artworkPack.SelectedAssetId is not { } sourceAssetId)
        {
            throw new JobHandlerException(
                "cover.approval_required",
                "Approve a cover before preparing the clean artwork.",
                retryable: false);
        }

        var artistName = string.IsNullOrWhiteSpace(artistNameSnapshot) ? project.ArtistName : artistNameSnapshot;
        var trackTitle = string.IsNullOrWhiteSpace(trackTitleSnapshot) ? project.TrackTitle : trackTitleSnapshot;
        var metadataHash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{artistName}\n{trackTitle}")));
        var canonical =
            $"workspaces/{project.WorkspaceId:N}/projects/{project.Id:N}/generated/artwork/{artworkPack.Id:N}/clean-cover-{metadataHash[..16]}-3000.png";
        var existing = await db.MediaAssets.SingleOrDefaultAsync(
            value => value.WorkspaceId == project.WorkspaceId &&
                     value.ProjectId == project.Id &&
                     value.Purpose == AssetPurpose.CleanCover &&
                     value.SupersedesAssetId == sourceAssetId &&
                     value.ObjectKey == canonical &&
                     value.State == AssetState.Ready,
            cancellationToken);
        if (existing is not null) return existing;

        var source = await db.MediaAssets.SingleAsync(
            value => value.Id == sourceAssetId &&
                     value.WorkspaceId == project.WorkspaceId &&
                     value.ProjectId == project.Id &&
                     value.Purpose == AssetPurpose.ApprovedCover &&
                     value.State == AssetState.Ready,
            cancellationToken);
        var composition = CoverComposition.Parse(artworkPack.CompositionJson);
        var workDirectory = Path.Combine(
            Path.GetTempPath(),
            "hook2stream-clean-cover",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var sourcePath = Path.Combine(workDirectory, "source");
        var artistPath = Path.Combine(workDirectory, "artist.txt");
        var titlePath = Path.Combine(workDirectory, "title.txt");
        var outputPath = Path.Combine(workDirectory, "clean-cover.png");

        try
        {
            await storage.DownloadAsync(source.ObjectKey, sourcePath, cancellationToken);
            await File.WriteAllTextAsync(artistPath, NormalizeText(artistName), Encoding.UTF8, cancellationToken);
            await File.WriteAllTextAsync(titlePath, NormalizeText(trackTitle), Encoding.UTF8, cancellationToken);

            var filters = new List<string>
            {
                BuildCropFilter(composition)
            };
            filters.AddRange(BuildTypographyFilters(composition, artistPath, titlePath));

            filters.Add("format=rgba");
            var execution = await processRunner.RunAsync(
                mediaOptions.Value.FfmpegPath,
                [
                    "-y", "-v", "error", "-i", sourcePath,
                    "-vf", string.Join(',', filters),
                    "-frames:v", "1", "-c:v", "png", "-compression_level", "6", outputPath
                ],
                TimeSpan.FromSeconds(mediaOptions.Value.ProcessTimeoutSeconds),
                workDirectory,
                cancellationToken);
            if (execution.ExitCode != 0)
            {
                throw new JobHandlerException(
                    "cover.composition_failed",
                    "The approved cover composition could not be rendered.",
                    retryable: true);
            }

            var probe = await processRunner.RunAsync(
                mediaOptions.Value.FfprobePath,
                [
                    "-v", "error", "-select_streams", "v:0",
                    "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", outputPath
                ],
                TimeSpan.FromSeconds(mediaOptions.Value.ProcessTimeoutSeconds),
                workDirectory,
                cancellationToken);
            if (probe.ExitCode != 0 ||
                !string.Equals(probe.StandardOutput.Trim(), $"{OutputSize}x{OutputSize}", StringComparison.Ordinal))
            {
                throw new JobHandlerException(
                    "cover.output_invalid",
                    "The approved cover did not produce a valid 3000 by 3000 image.",
                    retryable: true);
            }

            var promoted = await artifacts.StoreLocalAsync(
                outputPath,
                canonical,
                "image/png",
                durationMilliseconds: null,
                OutputSize,
                OutputSize,
                cancellationToken);
            var compositionHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(artworkPack.CompositionJson)));
            var asset = new MediaAsset
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Kind = AssetKind.Cover,
                Origin = AssetOrigin.Generated,
                Purpose = AssetPurpose.CleanCover,
                State = AssetState.Ready,
                OriginalFileName = "cover-3000x3000.png",
                DeclaredContentType = promoted.ContentType,
                DetectedContentType = promoted.ContentType,
                DeclaredBytes = promoted.SizeBytes,
                ActualBytes = promoted.SizeBytes,
                ObjectKey = promoted.ObjectKey,
                Revision = artworkPack.Number,
                IsActive = true,
                SupersedesAssetId = source.Id,
                ArtworkPackRevisionId = artworkPack.Id,
                Sha256 = promoted.Sha256,
                Width = OutputSize,
                Height = OutputSize,
                ProvenanceJson = JsonSerializer.Serialize(new
                {
                    renderer = "local-cover-compositor-v1",
                    artworkPackRevisionId = artworkPack.Id,
                    sourceAssetId = source.Id,
                    sourceSha256 = source.Sha256,
                    compositionSha256 = compositionHash,
                    metadataSha256 = metadataHash
                }, PipelineHandlerData.Json)
            };
            db.MediaAssets.Add(asset);
            return asset;
        }
        finally
        {
            PipelineHandlerData.TryDelete(workDirectory);
        }
    }

    private static string BuildCropFilter(CoverComposition composition)
    {
        var scaled = (int)Math.Ceiling(OutputSize * composition.CropScale);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"scale={scaled}:{scaled}:force_original_aspect_ratio=increase," +
            $"crop={OutputSize}:{OutputSize}:(iw-ow)*{composition.CropX:0.####}:(ih-oh)*{composition.CropY:0.####}");
    }

    internal static IReadOnlyList<string> BuildTypographyFilters(
        CoverComposition composition,
        string artistPath,
        string titlePath)
    {
        if (!composition.ShowArtist && !composition.ShowTitle) return [];

        var blockHeight =
            (composition.ShowArtist ? composition.ArtistFontSize : 0) +
            (composition.ShowArtist && composition.ShowTitle ? TextGap : 0) +
            (composition.ShowTitle ? composition.TitleFontSize : 0);
        var availableHeight = Math.Max(0, OutputSize - (2 * SafeMargin) - blockHeight);
        var originY = SafeMargin + (int)Math.Round(
            availableHeight * composition.TextY,
            MidpointRounding.AwayFromZero);
        var boxY = Math.Max(0, originY - TextBoxPadding);
        var boxHeight = Math.Min(OutputSize - boxY, blockHeight + (2 * TextBoxPadding));
        var textX = string.Create(
            CultureInfo.InvariantCulture,
            $"{SafeMargin}+(w-text_w-{2 * SafeMargin})*{composition.TextX:0.####}");
        var filters = new List<string>
        {
            $"drawbox=x=0:y={boxY}:w=iw:h={boxHeight}:color={composition.BackgroundColor}@0.58:t=fill"
        };
        var nextY = originY;

        if (composition.ShowArtist)
        {
            filters.Add(
                $"drawtext=font='{composition.FontFamily}':textfile='{EscapeFilterPath(artistPath)}':" +
                $"fontcolor={composition.AccentColor}:fontsize={composition.ArtistFontSize}:" +
                $"x={textX}:y={nextY}:fix_bounds=1");
            nextY += composition.ArtistFontSize + (composition.ShowTitle ? TextGap : 0);
        }

        if (composition.ShowTitle)
        {
            filters.Add(
                $"drawtext=font='{composition.FontFamily}':textfile='{EscapeFilterPath(titlePath)}':" +
                $"fontcolor={composition.ForegroundColor}:fontsize={composition.TitleFontSize}:" +
                $"x={textX}:y={nextY}:fix_bounds=1");
        }

        return filters;
    }

    private static string NormalizeText(string value)
    {
        var normalized = value.Replace('\0', ' ').Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized[..Math.Min(normalized.Length, 300)];
    }

    private static string EscapeFilterPath(string path) =>
        path.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    internal sealed record CoverComposition(
        double CropX,
        double CropY,
        double CropScale,
        string BackgroundColor,
        string ForegroundColor,
        string AccentColor,
        string FontFamily,
        int ArtistFontSize,
        int TitleFontSize,
        double TextX,
        double TextY,
        bool ShowArtist,
        bool ShowTitle)
    {
        public static CoverComposition Parse(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
                return new CoverComposition(
                    Number(root, "cropX", .5, 0, 1),
                    Number(root, "cropY", .5, 0, 1),
                    Number(root, "cropScale", 1, 1, 2),
                    PaletteColor(root, 0, "0x121212"),
                    PaletteColor(root, 1, "0xfffaf2"),
                    PaletteColor(root, 2, "0xff5c35"),
                    Font(root),
                    Integer(root, "artistFontSize", 112, 72, 220),
                    Integer(root, "titleFontSize", 188, 96, 360),
                    Number(root, "textX", 0, 0, 1),
                    Number(root, "textY", 1, 0, 1),
                    Boolean(root, "showArtist", true),
                    Boolean(root, "showTitle", true));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw new JobHandlerException(
                    "cover.composition_invalid",
                    "The approved cover composition is invalid.",
                    retryable: false);
            }
        }

        private static double Number(
            JsonElement root,
            string property,
            double fallback,
            double minimum,
            double maximum) =>
            root.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var parsed) &&
            double.IsFinite(parsed)
                ? Math.Clamp(parsed, minimum, maximum)
                : fallback;

        private static int Integer(
            JsonElement root,
            string property,
            int fallback,
            int minimum,
            int maximum) =>
            root.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var parsed) &&
            double.IsFinite(parsed)
                ? (int)Math.Round(Math.Clamp(parsed, minimum, maximum), MidpointRounding.AwayFromZero)
                : fallback;

        private static bool Boolean(JsonElement root, string property, bool fallback) =>
            root.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;

        private static string Font(JsonElement root)
        {
            if (!root.TryGetProperty("fontFamily", out var value) || value.ValueKind != JsonValueKind.String)
            {
                return "Sans";
            }

            return value.GetString()?.ToLowerInvariant() switch
            {
                "serif" => "Serif",
                "monospace" => "Monospace",
                _ => "Sans"
            };
        }

        private static string PaletteColor(JsonElement root, int index, string fallback)
        {
            if (!root.TryGetProperty("palette", out var palette) ||
                palette.ValueKind != JsonValueKind.Array ||
                palette.GetArrayLength() <= index)
            {
                return fallback;
            }

            var value = palette[index];
            return value.ValueKind == JsonValueKind.String
                ? Color(value.GetString()) ?? fallback
                : fallback;
        }

        private static string? Color(string? value)
        {
            if (value is null || value.Length != 7 || value[0] != '#' ||
                value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0)
            {
                return null;
            }

            return $"0x{value[1..].ToLowerInvariant()}";
        }
    }
}

public sealed class CleanCoverRenderJobHandler(
    Hook2StreamDbContext db,
    ICleanCoverComposer composer) : IJobHandler
{
    public JobType Type => JobType.CleanCoverRender;
    public string Capability => "render";

    public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        var payload = PipelineHandlerData.Payload<CleanCoverPayload>(job);
        var project = await db.Projects.SingleAsync(
            value => value.Id == payload.ProjectId && value.WorkspaceId == job.WorkspaceId,
            cancellationToken);
        var entitlement = await db.Entitlements.SingleAsync(
            value => value.Id == payload.EntitlementId &&
                     value.WorkspaceId == job.WorkspaceId &&
                     value.ProjectId == project.Id,
            cancellationToken);
        var entitledAssets = PipelineHandlerData.Deserialize<List<Guid>>(entitlement.ItemIdsJson) ?? [];
        if (entitlement.ProductCode != BillingProducts.CleanCover ||
            entitlement.State != EntitlementState.Active ||
            entitlement.RevokedAt is not null ||
            entitlement.ValidUntil is { } validUntil && validUntil <= DateTimeOffset.UtcNow ||
            !entitledAssets.Contains(payload.SelectedAssetId))
        {
            throw new JobHandlerException(
                "cover.entitlement_invalid",
                "An active clean-cover entitlement is required.",
                retryable: false);
        }

        var pack = await db.ArtworkPackRevisions.SingleAsync(
            value => value.Id == payload.ArtworkPackRevisionId &&
                     value.ProjectId == project.Id &&
                     value.WorkspaceId == job.WorkspaceId,
            cancellationToken);
        var compositionHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(pack.CompositionJson)));
        if (pack.SelectedAssetId != payload.SelectedAssetId ||
            pack.ApprovedAt is null ||
            entitlement.ArtworkPackRevisionId != pack.Id ||
            !string.Equals(entitlement.ArtworkCompositionHash, compositionHash, StringComparison.Ordinal))
        {
            throw new JobHandlerException(
                "cover.selection_stale",
                "The purchased cover selection no longer matches its approved artwork revision.",
                retryable: false);
        }

        var selectedSource = await db.MediaAssets.SingleAsync(
            value => value.Id == payload.SelectedAssetId && value.ProjectId == project.Id,
            cancellationToken);
        if (selectedSource.Origin == AssetOrigin.Uploaded &&
            !await db.RightsAttestations.AnyAsync(
                value => value.ProjectId == project.Id && value.OwnsVisualRights,
                cancellationToken))
        {
            throw new JobBlockedException(
                "rights.visual_required",
                "Clean-cover rendering is waiting for confirmation of rights to the uploaded artwork.");
        }

        var artistName = entitlement.ArtistNameSnapshot ?? payload.ArtistName ?? project.ArtistName;
        var trackTitle = entitlement.TrackTitleSnapshot ?? payload.TrackTitle ?? project.TrackTitle;
        var asset = await composer.EnsureAsync(
            project,
            pack,
            cancellationToken,
            artistName,
            trackTitle);
        asset.ArtworkPackRevisionId = pack.Id;
        asset.ProvenanceJson = JsonSerializer.Serialize(new
        {
            renderer = "local-cover-compositor-v1",
            entitlementId = entitlement.Id,
            artworkPackRevisionId = pack.Id,
            selectedAssetId = payload.SelectedAssetId,
            compositionSha256 = compositionHash,
            metadataSha256 = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{artistName}\n{trackTitle}")))
        }, PipelineHandlerData.Json);
        PipelineOutbox.Reconcile(db, project, "cover.clean_completed", job.Id);
        await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
    }

    private sealed record CleanCoverPayload(
        Guid ProjectId,
        Guid EntitlementId,
        Guid ArtworkPackRevisionId,
        Guid SelectedAssetId,
        string? ArtistName = null,
        string? TrackTitle = null);
}
