using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Hook2Stream.Application;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Storage;

public sealed class S3ObjectStorage(
    IAmazonS3 client,
    IOptions<StorageOptions> options) : IObjectStorage
{
    private readonly StorageOptions _options = options.Value;

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
                    AllowedOrigins = ["http://localhost:3000", "http://127.0.0.1:3000"],
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

    public Task AbortMultipartUploadAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken) =>
        client.AbortMultipartUploadAsync(
            new AbortMultipartUploadRequest
            {
                BucketName = _options.Bucket,
                Key = objectKey,
                UploadId = uploadId
            },
            cancellationToken);

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
