using Hook2Stream.Domain;

namespace Hook2Stream.Application;

public sealed record StorageObjectInfo(long SizeBytes, string? ETag, string? ContentType);

public sealed record MultipartUpload(string UploadId);

public sealed record MultipartPart(int PartNumber, string ETag);

public interface IObjectStorage
{
    Task EnsureBucketAsync(CancellationToken cancellationToken);
    Task<Uri> CreateUploadUrlAsync(
        string objectKey,
        string contentType,
        TimeSpan lifetime,
        CancellationToken cancellationToken);
    Task<Uri> CreateReadUrlAsync(
        string objectKey,
        TimeSpan lifetime,
        CancellationToken cancellationToken);
    Task<MultipartUpload> CreateMultipartUploadAsync(
        string objectKey,
        string contentType,
        CancellationToken cancellationToken);
    Task<Uri> CreateMultipartPartUploadUrlAsync(
        string objectKey,
        string uploadId,
        int partNumber,
        TimeSpan lifetime,
        CancellationToken cancellationToken);
    Task CompleteMultipartUploadAsync(
        string objectKey,
        string uploadId,
        IReadOnlyList<MultipartPart> parts,
        CancellationToken cancellationToken);
    Task AbortMultipartUploadAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken);
    Task<StorageObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken);
    Task DownloadAsync(string objectKey, string destinationPath, CancellationToken cancellationToken);
    Task UploadAsync(
        string objectKey,
        string sourcePath,
        string contentType,
        CancellationToken cancellationToken);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

public sealed record LeasedJob(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    Guid? AssetId,
    JobType Type,
    string PayloadJson,
    int AttemptNumber,
    int MaxAttempts,
    string RequiredCapability,
    string HandlerVersion,
    string? InputFingerprint,
    int PayloadSchemaVersion,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresAt,
    Guid LeaseToken);

public sealed record JobEnqueueRequest(
    Guid WorkspaceId,
    Guid? ProjectId,
    Guid? AssetId,
    JobType Type,
    string PayloadJson,
    string? IdempotencyKey,
    string RequiredCapability = "media",
    string HandlerVersion = "v1",
    string? InputFingerprint = null,
    int PayloadSchemaVersion = 1,
    Guid? PipelineRunId = null,
    string? PipelineStage = null);

public interface IJobQueue
{
    Task<Guid> EnqueueAsync(JobEnqueueRequest request, CancellationToken cancellationToken);
    Task<Guid> EnqueueAsync(
        Guid workspaceId,
        Guid? projectId,
        Guid? assetId,
        JobType type,
        string payloadJson,
        string? idempotencyKey,
        CancellationToken cancellationToken) =>
        EnqueueAsync(
            new JobEnqueueRequest(
                workspaceId,
                projectId,
                assetId,
                type,
                payloadJson,
                idempotencyKey),
            cancellationToken);
    Task<LeasedJob?> TryLeaseAsync(
        string workerId,
        TimeSpan leaseDuration,
        IReadOnlyCollection<string> capabilities,
        CancellationToken cancellationToken);
    Task<LeasedJob?> TryLeaseAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        TryLeaseAsync(workerId, leaseDuration, ["media"], cancellationToken);
    Task<bool> HeartbeatAsync(
        Guid jobId,
        string workerId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        int progressPercent,
        string stage,
        CancellationToken cancellationToken);
    Task CompleteAsync(Guid jobId, string workerId, Guid leaseToken, CancellationToken cancellationToken);
    Task DeferAsync(
        LeasedJob job,
        TimeSpan delay,
        string reasonCode,
        CancellationToken cancellationToken);
    Task BlockAsync(
        LeasedJob job,
        string reasonCode,
        string safeMessage,
        CancellationToken cancellationToken);
    Task FailAsync(
        LeasedJob job,
        string errorCode,
        string safeMessage,
        bool retryable,
        CancellationToken cancellationToken);
    Task AppendEventAsync(
        Guid jobId,
        string eventType,
        object data,
        CancellationToken cancellationToken);
}

public sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration);

public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string workingDirectory,
        CancellationToken cancellationToken);
}

public interface IMediaIngestProcessor
{
    Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken);
}
