using Amazon.S3;
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
            PublicServiceUrl = "http://127.0.0.1:9000",
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
            serviceUrl: "http://localhost:9000",
            publicServiceUrl: "http://127.0.0.1:9000");
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

    [Theory]
    [InlineData("http://storage.example.test")]
    [InlineData("ftp://storage.example.test")]
    [InlineData("https://user:password@storage.example.test")]
    [InlineData("https://storage.example.test/minio")]
    [InlineData("https://storage.example.test?tenant=a")]
    [InlineData("https://storage.example.test#fragment")]
    public void Production_rejects_a_public_endpoint_that_is_not_an_https_origin(
        string publicServiceUrl)
    {
        using var provider = StorageServices(
            Environments.Production,
            serviceUrl: "http://minio:9000",
            publicServiceUrl);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<StorageOptions>>().Value);

        Assert.Contains("PublicServiceUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_accepts_https_public_origin_with_http_internal_origin()
    {
        using var provider = StorageServices(
            Environments.Production,
            serviceUrl: "http://minio:9000",
            publicServiceUrl: "https://media.example.test");

        var options = provider.GetRequiredService<IOptions<StorageOptions>>().Value;

        Assert.Equal("http://minio:9000", options.ServiceUrl);
        Assert.Equal("https://media.example.test", options.PublicServiceUrl);
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
            serviceUrl,
            publicServiceUrl: "http://127.0.0.1:9000");

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<StorageOptions>>().Value);

        Assert.Contains("ServiceUrl", exception.Message, StringComparison.Ordinal);
    }

    private static void AssertPublicSignedUrl(Uri uri)
    {
        Assert.Equal(Uri.UriSchemeHttp, uri.Scheme);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(9000, uri.Port);
        Assert.Contains("X-Amz-Signature=", uri.Query, StringComparison.Ordinal);
        var signedHeadersParameter = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Single(value => value.StartsWith("X-Amz-SignedHeaders=", StringComparison.Ordinal));
        var signedHeaders = Uri.UnescapeDataString(
            signedHeadersParameter[(signedHeadersParameter.IndexOf('=') + 1)..]);
        Assert.Contains("host", signedHeaders.Split(';', StringSplitOptions.RemoveEmptyEntries));
    }

    private static ServiceProvider StorageServices(
        string environment,
        string serviceUrl,
        string publicServiceUrl)
    {
        var services = new ServiceCollection();
        services.AddHook2StreamInfrastructure(
            StorageConfiguration(serviceUrl, publicServiceUrl),
            new TestHostEnvironment(environment),
            includeBilling: false);
        return services.BuildServiceProvider();
    }

    private static IConfiguration StorageConfiguration(
        string serviceUrl,
        string publicServiceUrl) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:ServiceUrl"] = serviceUrl,
                ["Storage:PublicServiceUrl"] = publicServiceUrl,
                ["Storage:AccessKey"] = "test-access-key",
                ["Storage:SecretKey"] = "test-secret-key",
                ["ConnectionStrings:hook2stream"] =
                    "Host=postgres;Database=hook2stream;Username=app;Password=secret"
            })
            .Build();

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Hook2Stream.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
