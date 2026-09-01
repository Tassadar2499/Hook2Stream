namespace Hook2Stream.Domain;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class AppUser : Entity
{
    public required string ExternalSubject { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public Workspace? Workspace { get; set; }
}

public sealed class Workspace : Entity
{
    public Guid OwnerUserId { get; set; }
    public AppUser OwnerUser { get; set; } = null!;
    public required string Name { get; set; }
    public required string TermsVersion { get; set; }
    public required string PrivacyVersion { get; set; }
    public DateTimeOffset TermsAcceptedAt { get; set; }
    public DateTimeOffset PrivacyAcceptedAt { get; set; }
    public BrandKit? BrandKit { get; set; }
    public List<ReleaseProject> Projects { get; set; } = [];
}

public sealed class BrandKit : Entity
{
    public Guid WorkspaceId { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public string DisplayName { get; set; } = "My artist";
    public string PrimaryColor { get; set; } = "#111827";
    public string SecondaryColor { get; set; } = "#F9FAFB";
    public string AccentColor { get; set; } = "#F97316";
    public string HeadingFont { get; set; } = "Oswald";
    public string BodyFont { get; set; } = "Inter";
    public string DefaultCta { get; set; } = "Listen now";
    public string? SmartLink { get; set; }
    public string? ToneRestrictions { get; set; }
    public bool CharacterLayerEnabled { get; set; }
}

public sealed class ReleaseProject : Entity
{
    public Guid WorkspaceId { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
    public required string ProjectLabel { get; set; }
    public required string ArtistName { get; set; }
    public required string TrackTitle { get; set; }
    public string Language { get; set; } = "en";
    public string? InternalNotes { get; set; }
    public string? LyricsText { get; set; }
    public bool IsInstrumental { get; set; }
    public bool IsInstrumentalConfirmed { get; set; }
    public FlowKind FlowKind { get; set; } = FlowKind.Legacy;
    public ReleaseMode Mode { get; set; } = ReleaseMode.Upcoming;
    public DateOnly? ReleaseDate { get; set; }
    public DateOnly? CampaignStartDate { get; set; }
    public ProjectState State { get; set; } = ProjectState.Draft;
    public bool IsArchived { get; set; }
    public ProjectState? StateBeforeArchive { get; set; }
    public long BrandKitVersion { get; set; }
    public DateTimeOffset? SetupCompletedAt { get; set; }
    public Guid? CurrentTranscriptRevisionId { get; set; }
    public Guid? CurrentArtworkPackRevisionId { get; set; }
    public Guid? CurrentHookSetRevisionId { get; set; }
    public Guid? CurrentCampaignPlanRevisionId { get; set; }
    public RightsAttestation? RightsAttestation { get; set; }
    public List<MediaAsset> Assets { get; set; } = [];
    public List<Job> Jobs { get; set; } = [];
    public List<PipelineRun> PipelineRuns { get; set; } = [];

    public void Archive()
    {
        if (IsArchived) return;
        StateBeforeArchive = State;
        IsArchived = true;
        State = ProjectState.Archived;
    }

    public void Restore()
    {
        if (!IsArchived) return;
        IsArchived = false;
        State = StateBeforeArchive is { } previous && previous != ProjectState.Archived
            ? previous
            : ProjectState.Draft;
        StateBeforeArchive = null;
    }
}

public sealed class RightsAttestation : Entity
{
    public Guid ProjectId { get; set; }
    public ReleaseProject Project { get; set; } = null!;
    public required string ActorSubject { get; set; }
    public required string PolicyVersion { get; set; }
    public bool OwnsAudioRights { get; set; }
    public bool OwnsLyricsRights { get; set; }
    public bool OwnsVisualRights { get; set; }
    public bool AllowsExternalAiArtwork { get; set; }
    public bool AllowsExternalAiProcessing { get; set; }
    public Guid? AudioAssetId { get; set; }
    public string? AudioFingerprint { get; set; }
    public SyntheticContentStatus SyntheticContentStatus { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
}

public sealed class MediaAsset : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public ReleaseProject Project { get; set; } = null!;
    public AssetKind Kind { get; set; }
    public AssetOrigin Origin { get; set; } = AssetOrigin.Uploaded;
    public AssetPurpose Purpose { get; set; } = AssetPurpose.Source;
    public AssetState State { get; set; } = AssetState.Reserved;
    public required string OriginalFileName { get; set; }
    public required string DeclaredContentType { get; set; }
    public string? DetectedContentType { get; set; }
    public long DeclaredBytes { get; set; }
    public long? ActualBytes { get; set; }
    public required string ObjectKey { get; set; }
    public int Revision { get; set; } = 1;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public Guid? SupersedesAssetId { get; set; }
    public string? Sha256 { get; set; }
    public long? DurationMilliseconds { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public string? ProvenanceJson { get; set; }
    public Guid? ProducerJobId { get; set; }
    public Guid? CampaignItemId { get; set; }
    public Guid? RenderBatchId { get; set; }
    public Guid? ArtworkPackRevisionId { get; set; }
    public List<MediaDerivative> Derivatives { get; set; } = [];
    public List<UploadSession> UploadSessions { get; set; } = [];
}

public sealed class MediaDerivative : Entity
{
    public Guid AssetId { get; set; }
    public MediaAsset Asset { get; set; } = null!;
    public DerivativeKind Kind { get; set; }
    public required string ProcessorVersion { get; set; }
    public required string ObjectKey { get; set; }
    public required string ContentType { get; set; }
    public long Bytes { get; set; }
    public string? Sha256 { get; set; }
    public long? DurationMilliseconds { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}

public sealed class UploadSession : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid AssetId { get; set; }
    public MediaAsset Asset { get; set; } = null!;
    public required string ObjectKey { get; set; }
    public UploadState State { get; set; } = UploadState.Initiated;
    public bool IsMultipart { get; set; }
    public string? MultipartUploadId { get; set; }
    public long PartSizeBytes { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? AbortedAt { get; set; }
    public List<UploadPart> Parts { get; set; } = [];
}

public sealed class UploadPart : Entity
{
    public Guid UploadSessionId { get; set; }
    public UploadSession UploadSession { get; set; } = null!;
    public int PartNumber { get; set; }
    public long PlaintextLength { get; set; }
    public required string PlaintextSha256 { get; set; }
    public required string StorageETag { get; set; }
    public required string ObjectKey { get; set; }
    public UploadPartState State { get; set; } = UploadPartState.Stored;
}

public sealed class Job : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public ReleaseProject? Project { get; set; }
    public Guid? AssetId { get; set; }
    public JobType Type { get; set; }
    public Guid? PipelineRunId { get; set; }
    public string? PipelineStage { get; set; }
    public string RequiredCapability { get; set; } = "media";
    public string HandlerVersion { get; set; } = "v1";
    public string? InputFingerprint { get; set; }
    public int PayloadSchemaVersion { get; set; } = 1;
    public Guid? LeaseToken { get; set; }
    public JobState State { get; set; } = JobState.Queued;
    public required string PayloadJson { get; set; }
    public string? IdempotencyKey { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int ProgressPercent { get; set; }
    public string? ProgressStage { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<JobAttempt> Attempts { get; set; } = [];
    public List<JobEvent> Events { get; set; } = [];
}

public sealed class PipelineRun : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public ReleaseProject Project { get; set; } = null!;
    public int Number { get; set; }
    public PipelineStageState State { get; set; } = PipelineStageState.NotStarted;
    public string Trigger { get; set; } = "audio-upload";
    public string? InputFingerprint { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<PipelineStage> Stages { get; set; } = [];
}

public sealed class PipelineStage : Entity
{
    public Guid PipelineRunId { get; set; }
    public PipelineRun PipelineRun { get; set; } = null!;
    public WorkflowLane Lane { get; set; }
    public PipelineStageState State { get; set; } = PipelineStageState.NotStarted;
    public int ProgressPercent { get; set; }
    public string? BlockerCode { get; set; }
    public string? ErrorCode { get; set; }
    public Guid? CurrentJobId { get; set; }
    public Guid? CurrentRenderBatchId { get; set; }
}

public sealed class ProjectEvent : Entity
{
    public long Sequence { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public required string EventType { get; set; }
    public required string DataJson { get; set; }
}

public sealed class TrackAnalysisRevision : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SourceAssetId { get; set; }
    public int Number { get; set; }
    public RevisionState State { get; set; } = RevisionState.Processing;
    public string SourceFingerprint { get; set; } = string.Empty;
    public string AnalysisJson { get; set; } = "{}";
    public string ProcessorVersionsJson { get; set; } = "{}";
}

public sealed class TranscriptRevision : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public int Number { get; set; }
    public TranscriptSource Source { get; set; }
    public RevisionState State { get; set; } = RevisionState.ReadyForReview;
    public string Language { get; set; } = "en";
    public string PhrasesJson { get; set; } = "[]";
    public string SourceFingerprint { get; set; } = string.Empty;
    public Guid? SupersedesRevisionId { get; set; }
    public string? ApprovedBySubject { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}

public sealed class ArtworkPackRevision : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public int Number { get; set; }
    public int OperationNumber { get; set; }
    public RevisionState State { get; set; } = RevisionState.Processing;
    public string Prompt { get; set; } = string.Empty;
    public string CandidateAssetIdsJson { get; set; } = "[]";
    public string BackgroundAssetIdsJson { get; set; } = "[]";
    public Guid? SelectedAssetId { get; set; }
    public string CompositionJson { get; set; } = "{}";
    public string SourceFingerprint { get; set; } = string.Empty;
    public string? ApprovedBySubject { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}

public sealed class HookSetRevision : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public int Number { get; set; }
    public RevisionState State { get; set; } = RevisionState.ReadyForReview;
    public Guid TranscriptRevisionId { get; set; }
    public string HooksJson { get; set; } = "[]";
    public string SourceFingerprint { get; set; } = string.Empty;
}

public sealed class CampaignPlanRevision : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public int Number { get; set; }
    public RevisionState State { get; set; } = RevisionState.Processing;
    public Guid TranscriptRevisionId { get; set; }
    public Guid ArtworkPackRevisionId { get; set; }
    public Guid HookSetRevisionId { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public string SourceFingerprint { get; set; } = string.Empty;
}

public sealed class ApiIdempotencyRecord : Entity
{
    public Guid WorkspaceId { get; set; }
    public required string Scope { get; set; }
    public required string Key { get; set; }
    public required string RequestHash { get; set; }
    public Guid ResourceId { get; set; }
    public Guid? SecondaryResourceId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class OutboxMessage : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid? AggregateId { get; set; }
    public required string Destination { get; set; }
    public required string MessageType { get; set; }
    public required string DedupeKey { get; set; }
    public required string PayloadJson { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class InboxMessage : Entity
{
    public required string Source { get; set; }
    public required string MessageId { get; set; }
    public required string PayloadHash { get; set; }
    public string State { get; set; } = "received";
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class BillingCheckout : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public required string ProductCode { get; set; }
    public int AmountCents { get; set; }
    public string Currency { get; set; } = "usd";
    public CheckoutState State { get; set; } = CheckoutState.Pending;
    public string ItemIdsJson { get; set; } = "[]";
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public string? ExternalSessionId { get; set; }
    public string? CheckoutUrl { get; set; }
    public string? ExternalCustomerId { get; set; }
    public string? ExternalSubscriptionId { get; set; }
    public string? ExternalPaymentIntentId { get; set; }
    public Guid? ArtworkPackRevisionId { get; set; }
    public string? ArtworkCompositionHash { get; set; }
    public Guid? CampaignPlanRevisionId { get; set; }
    public string? ArtistNameSnapshot { get; set; }
    public string? TrackTitleSnapshot { get; set; }
    public DateOnly? ScheduleAnchorSnapshot { get; set; }
    public ReleaseMode? ReleaseModeSnapshot { get; set; }
    public Guid? AudioAssetIdSnapshot { get; set; }
    public string? AudioFingerprintSnapshot { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? RefundedAt { get; set; }
    public DateTimeOffset? SubscriptionAccessEndedAt { get; set; }
    public DateTimeOffset? ProviderAccessRevokedAt { get; set; }
}

public sealed class Entitlement : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid CheckoutId { get; set; }
    public Guid? ProjectId { get; set; }
    public required string ProductCode { get; set; }
    public EntitlementState State { get; set; } = EntitlementState.Active;
    public string ItemIdsJson { get; set; } = "[]";
    public int IncludedItemCount { get; set; }
    public int RemainingContentRerenders { get; set; }
    public required string ProviderPeriodKey { get; set; }
    public string? ExternalSubscriptionId { get; set; }
    public string? ExternalPaymentIntentId { get; set; }
    public string? ExternalInvoiceId { get; set; }
    public Guid? ArtworkPackRevisionId { get; set; }
    public string? ArtworkCompositionHash { get; set; }
    public Guid? CampaignPlanRevisionId { get; set; }
    public string? ArtistNameSnapshot { get; set; }
    public string? TrackTitleSnapshot { get; set; }
    public DateOnly? ScheduleAnchorSnapshot { get; set; }
    public ReleaseMode? ReleaseModeSnapshot { get; set; }
    public Guid? AudioAssetIdSnapshot { get; set; }
    public string? AudioFingerprintSnapshot { get; set; }
    public DateTimeOffset? PeriodStartsAt { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? ProviderEventOccurredAt { get; set; }
}

public sealed class WorkspaceArtworkCredit : Entity
{
    public Guid WorkspaceId { get; set; }
    public int Balance { get; set; }
}

public sealed class ArtworkCreditGrant : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid CheckoutId { get; set; }
    public int Granted { get; set; }
    public int Remaining { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class ArtworkCreditTransaction : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid? GrantId { get; set; }
    public int Delta { get; set; }
    public int BalanceAfter { get; set; }
    public required string Reason { get; set; }
    public required string Reference { get; set; }
}

public sealed class RenderBatch : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? PipelineRunId { get; set; }
    public Guid EntitlementId { get; set; }
    public RenderBatchState State { get; set; } = RenderBatchState.Queued;
    public RenderRequestKind Kind { get; set; } = RenderRequestKind.Initial;
    public string ItemIdsJson { get; set; } = "[]";
    public string JobIdsJson { get; set; } = "[]";
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class RenderItemUsage : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid EntitlementId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid CampaignItemId { get; set; }
    public int InitialRenderCount { get; set; }
    public int ContentRerenderCount { get; set; }
    public int TechnicalRetryCount { get; set; }
}

public sealed class JobAttempt : Entity
{
    public Guid JobId { get; set; }
    public Job Job { get; set; } = null!;
    public int Number { get; set; }
    public required string WorkerId { get; set; }
    public JobState State { get; set; } = JobState.Running;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class JobEvent : Entity
{
    public long Sequence { get; set; }
    public Guid JobId { get; set; }
    public Job Job { get; set; } = null!;
    public required string EventType { get; set; }
    public required string DataJson { get; set; }
}

public sealed class AuditEvent : Entity
{
    public Guid WorkspaceId { get; set; }
    public string? ActorSubject { get; set; }
    public required string Action { get; set; }
    public required string ResourceType { get; set; }
    public Guid? ResourceId { get; set; }
    public required string DataJson { get; set; }
}

/// <summary>
/// Privacy-safe operational metadata for one provider invocation. Provider
/// inputs and outputs are intentionally excluded; only immutable hashes and
/// aggregate usage may be persisted here.
/// </summary>
public sealed class AiProviderInvocation : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid JobId { get; set; }
    public Guid OperationId { get; set; }
    public int AttemptNumber { get; set; }
    public required string Stage { get; set; }
    public required string Status { get; set; }
    public string? FailureCode { get; set; }
    public required string RequestedProvider { get; set; }
    public string? ResolvedProvider { get; set; }
    public required string RequestedModel { get; set; }
    public string? ResolvedModel { get; set; }
    public string? RequestId { get; set; }
    public string? GenerationId { get; set; }
    public required string InputHash { get; set; }
    public required string ParameterHash { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? TotalTokens { get; set; }
    public double? AudioSeconds { get; set; }
    public int? GeneratedImages { get; set; }
    public decimal? CostUsd { get; set; }
}
