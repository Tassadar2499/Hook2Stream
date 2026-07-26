using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class RetentionSweepTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Unpaid_retention_uses_user_activity_and_persists_fence_with_outbox()
    {
        await using var fixture = await RetentionFixture.CreateAsync();
        var cutoff = Now.AddDays(-30);
        var exactCutoff = NewProject("exact", cutoff);
        var oldCreatedButActive = NewProject("active", cutoff.AddTicks(1));
        oldCreatedButActive.CreatedAt = Now.AddDays(-365);
        await fixture.SeedAsync(exactCutoff, oldCreatedButActive);

        await fixture.SweepAsync();

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var fenced = await db.Projects.IgnoreQueryFilters()
            .SingleAsync(value => value.Id == exactCutoff.Id);
        var active = await db.Projects.SingleAsync(
            value => value.Id == oldCreatedButActive.Id);
        Assert.NotNull(fenced.DeletedAt);
        Assert.Null(active.DeletedAt);

        var tombstone = await db.ProjectDeletionTombstones.SingleAsync(
            value => value.ProjectId == exactCutoff.Id);
        var outbox = await db.OutboxMessages.SingleAsync(
            value => value.DedupeKey == $"retention:project:{tombstone.Id:N}");
        Assert.Equal("job", outbox.Destination);
        Assert.Equal("job.asset_cleanup", outbox.MessageType);
        Assert.Null(outbox.ProcessedAt);
        Assert.Empty(await db.Jobs.Where(value =>
            value.ProjectId == exactCutoff.Id &&
            value.Type == JobType.AssetCleanup).ToListAsync());
    }

    [Fact]
    public async Task Entitlement_checkout_and_active_work_each_protect_an_old_project()
    {
        await using var fixture = await RetentionFixture.CreateAsync();
        var old = Now.AddDays(-31);
        var entitled = NewProject("entitled", old);
        var checkingOut = NewProject("checkout", old);
        var processing = NewProject("processing", old);
        await fixture.SeedAsync(
            entitled,
            checkingOut,
            processing,
            new Entitlement
            {
                WorkspaceId = entitled.WorkspaceId,
                ProjectId = entitled.Id,
                ProductCode = "release-pack",
                State = EntitlementState.Exhausted,
                ItemIdsJson = "[]",
                ProviderPeriodKey = "retention-period"
            },
            new BillingCheckout
            {
                WorkspaceId = checkingOut.WorkspaceId,
                ProjectId = checkingOut.Id,
                ProductCode = "release-pack",
                AmountCents = 1,
                State = CheckoutState.Pending,
                IdempotencyKey = "retention-checkout",
                RequestHash = "hash"
            },
            new Job
            {
                WorkspaceId = processing.WorkspaceId,
                ProjectId = processing.Id,
                Type = JobType.AudioAnalysis,
                RequiredCapability = JobRoutingRegistry.Analysis,
                PayloadJson = "{}",
                State = JobState.Running
            });

        await fixture.SweepAsync();

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var ids = new[] { entitled.Id, checkingOut.Id, processing.Id };
        Assert.Equal(3, await db.Projects.CountAsync(value => ids.Contains(value.Id)));
        Assert.Empty(await db.ProjectDeletionTombstones.ToListAsync());
    }

    [Fact]
    public async Task Disabled_sweep_is_a_no_op()
    {
        await using var fixture = await RetentionFixture.CreateAsync(enabled: false);
        var project = NewProject("disabled", Now.AddDays(-365));
        await fixture.SeedAsync(project);

        await fixture.SweepAsync();

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.NotNull(await db.Projects.SingleOrDefaultAsync(
            value => value.Id == project.Id));
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Fenced_asset_without_delivery_is_recovered_through_outbox()
    {
        await using var fixture = await RetentionFixture.CreateAsync();
        var project = NewProject("asset recovery", Now);
        var asset = new MediaAsset
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Kind = AssetKind.Visual,
            Purpose = AssetPurpose.Source,
            State = AssetState.Deleted,
            IsActive = false,
            OriginalFileName = "stranded.png",
            DeclaredContentType = "image/png",
            ObjectKey = $"workspaces/{project.WorkspaceId:N}/projects/{project.Id:N}/asset.png",
            DeletedAt = Now.AddHours(-1)
        };
        await fixture.SeedAsync(project, asset);

        await fixture.SweepAsync();

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var outbox = await db.OutboxMessages.SingleAsync(
            value => value.DedupeKey == $"retention:asset:{asset.Id:N}");
        Assert.Equal("job", outbox.Destination);
        Assert.Contains(asset.Id.ToString(), outbox.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_asset_cleanup_is_requeued_without_duplicate_outbox()
    {
        await using var fixture = await RetentionFixture.CreateAsync();
        var project = NewProject("asset retry", Now);
        var asset = new MediaAsset
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Kind = AssetKind.Visual,
            Purpose = AssetPurpose.Source,
            State = AssetState.Deleted,
            IsActive = false,
            OriginalFileName = "retry.png",
            DeclaredContentType = "image/png",
            ObjectKey = $"workspaces/{project.WorkspaceId:N}/projects/{project.Id:N}/retry.png",
            DeletedAt = Now.AddHours(-1)
        };
        var failed = new Job
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            AssetId = asset.Id,
            Type = JobType.AssetCleanup,
            RequiredCapability = JobRoutingRegistry.Control,
            PayloadJson = "{}",
            IdempotencyKey = $"asset-cleanup:{asset.Id:N}",
            State = JobState.Failed,
            AttemptCount = 3,
            MaxAttempts = 3,
            CompletedAt = Now.AddMinutes(-1),
            ErrorCode = "storage.unavailable"
        };
        await fixture.SeedAsync(project, asset, failed);

        await fixture.SweepAsync();

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var requeued = await db.Jobs.SingleAsync(value => value.Id == failed.Id);
        Assert.Equal(JobState.Queued, requeued.State);
        Assert.True(requeued.MaxAttempts >= 4);
        Assert.Null(requeued.ErrorCode);
        Assert.Empty(await db.OutboxMessages.ToListAsync());
        Assert.Single(await db.JobEvents.Where(
            value => value.JobId == failed.Id && value.EventType == "requeued").ToListAsync());
    }

    [Fact]
    public async Task Worker_updates_do_not_extend_activity_and_touch_is_monotonic()
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"retention-activity-{Guid.NewGuid():N}")
            .Options;
        var activity = Now.AddDays(-20);
        await using var db = new Hook2StreamDbContext(options);
        var project = NewProject("worker", activity);
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        project.State = ProjectState.Analyzing;
        await db.SaveChangesAsync();
        Assert.Equal(activity, project.LastActivityAt);

        ProjectActivity.Touch(project, activity.AddDays(-1));
        Assert.Equal(activity, project.LastActivityAt);
        ProjectActivity.Touch(project, Now);
        Assert.Equal(Now, project.LastActivityAt);
    }

    private static ReleaseProject NewProject(string label, DateTimeOffset activity) => new()
    {
        WorkspaceId = Guid.CreateVersion7(),
        ProjectLabel = label,
        ArtistName = "Artist",
        TrackTitle = "Track",
        FlowKind = FlowKind.Mp3First,
        LastActivityAt = activity
    };

    private sealed class RetentionFixture(
        ServiceProvider services,
        OperationalPolicyOptions policy) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;

        public static Task<RetentionFixture> CreateAsync(bool enabled = true)
        {
            var services = new ServiceCollection();
            var databaseName = $"retention-sweep-{Guid.NewGuid():N}";
            services.AddDbContext<Hook2StreamDbContext>(options =>
                options
                    .UseInMemoryDatabase(databaseName)
                    .ConfigureWarnings(value =>
                        value.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            var provider = services.BuildServiceProvider();
            return Task.FromResult(new RetentionFixture(
                provider,
                new OperationalPolicyOptions
                {
                    RetentionSweepEnabled = enabled,
                    UnpaidProjectDays = 30,
                    DeletionFenceMinutes = 15
                }));
        }

        public async Task SeedAsync(params object[] entities)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            db.AddRange(entities);
            await db.SaveChangesAsync();
        }

        public Task SweepAsync()
        {
            var service = new RetentionSweepService(
                Services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new WorkerOptions
                {
                    Capabilities = [JobRoutingRegistry.Control]
                }),
                Options.Create(policy),
                new FixedTimeProvider(Now),
                NullLogger<RetentionSweepService>.Instance);
            return service.SweepAsync(CancellationToken.None);
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
