using System.Net;
using System.Net.Http.Headers;
using Amazon.S3;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace Hook2Stream.IntegrationTests;

public sealed class S3ObjectStorageMinioTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Presigned_put_get_and_multipart_work_with_distinct_loopback_hosts()
    {
        var internalServiceUrl = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO");
        var accessKey = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO_ACCESS_KEY");
        var secretKey = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(internalServiceUrl) ||
            string.IsNullOrWhiteSpace(accessKey) ||
            string.IsNullOrWhiteSpace(secretKey))
        {
            Assert.False(
                string.Equals(
                    Environment.GetEnvironmentVariable("CI"),
                    "true",
                    StringComparison.OrdinalIgnoreCase),
                "CI must provide the MinIO acceptance environment so distinct-host PUT, GET, and multipart signing are exercised.");
            output.WriteLine(
                "MinIO acceptance test was not configured. Set HOOK2STREAM_TEST_MINIO, " +
                "HOOK2STREAM_TEST_MINIO_ACCESS_KEY, and HOOK2STREAM_TEST_MINIO_SECRET_KEY.");
            return;
        }

        var publicServiceUrl =
            Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO_PUBLIC") ??
            AlternateLoopbackOrigin(internalServiceUrl);
        var internalOrigin = new Uri(internalServiceUrl);
        var publicOrigin = new Uri(publicServiceUrl);
        Assert.NotEqual(internalOrigin.Host, publicOrigin.Host);
        Assert.Equal(internalOrigin.Port, publicOrigin.Port);

        var bucket = $"hook2stream-presign-{Guid.NewGuid():N}";
        using var provider = CreateServices(
            internalServiceUrl,
            publicServiceUrl,
            bucket,
            accessKey,
            secretKey);
        await using var scope = provider.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        var internalClient = provider.GetRequiredService<IAmazonS3>();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var singleKey = $"acceptance/{Guid.NewGuid():N}/single.bin";
        var multipartKey = $"acceptance/{Guid.NewGuid():N}/multipart.bin";
        string? multipartUploadId = null;
        var multipartCompleted = false;

        try
        {
            await storage.EnsureBucketAsync(CancellationToken.None);

            var singlePayload = "signed-for-the-public-host"u8.ToArray();
            var uploadUrl = await storage.CreateUploadUrlAsync(
                singleKey,
                "application/octet-stream",
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            Assert.Equal(publicOrigin.Host, uploadUrl.Host);

            var wrongHostUrl = new UriBuilder(uploadUrl) { Host = internalOrigin.Host }.Uri;
            using (var wrongHostContent = BinaryContent(singlePayload))
            using (var wrongHostResponse = await http.PutAsync(wrongHostUrl, wrongHostContent))
            {
                Assert.Equal(HttpStatusCode.Forbidden, wrongHostResponse.StatusCode);
            }

            using (var uploadContent = BinaryContent(singlePayload))
            using (var uploadResponse = await http.PutAsync(uploadUrl, uploadContent))
            {
                uploadResponse.EnsureSuccessStatusCode();
            }

            var readUrl = await storage.CreateReadUrlAsync(
                singleKey,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            Assert.Equal(publicOrigin.Host, readUrl.Host);
            Assert.Equal(singlePayload, await http.GetByteArrayAsync(readUrl));

            var multipartPayload = new byte[(5 * 1024 * 1024) + 17];
            Random.Shared.NextBytes(multipartPayload);
            var multipart = await storage.CreateMultipartUploadAsync(
                multipartKey,
                "application/octet-stream",
                CancellationToken.None);
            multipartUploadId = multipart.UploadId;
            var partUrl = await storage.CreateMultipartPartUploadUrlAsync(
                multipartKey,
                multipart.UploadId,
                partNumber: 1,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            Assert.Equal(publicOrigin.Host, partUrl.Host);

            string partEtag;
            using (var partContent = BinaryContent(multipartPayload))
            using (var partResponse = await http.PutAsync(partUrl, partContent))
            {
                partResponse.EnsureSuccessStatusCode();
                partEtag = Assert.IsType<EntityTagHeaderValue>(partResponse.Headers.ETag).Tag;
            }

            await storage.CompleteMultipartUploadAsync(
                multipartKey,
                multipart.UploadId,
                [new MultipartPart(1, partEtag)],
                CancellationToken.None);
            multipartCompleted = true;

            var multipartReadUrl = await storage.CreateReadUrlAsync(
                multipartKey,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            Assert.Equal(multipartPayload, await http.GetByteArrayAsync(multipartReadUrl));
        }
        finally
        {
            if (multipartUploadId is not null && !multipartCompleted)
            {
                await storage.AbortMultipartUploadAsync(
                    multipartKey,
                    multipartUploadId,
                    CancellationToken.None);
            }

            await storage.DeleteAsync(singleKey, CancellationToken.None);
            await storage.DeleteAsync(multipartKey, CancellationToken.None);
            await internalClient.DeleteBucketAsync(bucket);
        }
    }

    private static ByteArrayContent BinaryContent(byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private static ServiceProvider CreateServices(
        string internalServiceUrl,
        string publicServiceUrl,
        string bucket,
        string accessKey,
        string secretKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:ServiceUrl"] = internalServiceUrl,
                ["Storage:PublicServiceUrl"] = publicServiceUrl,
                ["Storage:Bucket"] = bucket,
                ["Storage:AccessKey"] = accessKey,
                ["Storage:SecretKey"] = secretKey,
                ["Storage:RequireCredentials"] = "true",
                ["Storage:ConfigureBucketCors"] = "false",
                ["Storage:ConfigureBucketLifecycle"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddHook2StreamInfrastructure(
            configuration,
            new TestHostEnvironment(),
            includeBilling: false);
        return services.BuildServiceProvider();
    }

    private static string AlternateLoopbackOrigin(string internalServiceUrl)
    {
        var builder = new UriBuilder(internalServiceUrl)
        {
            Host = new Uri(internalServiceUrl).Host switch
            {
                "localhost" => "127.0.0.1",
                "127.0.0.1" => "localhost",
                _ => throw new InvalidOperationException(
                    "HOOK2STREAM_TEST_MINIO_PUBLIC is required when the internal MinIO endpoint is not loopback.")
            }
        };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Hook2Stream.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
