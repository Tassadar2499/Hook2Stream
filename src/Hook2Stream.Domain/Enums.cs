namespace Hook2Stream.Domain;

public enum ReleaseMode
{
    Upcoming = 1,
    Released = 2,
    Unscheduled = 3
}

public enum FlowKind
{
    Legacy = 1,
    Mp3First = 2
}

public enum ProjectState
{
    Draft = 1,
    Analyzing = 2,
    HookReview = 3,
    CampaignReady = 4,
    PreviewReady = 5,
    Rendering = 6,
    Ready = 7,
    PartiallyReady = 8,
    Archived = 9
}

public enum AssetKind
{
    Audio = 1,
    Cover = 2,
    Visual = 3,
    BrandCharacter = 4
}

public enum AssetOrigin
{
    Uploaded = 1,
    Generated = 2
}

public enum AssetPurpose
{
    Source = 1,
    AudioMaster = 2,
    CoverCandidate = 3,
    ApprovedCover = 4,
    CampaignBackground = 5,
    CampaignVideo = 6,
    PreviewVideo = 7,
    ExportBundle = 8,
    CleanCover = 9
}

public enum AssetState
{
    Reserved = 1,
    Uploading = 2,
    Uploaded = 3,
    Processing = 4,
    Ready = 5,
    Rejected = 6,
    Deleted = 7
}

public enum UploadState
{
    Initiated = 1,
    Uploading = 2,
    Completed = 3,
    Aborted = 4,
    Expired = 5
}

public enum UploadPartState
{
    Stored = 0,
    Committed = 1,
    Deleted = 2
}

public enum DerivativeKind
{
    AudioAnalysisWave = 1,
    AudioPreview = 2,
    ImageProxy = 3,
    VideoProxy = 4,
    Thumbnail = 5
}

public enum JobType
{
    MediaIngest = 1,
    AssetCleanup = 2,
    AudioAnalysis = 3,
    Transcription = 4,
    ArtworkGeneration = 5,
    CampaignGeneration = 6,
    PreviewRender = 7,
    FinalRender = 8,
    ExportBundle = 9,
    CleanCoverRender = 10
}

public enum JobState
{
    Queued = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5
}

public enum SyntheticContentStatus
{
    None = 1,
    Assisted = 2,
    FullySynthetic = 3,
    Unknown = 4
}

public enum WorkflowLane
{
    Audio = 1,
    Analysis = 2,
    Transcript = 3,
    Artwork = 4,
    Hooks = 5,
    Campaign = 6,
    Preview = 7,
    FinalRender = 8
}

public enum PipelineStageState
{
    NotStarted = 1,
    Queued = 2,
    Running = 3,
    WaitingUser = 4,
    Retrying = 5,
    Succeeded = 6,
    Degraded = 7,
    Failed = 8,
    Cancelled = 9,
    Stale = 10
}

public enum RevisionState
{
    Draft = 1,
    Processing = 2,
    ReadyForReview = 3,
    Approved = 4,
    Failed = 5,
    Superseded = 6
}

public enum TranscriptSource
{
    Automatic = 1,
    Imported = 2,
    Manual = 3,
    Instrumental = 4
}

public enum CheckoutState
{
    Pending = 1,
    Completed = 2,
    Failed = 3,
    Refunded = 4
}

public enum EntitlementState
{
    Active = 1,
    Exhausted = 2,
    Revoked = 3,
    Expired = 4
}

public enum RenderBatchState
{
    Queued = 1,
    Running = 2,
    Succeeded = 3,
    PartiallySucceeded = 4,
    Failed = 5,
    Cancelled = 6
}

public enum RenderRequestKind
{
    Initial = 1,
    ContentChange = 2,
    TechnicalRetry = 3
}
