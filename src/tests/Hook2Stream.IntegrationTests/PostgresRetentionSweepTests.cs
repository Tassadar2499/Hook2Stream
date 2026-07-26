extern alias worker;

using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RetentionSweepService =
    worker::Hook2Stream.Worker.RetentionSweepService;
using WorkerOptions = worker::Hook2Stream.Worker.WorkerOptions;

namespace Hook2Stream.IntegrationTests;

public sealed class PostgresRetentionSweepTests
{
    private const string PreviousMigration =
        "20260721174440_ProductionCoreHardening";
    private const string RetentionMigration =
        "20260725150204_RetentionActivity";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_atomic_delivery_and_locked_activity_are_safe_on_postgresql()
    {
        var adminConnectionString =
            Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            Assert.False(
                string.Equals(
                    Environment.GetEnvironmentVariable("CI"),
                    "true",
                    StringComparison.OrdinalIgnoreCase),
                "CI must provide HOOK2STREAM_TEST_POSTGRES for retention concurrency tests.");
            return;
        }

        var databaseName = $"hook2stream_retention_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(
            adminConnectionString)
        {
            Database = databaseName,
            Pooling = false
        }.ConnectionString;

        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using (var create =
                     new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var workspaceId = await VerifyMigrationGraceAsync(connectionString);
            await VerifyAtomicSweepAsync(connectionString, workspaceId);
            await VerifyLockedActivityWinsAsync(connectionString, workspaceId);
            await VerifyFailedSaveRollsBackFenceAsync(connectionString, workspaceId);
        }
        finally
        {
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<Guid> VerifyMigrationGraceAsync(string connectionString)
    {
        await using (var db = CreateDb(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        var userId = Guid.CreateVersion7();
        var workspaceId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var historical = Now.AddYears(-1);
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand(
                """
                INSERT INTO users
                    (id, external_subject, created_at, updated_at, version)
                VALUES
                    (@user_id, 'retention-migration-user', @historical, @historical, 1);

                INSERT INTO workspaces
                    (id, owner_user_id, name, terms_version, privacy_version,
                     terms_accepted_at, privacy_accepted_at,
                     created_at, updated_at, version)
                VALUES
                    (@workspace_id, @user_id, 'Retention migration workspace',
                     'test', 'test', @historical, @historical,
                     @historical, @historical, 1);

                INSERT INTO release_projects
                    (id, workspace_id, project_label, artist_name, track_title,
                     language, is_instrumental, mode, state, is_archived,
                     brand_kit_version, created_at, updated_at, version)
                VALUES
                    (@project_id, @workspace_id, 'Historical project', 'Artist',
                     'Track', 'en', false, 3, 1, false, 1,
                     @historical, @historical, 1);
                """,
                connection);
            seed.Parameters.AddWithValue("user_id", userId);
            seed.Parameters.AddWithValue("workspace_id", workspaceId);
            seed.Parameters.AddWithValue("project_id", projectId);
            seed.Parameters.AddWithValue("historical", historical);
            await seed.ExecuteNonQueryAsync();
        }

        var graceLowerBound = DateTimeOffset.UtcNow.AddSeconds(-1);
        await using (var db = CreateDb(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(RetentionMigration);
        }
        var graceUpperBound = DateTimeOffset.UtcNow.AddSeconds(1);

        await using (var db = CreateDb(connectionString))
        {
            var activity = await db.Projects
                .Where(value => value.Id == projectId)
                .Select(value => value.LastActivityAt)
                .SingleAsync();
            Assert.InRange(activity, graceLowerBound, graceUpperBound);
            await db.Database.MigrateAsync();
        }

        return workspaceId;
    }

    private static async Task VerifyAtomicSweepAsync(
        string connectionString,
        Guid workspaceId)
    {
        var old = NewProject(workspaceId, "postgres-old", Now.AddDays(-31));
        var active = NewProject(workspaceId, "postgres-active", Now.AddDays(-1));
        await using (var db = CreateDb(connectionString))
        {
            db.Projects.AddRange(old, active);
            await db.SaveChangesAsync();
        }

        await using var services = CreateServices(connectionString);
        await SweepAsync(services);

        await using var verify = CreateDb(connectionString);
        Assert.NotNull((await verify.Projects.IgnoreQueryFilters()
            .SingleAsync(value => value.Id == old.Id)).DeletedAt);
        Assert.NotNull(await verify.Projects.SingleOrDefaultAsync(
            value => value.Id == active.Id));
        var tombstone = await verify.ProjectDeletionTombstones.SingleAsync(
            value => value.ProjectId == old.Id);
        var outbox = await verify.OutboxMessages.SingleAsync(
            value => value.DedupeKey ==
                     $"retention:project:{tombstone.Id:N}");
        Assert.Equal("job", outbox.Destination);
        Assert.Null(outbox.ProcessedAt);
        Assert.False(await verify.Jobs.AnyAsync(value =>
            value.ProjectId == old.Id &&
            value.Type == JobType.AssetCleanup));
    }

    private static async Task VerifyLockedActivityWinsAsync(
        string connectionString,
        Guid workspaceId)
    {
        var project = NewProject(
            workspaceId,
            "postgres-concurrent-activity",
            Now.AddDays(-31));
        await using (var seed = CreateDb(connectionString))
        {
            seed.Projects.Add(project);
            await seed.SaveChangesAsync();
        }

        await using var activityDb = CreateDb(connectionString);
        await using var activityTransaction =
            await activityDb.Database.BeginTransactionAsync();
        var locked = await activityDb.Projects
            .FromSqlInterpolated(
                $"""
                 SELECT *
                 FROM release_projects
                 WHERE id = {project.Id}
                 FOR UPDATE
                 """)
            .SingleAsync();

        await using var services = CreateServices(connectionString);
        await SweepAsync(services).WaitAsync(TimeSpan.FromSeconds(10));

        ProjectActivity.Touch(locked, Now);
        await activityDb.SaveChangesAsync();
        await activityTransaction.CommitAsync();

        await using var verify = CreateDb(connectionString);
        var visible = await verify.Projects.SingleAsync(
            value => value.Id == project.Id);
        Assert.Equal(Now, visible.LastActivityAt);
        Assert.False(await verify.ProjectDeletionTombstones.AnyAsync(
            value => value.ProjectId == project.Id));
    }

    private static async Task VerifyFailedSaveRollsBackFenceAsync(
        string connectionString,
        Guid workspaceId)
    {
        var project = NewProject(
            workspaceId,
            "postgres-atomic-failure",
            Now.AddDays(-31));
        await using (var seed = CreateDb(connectionString))
        {
            seed.Projects.Add(project);
            await seed.SaveChangesAsync();
        }

        await using var services = CreateServices(
            connectionString,
            new FailCleanupOutboxInterceptor(project.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SweepAsync(services));

        await using var verify = CreateDb(connectionString);
        Assert.NotNull(await verify.Projects.SingleOrDefaultAsync(
            value => value.Id == project.Id));
        Assert.False(await verify.ProjectDeletionTombstones.AnyAsync(
            value => value.ProjectId == project.Id));
        Assert.False(await verify.OutboxMessages.AnyAsync(
            value => value.AggregateId == project.Id));
    }

    private static ServiceProvider CreateServices(
        string connectionString,
        SaveChangesInterceptor? interceptor = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<Hook2StreamDbContext>(options =>
        {
            // Match the production registration. Opening an explicit
            // transaction outside this retry strategy must make this test fail.
            options.UseNpgsql(
                    connectionString,
                    npgsql => npgsql.EnableRetryOnFailure())
                .UseSnakeCaseNamingConvention();
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
        });
        return services.BuildServiceProvider();
    }

    private static Task SweepAsync(ServiceProvider services)
    {
        var service = new RetentionSweepService(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkerOptions
            {
                Capabilities = [JobRoutingRegistry.Control]
            }),
            Options.Create(new OperationalPolicyOptions
            {
                RetentionSweepEnabled = true,
                UnpaidProjectDays = 30,
                DeletionFenceMinutes = 15
            }),
            new FixedTimeProvider(Now),
            NullLogger<RetentionSweepService>.Instance);
        return service.SweepAsync(CancellationToken.None);
    }

    private static Hook2StreamDbContext CreateDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new Hook2StreamDbContext(options);
    }

    private static ReleaseProject NewProject(
        Guid workspaceId,
        string label,
        DateTimeOffset activity) => new()
    {
        WorkspaceId = workspaceId,
        ProjectLabel = label,
        ArtistName = "Artist",
        TrackTitle = "Track",
        FlowKind = FlowKind.Mp3First,
        LastActivityAt = activity
    };

    private sealed class FailCleanupOutboxInterceptor(Guid projectId)
        : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<OutboxMessage>().Any(
                    value =>
                        value.State == EntityState.Added &&
                        value.Entity.AggregateId == projectId &&
                        value.Entity.Destination == "job") == true)
            {
                throw new InvalidOperationException(
                    "Simulated crash before the retention transaction commit.");
            }

            return base.SavingChangesAsync(
                eventData,
                result,
                cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
