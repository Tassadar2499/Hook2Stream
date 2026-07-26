using System.Security.Cryptography;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class PipelineArtifactStoreTests
{
    private static readonly byte[] ZipArtifact = "PK\u0003\u0004fixture-artifact"u8.ToArray();

    [Fact]
    public async Task PromoteAsync_deletes_materialized_staging_after_canonical_commit()
    {
        const string stagingKey = "staging/provider/artifact.zip";
        const string canonicalKey = "w/workspace/p/project/artifacts/result.zip";
        var storage = new RecordingStorage(stagingKey, ZipArtifact);
        var logger = new RecordingLogger();
        var sut = CreateStore(storage, logger);

        var result = await sut.PromoteAsync(
            Manifest(stagingKey, ZipArtifact),
            canonicalKey,
            CancellationToken.None);

        Assert.Equal(canonicalKey, result.ObjectKey);
        Assert.Equal(ZipArtifact, storage.Objects[canonicalKey]);
        Assert.False(storage.Objects.ContainsKey(stagingKey));
        Assert.Equal([stagingKey], storage.DeleteAttempts);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task PromoteAsync_returns_committed_artifact_when_staging_cleanup_fails_without_logging_sensitive_data()
    {
        const string stagingKey = "staging/private/customer-email@example.test/artifact.zip";
        const string canonicalKey = "w/workspace/p/project/artifacts/result.zip";
        const string sensitiveFailure = "storage failure containing a private object key";
        var storage = new RecordingStorage(stagingKey, ZipArtifact)
        {
            DeleteException = new IOException(sensitiveFailure)
        };
        var logger = new RecordingLogger();
        var sut = CreateStore(storage, logger);

        var result = await sut.PromoteAsync(
            Manifest(stagingKey, ZipArtifact),
            canonicalKey,
            CancellationToken.None);

        Assert.Equal(canonicalKey, result.ObjectKey);
        Assert.Equal(ZipArtifact.LongLength, result.SizeBytes);
        Assert.Equal(ZipArtifact, storage.Objects[canonicalKey]);
        Assert.Equal(ZipArtifact, storage.Objects[stagingKey]);
        Assert.Equal([stagingKey], storage.DeleteAttempts);

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("PipelineArtifactStagingCleanupFailed", warning.EventId.Name);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain(stagingKey, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveFailure, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromoteAsync_preserves_caller_cancellation_during_staging_cleanup()
    {
        const string stagingKey = "staging/provider/cancelled-artifact.zip";
        const string canonicalKey = "w/workspace/p/project/artifacts/cancelled-result.zip";
        using var cancellation = new CancellationTokenSource();
        var storage = new RecordingStorage(stagingKey, ZipArtifact)
        {
            BeforeDelete = cancellation.Cancel
        };
        var logger = new RecordingLogger();
        var sut = CreateStore(storage, logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.PromoteAsync(
            Manifest(stagingKey, ZipArtifact),
            canonicalKey,
            cancellation.Token));

        Assert.Equal(ZipArtifact, storage.Objects[canonicalKey]);
        Assert.Equal(ZipArtifact, storage.Objects[stagingKey]);
        Assert.Empty(logger.Entries);
    }

    private static PipelineArtifactStore CreateStore(
        RecordingStorage storage,
        RecordingLogger logger) =>
        new(
            storage,
            new UnexpectedProcessRunner(),
            Options.Create(new MediaToolsOptions()),
            logger);

    private static ProviderArtifactManifest Manifest(string objectKey, byte[] contents) =>
        new(
            Guid.NewGuid(),
            "archive",
            objectKey,
            Convert.ToHexStringLower(SHA256.HashData(contents)),
            "application/zip",
            contents.LongLength,
            Materialized: true);

    private sealed class RecordingStorage : IObjectStorage
    {
        public RecordingStorage(string stagingKey, byte[] stagingContents)
        {
            Objects[stagingKey] = stagingContents.ToArray();
        }

        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public List<string> DeleteAttempts { get; } = [];
        public Exception? DeleteException { get; init; }
        public Action? BeforeDelete { get; init; }

        public Task<StorageObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken)
        {
            var result = Objects.TryGetValue(objectKey, out var contents)
                ? new StorageObjectInfo(contents.LongLength, null, "application/zip")
                : null;
            return Task.FromResult(result);
        }

        public Task DownloadAsync(
            string objectKey,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            File.WriteAllBytes(destinationPath, Objects[objectKey]);
            return Task.CompletedTask;
        }

        public Task UploadAsync(
            string objectKey,
            string sourcePath,
            string contentType,
            CancellationToken cancellationToken)
        {
            Objects[objectKey] = File.ReadAllBytes(sourcePath);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        {
            DeleteAttempts.Add(objectKey);
            BeforeDelete?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (DeleteException is not null)
            {
                return Task.FromException(DeleteException);
            }

            Objects.Remove(objectKey);
            return Task.CompletedTask;
        }

        public Task EnsureBucketAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Uri> CreateUploadUrlAsync(string objectKey, string contentType, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Uri> CreateReadUrlAsync(string objectKey, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MultipartUpload> CreateMultipartUploadAsync(string objectKey, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Uri> CreateMultipartPartUploadUrlAsync(string objectKey, string uploadId, int partNumber, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteMultipartUploadAsync(string objectKey, string uploadId, IReadOnlyList<MultipartPart> parts, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortMultipartUploadAsync(string objectKey, string uploadId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteProjectObjectsAsync(ProjectStorageScope scope, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAssetObjectsAsync(AssetStorageScope scope, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnexpectedProcessRunner : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The archive promotion path must not invoke media tools.");
    }

    private sealed class RecordingLogger : ILogger<PipelineArtifactStore>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception);
}
