using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Collections.Concurrent;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hook2Stream.IntegrationTests;

public sealed class Hook2StreamApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"hook2stream-tests-{Guid.NewGuid():N}";
    private readonly Action<IServiceCollection>? _configureTestServices;
    private readonly Action<DbContextOptionsBuilder>? _configureDbContext;

    public Hook2StreamApiFactory(
        Action<IServiceCollection>? configureTestServices = null,
        Action<DbContextOptionsBuilder>? configureDbContext = null)
    {
        _configureTestServices = configureTestServices;
        _configureDbContext = configureDbContext;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Auth:Mode", "OAuth");
        builder.UseSetting("Storage:AccessKey", "test-access-key");
        builder.UseSetting("Storage:SecretKey", "test-secret-key");
        builder.UseSetting("StorageEncryption:Mode", "Plaintext");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<Hook2StreamDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<Hook2StreamDbContext>>();
            services.RemoveAll<Hook2StreamDbContext>();
            services.AddDbContext<Hook2StreamDbContext>(options =>
            {
                options.UseInMemoryDatabase(_databaseName);
                _configureDbContext?.Invoke(options);
            });

            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<IObjectStorage, FakeObjectStorage>();

            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName,
                    _ => { });

            _configureTestServices?.Invoke(services);
        });
    }
}

internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Hook2Stream.Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subject = Request.Headers["X-Test-Subject"].FirstOrDefault() ?? "user-a";
        var claims = new[]
        {
            new Claim("sub", subject),
            new Claim("email", $"{subject}@example.test"),
            new Claim("name", subject)
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal sealed class FakeObjectStorage : IObjectStorage
{
    private readonly ConcurrentQueue<(string ObjectKey, string UploadId)> _abortedMultipartUploads = new();
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();

    public IReadOnlyCollection<(string ObjectKey, string UploadId)> AbortedMultipartUploads =>
        _abortedMultipartUploads.ToArray();

    public Exception? AbortMultipartException { get; set; }

    public Task EnsureBucketAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<Uri> CreateUploadUrlAsync(
        string objectKey,
        string contentType,
        TimeSpan lifetime,
        CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://storage.example.test/{objectKey}"));

    public Task<Uri> CreateReadUrlAsync(
        string objectKey,
        TimeSpan lifetime,
        CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://storage.example.test/{objectKey}?read=true"));

    public Task<MultipartUpload> CreateMultipartUploadAsync(
        string objectKey,
        string contentType,
        CancellationToken cancellationToken) =>
        Task.FromResult(new MultipartUpload("test-upload"));

    public Task<Uri> CreateMultipartPartUploadUrlAsync(
        string objectKey,
        string uploadId,
        int partNumber,
        TimeSpan lifetime,
        CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://storage.example.test/{objectKey}?partNumber={partNumber}"));

    public Task CompleteMultipartUploadAsync(
        string objectKey,
        string uploadId,
        IReadOnlyList<MultipartPart> parts,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AbortMultipartUploadAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken)
    {
        _abortedMultipartUploads.Enqueue((objectKey, uploadId));
        return AbortMultipartException is null
            ? Task.CompletedTask
            : Task.FromException(AbortMultipartException);
    }

    public Task<StorageObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken) =>
        Task.FromResult<StorageObjectInfo?>(_objects.TryGetValue(objectKey, out var bytes)
            ? new StorageObjectInfo(bytes.LongLength, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), "application/octet-stream")
            : new StorageObjectInfo(1, "etag", "application/octet-stream"));

    public Task DownloadAsync(
        string objectKey,
        string destinationPath,
        CancellationToken cancellationToken) =>
        _objects.TryGetValue(objectKey, out var bytes)
            ? File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken)
            : File.WriteAllBytesAsync(destinationPath, [0], cancellationToken);

    public Task UploadAsync(
        string objectKey,
        string sourcePath,
        string contentType,
        CancellationToken cancellationToken) =>
        StoreAsync(objectKey, sourcePath, cancellationToken);

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteProjectObjectsAsync(
        ProjectStorageScope scope,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteAssetObjectsAsync(
        AssetStorageScope scope,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CopyToAsync(string objectKey, Stream destination, long offset, long? length, CancellationToken cancellationToken)
    {
        var bytes = _objects.TryGetValue(objectKey, out var stored) ? stored : new byte[] { 0 };
        var count = checked((int)(length ?? bytes.LongLength - offset));
        return destination.WriteAsync(bytes.AsMemory(checked((int)offset), count), cancellationToken).AsTask();
    }

    private async Task StoreAsync(string objectKey, string sourcePath, CancellationToken cancellationToken) =>
        _objects[objectKey] = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
}
