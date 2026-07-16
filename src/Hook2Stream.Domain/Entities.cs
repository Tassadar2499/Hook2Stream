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
    public required string ClerkSubject { get; set; }
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
    public required string ProjectLabel { get; set; }
    public required string ArtistName { get; set; }
    public required string TrackTitle { get; set; }
    public string Language { get; set; } = "en";
    public string? InternalNotes { get; set; }
    public string? LyricsText { get; set; }
    public bool IsInstrumental { get; set; }
    public ReleaseMode Mode { get; set; } = ReleaseMode.Upcoming;
    public DateOnly? ReleaseDate { get; set; }
    public DateOnly? CampaignStartDate { get; set; }
    public ProjectState State { get; set; } = ProjectState.Draft;
    public bool IsArchived { get; set; }
    public long BrandKitVersion { get; set; }
    public RightsAttestation? RightsAttestation { get; set; }
    public List<MediaAsset> Assets { get; set; } = [];
    public List<Job> Jobs { get; set; } = [];

    public void Archive()
    {
        IsArchived = true;
        State = ProjectState.Archived;
    }

    public void Restore()
    {
        IsArchived = false;
        State = ProjectState.Draft;
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
    public SyntheticContentStatus SyntheticContentStatus { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
}

public sealed class MediaAsset : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public ReleaseProject Project { get; set; } = null!;
    public AssetKind Kind { get; set; }
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
}

public sealed class Job : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public ReleaseProject? Project { get; set; }
    public Guid? AssetId { get; set; }
    public JobType Type { get; set; }
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
