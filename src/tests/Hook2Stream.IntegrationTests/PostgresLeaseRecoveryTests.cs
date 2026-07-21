using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Jobs;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Hook2Stream.IntegrationTests;

public sealed class PostgresLeaseRecoveryTests
{
    private const string PreviousMigration = "20260721151835_RenameClerkSubjectToExternalSubject";

    [Fact]
    public async Task Migration_and_expired_lease_recovery_work_on_postgresql()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            Assert.False(
                string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "CI must provide HOOK2STREAM_TEST_POSTGRES so raw queue SQL is exercised.");
            return;
        }

        var databaseName = $"hook2stream_ci_{Guid.NewGuid():N}";
        var testConnection = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            Pooling = false
        }.ConnectionString;

        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await VerifyCapabilityMigrationAsync(testConnection);
            await VerifyLeaseRecoveryAsync(testConnection);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task VerifyCapabilityMigrationAsync(string connectionString)
    {
        var legacyJobId = Guid.NewGuid();
        await using (var db = CreateDb(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            db.Jobs.Add(new Job
            {
                Id = legacyJobId,
                WorkspaceId = Guid.NewGuid(),
                Type = JobType.Transcription,
                RequiredCapability = JobRoutingRegistry.Analysis,
                HandlerVersion = "legacy-v1",
                PayloadJson = "{}",
                State = JobState.Cancelled,
                ErrorCode = "rights.required",
                CompletedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateDb(connectionString))
        {
            await db.Database.MigrateAsync();
            var migratedCapability = await db.Jobs
                .Where(value => value.Id == legacyJobId)
                .Select(value => value.RequiredCapability)
                .SingleAsync();
            Assert.Equal(JobRoutingRegistry.Control, migratedCapability);
        }
    }

    private static async Task VerifyLeaseRecoveryAsync(string connectionString)
    {
        var jobId = Guid.NewGuid();
        await using (var db = CreateDb(connectionString))
        {
            var job = new Job
            {
                Id = jobId,
                WorkspaceId = Guid.NewGuid(),
                Type = JobType.MediaIngest,
                RequiredCapability = JobRoutingRegistry.Media,
                HandlerVersion = "postgres-smoke-v1",
                PayloadJson = "{}",
                State = JobState.Running,
                AttemptCount = 1,
                MaxAttempts = 3,
                LeaseOwner = "worker-1",
                LeaseToken = Guid.NewGuid(),
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            db.Jobs.Add(job);
            db.JobAttempts.Add(new JobAttempt
            {
                JobId = job.Id,
                Number = 1,
                WorkerId = "worker-1",
                State = JobState.Running,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
            });
            await db.SaveChangesAsync();
        }

        var second = await LeaseAsync(connectionString, "worker-2");
        Assert.NotNull(second);
        Assert.Equal(2, second.AttemptNumber);
        await ExpireAsync(connectionString, jobId);

        var third = await LeaseAsync(connectionString, "worker-3");
        Assert.NotNull(third);
        Assert.Equal(3, third.AttemptNumber);
        await ExpireAsync(connectionString, jobId);

        Assert.Null(await LeaseAsync(connectionString, "worker-4"));
        await using var verify = CreateDb(connectionString);
        var jobState = await verify.Jobs
            .Where(value => value.Id == jobId)
            .Select(value => new { value.State, value.ErrorCode })
            .SingleAsync();
        Assert.Equal(JobState.Failed, jobState.State);
        Assert.Equal("job.lease_expired", jobState.ErrorCode);
        var attempts = await verify.JobAttempts
            .Where(value => value.JobId == jobId)
            .OrderBy(value => value.Number)
            .Select(value => new { value.Number, value.State })
            .ToArrayAsync();
        Assert.Equal([1, 2, 3], attempts.Select(value => value.Number));
        Assert.All(attempts, value => Assert.Equal(JobState.Failed, value.State));
    }

    private static async Task<LeasedJob?> LeaseAsync(string connectionString, string workerId)
    {
        await using var db = CreateDb(connectionString);
        return await new PostgresJobQueue(db).TryLeaseAsync(
            workerId,
            TimeSpan.FromMinutes(1),
            [JobRoutingRegistry.Media],
            CancellationToken.None);
    }

    private static async Task ExpireAsync(string connectionString, Guid jobId)
    {
        await using var db = CreateDb(connectionString);
        var job = await db.Jobs.SingleAsync(value => value.Id == jobId);
        job.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private static Hook2StreamDbContext CreateDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new Hook2StreamDbContext(options);
    }
}
