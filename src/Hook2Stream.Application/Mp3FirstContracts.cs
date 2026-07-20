using Hook2Stream.Domain;

namespace Hook2Stream.Application;

public sealed record QuickAudioUploadRequest(
    string FileName,
    string ContentType,
    long SizeBytes,
    bool ConfirmsContentRights,
    bool AllowsExternalAiProcessing);

public sealed record QuickAudioUploadResponse(
    ReleaseResponse Project,
    UploadSessionResponse Upload,
    WorkflowResponse Workflow);

public sealed record SetupReleaseRequest(
    string ProjectLabel,
    string ArtistName,
    string TrackTitle,
    string Language,
    ReleaseMode Mode,
    DateOnly? ReleaseDate,
    DateOnly? CampaignStartDate,
    bool IsInstrumental,
    bool IsInstrumentalConfirmed,
    string? InternalNotes);

public sealed record WorkflowLaneResponse(
    WorkflowLane Lane,
    PipelineStageState State,
    int ProgressPercent,
    string? BlockerCode,
    string? ErrorCode,
    Guid? CurrentJobId);

public sealed record WorkflowResponse(
    Guid ProjectId,
    FlowKind FlowKind,
    long ProjectVersion,
    IReadOnlyList<string> Blockers,
    string? NextAction,
    IReadOnlyList<WorkflowLaneResponse> Lanes);

public sealed record TranscriptWordResponse(
    string Text,
    long StartMilliseconds,
    long EndMilliseconds,
    double Confidence);

public sealed record TranscriptPhraseRequest(
    string Id,
    int Order,
    string Text,
    long StartMilliseconds,
    long EndMilliseconds,
    double Confidence,
    bool WarningAcknowledged,
    IReadOnlyList<TranscriptWordResponse>? Words);

public sealed record PutTranscriptRequest(
    TranscriptSource Source,
    string Language,
    bool IsInstrumental,
    IReadOnlyList<TranscriptPhraseRequest> Phrases);

public sealed record TranscriptResponse(
    Guid RevisionId,
    int Number,
    TranscriptSource Source,
    RevisionState State,
    string Language,
    bool IsInstrumental,
    IReadOnlyList<TranscriptPhraseRequest> Phrases,
    DateTimeOffset? ApprovedAt,
    long Version);

public sealed record ApproveRevisionRequest(Guid RevisionId);
public sealed record JobAcceptedResponse(Guid JobId, Guid? RevisionId);

public sealed record GenerateArtworkRequest(string Prompt, string? Style);

public sealed record UpdateArtworkSelectionRequest(
    Guid PackRevisionId,
    Guid SelectedAssetId,
    string CompositionJson);

public sealed record ArtworkPackResponse(
    Guid RevisionId,
    int Number,
    int OperationNumber,
    RevisionState State,
    string Prompt,
    IReadOnlyList<Guid> CandidateAssetIds,
    IReadOnlyList<Guid> BackgroundAssetIds,
    Guid? SelectedAssetId,
    string CompositionJson,
    DateTimeOffset? ApprovedAt,
    long Version);

public sealed record HookRequest(
    string Id,
    string Kind,
    long StartMilliseconds,
    long EndMilliseconds,
    string? Label);

public sealed record PutHooksRequest(IReadOnlyList<HookRequest> Hooks);

public sealed record HookSetResponse(
    Guid RevisionId,
    int Number,
    Guid TranscriptRevisionId,
    IReadOnlyList<HookRequest> Hooks,
    long Version);

public sealed record CampaignItemRequest(
    Guid Id,
    int Slot,
    string Template,
    string HookId,
    Guid? BackgroundAssetId,
    string Text,
    string CompositionJson);

public sealed record PutCampaignItemRequest(
    string Template,
    string HookId,
    Guid? BackgroundAssetId,
    string Text,
    string CompositionJson);

public sealed record CampaignResponse(
    Guid RevisionId,
    int Number,
    RevisionState State,
    IReadOnlyList<CampaignItemRequest> Items,
    long Version);

public sealed record AssetReadUrlResponse(Guid AssetId, string Url, DateTimeOffset ExpiresAt);
