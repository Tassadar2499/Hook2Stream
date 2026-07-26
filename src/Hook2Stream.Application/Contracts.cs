using Hook2Stream.Domain;

namespace Hook2Stream.Application;

public sealed record CompleteOnboardingRequest(
    string WorkspaceName,
    bool AcceptTerms,
    bool AcceptPrivacy,
    string TermsVersion,
    string PrivacyVersion,
    string? DisplayName);

public sealed record AccountResponse(
    Guid UserId,
    string Subject,
    string? Email,
    string? DisplayName,
    bool OnboardingRequired,
    Guid? WorkspaceId,
    string? WorkspaceName,
    long? Version);

public sealed record UpdateBrandKitRequest(
    string DisplayName,
    string PrimaryColor,
    string SecondaryColor,
    string AccentColor,
    string HeadingFont,
    string BodyFont,
    string DefaultCta,
    string? SmartLink,
    string? ToneRestrictions,
    bool CharacterLayerEnabled);

public sealed record BrandKitResponse(
    Guid Id,
    string DisplayName,
    string PrimaryColor,
    string SecondaryColor,
    string AccentColor,
    string HeadingFont,
    string BodyFont,
    string DefaultCta,
    string? SmartLink,
    string? ToneRestrictions,
    bool CharacterLayerEnabled,
    long Version);

public sealed record CreateReleaseRequest(
    string ProjectLabel,
    string ArtistName,
    string TrackTitle,
    string Language,
    string? InternalNotes,
    string? LyricsText,
    bool IsInstrumental,
    ReleaseMode Mode,
    DateOnly? ReleaseDate,
    DateOnly? CampaignStartDate);

public sealed record UpdateReleaseRequest(
    string ProjectLabel,
    string ArtistName,
    string TrackTitle,
    string Language,
    string? InternalNotes,
    string? LyricsText,
    bool IsInstrumental,
    ReleaseMode Mode,
    DateOnly? ReleaseDate,
    DateOnly? CampaignStartDate);

public sealed record ReleaseResponse(
    Guid Id,
    FlowKind FlowKind,
    string ProjectLabel,
    string ArtistName,
    string TrackTitle,
    string Language,
    string? InternalNotes,
    string? LyricsText,
    bool IsInstrumental,
    bool IsInstrumentalConfirmed,
    ReleaseMode Mode,
    DateOnly? ReleaseDate,
    DateOnly? CampaignStartDate,
    ProjectState State,
    bool IsArchived,
    long Version,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AssetResponse> Assets);

public sealed record RightsAttestationRequest(
    bool OwnsAudioRights,
    bool OwnsLyricsRights,
    bool OwnsVisualRights,
    bool AllowsExternalAiArtwork,
    bool AllowsExternalAiProcessing,
    SyntheticContentStatus SyntheticContentStatus,
    string PolicyVersion);

public sealed record RightsAttestationResponse(
    Guid Id,
    bool OwnsAudioRights,
    bool OwnsLyricsRights,
    bool OwnsVisualRights,
    bool AllowsExternalAiArtwork,
    bool AllowsExternalAiProcessing,
    SyntheticContentStatus SyntheticContentStatus,
    string PolicyVersion,
    DateTimeOffset AcceptedAt,
    Guid? AudioAssetId,
    string? AudioFingerprint,
    long ProjectVersion);

public sealed record ReadinessResponse(
    bool Ready,
    IReadOnlyList<string> Missing,
    int ReadyVisuals,
    bool HasAudio,
    bool HasCover,
    bool HasLyricsOrInstrumental,
    bool HasRightsAttestation);

public sealed record CreateUploadRequest(
    AssetKind Kind,
    string FileName,
    string ContentType,
    long SizeBytes,
    Guid? ReplacesAssetId);

public sealed record UploadSessionResponse(
    Guid SessionId,
    Guid AssetId,
    bool Multipart,
    string? UploadUrl,
    string? MultipartUploadId,
    long PartSizeBytes,
    int PartCount,
    DateTimeOffset UrlExpiresAt,
    DateTimeOffset SessionExpiresAt)
{
    public Guid Id => SessionId;

    // Kept as a transition alias for clients generated from the earlier
    // contract. New clients must distinguish the URL and session lifetimes.
    public DateTimeOffset ExpiresAt => UrlExpiresAt;
}

public sealed record UploadPartRequest(int PartNumber);

public sealed record UploadPartResponse(
    int PartNumber,
    string UploadUrl,
    DateTimeOffset UrlExpiresAt)
{
    public DateTimeOffset ExpiresAt => UrlExpiresAt;
}

public sealed record CompletedPartRequest(int PartNumber, string ETag);

public sealed record CompleteUploadRequest(IReadOnlyList<CompletedPartRequest> Parts);

public sealed record CompleteUploadResponse(Guid AssetId, Guid JobId);

public sealed record DeletionStatusResponse(
    Guid DeletionId,
    Guid ProjectId,
    DateTimeOffset DeletedAt,
    DateTimeOffset PurgeDueAt,
    string State);

public sealed record AssetDeletionResponse(Guid AssetId, Guid CleanupJobId);

public sealed record AssetResponse(
    Guid Id,
    AssetKind Kind,
    AssetOrigin Origin,
    AssetPurpose Purpose,
    AssetState State,
    string FileName,
    string ContentType,
    long DeclaredBytes,
    long? ActualBytes,
    int Revision,
    int SortOrder,
    bool IsActive,
    string? FailureCode,
    string? FailureMessage,
    long? DurationMilliseconds,
    int? Width,
    int? Height,
    long Version);

public sealed record ReorderAssetsRequest(IReadOnlyList<Guid> AssetIds);

public sealed record JobResponse(
    Guid Id,
    JobType Type,
    JobState State,
    int ProgressPercent,
    string? ProgressStage,
    string? ErrorCode,
    string? ErrorMessage,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    long Version);

public sealed record ApiError(
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]>? Errors,
    string TraceId);
