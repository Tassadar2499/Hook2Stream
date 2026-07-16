using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:AccessKey", "test-access-key");
        builder.UseSetting("Storage:SecretKey", "test-secret-key");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<Hook2StreamDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<Hook2StreamDbContext>>();
            services.RemoveAll<Hook2StreamDbContext>();
            services.AddDbContext<Hook2StreamDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

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
    public Task EnsureBucketAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<Uri> CreateUploadUrlAsync(
        string objectKey,
        string contentType,
        TimeSpan lifetime,
        CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://storage.example.test/{objectKey}"));

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
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<StorageObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken) =>
        Task.FromResult<StorageObjectInfo?>(new StorageObjectInfo(1, "\"etag\"", "application/octet-stream"));

    public Task DownloadAsync(
        string objectKey,
        string destinationPath,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task UploadAsync(
        string objectKey,
        string sourcePath,
        string contentType,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
}
