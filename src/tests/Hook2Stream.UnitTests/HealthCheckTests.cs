using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class HealthCheckTests
{
    [Fact]
    public async Task Object_storage_readiness_checks_the_configured_bucket()
    {
        using var client = new RecordingS3Client();
        var healthCheck = new ObjectStorageHealthCheck(
            client,
            Options.Create(new StorageOptions { Bucket = "runtime-media" }));

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.NotNull(client.LastListRequest);
        Assert.Equal("runtime-media", client.LastListRequest.BucketName);
        Assert.Equal(1, client.LastListRequest.MaxKeys);
    }

    [Fact]
    public async Task Database_readiness_supports_non_relational_test_providers()
    {
        var dbOptions = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"health-{Guid.NewGuid():N}")
            .Options;
        await using var dbContext = new Hook2StreamDbContext(dbOptions);
        var healthCheck = new DatabaseHealthCheck(dbContext);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private sealed class RecordingS3Client() : AmazonS3Client(
        new AnonymousAWSCredentials(),
        new AmazonS3Config
        {
            ServiceURL = "http://localhost:9000",
            ForcePathStyle = true
        })
    {
        public ListObjectsV2Request? LastListRequest { get; private set; }

        public override Task<ListObjectsV2Response> ListObjectsV2Async(
            ListObjectsV2Request request,
            CancellationToken cancellationToken = default)
        {
            LastListRequest = request;
            return Task.FromResult(new ListObjectsV2Response());
        }
    }
}
