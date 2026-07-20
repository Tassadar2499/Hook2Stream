namespace Hook2Stream.Application;

public enum ProviderFailureKind
{
    UserInput = 1,
    Moderation = 2,
    Transient = 3,
    Authentication = 4,
    Quota = 5,
    Permanent = 6,
    Unknown = 7
}

public sealed record ProviderFailure(
    ProviderFailureKind Kind,
    string Code,
    string SafeMessage,
    TimeSpan? RetryAfter = null)
{
    public bool Retryable => Kind == ProviderFailureKind.Transient;
}

public sealed record ProviderProvenance(
    string Provider,
    string Model,
    string Version,
    string? RequestId,
    string InputHash,
    string ParameterHash,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? RequestedModel = null,
    string? ResolvedProvider = null,
    string? GenerationId = null,
    ProviderUsage? Usage = null);

public sealed record ProviderUsage(
    long? InputTokens = null,
    long? OutputTokens = null,
    long? TotalTokens = null,
    double? AudioSeconds = null,
    int? GeneratedImages = null,
    decimal? CostUsd = null);

public sealed record ProviderExecutionContext(
    Guid OperationId,
    string InputHash,
    string ParameterHash,
    string StagingPrefix);

public sealed record ProviderObjectReference(
    Guid AssetId,
    string ObjectKey,
    string Sha256,
    string ContentType,
    long SizeBytes,
    long? DurationMilliseconds = null,
    int? Width = null,
    int? Height = null);

public sealed record ProviderArtifactManifest(
    Guid ArtifactId,
    string Role,
    string ObjectKey,
    string Sha256,
    string ContentType,
    long SizeBytes,
    bool Materialized,
    long? DurationMilliseconds = null,
    int? Width = null,
    int? Height = null);

public sealed record ProviderResult<T>
    where T : class
{
    private ProviderResult(T? value, ProviderFailure? failure, ProviderProvenance provenance)
    {
        Value = value;
        Failure = failure;
        Provenance = provenance;
    }

    public T? Value { get; }
    public ProviderFailure? Failure { get; }
    public ProviderProvenance Provenance { get; }
    public bool IsSuccess => Value is not null && Failure is null;

    public static ProviderResult<T> Succeeded(T value, ProviderProvenance provenance) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), null, provenance);

    public static ProviderResult<T> Failed(ProviderFailure failure, ProviderProvenance provenance) =>
        new(null, failure ?? throw new ArgumentNullException(nameof(failure)), provenance);
}

public sealed record AudioAnalysisRequest(
    ProviderExecutionContext Context,
    ProviderObjectReference Audio,
    string? LanguageHint,
    string Profile = "music-v1");

public sealed record AudioSection(
    string Kind,
    long StartMilliseconds,
    long EndMilliseconds,
    double Confidence);

public sealed record EnergyPoint(long AtMilliseconds, double Energy);

public sealed record AudioAnalysisResult(
    long DurationMilliseconds,
    double BeatsPerMinute,
    IReadOnlyList<long> BeatMilliseconds,
    IReadOnlyList<AudioSection> Sections,
    IReadOnlyList<EnergyPoint> EnergyCurve,
    double InstrumentalConfidence,
    IReadOnlyList<ProviderArtifactManifest> Artifacts);

public interface IAudioAnalysisProvider
{
    Task<ProviderResult<AudioAnalysisResult>> AnalyzeAsync(
        AudioAnalysisRequest request,
        CancellationToken cancellationToken);
}

public sealed record TranscriptionRequest(
    ProviderExecutionContext Context,
    ProviderObjectReference Audio,
    ProviderObjectReference? FallbackAudio,
    string Language,
    bool? InstrumentalHint = null);

public sealed record TranscriptionWord(
    Guid Id,
    string Text,
    long StartMilliseconds,
    long EndMilliseconds,
    double Confidence);

public sealed record TranscriptionPhrase(
    Guid Id,
    string Text,
    long StartMilliseconds,
    long EndMilliseconds,
    double Confidence,
    IReadOnlyList<TranscriptionWord> Words);

public sealed record TranscriptionResult(
    string Language,
    double LanguageConfidence,
    bool IsInstrumentalCandidate,
    bool UsedFallbackAudio,
    IReadOnlyList<TranscriptionPhrase> Phrases,
    IReadOnlyList<ProviderArtifactManifest> Artifacts);

public interface ITranscriptionProvider
{
    Task<ProviderResult<TranscriptionResult>> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken);
}

public sealed record ArtworkCreativeBrief(
    string Mood,
    IReadOnlyList<string> Palette,
    IReadOnlyList<string> ShortLyricExcerpts,
    string? UserPrompt);

public sealed record ArtworkGenerationRequest(
    ProviderExecutionContext Context,
    string ArtistName,
    string TrackTitle,
    ArtworkCreativeBrief Brief,
    int CandidateCount,
    int Width,
    int Height,
    ProviderObjectReference? ReferenceImage = null);

public sealed record ArtworkCandidate(
    Guid CandidateId,
    int CandidateNumber,
    ProviderArtifactManifest Artwork);

public sealed record ArtworkGenerationResult(
    IReadOnlyList<ArtworkCandidate> Candidates,
    IReadOnlyList<ProviderArtifactManifest> Artifacts);

public interface IArtworkProvider
{
    Task<ProviderResult<ArtworkGenerationResult>> GenerateAsync(
        ArtworkGenerationRequest request,
        CancellationToken cancellationToken);
}

public sealed record CampaignHookInput(
    Guid HookId,
    string Label,
    long StartMilliseconds,
    long EndMilliseconds,
    string Excerpt);

public sealed record CampaignPlanningRequest(
    ProviderExecutionContext Context,
    string ArtistName,
    string TrackTitle,
    DateOnly? ReleaseDate,
    bool IsAlreadyReleased,
    string Tone,
    string CallToAction,
    IReadOnlyList<CampaignHookInput> Hooks,
    IReadOnlyList<ProviderObjectReference> Artwork);

public sealed record CampaignItemPlan(
    Guid ItemId,
    int Sequence,
    int RelativeDay,
    string TemplateKey,
    Guid? HookId,
    string Headline,
    string Caption,
    string CallToAction,
    Guid? ArtworkAssetId,
    long DurationMilliseconds = 15_000,
    string CompositionJson = "{}");

public sealed record CampaignPlanningResult(
    IReadOnlyList<CampaignItemPlan> Items,
    IReadOnlyList<ProviderArtifactManifest> Artifacts);

public interface ICampaignPlanner
{
    Task<ProviderResult<CampaignPlanningResult>> PlanAsync(
        CampaignPlanningRequest request,
        CancellationToken cancellationToken);
}

public sealed record VideoCompositionSpec(
    Guid CampaignItemId,
    string TemplateKey,
    ProviderObjectReference Audio,
    ProviderObjectReference Cover,
    ProviderObjectReference? Background,
    string Headline,
    string Caption,
    string PrimaryColor,
    string SecondaryColor,
    double FocalPointX,
    double FocalPointY,
    long HookStartMilliseconds,
    long HookEndMilliseconds,
    string Fit,
    string Opening,
    string TextLayout,
    string CallToAction,
    long DurationMilliseconds,
    string CompositionHash);

public sealed record VideoRenderProfile(
    int Width,
    int Height,
    int FramesPerSecond,
    string VideoCodec,
    string AudioCodec,
    bool Watermarked);

public sealed record VideoRenderRequest(
    ProviderExecutionContext Context,
    VideoCompositionSpec Composition,
    VideoRenderProfile Profile);

public sealed record VideoRenderResult(
    ProviderArtifactManifest Video,
    ProviderArtifactManifest Poster,
    IReadOnlyList<ProviderArtifactManifest> Artifacts);

public interface IVideoRenderer
{
    Task<ProviderResult<VideoRenderResult>> RenderAsync(
        VideoRenderRequest request,
        CancellationToken cancellationToken);
}
