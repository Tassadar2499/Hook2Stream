using Amazon.S3;
using Amazon.S3.Model;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Hook2Stream.IntegrationTests;

public sealed class S3ObjectStorageMinioTests(ITestOutputHelper output)
{
    private const int H2seChunkSize = 1024 * 1024;

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
            ["Storage:ProvisioningMode"] = "Manage",
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

    [Fact]
    public async Task H2se_round_trips_ranges_and_never_persists_plaintext_in_real_minio()
    {
        var endpoint = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO");
        var accessKey = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO_ACCESS_KEY");
        var secretKey = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO_SECRET_KEY");
        var postgresConnection = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(accessKey) ||
            string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(postgresConnection))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "CI must provide the MinIO and PostgreSQL acceptance environments.");
            output.WriteLine("MinIO/PostgreSQL H2SE acceptance environment is not configured.");
            return;
        }

        var configuredBucket = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_MINIO_BUCKET");
        var bucket = configuredBucket ?? $"hook2stream-h2se-{Guid.NewGuid():N}";
        var ownsBucket = string.IsNullOrWhiteSpace(configuredBucket);
        var objectKey = $"acceptance/h2se/{Guid.NewGuid():N}/media";
        var physicalPrefix = objectKey + ".h2se/";
        var source = NewTempPath("source");
        var destination = NewTempPath("destination");
        var keyringPath = CreateKeyring();
        var sentinels = CreatePlaintextSentinels();
        var plaintext = RandomNumberGenerator.GetBytes(2 * H2seChunkSize + 4096);
        try
        {
            Insert(plaintext, 97, sentinels[0]);
            Insert(plaintext, H2seChunkSize - sentinels[1].Length / 2, sentinels[1]);
            Insert(plaintext, H2seChunkSize + 173, sentinels[2]);
            await File.WriteAllBytesAsync(source, plaintext);

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:hook2stream"] = postgresConnection,
                ["Storage:ServiceUrl"] = endpoint,
                ["Storage:Region"] = "us-east-1",
                ["Storage:Bucket"] = bucket,
                ["Storage:ProvisioningMode"] = "Manage",
                ["Storage:CredentialMode"] = "Static",
                ["Storage:AccessKey"] = accessKey,
                ["Storage:SecretKey"] = secretKey,
                ["Storage:ForcePathStyle"] = "true",
                ["Storage:RequireCredentials"] = "true",
                ["Storage:ConfigureBucketCors"] = "false",
                ["Storage:ConfigureBucketLifecycle"] = "false",
                ["Storage:ConfigureMultipartAbortLifecycle"] = "false",
                ["StorageEncryption:Mode"] = "H2se",
                ["StorageEncryption:KeyringPath"] = keyringPath,
                ["StorageEncryption:AllowLegacyPlaintextReads"] = "false",
                ["StorageEncryption:ChunkSizeBytes"] = H2seChunkSize.ToString()
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHook2StreamInfrastructure(configuration, new TestEnvironment(), includeBilling: false);
            await using var provider = services.BuildServiceProvider();
            var storage = provider.GetRequiredService<IObjectStorage>();
            var s3 = provider.GetRequiredService<IAmazonS3>();

            try
            {
                await storage.EnsureBucketAsync(default);
                await storage.UploadAsync(objectKey, source, "audio/mpeg", default);

                var info = await storage.HeadAsync(objectKey, default);
                Assert.NotNull(info);
                Assert.Equal(plaintext.LongLength, info.SizeBytes);
                Assert.Equal("audio/mpeg", info.ContentType);

                const int rangeOffset = H2seChunkSize - 71;
                const int rangeLength = 257;
                await using (var range = new MemoryStream())
                {
                    await storage.CopyToAsync(objectKey, range, rangeOffset, rangeLength, default);
                    Assert.Equal(plaintext.AsSpan(rangeOffset, rangeLength).ToArray(), range.ToArray());
                }

                await storage.DownloadAsync(objectKey, destination, default);
                Assert.Equal(plaintext, await File.ReadAllBytesAsync(destination));

                var physicalKeys = await ListPhysicalKeysAsync(s3, bucket, physicalPrefix);
                Assert.Equal(2, physicalKeys.Count);
                Assert.Contains(physicalKeys, key => key.EndsWith("/manifest", StringComparison.Ordinal));
                Assert.Contains(physicalKeys, key => key.Contains("/data/", StringComparison.Ordinal));
                foreach (var physicalKey in physicalKeys)
                {
                    using var response = await s3.GetObjectAsync(bucket, physicalKey);
                    await using var physicalObject = new MemoryStream();
                    await response.ResponseStream.CopyToAsync(physicalObject);
                    var persistedBytes = physicalObject.ToArray();
                    Assert.All(sentinels, sentinel =>
                        Assert.False(Contains(persistedBytes, sentinel),
                            $"Physical H2SE object '{physicalKey}' contains a plaintext media sentinel."));
                }

                await storage.DeleteAsync(objectKey, default);
                Assert.Null(await storage.HeadAsync(objectKey, default));
                Assert.Empty(await ListPhysicalKeysAsync(s3, bucket, physicalPrefix));
            }
            finally
            {
                await DeletePhysicalPrefixAsync(s3, bucket, physicalPrefix);
                if (ownsBucket) await s3.DeleteBucketAsync(bucket);
            }
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
            File.Delete(keyringPath);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string CreateKeyring()
    {
        var path = NewTempPath("keyring");
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var options = new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var keyring = new FileStream(path, options);
            JsonSerializer.Serialize(keyring, new
            {
                activeKeyId = "minio-acceptance-kek",
                keys = new Dictionary<string, string>
                {
                    ["minio-acceptance-kek"] = Convert.ToBase64String(key)
                }
            });
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[][] CreatePlaintextSentinels() =>
    [
        Encoding.ASCII.GetBytes("ID3-HOOK2STREAM-MP3-PLAINTEXT-MUST-NEVER-REACH-MINIO-0123456789-ABCDEFGHIJKLMNOPQRSTUVWXYZ"),
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, .. Encoding.ASCII.GetBytes("HOOK2STREAM-PNG-PLAINTEXT-MUST-NEVER-REACH-MINIO-0123456789-ABCDEFGHIJKLMNOPQRSTUVWXYZ")],
        [0x50, 0x4b, 0x03, 0x04, .. Encoding.ASCII.GetBytes("HOOK2STREAM-ZIP-PLAINTEXT-MUST-NEVER-REACH-MINIO-0123456789-ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    ];

    private static void Insert(byte[] destination, int offset, byte[] value) =>
        value.CopyTo(destination, offset);

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;

    private static string NewTempPath(string purpose) =>
        Path.Combine(Path.GetTempPath(), $"h2s-minio-{purpose}-{Guid.NewGuid():N}");

    private static async Task<List<string>> ListPhysicalKeysAsync(
        IAmazonS3 s3,
        string bucket,
        string prefix)
    {
        var keys = new List<string>();
        string? continuationToken = null;
        do
        {
            var response = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = continuationToken
            });
            keys.AddRange((response.S3Objects ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value.Key))
                .Select(value => value.Key));
            continuationToken = response.IsTruncated == true
                ? response.NextContinuationToken
                : null;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));
        return keys;
    }

    private static async Task DeletePhysicalPrefixAsync(IAmazonS3 s3, string bucket, string prefix)
    {
        foreach (var key in await ListPhysicalKeysAsync(s3, bucket, prefix))
            await s3.DeleteObjectAsync(bucket, key);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Hook2Stream.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
