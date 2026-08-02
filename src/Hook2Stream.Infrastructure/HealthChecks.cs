using Amazon.S3;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure;

internal sealed class DatabaseHealthCheck(Hook2StreamDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
            }

            if (dbContext.Database.IsRelational())
            {
                var pendingMigrations = (await dbContext.Database
                        .GetPendingMigrationsAsync(cancellationToken))
                    .ToArray();
                if (pendingMigrations.Length > 0)
                {
                    return HealthCheckResult.Unhealthy(
                        $"PostgreSQL has {pendingMigrations.Length} pending migration(s).",
                        data: new Dictionary<string, object>
                        {
                            ["pendingMigrations"] = pendingMigrations
                        });
                }
            }

            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.", exception);
        }
    }
}

internal sealed class ObjectStorageHealthCheck(
    IAmazonS3 client,
    IOptions<StorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await client.ListObjectsV2Async(
                new Amazon.S3.Model.ListObjectsV2Request
                {
                    BucketName = options.Value.Bucket,
                    MaxKeys = 1
                },
                cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Object storage is unavailable.", exception);
        }
    }
}
