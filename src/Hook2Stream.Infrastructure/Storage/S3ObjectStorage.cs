using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Hook2Stream.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Storage;

public sealed class S3ObjectStorage(
    IAmazonS3 internalClient,
    [FromKeyedServices(S3ClientFactory.PublicPresignerKey)]
    IAmazonS3 publicPresigner,
    IOptions<StorageOptions> options,
    IOptions<OperationalPolicyOptions> policyOptions) : IObjectStorage
{
    private readonly StorageOptions _options = options.Value;
    private readonly OperationalPolicyOptions _policy = policyOptions.Value;
    private readonly Protocol _publicProtocol =
        new Uri(options.Value.PublicServiceUrl).Scheme == Uri.UriSchemeHttp
            ? Protocol.HTTP
            : Protocol.HTTPS;

    public async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        try
        {
            await internalClient.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = _options.Bucket,
                    MaxKeys = 1
                },
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == HttpStatusCode.NotFound ||
            string.Equals(exception.ErrorCode, "NoSuchBucket", StringComparison.Ordinal))
        {
            await internalClient.PutBucketAsync(
                new PutBucketRequest { BucketName = _options.Bucket },
                cancellationToken);
        }

        if (!_options.ConfigureBucketCors)
        {
            if (_options.ConfigureBucketLifecycle)
            {
                await ConfigureLifecycleAsync(cancellationToken);
            }

            return;
        }

        var cors = new CORSConfiguration
        {
            Rules =
            [
                new CORSRule
                {
                    Id = "hook2stream-browser-upload",
                    AllowedHeaders = ["*"],
                    AllowedMethods = ["PUT", "POST", "HEAD"],
                    AllowedOrigins = _options.BrowserUploadOrigins
                        .Select(value => new Uri(value.Trim()).GetLeftPart(UriPartial.Authority))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    ExposeHeaders = ["ETag"],
                    MaxAgeSeconds = 3600
                }
            ]
        };

        await internalClient.PutCORSConfigurationAsync(
            new PutCORSConfigurationRequest
            {
                BucketName = _options.Bucket,
                Configuration = cors
            },
            cancellationToken);

        if (_options.ConfigureBucketLifecycle)
        {
            await ConfigureLifecycleAsync(cancellationToken);
        }
    }

    public Task<Uri> CreateUploadUrlAsync(
        string objectKey,
        string contentType,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Protocol = _publicProtocol,
            Expires = DateTime.UtcNow.Add(lifetime),
            ContentType = contentType
        };

        return Task.FromResult(new Uri(publicPresigner.GetPreSignedURL(request)));
    }

    public Task<Uri> CreateReadUrlAsync(
        string objectKey,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Protocol = _publicProtocol,
            Expires = DateTime.UtcNow.Add(lifetime)
        };

        return Task.FromResult(new Uri(publicPresigner.GetPreSignedURL(request)));
    }

    public async Task<Hook2Stream.Application.MultipartUpload> CreateMultipartUploadAsync(
        string objectKey,
        string contentType,
        CancellationToken cancellationToken)
    {
        var response = await internalClient.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest
            {
                BucketName = _options.Bucket,
                Key = objectKey,
                ContentType = contentType
            },
            cancellationToken);

        return new Hook2Stream.Application.MultipartUpload(response.UploadId);
    }

    public Task<Uri> CreateMultipartPartUploadUrlAsync(
        string objectKey,
        string uploadId,
        int partNumber,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Protocol = _publicProtocol,
            Expires = DateTime.UtcNow.Add(lifetime),
            UploadId = uploadId,
            PartNumber = partNumber
        };

        return Task.FromResult(new Uri(publicPresigner.GetPreSignedURL(request)));
    }

    public async Task CompleteMultipartUploadAsync(
        string objectKey,
        string uploadId,
        IReadOnlyList<MultipartPart> parts,
        CancellationToken cancellationToken)
    {
        var request = new CompleteMultipartUploadRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            UploadId = uploadId,
            PartETags = parts
                .OrderBy(part => part.PartNumber)
                .Select(part => new PartETag(part.PartNumber, part.ETag))
                .ToList()
        };

        await internalClient.CompleteMultipartUploadAsync(request, cancellationToken);
    }

    public async Task AbortMultipartUploadAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken)
    {
        try
        {
            await internalClient.AbortMultipartUploadAsync(
                new AbortMultipartUploadRequest
                {
                    BucketName = _options.Bucket,
                    Key = objectKey,
                    UploadId = uploadId
                },
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == HttpStatusCode.NotFound ||
            string.Equals(exception.ErrorCode, "NoSuchUpload", StringComparison.Ordinal))
        {
            // Retention and deletion jobs are intentionally retryable.
        }
    }

    public async Task<StorageObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            var response = await internalClient.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = _options.Bucket,
                    Key = objectKey
                },
                cancellationToken);

            return new StorageObjectInfo(response.ContentLength, response.ETag, response.Headers.ContentType);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DownloadAsync(
        string objectKey,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await internalClient.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = _options.Bucket,
                Key = objectKey
            },
            cancellationToken);

        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await response.ResponseStream.CopyToAsync(destination, cancellationToken);
    }

    public async Task UploadAsync(
        string objectKey,
        string sourcePath,
        string contentType,
        CancellationToken cancellationToken)
    {
        await internalClient.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _options.Bucket,
                Key = objectKey,
                FilePath = sourcePath,
                ContentType = contentType,
                AutoCloseStream = true
            },
            cancellationToken);
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) =>
        internalClient.DeleteObjectAsync(
            new DeleteObjectRequest
            {
                BucketName = _options.Bucket,
                Key = objectKey
            },
            cancellationToken);

    public async Task DeleteProjectObjectsAsync(
        ProjectStorageScope scope,
        CancellationToken cancellationToken)
    {
        var prefixes = new[]
        {
            $"w/{scope.WorkspaceId:N}/p/{scope.ProjectId:N}/",
            $"workspaces/{scope.WorkspaceId:N}/projects/{scope.ProjectId:N}/",
            $"staging/{scope.WorkspaceId:N}/{scope.ProjectId:N}/"
        };

        foreach (var prefix in prefixes)
        {
            await DeletePrefixAsync(prefix, cancellationToken);
        }
    }

    public Task DeleteAssetObjectsAsync(
        AssetStorageScope scope,
        CancellationToken cancellationToken) =>
        DeletePrefixAsync(
            $"w/{scope.WorkspaceId:N}/p/{scope.ProjectId:N}/assets/{scope.AssetId:N}/",
            cancellationToken);

    private async Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        string? continuationToken = null;
        do
        {
            var page = await internalClient.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = _options.Bucket,
                    Prefix = prefix,
                    ContinuationToken = continuationToken
                },
                cancellationToken);

            var keys = page.S3Objects?
                .Where(value => !string.IsNullOrWhiteSpace(value.Key))
                .Select(value => new KeyVersion { Key = value.Key })
                .ToList() ?? [];
            if (keys.Count > 0)
            {
                var deleteResponse = await internalClient.DeleteObjectsAsync(
                    new DeleteObjectsRequest
                    {
                        BucketName = _options.Bucket,
                        Objects = keys,
                        Quiet = true
                    },
                    cancellationToken);
                if (deleteResponse.DeleteErrors?.Count > 0)
                {
                    var failures = string.Join(
                        ", ",
                        deleteResponse.DeleteErrors.Take(10).Select(value => $"{value.Key}:{value.Code}"));
                    throw new InvalidOperationException(
                        $"Object storage rejected one or more project deletions: {failures}");
                }
            }

            continuationToken = page.IsTruncated == true
                ? page.NextContinuationToken
                : null;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));
    }

    private Task ConfigureLifecycleAsync(CancellationToken cancellationToken) =>
        internalClient.PutLifecycleConfigurationAsync(
            new PutLifecycleConfigurationRequest
            {
                BucketName = _options.Bucket,
                Configuration = S3LifecycleConfigurationBuilder.Build(_options, _policy)
            },
            cancellationToken);

}

internal static class S3ClientFactory
{
    internal const string PublicPresignerKey = "hook2stream-public-s3-presigner";

    internal static IAmazonS3 Create(StorageOptions options, bool usePublicServiceUrl)
    {
        var serviceUrl = usePublicServiceUrl ? options.PublicServiceUrl : options.ServiceUrl;
        var config = new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            UseHttp = new Uri(serviceUrl).Scheme == Uri.UriSchemeHttp,
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        return UsesStaticCredentials(options)
            ? new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config)
            : new AmazonS3Client(config);
    }

    internal static bool UsesStaticCredentials(StorageOptions options) =>
        options.CredentialMode switch
        {
            StorageCredentialMode.Static => true,
            StorageCredentialMode.DefaultChain => false,
            StorageCredentialMode.Auto =>
                !string.IsNullOrWhiteSpace(options.AccessKey) &&
                !string.IsNullOrWhiteSpace(options.SecretKey),
            _ => throw new ArgumentOutOfRangeException(
                nameof(options),
                options.CredentialMode,
                "Unknown storage credential mode.")
        };
}

internal static class S3LifecycleConfigurationBuilder
{
    internal static LifecycleConfiguration Build(
        StorageOptions storageOptions,
        OperationalPolicyOptions policyOptions)
    {
        var rules = new List<LifecycleRule>
        {
            new()
            {
                Id = "hook2stream-staging-expiry",
                Status = LifecycleRuleStatus.Enabled,
                Filter = new LifecycleFilter
                {
                    LifecycleFilterPredicate = new LifecyclePrefixPredicate
                    {
                        Prefix = "staging/"
                    }
                },
                Expiration = new LifecycleRuleExpiration
                {
                    Days = ToLifecycleDays(policyOptions.StagingHours)
                }
            }
        };

        if (storageOptions.ConfigureMultipartAbortLifecycle)
        {
            rules.Add(
                new LifecycleRule
                {
                    Id = "hook2stream-abort-incomplete-multipart",
                    Status = LifecycleRuleStatus.Enabled,
                    Filter = new LifecycleFilter
                    {
                        LifecycleFilterPredicate = new LifecyclePrefixPredicate
                        {
                            Prefix = string.Empty
                        }
                    },
                    AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload
                    {
                        DaysAfterInitiation = ToLifecycleDays(policyOptions.UploadSessionHours)
                    }
                });
        }

        return new LifecycleConfiguration { Rules = rules };
    }

    private static int ToLifecycleDays(int hours) =>
        Math.Max(1, (int)Math.Ceiling(hours / 24d));
}
