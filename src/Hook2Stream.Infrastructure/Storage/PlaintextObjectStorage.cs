using Hook2Stream.Application;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Storage;

internal sealed class PlaintextObjectStorage(
    IRawObjectStorage raw,
    IOptions<StorageOptions> storageOptions,
    IOptions<OperationalPolicyOptions> policyOptions,
    TimeProvider timeProvider) : IObjectStorage
{
    public Task EnsureBucketAsync(CancellationToken token) => raw.EnsureBucketAsync(token);
    public Task<StorageObjectInfo?> HeadAsync(string key, CancellationToken token) => raw.HeadAsync(key, token);
    public Task DownloadAsync(string key, string path, CancellationToken token) => raw.DownloadAsync(key, path, token);
    public Task UploadAsync(string key, string path, string contentType, CancellationToken token) =>
        raw.UploadAsync(
            key,
            path,
            contentType,
            StorageObjectExpirationPolicy.GetExpiration(
                key,
                storageOptions.Value,
                policyOptions.Value,
                timeProvider),
            token);
    public Task DeleteAsync(string key, CancellationToken token) => raw.DeleteAsync(key, token);
    public Task DeleteProjectObjectsAsync(ProjectStorageScope scope, CancellationToken token) => raw.DeleteProjectObjectsAsync(scope, token);
    public Task DeleteAssetObjectsAsync(AssetStorageScope scope, CancellationToken token) => raw.DeleteAssetObjectsAsync(scope, token);
    public Task CopyToAsync(string key, Stream destination, long offset, long? length, CancellationToken token) =>
        length is { } count ? raw.CopyToAsync(key, destination, offset, count, token) : CopyAll(key, destination, offset, token);
    private async Task CopyAll(string key, Stream destination, long offset, CancellationToken token)
    {
        var info = await raw.HeadAsync(key, token) ?? throw new FileNotFoundException();
        await raw.CopyToAsync(key, destination, offset, info.SizeBytes - offset, token);
    }
    public Task<Uri> CreateUploadUrlAsync(string key, string type, TimeSpan life, CancellationToken token) => throw Disabled();
    public Task<Uri> CreateReadUrlAsync(string key, TimeSpan life, CancellationToken token) => throw Disabled();
    public Task<MultipartUpload> CreateMultipartUploadAsync(string key, string type, CancellationToken token) => throw Disabled();
    public Task<Uri> CreateMultipartPartUploadUrlAsync(string key, string id, int number, TimeSpan life, CancellationToken token) => throw Disabled();
    public Task CompleteMultipartUploadAsync(string key, string id, IReadOnlyList<MultipartPart> parts, CancellationToken token) => throw Disabled();
    public Task AbortMultipartUploadAsync(string key, string id, CancellationToken token) => DeleteAsync(key, token);
    private static NotSupportedException Disabled() => new("Browser presigned URLs are disabled.");
}
