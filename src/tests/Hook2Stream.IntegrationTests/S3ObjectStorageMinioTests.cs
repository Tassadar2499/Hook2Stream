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
        var configuredBucket =
            Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO_BUCKET");
        var browserOrigin =
            Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO_BROWSER_ORIGIN");
        var backupBucket =
            Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO_BACKUP_BUCKET");
        var internalOrigin = new Uri(internalServiceUrl);
        var publicOrigin = new Uri(publicServiceUrl);
        Assert.NotEqual(internalOrigin.Host, publicOrigin.Host);
        if (!string.IsNullOrWhiteSpace(browserOrigin))
        {
            Assert.Equal(Uri.UriSchemeHttps, publicOrigin.Scheme);
        }

        var ownsBucket = string.IsNullOrWhiteSpace(configuredBucket);
        var bucket = ownsBucket
            ? $"hook2stream-presign-{Guid.NewGuid():N}"
            : configuredBucket!;
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
            await AssertMediaLifecycleAsync(
                internalServiceUrl,
                publicServiceUrl,
                bucket);

            var singlePayload = "signed-for-the-public-host"u8.ToArray();
            var uploadUrl = await storage.CreateUploadUrlAsync(
                singleKey,
                "application/octet-stream",
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            Assert.Equal(publicOrigin.Host, uploadUrl.Host);
            if (!string.IsNullOrWhiteSpace(browserOrigin))
            {
                await AssertPublicProxyContractAsync(
                    http,
                    publicOrigin,
                    uploadUrl,
                    browserOrigin,
                    backupBucket);
            }

            var wrongHostUrl = new UriBuilder(uploadUrl)
            {
                Scheme = internalOrigin.Scheme,
                Host = internalOrigin.Host,
                Port = internalOrigin.Port
            }.Uri;
            using (var wrongHostContent = BinaryContent(singlePayload))
            using (var wrongHostResponse = await http.PutAsync(wrongHostUrl, wrongHostContent))
            {
                Assert.Equal(HttpStatusCode.Forbidden, wrongHostResponse.StatusCode);
            }

            using (var uploadContent = BinaryContent(singlePayload))
            using (var uploadResponse = await PutAsync(
                       http,
                       uploadUrl,
                       uploadContent,
                       browserOrigin))
            {
                uploadResponse.EnsureSuccessStatusCode();
                AssertCorsHeaders(uploadResponse, browserOrigin);
            }

            var readUrl = await storage.CreateReadUrlAsync(
                singleKey,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            Assert.Equal(publicOrigin.Host, readUrl.Host);
            Assert.Equal(
                singlePayload,
                await GetBytesAsync(http, readUrl, browserOrigin));

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
            using (var partResponse = await PutAsync(
                       http,
                       partUrl,
                       partContent,
                       browserOrigin))
            {
                partResponse.EnsureSuccessStatusCode();
                AssertCorsHeaders(partResponse, browserOrigin);
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
            Assert.Equal(
                multipartPayload,
                await GetBytesAsync(http, multipartReadUrl, browserOrigin));
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
            if (ownsBucket)
            {
                await internalClient.DeleteBucketAsync(bucket);
            }
        }
    }

    private static async Task AssertMediaLifecycleAsync(
        string internalServiceUrl,
        string publicServiceUrl,
        string bucket)
    {
        var bootstrapAccessKey = Environment.GetEnvironmentVariable(
            "HOOK2STREAM_TEST_MINIO_BOOTSTRAP_ACCESS_KEY");
        var bootstrapSecretKey = Environment.GetEnvironmentVariable(
            "HOOK2STREAM_TEST_MINIO_BOOTSTRAP_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(bootstrapAccessKey) ||
            string.IsNullOrWhiteSpace(bootstrapSecretKey))
        {
            Assert.False(
                string.Equals(
                    Environment.GetEnvironmentVariable("CI"),
                    "true",
                    StringComparison.OrdinalIgnoreCase),
                "CI must exercise the scoped bootstrap identity and media lifecycle against MinIO.");
            return;
        }

        using var provider = CreateServices(
            internalServiceUrl,
            publicServiceUrl,
            bucket,
            bootstrapAccessKey,
            bootstrapSecretKey,
            configureLifecycle: true);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<IObjectStorage>()
            .EnsureBucketAsync(CancellationToken.None);

        var client = provider.GetRequiredService<IAmazonS3>();
        var lifecycle = await client.GetLifecycleConfigurationAsync(
            bucket,
            CancellationToken.None);
        var rule = Assert.Single(lifecycle.Configuration.Rules);
        Assert.Equal("hook2stream-staging-expiry", rule.Id);
        Assert.Equal(1, rule.Expiration.Days);
        Assert.Null(rule.AbortIncompleteMultipartUpload);
    }

    private static async Task AssertPublicProxyContractAsync(
        HttpClient http,
        Uri publicOrigin,
        Uri mediaUrl,
        string browserOrigin,
        string? backupBucket)
    {
        using (var readinessResponse = await http.GetAsync(
                   new Uri(publicOrigin, "/minio/health/ready")))
        {
            readinessResponse.EnsureSuccessStatusCode();
        }

        using (var adminResponse = await http.GetAsync(
                   new Uri(publicOrigin, "/minio/admin/v3/info")))
        {
            Assert.Equal(HttpStatusCode.NotFound, adminResponse.StatusCode);
        }

        using (var preflight = Preflight(mediaUrl, browserOrigin, "PUT"))
        using (var preflightResponse = await http.SendAsync(preflight))
        {
            Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
            AssertCorsHeaders(preflightResponse, browserOrigin);
            Assert.Contains(
                "PUT",
                HeaderValues(preflightResponse, "Access-Control-Allow-Methods"));
            Assert.Contains(
                "content-type",
                HeaderValues(preflightResponse, "Access-Control-Allow-Headers"),
                StringComparison.OrdinalIgnoreCase);
        }

        using (var wrongOriginPreflight = Preflight(
                   mediaUrl,
                   "https://untrusted.invalid",
                   "PUT"))
        using (var wrongOriginResponse = await http.SendAsync(wrongOriginPreflight))
        {
            Assert.False(wrongOriginResponse.Headers.Contains("Access-Control-Allow-Origin"));
        }

        if (!string.IsNullOrWhiteSpace(backupBucket))
        {
            var backupUrl = new Uri(
                publicOrigin,
                $"/{backupBucket}/hook2stream/staging/postgres/cors-probe");
            using var backupPreflight = Preflight(backupUrl, browserOrigin, "PUT");
            using var backupResponse = await http.SendAsync(backupPreflight);
            Assert.False(backupResponse.Headers.Contains("Access-Control-Allow-Origin"));
        }
    }

    private static HttpRequestMessage Preflight(Uri url, string origin, string method)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, url);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", method);
        request.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Headers",
            "content-type,x-amz-date");
        return request;
    }

    private static async Task<HttpResponseMessage> PutAsync(
        HttpClient http,
        Uri url,
        HttpContent content,
        string? browserOrigin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = content
        };
        if (!string.IsNullOrWhiteSpace(browserOrigin))
        {
            request.Headers.TryAddWithoutValidation("Origin", browserOrigin);
        }

        return await http.SendAsync(request);
    }

    private static async Task<byte[]> GetBytesAsync(
        HttpClient http,
        Uri url,
        string? browserOrigin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(browserOrigin))
        {
            request.Headers.TryAddWithoutValidation("Origin", browserOrigin);
        }

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        AssertCorsHeaders(response, browserOrigin);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static void AssertCorsHeaders(
        HttpResponseMessage response,
        string? browserOrigin)
    {
        if (string.IsNullOrWhiteSpace(browserOrigin))
        {
            return;
        }

        Assert.Equal(
            browserOrigin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains(
            "ETag",
            HeaderValues(response, "Access-Control-Expose-Headers"),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string HeaderValues(HttpResponseMessage response, string name) =>
        string.Join(",", response.Headers.GetValues(name));

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
        string secretKey,
        bool configureLifecycle = false)
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
                ["Storage:ConfigureBucketLifecycle"] = configureLifecycle.ToString(),
                ["Storage:ConfigureMultipartAbortLifecycle"] = "false",
                ["OperationalPolicy:StagingHours"] = "24"
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
