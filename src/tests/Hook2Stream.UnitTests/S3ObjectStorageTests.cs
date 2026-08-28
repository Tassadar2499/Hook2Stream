using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class S3ObjectStorageTests
{
    [Fact]
    public void Raw_transport_uses_only_the_internal_endpoint()
    {
        var options = new StorageOptions
        {
            ServiceUrl = "http://localhost:9000",
            Bucket = "hook2stream-presign-tests",
            AccessKey = "test-access-key",
            SecretKey = "test-secret-key"
        };
        using var client = S3ClientFactory.Create(options);
        var config = Assert.IsType<AmazonS3Config>(client.Config);
        Assert.Equal("http://localhost:9000", new Uri(config.ServiceURL).GetLeftPart(UriPartial.Authority));
    }

    [Fact]
    public void Registration_does_not_create_a_public_presigner()
    {
        var configuration = StorageConfiguration(
            serviceUrl: "http://localhost:9000");
        var services = new ServiceCollection();
        services.AddHook2StreamInfrastructure(
            configuration,
            new TestHostEnvironment(Environments.Development),
            includeBilling: false);
        using var provider = services.BuildServiceProvider();

        var internalClient = provider.GetRequiredService<IAmazonS3>();
        var internalConfig = Assert.IsType<AmazonS3Config>(internalClient.Config);
        Assert.Equal(
            "http://localhost:9000",
            new Uri(internalConfig.ServiceURL).GetLeftPart(UriPartial.Authority));
        Assert.True(internalConfig.UseHttp);
    }

    [Fact]
    public void Production_uses_verify_only_by_default()
    {
        using var provider = StorageServices(
            Environments.Production,
            serviceUrl: "https://gateway.storjshare.io");

        var options = provider.GetRequiredService<IOptions<StorageOptions>>().Value;

        Assert.Equal(StorageProvisioningMode.VerifyOnly, options.ProvisioningMode);
    }

    [Fact]
    public void Production_rejects_manage_mode()
    {
        using var provider = StorageServices(
            Environments.Production,
            "https://gateway.storjshare.io",
            new KeyValuePair<string, string?>("Storage:ProvisioningMode", "Manage"));

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<StorageOptions>>().Value);

        Assert.Contains("ProvisioningMode=Manage", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Storage:ConfigureBucketCors")]
    [InlineData("Storage:ConfigureBucketLifecycle")]
    [InlineData("Storage:ConfigureMultipartAbortLifecycle")]
    public void Verify_only_rejects_bucket_mutation_configuration(string setting)
    {
        using var provider = StorageServices(
            Environments.Development,
            "http://localhost:9000",
            new KeyValuePair<string, string?>(setting, "true"));

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<StorageOptions>>().Value);

        Assert.Contains("VerifyOnly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_only_checks_an_existing_bucket_without_mutating_it()
    {
        using var client = new RecordingS3Client { BucketExists = true };
        var storage = Storage(client, new StorageOptions
        {
            Bucket = "existing-media",
            ProvisioningMode = StorageProvisioningMode.VerifyOnly,
            ConfigureBucketCors = true,
            ConfigureBucketLifecycle = true,
            ConfigureMultipartAbortLifecycle = true
        });

        await storage.EnsureBucketAsync(default);

        Assert.Equal("existing-media", client.LastListRequest?.BucketName);
        Assert.Equal(0, client.BucketMutationCount);
    }

    [Fact]
    public async Task Verify_only_fails_closed_when_the_bucket_is_missing()
    {
        using var client = new RecordingS3Client { BucketExists = false };
        var storage = Storage(client, new StorageOptions
        {
            Bucket = "missing-media",
            ProvisioningMode = StorageProvisioningMode.VerifyOnly
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.EnsureBucketAsync(default));

        Assert.Contains("VerifyOnly", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.BucketMutationCount);
    }

    [Fact]
    public async Task Manage_creates_a_missing_bucket_for_local_and_ci_storage()
    {
        using var client = new RecordingS3Client { BucketExists = false };
        var storage = Storage(client, new StorageOptions
        {
            Bucket = "local-media",
            ProvisioningMode = StorageProvisioningMode.Manage
        });

        await storage.EnsureBucketAsync(default);

        Assert.Equal(1, client.PutBucketCount);
    }

    [Fact]
    public async Task Storj_upload_sends_object_expiration_as_rfc3339_metadata()
    {
        using var client = new RecordingS3Client();
        var storage = Storage(client, new StorageOptions
        {
            Bucket = "runtime-media",
            ObjectExpirationMode = StorageObjectExpirationMode.Storj
        });
        var source = Path.Combine(Path.GetTempPath(), $"h2s-s3-{Guid.NewGuid():N}");
        var expiresAt = new DateTimeOffset(2026, 8, 25, 12, 34, 56, TimeSpan.Zero);
        await File.WriteAllTextAsync(source, "ciphertext");
        try
        {
            await storage.UploadAsync(
                "staging/workspace/object.h2se/manifest",
                source,
                "application/octet-stream",
                expiresAt,
                default);

            Assert.Equal(
                expiresAt.ToString("O"),
                client.LastPutObjectRequest?.Metadata["Object-Expires"]);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Theory]
    [InlineData(StorageCredentialMode.Auto, "access", "secret", true)]
    [InlineData(StorageCredentialMode.Auto, "", "", false)]
    [InlineData(StorageCredentialMode.Static, "access", "secret", true)]
    [InlineData(StorageCredentialMode.DefaultChain, "", "", false)]
    public void Credential_mode_selects_static_or_aws_default_chain(
        StorageCredentialMode mode,
        string accessKey,
        string secretKey,
        bool expectedStatic)
    {
        var options = new StorageOptions
        {
            CredentialMode = mode,
            AccessKey = accessKey,
            SecretKey = secretKey
        };

        Assert.Equal(expectedStatic, S3ClientFactory.UsesStaticCredentials(options));
    }

    [Theory]
    [InlineData("Auto", "access", "")]
    [InlineData("Static", "", "")]
    [InlineData("DefaultChain", "access", "secret")]
    public void Invalid_credential_combinations_fail_options_validation(
        string mode,
        string accessKey,
        string secretKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:CredentialMode"] = mode,
                ["Storage:AccessKey"] = accessKey,
                ["Storage:SecretKey"] = secretKey
            })
            .Build();
        var services = new ServiceCollection();
        services.AddHook2StreamInfrastructure(
            configuration,
            new TestHostEnvironment(Environments.Development),
            includeBilling: false);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<StorageOptions>>().Value);
    }

    [Theory]
    [InlineData("ftp://minio.example.test")]
    [InlineData("http://user:password@minio.example.test")]
    [InlineData("http://minio.example.test/base-path")]
    public void Internal_endpoint_must_be_an_http_origin(string serviceUrl)
    {
        using var provider = StorageServices(
            Environments.Development,
            serviceUrl);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<StorageOptions>>().Value);

        Assert.Contains("ServiceUrl", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider StorageServices(
        string environment,
        string serviceUrl,
        params KeyValuePair<string, string?>[] settings)
    {
        var services = new ServiceCollection();
        services.AddHook2StreamInfrastructure(
            StorageConfiguration(serviceUrl, settings),
            new TestHostEnvironment(environment),
            includeBilling: false);
        return services.BuildServiceProvider();
    }

    private static IConfiguration StorageConfiguration(
        string serviceUrl,
        params KeyValuePair<string, string?>[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["Storage:ServiceUrl"] = serviceUrl,
            ["Storage:AccessKey"] = "test-access-key",
            ["Storage:SecretKey"] = "test-secret-key",
            ["ConnectionStrings:hook2stream"] =
                "Host=postgres;Database=hook2stream;Username=app;Password=secret"
        };
        foreach (var setting in settings)
        {
            values[setting.Key] = setting.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static S3ObjectStorage Storage(IAmazonS3 client, StorageOptions options) =>
        new(
            client,
            Options.Create(options),
            Options.Create(new OperationalPolicyOptions()));

    private sealed class RecordingS3Client() : AmazonS3Client(
        new AnonymousAWSCredentials(),
        new AmazonS3Config
        {
            ServiceURL = "http://localhost:9000",
            ForcePathStyle = true
        })
    {
        public bool BucketExists { get; init; } = true;
        public int PutBucketCount { get; private set; }
        public int PutCorsCount { get; private set; }
        public int PutLifecycleCount { get; private set; }
        public int BucketMutationCount => PutBucketCount + PutCorsCount + PutLifecycleCount;
        public ListObjectsV2Request? LastListRequest { get; private set; }
        public PutObjectRequest? LastPutObjectRequest { get; private set; }

        public override Task<ListObjectsV2Response> ListObjectsV2Async(
            ListObjectsV2Request request,
            CancellationToken cancellationToken = default)
        {
            LastListRequest = request;
            return BucketExists
                ? Task.FromResult(new ListObjectsV2Response())
                : Task.FromException<ListObjectsV2Response>(new AmazonS3Exception("missing")
                {
                    StatusCode = HttpStatusCode.NotFound,
                    ErrorCode = "NoSuchBucket"
                });
        }

        public override Task<PutBucketResponse> PutBucketAsync(
            PutBucketRequest request,
            CancellationToken cancellationToken = default)
        {
            PutBucketCount++;
            return Task.FromResult(new PutBucketResponse());
        }

        public override Task<PutCORSConfigurationResponse> PutCORSConfigurationAsync(
            PutCORSConfigurationRequest request,
            CancellationToken cancellationToken = default)
        {
            PutCorsCount++;
            return Task.FromResult(new PutCORSConfigurationResponse());
        }

        public override Task<PutLifecycleConfigurationResponse> PutLifecycleConfigurationAsync(
            PutLifecycleConfigurationRequest request,
            CancellationToken cancellationToken = default)
        {
            PutLifecycleCount++;
            return Task.FromResult(new PutLifecycleConfigurationResponse());
        }

        public override Task<PutObjectResponse> PutObjectAsync(
            PutObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            LastPutObjectRequest = request;
            return Task.FromResult(new PutObjectResponse());
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Hook2Stream.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
