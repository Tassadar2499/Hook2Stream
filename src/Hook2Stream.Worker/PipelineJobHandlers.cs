using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Jobs;
using Hook2Stream.Infrastructure.Media;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Worker;

public sealed class AudioAnalysisJobHandler(
    Hook2StreamDbContext db,
    IAudioAnalysisProvider provider) : IJobHandler
{
    public JobType Type => JobType.AudioAnalysis;
    public string Capability => "analysis";

    public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        var payload = PipelineHandlerData.Payload<AnalysisPayload>(job);
        var revision = await db.TrackAnalysisRevisions.SingleAsync(
            value => value.Id == payload.AnalysisRevisionId &&
                     value.ProjectId == payload.ProjectId &&
                     value.WorkspaceId == job.WorkspaceId,
            cancellationToken);
        if (revision.State == RevisionState.Approved) return;
        PipelineHandlerData.EnsureFingerprint(job, revision.SourceFingerprint);
        var asset = await db.MediaAssets.SingleAsync(
            value => value.Id == payload.AssetId &&
                     value.ProjectId == payload.ProjectId &&
                     value.State == AssetState.Ready,
            cancellationToken);
        var project = await db.Projects.SingleAsync(value => value.Id == payload.ProjectId, cancellationToken);
        var result = await provider.AnalyzeAsync(
            new AudioAnalysisRequest(
                PipelineHandlerData.Context(job, "analysis"),
                PipelineHandlerData.Object(asset),
                project.Language),
            cancellationToken);
        if (!result.IsSuccess)
        {
            if (!result.Failure!.Retryable)
            {
                revision.State = RevisionState.Failed;
                await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
            }

            throw PipelineHandlerData.Failure(result.Failure!);
        }

        revision.AnalysisJson = JsonSerializer.Serialize(result.Value, PipelineHandlerData.Json);
        revision.ProcessorVersionsJson = JsonSerializer.Serialize(result.Provenance, PipelineHandlerData.Json);
        revision.State = RevisionState.Approved;
        project.State = ProjectState.Analyzing;
        PipelineOutbox.Reconcile(db, project, "analysis.completed", job.Id);
        await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
    }

    private sealed record AnalysisPayload(Guid ProjectId, Guid AssetId, Guid AnalysisRevisionId);
}

public sealed class TranscriptionJobHandler(
    Hook2StreamDbContext db,
    ITranscriptionProvider provider,
    IAiProviderInvocationWriter invocations) : IJobHandler
{
    public JobType Type => JobType.Transcription;
    public string Capability => "analysis";

    public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        var payload = PipelineHandlerData.Payload<TranscriptionPayload>(job);
        var project = await db.Projects.SingleAsync(
            value => value.Id == payload.ProjectId && value.WorkspaceId == job.WorkspaceId,
            cancellationToken);
        var asset = await db.MediaAssets.SingleAsync(
            value => value.Id == payload.AssetId &&
                     value.ProjectId == project.Id &&
                     value.State == AssetState.Ready,
            cancellationToken);
        if (project.IsInstrumental && project.IsInstrumentalConfirmed)
        {
            return;
        }
        var rights = await db.RightsAttestations.AsNoTracking().SingleOrDefaultAsync(
            value => value.ProjectId == project.Id,
            cancellationToken);
        var externalAi = ExternalAiProcessingGate.Evaluate(project, asset, rights);
        if (!externalAi.Allowed)
        {
            throw new JobBlockedException(
                externalAi.BlockerCode ?? "rights.external_ai_processing_required",
                "Transcription is paused until rights and external AI processing are confirmed.");
        }
        var revision = payload.TranscriptRevisionId is { } revisionId
            ? await db.TranscriptRevisions.SingleAsync(value => value.Id == revisionId, cancellationToken)
            : await CreateRevisionAsync(project, asset, cancellationToken);
        if (revision.State is RevisionState.ReadyForReview or RevisionState.Approved or RevisionState.Superseded) return;
        PipelineHandlerData.EnsureFingerprint(job, revision.SourceFingerprint);

        var source = PipelineHandlerData.Object(asset);
        var providerContext = PipelineHandlerData.Context(job, "transcription");
        var result = await provider.TranscribeAsync(
            new TranscriptionRequest(
                providerContext,
                source,
                FallbackAudio: null,
                project.Language,
                project.IsInstrumentalConfirmed ? project.IsInstrumental : null),
            cancellationToken);
        if (!result.IsSuccess)
        {
            await invocations.RecordAsync(
                job,
                "transcription",
                providerContext,
                result.Provenance,
                result.Failure,
                status: null,
                cancellationToken);
            if (!result.Failure!.Retryable)
            {
                revision.State = RevisionState.Failed;
                await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
            }

            throw PipelineHandlerData.Failure(result.Failure!);
        }

        var currentRights = await db.RightsAttestations.AsNoTracking().SingleOrDefaultAsync(
            value => value.ProjectId == project.Id,
            cancellationToken);
        var currentExternalAi = ExternalAiProcessingGate.Evaluate(project, asset, currentRights);
        if (!currentExternalAi.Allowed)
        {
            await invocations.RecordAsync(
                job,
                "transcription",
                providerContext,
                result.Provenance,
                failure: null,
                AiProviderInvocationLedger.DiscardedConsentRevoked,
                cancellationToken);
            throw new JobBlockedException(
                currentExternalAi.BlockerCode ?? "rights.external_ai_processing_required",
                "Transcription was not saved because external AI processing consent is no longer active.");
        }

        var phrases = result.Value!.Phrases.Select((phrase, index) => new TranscriptPhraseRequest(
            phrase.Id.ToString("N"),
            index,
            phrase.Text,
            phrase.StartMilliseconds,
            phrase.EndMilliseconds,
            phrase.Confidence,
            WarningAcknowledged: false,
            phrase.Words.Select(word => new TranscriptWordResponse(
                word.Text,
                word.StartMilliseconds,
                word.EndMilliseconds,
                word.Confidence)).ToArray())).ToArray();
        revision.Language = result.Value.Language;
        revision.PhrasesJson = JsonSerializer.Serialize(phrases, PipelineHandlerData.Json);
        revision.State = RevisionState.ReadyForReview;
        project.CurrentTranscriptRevisionId = revision.Id;
        project.LyricsText = phrases.Length == 0 ? null : string.Join('\n', phrases.Select(value => value.Text));
        if (result.Value.IsInstrumentalCandidate)
        {
            project.IsInstrumental = true;
            project.IsInstrumentalConfirmed = false;
        }

        await invocations.RecordAsync(
            job,
            "transcription",
            providerContext,
            result.Provenance,
            failure: null,
            status: null,
            cancellationToken);
        PipelineOutbox.Reconcile(db, project, "transcription.completed", job.Id);
        await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
    }

    private async Task<TranscriptRevision> CreateRevisionAsync(
        ReleaseProject project,
        MediaAsset asset,
        CancellationToken cancellationToken)
    {
        var previous = project.CurrentTranscriptRevisionId is { } previousId
            ? await db.TranscriptRevisions.SingleOrDefaultAsync(value => value.Id == previousId, cancellationToken)
            : null;
        if (previous is not null) previous.State = RevisionState.Superseded;
        var number = await db.TranscriptRevisions
            .Where(value => value.ProjectId == project.Id)
            .Select(value => value.Number)
            .DefaultIfEmpty()
            .MaxAsync(cancellationToken) + 1;
        var revision = new TranscriptRevision
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Number = number,
            Source = TranscriptSource.Automatic,
            State = RevisionState.Processing,
            Language = project.Language,
            SourceFingerprint = asset.Sha256!,
            SupersedesRevisionId = previous?.Id
        };
        db.TranscriptRevisions.Add(revision);
        project.CurrentTranscriptRevisionId = revision.Id;
        return revision;
    }

    private sealed record TranscriptionPayload(Guid ProjectId, Guid AssetId, Guid? TranscriptRevisionId);
}

public sealed class ArtworkGenerationJobHandler(
    Hook2StreamDbContext db,
    IArtworkProvider provider,
    IPipelineArtifactStore artifacts,
    IObjectStorage storage,
    IProcessRunner processRunner,
    IOptions<MediaToolsOptions> mediaOptions,
    TimeProvider timeProvider,
    ICleanCoverComposer coverComposer,
    IAiProviderInvocationWriter invocations) : IJobHandler
{
    public JobType Type => JobType.ArtworkGeneration;
    public string Capability => "artwork";

    public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        var payload = PipelineHandlerData.Payload<ArtworkPayload>(job);
        var project = await db.Projects.SingleAsync(
            value => value.Id == payload.ProjectId && value.WorkspaceId == job.WorkspaceId,
            cancellationToken);
        var pack = await db.ArtworkPackRevisions.SingleAsync(
            value => value.Id == payload.ArtworkPackRevisionId && value.ProjectId == project.Id,
            cancellationToken);
        if (project.CurrentArtworkPackRevisionId != pack.Id ||
            pack.State is RevisionState.Superseded or RevisionState.Failed)
        {
            throw new JobHandlerException(
                "artwork.revision_stale",
                "The queued artwork revision is no longer current.",
                retryable: false);
        }
        var isBackgrounds = string.Equals(payload.Mode, "backgrounds", StringComparison.OrdinalIgnoreCase);
        if (!isBackgrounds && pack.State is RevisionState.ReadyForReview or RevisionState.Approved) return;
        if (isBackgrounds && PipelineHandlerData.Deserialize<List<Guid>>(pack.BackgroundAssetIdsJson)?.Count == 3) return;
        if (!isBackgrounds) PipelineHandlerData.EnsureFingerprint(job, pack.SourceFingerprint);

        var audio = await db.MediaAssets.SingleOrDefaultAsync(
            value => value.ProjectId == project.Id &&
                     value.WorkspaceId == job.WorkspaceId &&
                     value.Kind == AssetKind.Audio &&
                     value.IsActive &&
                     value.State == AssetState.Ready &&
                     value.Sha256 != null,
            cancellationToken);
        var rights = await db.RightsAttestations.SingleOrDefaultAsync(
            value => value.ProjectId == project.Id,
            cancellationToken);
        var gate = audio is null
            ? new ArtworkAutomationDecision(false, "audio.not_ready")
            : ArtworkAutomationGate.Evaluate(
                project,
                audio,
                rights,
                DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime));
        if (!gate.Allowed)
        {
            throw new JobBlockedException(
                gate.BlockerCode ?? "rights.required",
                "Artwork generation is paused until the current audio rights and external AI consent are confirmed.");
        }

        try
        {
            var brand = await db.BrandKits.SingleAsync(value => value.WorkspaceId == project.WorkspaceId, cancellationToken);
            var reference = isBackgrounds
            ? await db.MediaAssets.SingleAsync(
                value => value.Id == pack.SelectedAssetId &&
                         value.ProjectId == project.Id &&
                         value.State == AssetState.Ready,
                cancellationToken)
            : null;
            if (reference is { Origin: AssetOrigin.Uploaded } && rights?.OwnsVisualRights != true)
            {
                throw new JobBlockedException(
                    "rights.visual_required",
                    "Background generation is paused until rights to the selected uploaded cover are confirmed.");
            }
            if (isBackgrounds)
            {
                // Materialize the approved crop and local typography once. It stays
                // owner-scoped and is never used as the external artwork reference.
                _ = await coverComposer.EnsureAsync(project, pack, cancellationToken);
            }
            var localComposition = isBackgrounds
                ? CleanCoverComposer.CoverComposition.Parse(pack.CompositionJson)
                : null;
            var palette = localComposition is null
                ? new[] { brand.PrimaryColor, brand.SecondaryColor, brand.AccentColor }
                : new[]
                {
                CssColor(localComposition.BackgroundColor),
                CssColor(localComposition.ForegroundColor),
                CssColor(localComposition.AccentColor)
                };
            var providerStage = isBackgrounds ? "artwork.backgrounds" : "artwork.covers";
            var providerContext = PipelineHandlerData.Context(job, isBackgrounds ? "backgrounds" : "covers");
            var request = new ArtworkGenerationRequest(
                providerContext,
                project.ArtistName,
                project.TrackTitle,
                new ArtworkCreativeBrief(
                    string.IsNullOrWhiteSpace(payload.Style)
                        ? "Derived from tempo, energy and the artist's release brief"
                        : $"Style direction: {payload.Style.Trim()[..Math.Min(payload.Style.Trim().Length, 300)]}. Derived from tempo, energy and the artist's release brief",
                    palette,
                    await ShortExcerptsAsync(project, cancellationToken),
                    payload.Prompt ?? pack.Prompt),
                payload.Count ?? 3,
                isBackgrounds ? 1088 : 2048,
                isBackgrounds ? 1920 : 2048,
                reference is null ? null : PipelineHandlerData.Object(reference));
            var result = await provider.GenerateAsync(request, cancellationToken);
            if (!result.IsSuccess)
            {
                await invocations.RecordAsync(
                    job,
                    providerStage,
                    providerContext,
                    result.Provenance,
                    result.Failure,
                    status: null,
                    cancellationToken);
                var terminalFailure = !result.Failure!.Retryable || job.AttemptNumber >= job.MaxAttempts;
                if (terminalFailure && !isBackgrounds)
                {
                    pack.State = RevisionState.Failed;
                    await ArtworkCreditLedger.ReleaseReservationAsync(
                        db,
                        project.WorkspaceId,
                        pack.Id,
                        cancellationToken);
                    await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
                }

                throw PipelineHandlerData.Failure(result.Failure!);
            }

            if (result.Value!.Candidates.Count != 3)
            {
                await invocations.RecordAsync(
                    job,
                    providerStage,
                    providerContext,
                    result.Provenance,
                    failure: null,
                    AiProviderInvocationLedger.Rejected,
                    cancellationToken);
                if (!isBackgrounds)
                {
                    pack.State = RevisionState.Failed;
                    await ArtworkCreditLedger.ReleaseReservationAsync(
                        db,
                        project.WorkspaceId,
                        pack.Id,
                        cancellationToken);
                    await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
                }

                throw new JobHandlerException(
                    isBackgrounds ? "artwork.background_batch_incomplete" : "artwork.candidate_batch_incomplete",
                    isBackgrounds
                        ? "Three campaign backgrounds are required."
                        : "Three cover candidates are required.",
                    retryable: false);
            }

            var latestRights = await db.RightsAttestations.AsNoTracking().SingleOrDefaultAsync(
                value => value.ProjectId == project.Id,
                cancellationToken);
            var latestGate = audio is null
                ? new ArtworkAutomationDecision(false, "audio.not_ready")
                : ArtworkAutomationGate.Evaluate(
                    project,
                    audio,
                    latestRights,
                    DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime));
            if (!latestGate.Allowed)
            {
                await invocations.RecordAsync(
                    job,
                    providerStage,
                    providerContext,
                    result.Provenance,
                    failure: null,
                    AiProviderInvocationLedger.DiscardedConsentRevoked,
                    cancellationToken);
                foreach (var artifact in result.Value.Artifacts.Where(value => value.Materialized))
                {
                    try
                    {
                        await storage.DeleteAsync(artifact.ObjectKey, cancellationToken);
                    }
                    catch
                    {
                        // Revocation wins; provider staging cleanup remains best effort.
                    }
                }

                throw new JobBlockedException(
                    latestGate.BlockerCode ?? "rights.external_ai_processing_required",
                    "Artwork was not saved because external AI processing consent is no longer active.");
            }

            await invocations.RecordAsync(
                job,
                providerStage,
                providerContext,
                result.Provenance,
                failure: null,
                status: null,
                cancellationToken);
            var assetIds = new List<Guid>();
            foreach (var candidate in result.Value!.Candidates)
            {
                var assetId = candidate.CandidateId;
                var existing = await db.MediaAssets.SingleOrDefaultAsync(value => value.Id == assetId, cancellationToken);
                if (existing is not null)
                {
                    assetIds.Add(existing.Id);
                    continue;
                }

                var role = isBackgrounds ? "backgrounds" : "candidates";
                var canonicalKey =
                    $"workspaces/{project.WorkspaceId:N}/projects/{project.Id:N}/generated/artwork/{pack.Id:N}/{role}/attempt-{job.AttemptNumber}-{job.LeaseToken:N}/{candidate.CandidateNumber}.png";
                var promoted = await artifacts.PromoteAsync(candidate.Artwork, canonicalKey, cancellationToken);
                var asset = new MediaAsset
                {
                    Id = assetId,
                    WorkspaceId = project.WorkspaceId,
                    ProjectId = project.Id,
                    Kind = isBackgrounds ? AssetKind.Visual : AssetKind.Cover,
                    Origin = AssetOrigin.Generated,
                    Purpose = isBackgrounds ? AssetPurpose.CampaignBackground : AssetPurpose.CoverCandidate,
                    State = AssetState.Ready,
                    OriginalFileName = isBackgrounds
                        ? $"campaign-background-{candidate.CandidateNumber}.png"
                        : $"cover-candidate-{candidate.CandidateNumber}.png",
                    DeclaredContentType = promoted.ContentType,
                    DetectedContentType = promoted.ContentType,
                    DeclaredBytes = promoted.SizeBytes,
                    ActualBytes = promoted.SizeBytes,
                    ObjectKey = promoted.ObjectKey,
                    Revision = pack.Number,
                    SortOrder = candidate.CandidateNumber,
                    IsActive = isBackgrounds,
                    Sha256 = promoted.Sha256,
                    Width = promoted.Width,
                    Height = promoted.Height,
                    ProvenanceJson = JsonSerializer.Serialize(result.Provenance, PipelineHandlerData.Json)
                };
                db.MediaAssets.Add(asset);
                await CreateProtectedProxiesAsync(asset, cancellationToken);
                assetIds.Add(asset.Id);
            }

            if (isBackgrounds)
            {
                pack.BackgroundAssetIdsJson = JsonSerializer.Serialize(assetIds, PipelineHandlerData.Json);
            }
            else
            {
                pack.CandidateAssetIdsJson = JsonSerializer.Serialize(assetIds, PipelineHandlerData.Json);
                pack.State = RevisionState.ReadyForReview;
                await ArtworkCreditLedger.CommitReservationAsync(
                    db,
                    project.WorkspaceId,
                    pack.Id,
                    cancellationToken);
            }

            PipelineOutbox.Reconcile(db, project, isBackgrounds ? "artwork.backgrounds_completed" : "artwork.completed", job.Id);
            await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
        }
        catch (Exception exception) when (ShouldReleaseReservation(exception, isBackgrounds, pack, job))
        {
            DiscardUncommittedCoverAssets(db, project.Id);
            pack.State = RevisionState.Failed;
            await ArtworkCreditLedger.ReleaseReservationAsync(
                db,
                project.WorkspaceId,
                pack.Id,
                cancellationToken);
            await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
            throw;
        }
    }

    internal static bool ShouldReleaseReservation(
        Exception exception,
        bool isBackgrounds,
        ArtworkPackRevision pack,
        LeasedJob job)
    {
        if (isBackgrounds || pack.State != RevisionState.Processing ||
            exception is JobBlockedException or JobDeferredException or OperationCanceledException)
        {
            return false;
        }

        return exception switch
        {
            JobHandlerException { Code: "job.lease_lost" } => false,
            JobHandlerException handler => !handler.Retryable || job.AttemptNumber >= job.MaxAttempts,
            _ => job.AttemptNumber >= job.MaxAttempts
        };
    }

    private static void DiscardUncommittedCoverAssets(Hook2StreamDbContext db, Guid projectId)
    {
        var candidates = db.ChangeTracker.Entries<MediaAsset>()
            .Where(value => value.State == EntityState.Added &&
                            value.Entity.ProjectId == projectId &&
                            value.Entity.Purpose == AssetPurpose.CoverCandidate)
            .ToArray();
        var candidateIds = candidates.Select(value => value.Entity.Id).ToHashSet();
        foreach (var derivative in db.ChangeTracker.Entries<MediaDerivative>()
                     .Where(value => value.State == EntityState.Added &&
                                     candidateIds.Contains(value.Entity.AssetId)))
        {
            derivative.State = EntityState.Detached;
        }

        foreach (var asset in candidates)
        {
            asset.State = EntityState.Detached;
        }
    }

    private async Task<IReadOnlyList<string>> ShortExcerptsAsync(
        ReleaseProject project,
        CancellationToken cancellationToken)
    {
        if (project.CurrentTranscriptRevisionId is not { } transcriptId) return [];
        var transcript = await db.TranscriptRevisions.SingleOrDefaultAsync(value => value.Id == transcriptId, cancellationToken);
        return (PipelineHandlerData.Deserialize<List<TranscriptPhraseRequest>>(transcript?.PhrasesJson ?? "[]") ?? [])
            .Select(value => value.Text)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(3)
            .ToArray();
    }

    private static string CssColor(string ffmpegColor) =>
        ffmpegColor.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? $"#{ffmpegColor[2..]}"
            : ffmpegColor;

    private async Task CreateProtectedProxiesAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        var workDirectory = Path.Combine(Path.GetTempPath(), "hook2stream-artwork-proxy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var source = Path.Combine(workDirectory, "source.png");
        var proxy = Path.Combine(workDirectory, "proxy.webp");
        var thumbnail = Path.Combine(workDirectory, "thumbnail.webp");
        try
        {
            await storage.DownloadAsync(asset.ObjectKey, source, cancellationToken);
            var timeout = TimeSpan.FromSeconds(mediaOptions.Value.ProcessTimeoutSeconds);
            var watermark = "scale=w='min(1024,iw)':h=-2,drawbox=x=0:y=ih-120:w=iw:h=120:color=black@0.65:t=fill,drawtext=text='HOOK2STREAM PREVIEW':fontcolor=white:fontsize=42:x=(w-text_w)/2:y=h-82";
            var proxyRun = await processRunner.RunAsync(
                mediaOptions.Value.FfmpegPath,
                ["-y", "-v", "error", "-i", source, "-vf", watermark, "-frames:v", "1", "-c:v", "libwebp", "-q:v", "75", proxy],
                timeout,
                workDirectory,
                cancellationToken);
            var thumbRun = await processRunner.RunAsync(
                mediaOptions.Value.FfmpegPath,
                ["-y", "-v", "error", "-i", source, "-vf", "scale=w='min(384,iw)':h=-2", "-frames:v", "1", "-c:v", "libwebp", "-q:v", "70", thumbnail],
                timeout,
                workDirectory,
                cancellationToken);
            if (proxyRun.ExitCode != 0 || thumbRun.ExitCode != 0)
            {
                throw new JobHandlerException(
                    "artwork.proxy_failed",
                    "The protected artwork preview could not be created.",
                    retryable: true);
            }

            var proxyKey = $"{asset.ObjectKey}.preview.webp";
            var thumbnailKey = $"{asset.ObjectKey}.thumbnail.webp";
            await storage.UploadAsync(proxyKey, proxy, "image/webp", cancellationToken);
            await storage.UploadAsync(thumbnailKey, thumbnail, "image/webp", cancellationToken);
            db.MediaDerivatives.AddRange(
                new MediaDerivative
                {
                    AssetId = asset.Id,
                    Kind = DerivativeKind.ImageProxy,
                    ProcessorVersion = "generated-preview-v1",
                    ObjectKey = proxyKey,
                    ContentType = "image/webp",
                    Bytes = new FileInfo(proxy).Length,
                    Sha256 = await PipelineHandlerData.Sha256Async(proxy, cancellationToken)
                },
                new MediaDerivative
                {
                    AssetId = asset.Id,
                    Kind = DerivativeKind.Thumbnail,
                    ProcessorVersion = "generated-preview-v1",
                    ObjectKey = thumbnailKey,
                    ContentType = "image/webp",
                    Bytes = new FileInfo(thumbnail).Length,
                    Sha256 = await PipelineHandlerData.Sha256Async(thumbnail, cancellationToken)
                });
        }
        finally
        {
            PipelineHandlerData.TryDelete(workDirectory);
        }
    }

    private sealed record ArtworkPayload(
        Guid ProjectId,
        Guid ArtworkPackRevisionId,
        string? Prompt,
        string? Style,
        string? Mode,
        int? Count);
}

public sealed class CampaignGenerationJobHandler(
    Hook2StreamDbContext db,
    ICampaignPlanner provider,
    IAiProviderInvocationWriter invocations) : IJobHandler
{
    public JobType Type => JobType.CampaignGeneration;
    public string Capability => "campaign";

    public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        var payload = PipelineHandlerData.Payload<CampaignPayload>(job);
        var revision = await db.CampaignPlanRevisions.SingleAsync(
            value => value.Id == payload.CampaignRevisionId && value.ProjectId == payload.ProjectId,
            cancellationToken);
        var project = await db.Projects.SingleAsync(value => value.Id == payload.ProjectId, cancellationToken);
        if (project.CurrentCampaignPlanRevisionId != revision.Id ||
            revision.State is RevisionState.Superseded or RevisionState.Failed)
        {
            throw new JobHandlerException(
                "campaign.revision_stale",
                "The queued campaign revision is no longer current.",
                retryable: false);
        }
        if (revision.State is RevisionState.ReadyForReview or RevisionState.Approved) return;
        PipelineHandlerData.EnsureFingerprint(job, revision.SourceFingerprint);
        var currentAudio = await db.MediaAssets.SingleOrDefaultAsync(
            value => value.ProjectId == project.Id &&
                     value.Kind == AssetKind.Audio &&
                     value.IsActive &&
                     value.State == AssetState.Ready,
            cancellationToken);
        var currentRights = await db.RightsAttestations.SingleOrDefaultAsync(
            value => value.ProjectId == project.Id,
            cancellationToken);
        var rightsDecision = currentAudio is null
            ? new ExternalAiProcessingDecision(false, "audio.not_ready")
            : ExternalAiProcessingGate.Evaluate(project, currentAudio, currentRights);
        if (!rightsDecision.Allowed)
        {
            throw new JobBlockedException(
                rightsDecision.BlockerCode ?? "rights.external_ai_processing_required",
                "Campaign generation is paused until rights and external AI processing are confirmed.");
        }
        var hooksRevision = await db.HookSetRevisions.SingleAsync(value => value.Id == revision.HookSetRevisionId, cancellationToken);
        var artworkRevision = await db.ArtworkPackRevisions.SingleAsync(value => value.Id == revision.ArtworkPackRevisionId, cancellationToken);
        var selectedCover = artworkRevision.SelectedAssetId is { } selectedCoverId
            ? await db.MediaAssets.SingleOrDefaultAsync(
                value => value.Id == selectedCoverId && value.ProjectId == project.Id,
                cancellationToken)
            : null;
        if (selectedCover is { Origin: AssetOrigin.Uploaded } && currentRights?.OwnsVisualRights != true)
        {
            throw new JobBlockedException(
                "rights.visual_required",
                "Campaign generation is paused until rights to the approved uploaded cover are confirmed.");
        }
        var brand = await db.BrandKits.SingleAsync(value => value.WorkspaceId == project.WorkspaceId, cancellationToken);
        if (payload.BrandKitVersion is { } queuedBrandVersion && brand.Version != queuedBrandVersion)
        {
            throw new JobHandlerException(
                "campaign.brand_stale",
                "The queued campaign no longer matches the current brand kit.",
                retryable: false);
        }
        var hooks = PipelineHandlerData.Deserialize<List<HookRequest>>(hooksRevision.HooksJson) ?? [];
        var backgroundIds = PipelineHandlerData.Deserialize<List<Guid>>(artworkRevision.BackgroundAssetIdsJson) ?? [];
        var backgrounds = await db.MediaAssets
            .Where(value => backgroundIds.Contains(value.Id) && value.State == AssetState.Ready)
            .OrderBy(value => value.SortOrder)
            .ToListAsync(cancellationToken);
        if (backgroundIds.Count != 3 || backgrounds.Count != 3)
        {
            throw new JobHandlerException(
                "campaign.backgrounds_incomplete",
                "Three approved campaign backgrounds are required.",
                retryable: false);
        }

        var providerContext = PipelineHandlerData.Context(job, "campaign");
        var planningRequest = new CampaignPlanningRequest(
                providerContext,
                project.ArtistName,
                project.TrackTitle,
                project.ReleaseDate,
                project.Mode == ReleaseMode.Released,
                brand.ToneRestrictions ?? "direct",
                brand.DefaultCta,
                hooks.Select(value => new CampaignHookInput(
                    PipelineHandlerData.StableGuid(value.Id),
                    value.Label ?? value.Kind,
                    value.StartMilliseconds,
                    value.EndMilliseconds,
                    value.Label ?? string.Empty)).ToArray(),
                backgrounds.Select(PipelineHandlerData.Object).ToArray());
        var result = await provider.PlanAsync(planningRequest, cancellationToken);
        if (!result.IsSuccess)
        {
            await invocations.RecordAsync(
                job,
                "campaign",
                providerContext,
                result.Provenance,
                result.Failure,
                status: null,
                cancellationToken);
            if (!result.Failure!.Retryable)
            {
                revision.State = RevisionState.Failed;
                await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
            }

            throw PipelineHandlerData.Failure(result.Failure!);
        }

        var latestRights = await db.RightsAttestations.AsNoTracking().SingleOrDefaultAsync(
            value => value.ProjectId == project.Id,
            cancellationToken);
        var latestDecision = currentAudio is null
            ? new ExternalAiProcessingDecision(false, "audio.not_ready")
            : ExternalAiProcessingGate.Evaluate(project, currentAudio, latestRights);
        if (!latestDecision.Allowed)
        {
            await invocations.RecordAsync(
                job,
                "campaign",
                providerContext,
                result.Provenance,
                failure: null,
                AiProviderInvocationLedger.DiscardedConsentRevoked,
                cancellationToken);
            throw new JobBlockedException(
                latestDecision.BlockerCode ?? "rights.external_ai_processing_required",
                "Campaign copy was not saved because external AI processing consent is no longer active.");
        }

        var validation = CampaignPlanContractValidator.Validate(planningRequest, result.Value!.Items);
        if (!validation.IsValid)
        {
            await invocations.RecordAsync(
                job,
                "campaign",
                providerContext,
                result.Provenance,
                failure: null,
                AiProviderInvocationLedger.Rejected,
                cancellationToken);
            throw new JobHandlerException(
                "campaign.recipe_invalid",
                "The campaign provider returned an invalid 18-item campaign recipe.",
                retryable: false);
        }

        await invocations.RecordAsync(
            job,
            "campaign",
            providerContext,
            result.Provenance,
            failure: null,
            status: null,
            cancellationToken);
        var items = result.Value.Items.Select((value, index) =>
        {
            var background = backgrounds[index % backgrounds.Count];
            var selectedBackgroundId = string.Equals(
                value.TemplateKey,
                "animated-cover",
                StringComparison.OrdinalIgnoreCase)
                ? (Guid?)null
                : background.Id;
            return new CampaignItemRequest(
                value.ItemId,
                value.Sequence,
                value.TemplateKey,
                value.HookId?.ToString("N") ?? string.Empty,
                selectedBackgroundId,
                value.Caption,
                MergeCampaignComposition(value, selectedBackgroundId, brand));
        }).ToArray();

        revision.ItemsJson = JsonSerializer.Serialize(items, PipelineHandlerData.Json);
        revision.State = RevisionState.ReadyForReview;
        project.State = ProjectState.CampaignReady;
        PipelineOutbox.Reconcile(db, project, "campaign.completed", job.Id);
        await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
    }

    private static string MergeCampaignComposition(
        CampaignItemPlan item,
        Guid? backgroundAssetId,
        BrandKit brand)
    {
        JsonObject composition;
        try
        {
            composition = JsonNode.Parse(item.CompositionJson)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            composition = new JsonObject();
        }

        composition["relativeDay"] = item.RelativeDay;
        composition["headline"] = item.Headline;
        composition["cta"] = item.CallToAction;
        composition["durationMilliseconds"] = item.DurationMilliseconds;
        composition["primaryColor"] = brand.PrimaryColor;
        composition["secondaryColor"] = brand.SecondaryColor;
        composition["brandVersion"] = brand.Version;
        composition["backgroundAssetId"] = backgroundAssetId;
        return composition.ToJsonString(PipelineHandlerData.Json);
    }

    private sealed record CampaignPayload(Guid ProjectId, Guid CampaignRevisionId, long? BrandKitVersion);
}

public sealed class VideoRenderJobHandler(
    JobType type,
    Hook2StreamDbContext db,
    IVideoRenderer provider,
    IPipelineArtifactStore artifacts,
    DeterministicVideoRenderer deterministicRenderer) : IJobHandler
{
    public JobType Type { get; } = type is JobType.PreviewRender or JobType.FinalRender
        ? type
        : throw new ArgumentOutOfRangeException(nameof(type));
    public string Capability => "render";

    public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        var payload = PipelineHandlerData.Payload<RenderPayload>(job);
        var project = await db.Projects.SingleAsync(
            value => value.Id == payload.ProjectId && value.WorkspaceId == job.WorkspaceId,
            cancellationToken);
        if (job.Type != Type)
        {
            throw new JobHandlerException(
                "render.handler_mismatch",
                "The render operation was routed to the wrong handler.",
                retryable: false);
        }

        var campaign = await db.CampaignPlanRevisions.SingleAsync(
            value => value.Id == payload.CampaignRevisionId && value.ProjectId == project.Id,
            cancellationToken);
        RenderBatch? renderBatch = null;
        if (Type == JobType.FinalRender)
        {
            if (payload.RenderBatchId is not { } batchId)
            {
                throw new JobHandlerException(
                    "render.batch_required",
                    "A final render must belong to a purchased render batch.",
                    retryable: false);
            }

            renderBatch = await db.RenderBatches.SingleAsync(
                value => value.Id == batchId &&
                         value.ProjectId == project.Id &&
                         value.WorkspaceId == job.WorkspaceId,
                cancellationToken);
            if (renderBatch.State == RenderBatchState.Queued)
            {
                renderBatch.State = RenderBatchState.Running;
                await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
            }
        }

        var existing = await db.MediaAssets.SingleOrDefaultAsync(
            value => value.ProjectId == project.Id &&
                     value.RenderBatchId == payload.RenderBatchId &&
                     value.CampaignItemId == payload.CampaignItemId &&
                     value.Purpose == (job.Type == JobType.PreviewRender ? AssetPurpose.PreviewVideo : AssetPurpose.CampaignVideo) &&
                     value.ProvenanceJson != null && value.ProvenanceJson.Contains(job.Id.ToString("N")),
            cancellationToken);
        if (existing is not null) return;
        var item = (PipelineHandlerData.Deserialize<List<CampaignItemRequest>>(campaign.ItemsJson) ?? [])
            .Single(value => value.Id == payload.CampaignItemId);
        if (payload.AudioAssetId is not { } audioAssetId ||
            string.IsNullOrWhiteSpace(payload.AudioFingerprint))
        {
            throw new JobHandlerException(
                "render.audio_snapshot_required",
                "A render requires an immutable audio snapshot.",
                retryable: false);
        }
        var audio = await db.MediaAssets.SingleOrDefaultAsync(
            value => value.Id == audioAssetId &&
                     value.ProjectId == project.Id &&
                     value.WorkspaceId == job.WorkspaceId &&
                     value.Kind == AssetKind.Audio &&
                     value.State == AssetState.Ready,
            cancellationToken) ?? throw new JobHandlerException(
            "render.audio_snapshot_unavailable",
            "The audio snapshot selected for this render is no longer available.",
            retryable: false);
        if (!string.Equals(audio.Sha256, payload.AudioFingerprint, StringComparison.Ordinal))
        {
            throw new JobHandlerException(
                "render.audio_snapshot_mismatch",
                "The selected audio no longer matches the queued render snapshot.",
                retryable: false);
        }
        if (Type == JobType.PreviewRender &&
            (project.CurrentCampaignPlanRevisionId != campaign.Id || !audio.IsActive))
        {
            throw new JobHandlerException(
                "render.preview_stale",
                "The queued preview no longer matches the current release inputs.",
                retryable: false);
        }
        var renderRights = await db.RightsAttestations.SingleOrDefaultAsync(
            value => value.ProjectId == project.Id,
            cancellationToken);
        var renderRightsDecision = ContentRightsGate.Evaluate(project, audio, renderRights);
        if (!renderRightsDecision.Allowed)
        {
            throw new JobBlockedException(
                renderRightsDecision.BlockerCode ?? "rights.required",
                "Rendering is paused until rights to the selected audio are confirmed.");
        }
        var artworkPack = await db.ArtworkPackRevisions.SingleAsync(value => value.Id == campaign.ArtworkPackRevisionId, cancellationToken);
        var selectedCoverSource = await db.MediaAssets.SingleAsync(
            value => value.Id == artworkPack.SelectedAssetId && value.ProjectId == project.Id,
            cancellationToken);
        var cover = await db.MediaAssets
            .Where(value => value.ProjectId == project.Id &&
                            value.ArtworkPackRevisionId == artworkPack.Id &&
                            value.Purpose == AssetPurpose.CleanCover &&
                            value.State == AssetState.Ready)
            .OrderByDescending(value => value.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? selectedCoverSource;
        var background = item.BackgroundAssetId is { } backgroundId
            ? await db.MediaAssets.SingleOrDefaultAsync(value => value.Id == backgroundId, cancellationToken)
            : null;
        if ((selectedCoverSource.Origin == AssetOrigin.Uploaded || background is { Origin: AssetOrigin.Uploaded }) &&
            renderRights?.OwnsVisualRights != true)
        {
            throw new JobBlockedException(
                "rights.visual_required",
                "Rendering is paused until rights to the selected uploaded visual are confirmed.");
        }
        var hooksRevision = await db.HookSetRevisions.SingleAsync(value => value.Id == campaign.HookSetRevisionId, cancellationToken);
        var hooks = PipelineHandlerData.Deserialize<List<HookRequest>>(hooksRevision.HooksJson) ?? [];
        var hook = hooks.FirstOrDefault(value => value.Id == item.HookId) ?? hooks.First();
        var controls = VideoCompositionControls.Parse(
            item,
            hook,
            audio.DurationMilliseconds,
            "#000000",
            "#ffffff",
            0);
        controls = controls.BindSources(audio, cover, background, controls.BrandVersion);
        var profile = job.Type == JobType.PreviewRender
            ? new VideoRenderProfile(540, 960, 30, "h264", "aac", Watermarked: true)
            : new VideoRenderProfile(1080, 1920, 30, "h264", "aac", Watermarked: false);
        var request = new VideoRenderRequest(
            PipelineHandlerData.Context(
                job,
                job.Type == JobType.PreviewRender ? "preview" : "final",
                controls.CompositionHash),
            new VideoCompositionSpec(
                item.Id,
                item.Template,
                PipelineHandlerData.Object(audio),
                PipelineHandlerData.Object(cover),
                background is null ? null : PipelineHandlerData.Object(background),
                controls.Headline,
                controls.Caption,
                controls.PrimaryColor,
                controls.SecondaryColor,
                controls.FocalX,
                controls.FocalY,
                hook.StartMilliseconds,
                controls.EndMilliseconds,
                controls.Fit,
                controls.Opening,
                controls.TextLayout,
                controls.CallToAction,
                controls.DurationMilliseconds,
                controls.CompositionHash),
            profile);
        var result = await provider.RenderAsync(request, cancellationToken);
        if (!result.IsSuccess) throw PipelineHandlerData.Failure(result.Failure!);

        var renderResult = result.Value!;
        ProviderProvenance? materializationProvenance = null;
        if (!renderResult.Video.Materialized || !renderResult.Poster.Materialized)
        {
            var materialized = await deterministicRenderer.RenderAsync(request, cancellationToken);
            if (!materialized.IsSuccess) throw PipelineHandlerData.Failure(materialized.Failure!);
            renderResult = materialized.Value!;
            materializationProvenance = materialized.Provenance;
        }

        var canonical = VideoObjectKey(project, campaign, item, profile, payload.RenderBatchId, job);
        var promoted = await artifacts.PromoteAsync(renderResult.Video, canonical, cancellationToken);
        var poster = await artifacts.PromoteAsync(
            renderResult.Poster,
            PosterObjectKey(project, campaign, item, profile, payload.RenderBatchId, job),
            cancellationToken);

        var asset = new MediaAsset
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Kind = AssetKind.Visual,
            Origin = AssetOrigin.Generated,
            Purpose = profile.Watermarked ? AssetPurpose.PreviewVideo : AssetPurpose.CampaignVideo,
            State = AssetState.Ready,
            OriginalFileName = profile.Watermarked ? "watermarked-preview.mp4" : $"campaign-{item.Slot:00}.mp4",
            DeclaredContentType = promoted.ContentType,
            DetectedContentType = promoted.ContentType,
            DeclaredBytes = promoted.SizeBytes,
            ActualBytes = promoted.SizeBytes,
            ObjectKey = promoted.ObjectKey,
            IsActive = true,
            CampaignItemId = item.Id,
            RenderBatchId = payload.RenderBatchId,
            Sha256 = promoted.Sha256,
            DurationMilliseconds = promoted.DurationMilliseconds,
            Width = promoted.Width,
            Height = promoted.Height,
            VideoCodec = "h264",
            AudioCodec = "aac",
            ProvenanceJson = JsonSerializer.Serialize(new
            {
                jobId = job.Id.ToString("N"),
                campaignRevisionId = campaign.Id,
                campaignItemId = item.Id,
                compositionHash = controls.CompositionHash,
                provider = result.Provenance,
                materializer = materializationProvenance
            }, PipelineHandlerData.Json)
        };
        asset.Derivatives.Add(new MediaDerivative
        {
            Kind = DerivativeKind.Thumbnail,
            ProcessorVersion = "deterministic-render-v1",
            ObjectKey = poster.ObjectKey,
            ContentType = poster.ContentType,
            Bytes = poster.SizeBytes,
            Sha256 = poster.Sha256,
            Width = poster.Width,
            Height = poster.Height
        });
        db.MediaAssets.Add(asset);
        if (profile.Watermarked)
        {
            project.State = ProjectState.PreviewReady;
        }
        PipelineOutbox.Reconcile(db, project, profile.Watermarked ? "preview.completed" : "render.completed", job.Id);
        await PipelineHandlerData.CommitAsync(db, job, cancellationToken);
    }

    private static string VideoObjectKey(
        ReleaseProject project,
        CampaignPlanRevision campaign,
        CampaignItemRequest item,
        VideoRenderProfile profile,
        Guid? renderBatchId,
        LeasedJob job) =>
        profile.Watermarked
            ? $"workspaces/{project.WorkspaceId:N}/projects/{project.Id:N}/generated/video/{campaign.Id:N}/{item.Id:N}/attempt-{job.AttemptNumber}-{job.LeaseToken:N}/preview.mp4"
            : $"workspaces/{project.WorkspaceId:N}/projects/{project.Id:N}/generated/video/{campaign.Id:N}/final/{renderBatchId?.ToString("N") ?? "unbatched"}/{item.Id:N}/attempt-{job.AttemptNumber}-{job.LeaseToken:N}.mp4";

    private static string PosterObjectKey(
        ReleaseProject project,
        CampaignPlanRevision campaign,
        CampaignItemRequest item,
        VideoRenderProfile profile,
        Guid? renderBatchId,
        LeasedJob job) =>
        profile.Watermarked
            ? $"workspaces/{project.WorkspaceId:N}/projects/{project.Id:N}/generated/video/{campaign.Id:N}/{item.Id:N}/attempt-{job.AttemptNumber}-{job.LeaseToken:N}/preview-poster.jpg"
            : $"workspaces/{project.WorkspaceId:N}/projects/{project.Id:N}/generated/video/{campaign.Id:N}/final/{renderBatchId?.ToString("N") ?? "unbatched"}/{item.Id:N}/attempt-{job.AttemptNumber}-{job.LeaseToken:N}.jpg";

    private sealed record RenderPayload(
        Guid ProjectId,
        Guid CampaignRevisionId,
        Guid CampaignItemId,
        Guid? RenderBatchId,
        RenderRequestKind? Kind,
        Guid? AudioAssetId,
        string? AudioFingerprint);
}

public sealed record VideoCompositionControls(
    string Headline,
    string Caption,
    string CallToAction,
    string PrimaryColor,
    string SecondaryColor,
    long BrandVersion,
    string Fit,
    double FocalX,
    double FocalY,
    string Opening,
    string TextLayout,
    long DurationMilliseconds,
    long EndMilliseconds,
    string CompositionHash)
{
    private static readonly HashSet<string> Fits = ["fill", "fit"];
    private static readonly HashSet<string> Openings = ["fade", "punch", "reveal"];
    private static readonly HashSet<string> TextLayouts = ["center", "lowerThird", "stacked"];

    public static VideoCompositionControls Parse(
        CampaignItemRequest item,
        HookRequest hook,
        long? audioDurationMilliseconds,
        string fallbackPrimaryColor = "#121212",
        string fallbackSecondaryColor = "#fffaf2",
        long fallbackBrandVersion = 0)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(item.CompositionJson) ? "{}" : item.CompositionJson);
            var root = document.RootElement;
            var duration = Math.Clamp(Integer(root, "durationMilliseconds", 15_000), 10_000, 30_000);
            var audioEnd = audioDurationMilliseconds ?? hook.StartMilliseconds + duration;
            var end = Math.Min(audioEnd, hook.StartMilliseconds + duration);
            if (end <= hook.StartMilliseconds)
            {
                throw Invalid();
            }

            var controls = new VideoCompositionControls(
                Text(root, "headline", item.Text, 500),
                Text(root, "caption", item.Text, 2_000),
                Text(root, "cta", Text(root, "callToAction", "Listen now", 300), 300),
                Color(root, "primaryColor", fallbackPrimaryColor),
                Color(root, "secondaryColor", fallbackSecondaryColor),
                Integer(root, "brandVersion", fallbackBrandVersion),
                Allowed(root, "fit", Fits, "fill"),
                Number(root, "focalX", .5),
                Number(root, "focalY", .5),
                Allowed(root, "opening", Openings, "fade"),
                Allowed(root, "textLayout", TextLayouts, "center"),
                end - hook.StartMilliseconds,
                end,
                string.Empty);
            return controls with { CompositionHash = controls.Hash(item, hook, null) };
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    public VideoCompositionControls BindSources(
        MediaAsset audio,
        MediaAsset cover,
        MediaAsset? background,
        long brandVersion)
    {
        var source = new
        {
            audio = audio.Sha256,
            cover = cover.Sha256,
            background = background?.Sha256,
            brandVersion
        };
        return this with { CompositionHash = Hash(item: null, hook: null, source) };
    }

    private string Hash(CampaignItemRequest? item, HookRequest? hook, object? source)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            itemId = item?.Id,
            template = item?.Template,
            backgroundAssetId = item?.BackgroundAssetId,
            itemText = item?.Text,
            hookId = hook?.Id,
            hookStartMilliseconds = hook?.StartMilliseconds,
            Headline,
            Caption,
            CallToAction,
            PrimaryColor,
            SecondaryColor,
            BrandVersion,
            Fit,
            FocalX,
            FocalY,
            Opening,
            TextLayout,
            DurationMilliseconds,
            EndMilliseconds,
            source,
            priorCompositionHash = string.IsNullOrEmpty(CompositionHash) ? null : CompositionHash
        }, PipelineHandlerData.Json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static long Integer(JsonElement root, string name, long fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed)
            ? parsed
            : fallback;

    private static double Number(JsonElement root, string name, double fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed)
            ? Math.Clamp(parsed, 0, 1)
            : fallback;

    private static string Text(
        JsonElement root,
        string name,
        string fallback,
        int maximumLength)
    {
        var value = root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? fallback
            : fallback;
        value = value.Replace('\0', ' ').Trim();
        return value[..Math.Min(value.Length, maximumLength)];
    }

    private static string Allowed(
        JsonElement root,
        string name,
        IReadOnlySet<string> allowed,
        string fallback)
    {
        var value = root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
        return value is not null && allowed.Contains(value) ? value : fallback;
    }

    private static string Color(JsonElement root, string name, string fallback)
    {
        var value = Text(root, name, fallback, 7);
        return value.Length == 7 && value[0] == '#' &&
               value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0
            ? value.ToUpperInvariant()
            : fallback;
    }

    private static JobHandlerException Invalid() => new(
        "render.composition_invalid",
        "The campaign composition contains invalid controls.",
        retryable: false);
}

internal static class PipelineHandlerData
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static T Payload<T>(LeasedJob job)
    {
        if (job.PayloadSchemaVersion != 1)
        {
            throw new JobHandlerException(
                "job.payload_schema_unsupported",
                "The queued operation uses an unsupported payload schema.",
                retryable: false);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(job.PayloadJson, Json)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new JobHandlerException(
                "job.payload_invalid",
                "The queued operation payload is invalid.",
                retryable: false,
                exception);
        }
    }

    public static ProviderExecutionContext Context(
        LeasedJob job,
        string purpose,
        string? inputHash = null) =>
        new(
            job.Id,
            inputHash ?? job.InputFingerprint ?? Hash(job.PayloadJson),
            Hash($"{job.HandlerVersion}:{job.PayloadSchemaVersion}:{purpose}"),
            $"staging/{job.WorkspaceId:N}/{job.ProjectId?.ToString("N") ?? "global"}/{job.Id:N}/attempt-{job.AttemptNumber}");

    public static ProviderObjectReference Object(MediaAsset asset) =>
        new(
            asset.Id,
            asset.ObjectKey,
            asset.Sha256 ?? throw new JobHandlerException(
                "asset.hash_missing",
                "The source asset has no immutable fingerprint.",
                retryable: false),
            asset.DetectedContentType ?? asset.DeclaredContentType,
            asset.ActualBytes ?? asset.DeclaredBytes,
            asset.DurationMilliseconds,
            asset.Width,
            asset.Height);

    public static void EnsureFingerprint(LeasedJob job, string expected)
    {
        if (!string.Equals(job.InputFingerprint, expected, StringComparison.Ordinal))
        {
            throw new JobHandlerException(
                "job.input_stale",
                "The queued operation no longer matches the current revision.",
                retryable: false);
        }
    }

    public static async Task CommitAsync(
        Hook2StreamDbContext db,
        LeasedJob job,
        CancellationToken cancellationToken)
    {
        try
        {
            await JobLeaseFence.CommitAsync(db, job, cancellationToken);
        }
        catch (JobLeaseFenceException exception)
        {
            throw new JobHandlerException(
                "job.lease_lost",
                "The operation lease was lost before its result could be committed.",
                retryable: true,
                exception);
        }
    }

    public static JobHandlerException Failure(ProviderFailure failure) =>
        new(failure.Code, failure.SafeMessage, failure.Retryable);

    public static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static Guid StableGuid(string value)
    {
        if (Guid.TryParse(value, out var parsed)) return parsed;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Temporary staging cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Container lifecycle cleanup can remove inaccessible files later.
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
