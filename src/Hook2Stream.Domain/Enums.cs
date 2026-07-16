namespace Hook2Stream.Domain;

public enum ReleaseMode
{
    Upcoming = 1,
    Released = 2
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
    AssetCleanup = 2
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
