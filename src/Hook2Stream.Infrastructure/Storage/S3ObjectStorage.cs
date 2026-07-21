using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Hook2Stream.Application;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Storage;

public sealed class S3ObjectStorage(
    IAmazonS3 client,
    IOptions<StorageOptions> options,
    IOptions<OperationalPolicyOptions> policyOptions) : IObjectStorage
{
    private readonly StorageOptions _options = options.Value;
    private readonly OperationalPolicyOptions _policy = policyOptions.Value;

    public async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        var buckets = await client.ListBucketsAsync(cancellationToken);
        if (buckets.Buckets?.Any(bucket => string.Equals(bucket.BucketName, _options.Bucket, StringComparison.Ordinal)) != true)
        {
            await client.PutBucketAsync(
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

        await client.PutCORSConfigurationAsync(
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
            Expires = DateTime.UtcNow.Add(lifetime),
            ContentType = contentType
        };

        return Task.FromResult(ToPublicUri(client.GetPreSignedURL(request)));
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
            Expires = DateTime.UtcNow.Add(lifetime)
        };

        return Task.FromResult(ToPublicUri(client.GetPreSignedURL(request)));
    }

    public async Task<Hook2Stream.Application.MultipartUpload> CreateMultipartUploadAsync(
        string objectKey,
        string contentType,
        CancellationToken cancellationToken)
    {
        var response = await client.InitiateMultipartUploadAsync(
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
            Expires = DateTime.UtcNow.Add(lifetime),
            UploadId = uploadId,
            PartNumber = partNumber
        };

        return Task.FromResult(ToPublicUri(client.GetPreSignedURL(request)));
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

        await client.CompleteMultipartUploadAsync(request, cancellationToken);
    }

    public async Task AbortMultipartUploadAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.AbortMultipartUploadAsync(
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
            var response = await client.GetObjectMetadataAsync(
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
        using var response = await client.GetObjectAsync(
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
        await client.PutObjectAsync(
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
        client.DeleteObjectAsync(
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
            var page = await client.ListObjectsV2Async(
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
                var deleteResponse = await client.DeleteObjectsAsync(
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
        client.PutLifecycleConfigurationAsync(
            new PutLifecycleConfigurationRequest
            {
                BucketName = _options.Bucket,
                Configuration = new LifecycleConfiguration
                {
                    Rules =
                    [
                        new LifecycleRule
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
                                Days = Math.Max(1, (int)Math.Ceiling(_policy.StagingHours / 24d))
                            }
                        },
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
                                DaysAfterInitiation = Math.Max(
                                    1,
                                    (int)Math.Ceiling(_policy.UploadSessionHours / 24d))
                            }
                        }
                    ]
                }
            },
            cancellationToken);

    private Uri ToPublicUri(string signedUrl)
    {
        var signed = new Uri(signedUrl);
        var publicBase = new Uri(_options.PublicServiceUrl);
        var builder = new UriBuilder(signed)
        {
            Scheme = publicBase.Scheme,
            Host = publicBase.Host,
            Port = publicBase.IsDefaultPort ? -1 : publicBase.Port
        };
        return builder.Uri;
    }
}
