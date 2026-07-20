using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Worker;

public sealed class ExportBundleJobHandler(
    Hook2StreamDbContext db,
    IObjectStorage storage,
    IPipelineArtifactStore artifacts) : IJobHandler
{
    public JobType Type => JobType.ExportBundle;
    public string Capability => "render";

    public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        var payload = PipelineHandlerData.Payload<ExportPayload>(job);
        var project = await db.Projects.SingleAsync(
            value => value.Id == payload.ProjectId && value.WorkspaceId == job.WorkspaceId,
            cancellationToken);
        var batch = await db.RenderBatches.SingleAsync(
            value => value.Id == payload.RenderBatchId &&
                     value.ProjectId == project.Id &&
                     value.WorkspaceId == job.WorkspaceId,
            cancellationToken);
        var existing = await db.MediaAssets.SingleOrDefaultAsync(
            value => value.ProjectId == project.Id &&
                     value.RenderBatchId == batch.Id &&
                     value.Purpose == AssetPurpose.ExportBundle &&
                     value.State == AssetState.Ready,
            cancellationToken);
        if (existing is not null) return;

        var renderJobIds = payload.RenderJobIds.Distinct().ToArray();
        if (renderJobIds.Length == 0)
        {
            await FailBatchAsync(project, batch, job, "export.dependencies_missing", cancellationToken);
            throw new JobHandlerException(
                "export.dependencies_missing",
                "The export has no final render operations.",
                retryable: false);
        }

        var renderJobs = await db.Jobs
            .Where(value => renderJobIds.Contains(value.Id) &&
                            value.WorkspaceId == job.WorkspaceId &&
                            value.ProjectId == project.Id &&
                            value.Type == JobType.FinalRender)
            .ToListAsync(cancellationToken);
        if (renderJobs.Count != renderJobIds.Length)
        {
            await FailBatchAsync(project, batch, job, "export.dependencies_invalid", cancellationToken);
            throw new JobHandlerException(
                "export.dependencies_invalid",
                "The export render dependencies are invalid.",
                retryable: false);
        }

        if (renderJobs.Any(value => value.State is JobState.Queued or JobState.Running))
        {
            batch.State = RenderBatchState.Running;
            await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
            throw new JobDeferredException(
                "export.renders_pending",
                "The export is waiting for final video renders.",
                TimeSpan.FromSeconds(30));
        }

        var dependencyItems = renderJobs.ToDictionary(
            value => value.Id,
            value => PipelineHandlerData.Deserialize<RenderDependencyPayload>(value.PayloadJson));
        var dependenciesValid = dependencyItems.Values.All(value =>
            value is not null &&
            value.ProjectId == project.Id &&
            value.RenderBatchId == batch.Id);
        var campaignRevisionIds = dependencyItems.Values
            .Where(value => value is not null)
            .Select(value => value!.CampaignRevisionId)
            .Distinct()
            .ToArray();
        if (!dependenciesValid || campaignRevisionIds.Length != 1 ||
            payload.CampaignRevisionId is { } snapshotCampaignId && campaignRevisionIds[0] != snapshotCampaignId)
        {
            await FailBatchAsync(project, batch, job, "export.dependencies_stale", cancellationToken);
            throw new JobHandlerException(
                "export.dependencies_stale",
                "The export render dependencies no longer describe one immutable campaign.",
                retryable: false);
        }

        var campaign = await db.CampaignPlanRevisions.SingleAsync(
            value => value.Id == campaignRevisionIds[0] && value.ProjectId == project.Id,
            cancellationToken);
        var campaignItems = (PipelineHandlerData.Deserialize<List<CampaignItemRequest>>(campaign.ItemsJson) ?? [])
            .ToDictionary(value => value.Id);
        var artwork = await db.ArtworkPackRevisions.AsNoTracking().SingleAsync(
            value => value.Id == campaign.ArtworkPackRevisionId && value.ProjectId == project.Id,
            cancellationToken);
        var sourceVisualIds = campaignItems.Values
            .Select(value => value.BackgroundAssetId)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Append(artwork.SelectedAssetId ?? Guid.Empty)
            .Where(value => value != Guid.Empty)
            .Distinct()
            .ToArray();
        var usesUploadedVisual = await db.MediaAssets.AsNoTracking().AnyAsync(
            value => sourceVisualIds.Contains(value.Id) &&
                     value.ProjectId == project.Id &&
                     value.Origin == AssetOrigin.Uploaded,
            cancellationToken);
        if (usesUploadedVisual && !await db.RightsAttestations.AsNoTracking().AnyAsync(
                value => value.ProjectId == project.Id && value.OwnsVisualRights,
                cancellationToken))
        {
            throw new JobBlockedException(
                "rights.visual_required",
                "Export is paused until rights to the uploaded campaign visuals are confirmed.");
        }

        var videos = await db.MediaAssets
            .Where(value => value.ProjectId == project.Id &&
                            value.RenderBatchId == batch.Id &&
                            value.Purpose == AssetPurpose.CampaignVideo &&
                            value.State == AssetState.Ready)
            .ToListAsync(cancellationToken);
        var successful = renderJobs
            .Where(value => value.State == JobState.Succeeded &&
                            dependencyItems[value.Id] is { } dependency &&
                            videos.Any(asset => asset.CampaignItemId == dependency.CampaignItemId))
            .ToList();
        if (successful.Count == 0)
        {
            await FailBatchAsync(project, batch, job, "export.no_successful_renders", cancellationToken);
            throw new JobHandlerException(
                "export.no_successful_renders",
                "No final videos were available for export.",
                retryable: false);
        }

        var workDirectory = Path.Combine(
            Path.GetTempPath(),
            "hook2stream-export",
            Guid.NewGuid().ToString("N"));
        var bundleDirectory = Path.Combine(workDirectory, "bundle");
        var videosDirectory = Path.Combine(bundleDirectory, "videos");
        Directory.CreateDirectory(videosDirectory);
        var zipPath = Path.Combine(workDirectory, "release.zip");

        try
        {
            var exported = new List<ExportedVideo>();
            foreach (var renderJob in successful.OrderBy(
                         value => campaignItems[dependencyItems[value.Id]!.CampaignItemId].Slot))
            {
                var dependency = dependencyItems[renderJob.Id]!;
                var item = campaignItems[dependency.CampaignItemId];
                var asset = videos.Single(value => value.CampaignItemId == item.Id);
                var relativePath = $"videos/{item.Slot:00}-{item.Id:N}.mp4";
                await storage.DownloadAsync(
                    asset.ObjectKey,
                    Path.Combine(bundleDirectory, relativePath),
                    cancellationToken);
                exported.Add(new ExportedVideo(
                    item.Id,
                    item.Slot,
                    relativePath,
                    asset.Sha256 ?? string.Empty,
                    asset.ActualBytes ?? asset.DeclaredBytes));
            }

            var calendarDirectory = Path.Combine(bundleDirectory, "calendar");
            var copyDirectory = Path.Combine(bundleDirectory, "copy");
            Directory.CreateDirectory(calendarDirectory);
            Directory.CreateDirectory(copyDirectory);
            var csv = BuildCampaignCsv(renderJobs, dependencyItems, campaignItems, exported);
            await File.WriteAllTextAsync(
                Path.Combine(copyDirectory, "campaign.csv"),
                csv,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(calendarDirectory, "calendar.csv"),
                csv,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(calendarDirectory, "calendar.ics"),
                BuildCampaignCalendar(
                    payload.ScheduleAnchor ?? (project.Mode == ReleaseMode.Released
                        ? project.CampaignStartDate ?? project.ReleaseDate
                        : project.ReleaseDate ?? project.CampaignStartDate) ??
                    DateOnly.FromDateTime(project.CreatedAt.UtcDateTime),
                    renderJobs,
                    dependencyItems,
                    campaignItems),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(copyDirectory, "campaign.txt"),
                BuildCampaignCopy(renderJobs, dependencyItems, campaignItems),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            var manifest = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectId = project.Id,
                renderBatchId = batch.Id,
                campaignRevisionId = campaign.Id,
                releaseSnapshot = new
                {
                    artistName = payload.ArtistName ?? project.ArtistName,
                    trackTitle = payload.TrackTitle ?? project.TrackTitle,
                    scheduleAnchor = payload.ScheduleAnchor,
                    releaseMode = payload.ReleaseMode
                },
                state = successful.Count == renderJobs.Count ? "succeeded" : "partially_succeeded",
                videos = exported,
                cleanCover = (object?)null,
                failed = renderJobs
                    .Where(value => successful.All(success => success.Id != value.Id))
                    .OrderBy(value => value.Id)
                    .Select(value => new
                    {
                        jobId = value.Id,
                        campaignItemId = dependencyItems[value.Id]?.CampaignItemId,
                        errorCode = value.ErrorCode ?? "render.output_missing"
                    })
            }, new JsonSerializerOptions(PipelineHandlerData.Json) { WriteIndented = true });
            await File.WriteAllTextAsync(
                Path.Combine(bundleDirectory, "manifest.json"),
                manifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            CreateDeterministicZip(bundleDirectory, zipPath);

            var canonical =
                $"workspaces/{project.WorkspaceId:N}/projects/{project.Id:N}/generated/exports/{batch.Id:N}/attempt-{job.AttemptNumber}-{job.LeaseToken:N}/release.zip";
            var promoted = await artifacts.StoreLocalAsync(
                zipPath,
                canonical,
                "application/zip",
                durationMilliseconds: null,
                width: null,
                height: null,
                cancellationToken);
            var isComplete = successful.Count == renderJobs.Count;
            db.MediaAssets.Add(new MediaAsset
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Kind = AssetKind.Visual,
                Origin = AssetOrigin.Generated,
                Purpose = AssetPurpose.ExportBundle,
                State = AssetState.Ready,
                OriginalFileName = "hook2stream-release.zip",
                DeclaredContentType = promoted.ContentType,
                DetectedContentType = promoted.ContentType,
                DeclaredBytes = promoted.SizeBytes,
                ActualBytes = promoted.SizeBytes,
                ObjectKey = promoted.ObjectKey,
                IsActive = true,
                RenderBatchId = batch.Id,
                Sha256 = promoted.Sha256,
                ProvenanceJson = JsonSerializer.Serialize(new
                {
                    exporter = "hook2stream-export-v1",
                    jobId = job.Id,
                    renderBatchId = batch.Id,
                    successfulVideoCount = successful.Count,
                    failedVideoCount = renderJobs.Count - successful.Count,
                    includesCleanCover = false
                }, PipelineHandlerData.Json)
            });
            batch.State = isComplete
                ? RenderBatchState.Succeeded
                : RenderBatchState.PartiallySucceeded;
            batch.CompletedAt = DateTimeOffset.UtcNow;
            project.State = isComplete ? ProjectState.Ready : ProjectState.PartiallyReady;
            PipelineOutbox.Reconcile(
                db,
                project,
                isComplete ? "export.completed" : "export.partially_completed",
                job.Id);
            await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
        }
        finally
        {
            PipelineHandlerData.TryDelete(workDirectory);
        }
    }

    private async Task FailBatchAsync(
        ReleaseProject project,
        RenderBatch batch,
        LeasedJob job,
        string reason,
        CancellationToken cancellationToken)
    {
        batch.State = RenderBatchState.Failed;
        batch.CompletedAt = DateTimeOffset.UtcNow;
        project.State = ProjectState.PartiallyReady;
        PipelineOutbox.Reconcile(db, project, reason, job.Id);
        await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
    }

    private static string BuildCampaignCsv(
        IReadOnlyCollection<Job> renderJobs,
        IReadOnlyDictionary<Guid, RenderDependencyPayload?> dependencies,
        IReadOnlyDictionary<Guid, CampaignItemRequest> items,
        IReadOnlyCollection<ExportedVideo> exported)
    {
        var builder = new StringBuilder();
        builder.AppendLine("slot,campaign_item_id,template,hook_id,background_asset_id,text,neutral_copy,emotional_copy,tiktok,youtube_shorts,instagram_reels,vk_clips,hashtags,composition_json,status,video_path");
        foreach (var job in renderJobs.OrderBy(value =>
                     dependencies[value.Id] is { } dependency && items.TryGetValue(dependency.CampaignItemId, out var item)
                         ? item.Slot
                         : int.MaxValue))
        {
            var dependency = dependencies[job.Id];
            if (dependency is null || !items.TryGetValue(dependency.CampaignItemId, out var item)) continue;
            var video = exported.SingleOrDefault(value => value.CampaignItemId == item.Id);
            var status = video is null ? "failed" : "succeeded";
            var copy = CampaignCopySnapshot.Parse(item);
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(item.Slot.ToString()),
                Csv(item.Id.ToString("N")),
                Csv(item.Template),
                Csv(item.HookId),
                Csv(item.BackgroundAssetId?.ToString("N") ?? string.Empty),
                Csv(item.Text),
                Csv(copy.Neutral),
                Csv(copy.Emotional),
                Csv(copy.TikTok),
                Csv(copy.YouTubeShorts),
                Csv(copy.InstagramReels),
                Csv(copy.VkClips),
                Csv(string.Join(' ', copy.Hashtags)),
                Csv(item.CompositionJson),
                Csv(status),
                Csv(video?.Path ?? string.Empty)
            }));
        }

        return builder.ToString();
    }

    private static string BuildCampaignCopy(
        IReadOnlyCollection<Job> renderJobs,
        IReadOnlyDictionary<Guid, RenderDependencyPayload?> dependencies,
        IReadOnlyDictionary<Guid, CampaignItemRequest> items)
    {
        var builder = new StringBuilder();
        foreach (var job in OrderedRenderJobs(renderJobs, dependencies, items))
        {
            var dependency = dependencies[job.Id];
            if (dependency is null || !items.TryGetValue(dependency.CampaignItemId, out var item)) continue;
            var copy = CampaignCopySnapshot.Parse(item);
            builder.Append("ITEM ").Append(item.Slot.ToString("00")).Append(" · ")
                .AppendLine(item.Template);
            builder.Append("Neutral: ").AppendLine(copy.Neutral);
            builder.Append("Emotional: ").AppendLine(copy.Emotional);
            builder.Append("TikTok: ").AppendLine(copy.TikTok);
            builder.Append("YouTube Shorts: ").AppendLine(copy.YouTubeShorts);
            builder.Append("Instagram Reels: ").AppendLine(copy.InstagramReels);
            builder.Append("VK Clips: ").AppendLine(copy.VkClips);
            builder.Append("Hashtags: ").AppendLine(string.Join(' ', copy.Hashtags));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildCampaignCalendar(
        DateOnly anchor,
        IReadOnlyCollection<Job> renderJobs,
        IReadOnlyDictionary<Guid, RenderDependencyPayload?> dependencies,
        IReadOnlyDictionary<Guid, CampaignItemRequest> items)
    {
        var builder = new StringBuilder();
        builder.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n")
            .Append("PRODID:-//Hook2Stream//Release Campaign//EN\r\nCALSCALE:GREGORIAN\r\n");
        foreach (var job in OrderedRenderJobs(renderJobs, dependencies, items))
        {
            var dependency = dependencies[job.Id];
            if (dependency is null || !items.TryGetValue(dependency.CampaignItemId, out var item)) continue;
            var relativeDay = RelativeDay(item.CompositionJson);
            var date = anchor.AddDays(relativeDay);
            builder.Append("BEGIN:VEVENT\r\nUID:").Append(item.Id.ToString("N"))
                .Append("@hook2stream\r\nDTSTART;VALUE=DATE:")
                .Append(date.ToString("yyyyMMdd"))
                .Append("\r\nSUMMARY:").Append(Ics($"Campaign item {item.Slot}: {item.Template}"))
                .Append("\r\nDESCRIPTION:").Append(Ics(item.Text))
                .Append("\r\nEND:VEVENT\r\n");
        }

        builder.Append("END:VCALENDAR\r\n");
        return builder.ToString();
    }

    private static IEnumerable<Job> OrderedRenderJobs(
        IReadOnlyCollection<Job> renderJobs,
        IReadOnlyDictionary<Guid, RenderDependencyPayload?> dependencies,
        IReadOnlyDictionary<Guid, CampaignItemRequest> items) =>
        renderJobs.OrderBy(value =>
            dependencies[value.Id] is { } dependency && items.TryGetValue(dependency.CampaignItemId, out var item)
                ? item.Slot
                : int.MaxValue);

    private static int RelativeDay(string compositionJson)
    {
        try
        {
            using var document = JsonDocument.Parse(compositionJson);
            return document.RootElement.TryGetProperty("relativeDay", out var value) && value.TryGetInt32(out var day)
                ? Math.Clamp(day, -365, 365)
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string Ics(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static void CreateDeterministicZip(string sourceDirectory, string zipPath)
    {
        var epoch = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(value => Path.GetRelativePath(sourceDirectory, value), StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath).Replace('\\', '/');
            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
            entry.LastWriteTime = epoch;
            using var entryStream = entry.Open();
            using var source = File.OpenRead(sourcePath);
            source.CopyTo(entryStream);
        }
    }

    private sealed record ExportPayload(
        Guid ProjectId,
        Guid RenderBatchId,
        IReadOnlyList<Guid> RenderJobIds,
        Guid? CampaignRevisionId = null,
        DateOnly? ScheduleAnchor = null,
        string? ArtistName = null,
        string? TrackTitle = null,
        ReleaseMode? ReleaseMode = null);
    private sealed record RenderDependencyPayload(
        Guid ProjectId,
        Guid CampaignRevisionId,
        Guid CampaignItemId,
        Guid RenderBatchId,
        RenderRequestKind Kind);
    private sealed record ExportedVideo(
        Guid CampaignItemId,
        int Slot,
        string Path,
        string Sha256,
        long SizeBytes);

    private sealed record CampaignCopySnapshot(
        string Neutral,
        string Emotional,
        string TikTok,
        string YouTubeShorts,
        string InstagramReels,
        string VkClips,
        IReadOnlyList<string> Hashtags)
    {
        public static CampaignCopySnapshot Parse(CampaignItemRequest item)
        {
            try
            {
                using var document = JsonDocument.Parse(item.CompositionJson);
                var root = document.RootElement;
                var variants = root.TryGetProperty("copyVariants", out var copy) && copy.ValueKind == JsonValueKind.Object
                    ? copy
                    : default;
                var destinations = variants.ValueKind == JsonValueKind.Object &&
                                   variants.TryGetProperty("destinations", out var destinationValue) &&
                                   destinationValue.ValueKind == JsonValueKind.Object
                    ? destinationValue
                    : default;
                var hashtags = root.TryGetProperty("hashtags", out var hashtagValue) &&
                               hashtagValue.ValueKind == JsonValueKind.Array
                    ? hashtagValue.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString() ?? string.Empty)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToArray()
                    : [];
                var fallback = item.Text.Trim();
                var neutral = Text(variants, "neutral", fallback);
                var emotional = Text(variants, "emotional", fallback);
                return new CampaignCopySnapshot(
                    neutral,
                    emotional,
                    Text(destinations, "tiktok", emotional),
                    Text(destinations, "youtubeShorts", neutral),
                    Text(destinations, "instagramReels", emotional),
                    Text(destinations, "vkClips", neutral),
                    hashtags);
            }
            catch (JsonException)
            {
                return Fallback(item.Text);
            }
            catch (InvalidOperationException)
            {
                return Fallback(item.Text);
            }
        }

        private static string Text(JsonElement parent, string property, string fallback) =>
            parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : fallback;

        private static CampaignCopySnapshot Fallback(string value)
        {
            var text = value.Trim();
            return new CampaignCopySnapshot(text, text, text, text, text, text, []);
        }
    }
}
