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
    public async Task Internal_raw_transport_round_trips_without_a_browser_presigner()
    {
        var endpoint = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO");
        var accessKey = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO_ACCESS_KEY");
        var secretKey = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "CI must provide the MinIO acceptance environment.");
            output.WriteLine("MinIO acceptance environment is not configured.");
            return;
        }

        var bucket = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO_BUCKET") ?? $"hook2stream-gateway-{Guid.NewGuid():N}";
        var ownsBucket = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO_BUCKET"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ServiceUrl"] = endpoint,
            ["Storage:Bucket"] = bucket,
            ["Storage:CredentialMode"] = "Static",
            ["Storage:AccessKey"] = accessKey,
            ["Storage:SecretKey"] = secretKey,
            ["Storage:ConfigureBucketCors"] = "false",
            ["StorageEncryption:Mode"] = "Plaintext"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHook2StreamInfrastructure(configuration, new TestEnvironment(), includeBilling: false);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        var s3 = provider.GetRequiredService<IAmazonS3>();
        var key = $"acceptance/{Guid.NewGuid():N}/object.bin";
        var source = Path.Combine(Path.GetTempPath(), $"h2s-source-{Guid.NewGuid():N}");
        var destination = Path.Combine(Path.GetTempPath(), $"h2s-destination-{Guid.NewGuid():N}");
        var bytes = Random.Shared.GetItems<byte>(Enumerable.Range(0, 256).Select(value => (byte)value).ToArray(), 128 * 1024);
        await File.WriteAllBytesAsync(source, bytes);
        try
        {
            await storage.EnsureBucketAsync(default);
            await storage.UploadAsync(key, source, "application/octet-stream", default);
            await storage.DownloadAsync(key, destination, default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
            await storage.DeleteAsync(key, default);
        }
        finally
        {
            File.Delete(source); File.Delete(destination);
            if (ownsBucket) await s3.DeleteBucketAsync(bucket);
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Hook2Stream.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
