using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;

namespace Hook2Stream.Infrastructure.Providers;

public sealed class FixtureAudioAnalysisProvider(TimeProvider timeProvider) : IAudioAnalysisProvider
{
    public Task<ProviderResult<AudioAnalysisResult>> AnalyzeAsync(
        AudioAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provenance = FixtureProviderData.Provenance(request.Context, "audio-analysis", timeProvider);
        if (!FixtureProviderData.HasValidSource(request.Audio))
        {
            return Task.FromResult(ProviderResult<AudioAnalysisResult>.Failed(
                FixtureProviderData.InvalidSource(),
                provenance));
        }

        var duration = Math.Max(request.Audio.DurationMilliseconds ?? 180_000, 10_000);
        var tempoSeed = FixtureProviderData.SeedInt(request.Context, "tempo");
        var bpm = 90d + tempoSeed % 50;
        var beatInterval = (long)Math.Round(60_000d / bpm, MidpointRounding.AwayFromZero);
        var beats = Enumerable.Range(0, checked((int)(duration / beatInterval)))
            .Select(index => (long)index * beatInterval)
            .ToArray();
        var sectionLength = duration / 5;
        string[] sectionKinds = ["intro", "verse", "chorus", "drop", "outro"];
        var sections = sectionKinds.Select((kind, index) => new AudioSection(
            kind,
            index * sectionLength,
            index == sectionKinds.Length - 1 ? duration : (index + 1) * sectionLength,
            0.9)).ToArray();
        var energy = Enumerable.Range(0, 11)
            .Select(index => new EnergyPoint(
                duration * index / 10,
                Math.Round(0.25 + ((tempoSeed + index * 17) % 70) / 100d, 2)))
            .ToArray();
        var artifacts = new[]
        {
            FixtureProviderData.Artifact(
                request.Context,
                "normalized-audio",
                "audio/wav",
                ".wav",
                duration * 192,
                durationMilliseconds: duration)
        };

        var result = new AudioAnalysisResult(
            duration,
            bpm,
            beats,
            sections,
            energy,
            0.1,
            artifacts);
        return Task.FromResult(ProviderResult<AudioAnalysisResult>.Succeeded(result, provenance));
    }
}

public sealed class FixtureTranscriptionProvider(TimeProvider timeProvider) : ITranscriptionProvider
{
    public Task<ProviderResult<TranscriptionResult>> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provenance = FixtureProviderData.Provenance(request.Context, "transcription", timeProvider);
        if (!FixtureProviderData.HasValidSource(request.Audio))
        {
            return Task.FromResult(ProviderResult<TranscriptionResult>.Failed(
                FixtureProviderData.InvalidSource(),
                provenance));
        }

        var language = request.Language.Trim().ToLowerInvariant();
        if (language is not ("ru" or "en"))
        {
            return Task.FromResult(ProviderResult<TranscriptionResult>.Failed(
                new ProviderFailure(
                    ProviderFailureKind.UserInput,
                    "provider.language_not_supported",
                    "Automatic transcription currently supports Russian and English."),
                provenance));
        }

        var instrumental = request.InstrumentalHint == true;
        var phrases = instrumental
            ? []
            : CreatePhrases(request.Context, language);
        var artifact = FixtureProviderData.Artifact(
            request.Context,
            "transcript",
            "application/json",
            ".json",
            2_048);
        var result = new TranscriptionResult(
            language,
            0.99,
            instrumental,
            false,
            phrases,
            [artifact]);
        return Task.FromResult(ProviderResult<TranscriptionResult>.Succeeded(result, provenance));
    }

    private static IReadOnlyList<TranscriptionPhrase> CreatePhrases(
        ProviderExecutionContext context,
        string language)
    {
        string[] texts = language == "ru"
            ? ["Демо строка звучит", "Ритм ведёт вперёд", "Припев начинается здесь"]
            : ["The demo line begins", "The rhythm carries on", "The chorus starts right here"];

        return texts.Select((text, phraseIndex) =>
        {
            var phraseStart = 5_000L + phraseIndex * 8_000L;
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var wordDuration = 5_000L / words.Length;
            var wordResults = words.Select((word, wordIndex) => new TranscriptionWord(
                FixtureProviderData.StableId(context, $"phrase-{phraseIndex}-word-{wordIndex}"),
                word,
                phraseStart + wordIndex * wordDuration,
                phraseStart + (wordIndex + 1) * wordDuration,
                wordIndex == words.Length - 1 && phraseIndex == 1 ? 0.64 : 0.96)).ToArray();
            return new TranscriptionPhrase(
                FixtureProviderData.StableId(context, $"phrase-{phraseIndex}"),
                text,
                phraseStart,
                phraseStart + 5_000,
                wordResults.Min(word => word.Confidence),
                wordResults);
        }).ToArray();
    }
}

public sealed class FixtureArtworkProvider(TimeProvider timeProvider) : IArtworkProvider
{
    public Task<ProviderResult<ArtworkGenerationResult>> GenerateAsync(
        ArtworkGenerationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provenance = FixtureProviderData.Provenance(request.Context, "artwork", timeProvider);
        if (request.CandidateCount is < 1 or > 4 || request.Width <= 0 || request.Height <= 0)
        {
            return Task.FromResult(ProviderResult<ArtworkGenerationResult>.Failed(
                new ProviderFailure(
                    ProviderFailureKind.UserInput,
                    "provider.invalid_artwork_dimensions",
                    "Artwork dimensions and candidate count are invalid."),
                provenance));
        }

        var candidates = Enumerable.Range(1, request.CandidateCount)
            .Select(number =>
            {
                var artifact = FixtureProviderData.Artifact(
                    request.Context,
                    $"cover-candidate-{number}",
                    "image/png",
                    ".png",
                    request.Width * (long)request.Height * 2,
                    width: request.Width,
                    height: request.Height);
                return new ArtworkCandidate(artifact.ArtifactId, number, artifact);
            }).ToArray();
        var result = new ArtworkGenerationResult(
            candidates,
            candidates.Select(candidate => candidate.Artwork).ToArray());
        return Task.FromResult(ProviderResult<ArtworkGenerationResult>.Succeeded(result, provenance));
    }
}

public sealed class FixtureCampaignPlanner(TimeProvider timeProvider) : ICampaignPlanner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ProviderResult<CampaignPlanningResult>> PlanAsync(
        CampaignPlanningRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provenance = FixtureProviderData.Provenance(request.Context, "campaign-planning", timeProvider);
        if (request.Hooks.Count != 3)
        {
            return Task.FromResult(ProviderResult<CampaignPlanningResult>.Failed(
                new ProviderFailure(
                    ProviderFailureKind.UserInput,
                    "provider.insufficient_hooks",
                    "Exactly three approved hooks are required to plan a campaign."),
                provenance));
        }

        var items = CampaignPlanContractValidator.CanonicalSlots(request.IsAlreadyReleased)
            .Select((slot, index) =>
            {
                var sequence = index + 1;
                var hook = slot.HookIndex is { } hookIndex ? request.Hooks[hookIndex] : null;
                var duration = hook is null
                    ? 15_000
                    : Math.Clamp(
                        hook.EndMilliseconds - hook.StartMilliseconds,
                        CampaignPlanContractValidator.MinimumDurationMilliseconds,
                        CampaignPlanContractValidator.MaximumDurationMilliseconds);
                var headline = Headline(request, slot);
                var caption = Caption(request, slot, hook);
                var artworkId = request.Artwork.Count == 0
                    ? (Guid?)null
                    : request.Artwork[index % request.Artwork.Count].AssetId;
                return new CampaignItemPlan(
                    FixtureProviderData.StableId(
                        request.Context,
                        $"campaign-{sequence}-{slot.TemplateKey}-{slot.Variant}"),
                    sequence,
                    slot.RelativeDay,
                    slot.TemplateKey,
                    hook?.HookId,
                    headline,
                    caption,
                    request.CallToAction,
                    artworkId,
                    duration,
                    Composition(request, slot, caption, duration));
            })
            .ToArray();

        var validation = CampaignPlanContractValidator.Validate(request, items);
        if (!validation.IsValid)
        {
            return Task.FromResult(ProviderResult<CampaignPlanningResult>.Failed(
                new ProviderFailure(
                    ProviderFailureKind.Permanent,
                    "provider.invalid_campaign_recipe",
                    "The campaign provider returned an invalid canonical recipe."),
                provenance));
        }

        var artifact = FixtureProviderData.Artifact(
            request.Context,
            "campaign-plan",
            "application/json",
            ".json",
            8_192);
        var result = new CampaignPlanningResult(items, [artifact]);
        return Task.FromResult(ProviderResult<CampaignPlanningResult>.Succeeded(result, provenance));
    }

    private static string Headline(
        CampaignPlanningRequest request,
        CampaignRecipeSlot slot) => slot.TemplateKey switch
        {
            "countdown" => $"{Math.Abs(slot.RelativeDay)} days to {request.TrackTitle}",
            "out-now" => $"{request.TrackTitle} is out now",
            "post-release-cta" => $"Keep {request.TrackTitle} moving",
            "teaser" => slot.Variant == 1
                ? $"First look: {request.TrackTitle}"
                : $"Another side of {request.TrackTitle}",
            _ => $"{request.ArtistName} — {request.TrackTitle}"
        };

    private static string Caption(
        CampaignPlanningRequest request,
        CampaignRecipeSlot slot,
        CampaignHookInput? hook) => slot.TemplateKey switch
        {
            "countdown" => $"{Math.Abs(slot.RelativeDay)} days until {request.TrackTitle}.",
            "out-now" => slot.Variant == 1
                ? $"{request.TrackTitle} by {request.ArtistName} is available now."
                : $"Press play on {request.TrackTitle} by {request.ArtistName}.",
            "post-release-cta" => slot.Variant == 1
                ? $"Put {request.TrackTitle} back in rotation."
                : $"Share your favorite moment from {request.TrackTitle}.",
            "teaser" => slot.Variant == 1
                ? $"A first look at {request.TrackTitle}."
                : $"A different moment from {request.TrackTitle}.",
            _ when !string.IsNullOrWhiteSpace(hook?.Excerpt) => hook.Excerpt,
            _ => $"A highlight from {request.TrackTitle}."
        };

    private static string Composition(
        CampaignPlanningRequest request,
        CampaignRecipeSlot slot,
        string caption,
        long durationMilliseconds)
    {
        var hashtags = new[]
        {
            Hashtag(request.ArtistName, "Artist"),
            Hashtag(request.TrackTitle, "NewMusic"),
            "#NewMusic"
        }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hashtagText = string.Join(' ', hashtags);
        var neutral = $"{request.ArtistName} — {request.TrackTitle}. {request.CallToAction}";
        var emotional = $"{caption} {request.CallToAction}";
        return JsonSerializer.Serialize(new
        {
            durationMilliseconds,
            opening = slot.Variant == 1 ? "title-card" : "cold-open",
            hashtags,
            copyVariants = new
            {
                neutral,
                emotional,
                destinations = new
                {
                    tiktok = $"{emotional} {hashtagText}",
                    youtubeShorts = $"{neutral} {hashtagText}",
                    instagramReels = $"{caption} {request.CallToAction} {hashtagText}",
                    vkClips = $"{neutral} {hashtagText}"
                }
            }
        }, Json);
    }

    private static string Hashtag(string source, string fallback)
    {
        var normalized = new string(source.Where(char.IsLetterOrDigit).Take(40).ToArray());
        return $"#{(string.IsNullOrWhiteSpace(normalized) ? fallback : normalized)}";
    }
}

public sealed class FixtureVideoRenderer(TimeProvider timeProvider) : IVideoRenderer
{
    public Task<ProviderResult<VideoRenderResult>> RenderAsync(
        VideoRenderRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provenance = FixtureProviderData.Provenance(request.Context, "video-render", timeProvider);
        var composition = request.Composition;
        var profile = request.Profile;
        if (composition.HookStartMilliseconds < 0 ||
            composition.HookEndMilliseconds <= composition.HookStartMilliseconds ||
            profile.Width <= 0 || profile.Height <= 0 || profile.FramesPerSecond <= 0)
        {
            return Task.FromResult(ProviderResult<VideoRenderResult>.Failed(
                new ProviderFailure(
                    ProviderFailureKind.UserInput,
                    "provider.invalid_composition",
                    "The video composition has invalid timing or output dimensions."),
                provenance));
        }

        var duration = composition.HookEndMilliseconds - composition.HookStartMilliseconds;
        var video = FixtureProviderData.Artifact(
            request.Context,
            "video",
            "video/mp4",
            ".mp4",
            Math.Max(1_000_000, duration * profile.Width * profile.Height / 20_000),
            duration,
            profile.Width,
            profile.Height);
        var poster = FixtureProviderData.Artifact(
            request.Context,
            "poster",
            "image/jpeg",
            ".jpg",
            Math.Max(100_000, profile.Width * (long)profile.Height / 4),
            width: profile.Width,
            height: profile.Height);
        var result = new VideoRenderResult(video, poster, [video, poster]);
        return Task.FromResult(ProviderResult<VideoRenderResult>.Succeeded(result, provenance));
    }
}

internal static class FixtureProviderData
{
    private const string ProviderName = "hook2stream.fixture";
    private const string Version = "fixture-v1";

    public static ProviderProvenance Provenance(
        ProviderExecutionContext context,
        string model,
        TimeProvider timeProvider)
    {
        var startedAt = timeProvider.GetUtcNow();
        return new ProviderProvenance(
            ProviderName,
            model,
            Version,
            Hash(context, $"request-{model}")[..24],
            context.InputHash,
            context.ParameterHash,
            startedAt,
            timeProvider.GetUtcNow());
    }

    public static ProviderFailure InvalidSource() =>
        new(
            ProviderFailureKind.UserInput,
            "provider.invalid_source",
            "The source artifact is missing required immutable metadata.");

    public static bool HasValidSource(ProviderObjectReference source) =>
        source.AssetId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(source.ObjectKey) &&
        source.SizeBytes > 0 &&
        IsSha256(source.Sha256);

    public static int SeedInt(ProviderExecutionContext context, string purpose)
    {
        var bytes = Convert.FromHexString(Hash(context, purpose));
        return BitConverter.ToUInt16(bytes, 0);
    }

    public static Guid StableId(ProviderExecutionContext context, string purpose)
    {
        var bytes = Convert.FromHexString(Hash(context, purpose))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    public static ProviderArtifactManifest Artifact(
        ProviderExecutionContext context,
        string role,
        string contentType,
        string extension,
        long sizeBytes,
        long? durationMilliseconds = null,
        int? width = null,
        int? height = null)
    {
        var prefix = context.StagingPrefix.Trim().Trim('/');
        var objectKey = $"{prefix}/{role}{extension}";
        return new ProviderArtifactManifest(
            StableId(context, $"artifact-{role}"),
            role,
            objectKey,
            Hash(context, $"artifact-content-{role}"),
            contentType,
            sizeBytes,
            Materialized: false,
            durationMilliseconds,
            width,
            height);
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string Hash(ProviderExecutionContext context, string purpose)
    {
        var input = $"{context.InputHash}\n{context.ParameterHash}\n{purpose}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
