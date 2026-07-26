using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Hook2Stream.IntegrationTests;

public sealed class PostgresPreviewRetryTests
{
    [Fact]
    public async Task Concurrent_replay_requeues_preview_exactly_once_on_postgresql()
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
                "CI must provide HOOK2STREAM_TEST_POSTGRES for preview retry concurrency tests.");
            return;
        }

        var databaseName = $"hook2stream_preview_retry_{Guid.NewGuid():N}";
        var testConnectionString = new NpgsqlConnectionStringBuilder(
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
            await using (var migrationDb = CreateDb(testConnectionString))
            {
                await migrationDb.Database.MigrateAsync();
            }

            var saveBarrier = new PreviewRetrySaveBarrier();
            await using (var factory = new PostgresPreviewRetryApiFactory(
                             testConnectionString,
                             saveBarrier))
            using (var client = factory.CreateClient())
            {
                await Onboard(client);
                var seeded = await SeedFailedPreview(factory);

                using var release =
                    await client.GetAsync($"/api/v1/releases/{seeded.ProjectId}");
                release.EnsureSuccessStatusCode();
                var etag = release.Headers.ETag!.Tag;

                using var firstRequest = RetryRequest(
                    seeded.ProjectId,
                    seeded.JobId,
                    etag,
                    "postgres-concurrent-retry");
                using var secondRequest = RetryRequest(
                    seeded.ProjectId,
                    seeded.JobId,
                    etag,
                    "postgres-concurrent-retry");

                var firstTask = client.SendAsync(firstRequest);
                var secondTask = client.SendAsync(secondRequest);
                var responses = await Task.WhenAll(firstTask, secondTask)
                    .WaitAsync(TimeSpan.FromSeconds(30));

                using var first = responses[0];
                using var second = responses[1];
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
                Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
                Assert.Equal(2, saveBarrier.ArrivalCount);

                var firstBody =
                    await first.Content.ReadFromJsonAsync<JsonElement>();
                var secondBody =
                    await second.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(
                    seeded.JobId,
                    firstBody.GetProperty("jobId").GetGuid());
                Assert.Equal(
                    seeded.JobId,
                    secondBody.GetProperty("jobId").GetGuid());

                await using var scope = factory.Services.CreateAsyncScope();
                var db =
                    scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
                Assert.Equal(
                    1,
                    await db.ApiIdempotencyRecords.CountAsync(value =>
                        value.WorkspaceId == seeded.WorkspaceId &&
                        value.Scope == "preview.retry" &&
                        value.Key == "postgres-concurrent-retry"));
                Assert.Equal(
                    1,
                    await db.JobEvents.CountAsync(value =>
                        value.JobId == seeded.JobId &&
                        value.EventType == "requeued"));
                Assert.Equal(
                    1,
                    await db.ProjectEvents.CountAsync(value =>
                        value.ProjectId == seeded.ProjectId &&
                        value.EventType == "preview.retry_requested"));
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static HttpRequestMessage RetryRequest(
        Guid projectId,
        Guid jobId,
        string etag,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/releases/{projectId}/preview/retries")
        {
            Content = JsonContent.Create(new { failedJobId = jobId })
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            idempotencyKey);
        return request;
    }

    private static async Task<SeededPreview> SeedFailedPreview(
        WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db =
            scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var workspaceId =
            await db.Workspaces.Select(value => value.Id).SingleAsync();
        var project = new ReleaseProject
        {
            WorkspaceId = workspaceId,
            ProjectLabel = "PostgreSQL preview retry",
            ArtistName = "Test artist",
            TrackTitle = "Test track",
            Language = "en",
            FlowKind = FlowKind.Mp3First,
            Mode = ReleaseMode.Unscheduled,
            SetupCompletedAt = DateTimeOffset.UtcNow
        };
        var campaign = new CampaignPlanRevision
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Number = 1,
            State = RevisionState.ReadyForReview,
            TranscriptRevisionId = Guid.CreateVersion7(),
            ArtworkPackRevisionId = Guid.CreateVersion7(),
            HookSetRevisionId = Guid.CreateVersion7(),
            ItemsJson = "[]",
            SourceFingerprint = new string('c', 64)
        };
        project.CurrentCampaignPlanRevisionId = campaign.Id;
        var job = new Job
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Type = JobType.PreviewRender,
            RequiredCapability = "render",
            HandlerVersion = "deterministic-render-v1",
            PayloadJson = JsonSerializer.Serialize(new
            {
                projectId = project.Id,
                campaignRevisionId = campaign.Id,
                campaignItemId = Guid.CreateVersion7()
            }),
            State = JobState.Failed,
            AttemptCount = 3,
            MaxAttempts = 3,
            ProgressPercent = 65,
            ProgressStage = "rendering",
            ErrorCode = "job.database_contract_invalid",
            ErrorMessage = "Processing failed and requires attention.",
            CompletedAt = DateTimeOffset.UtcNow
        };
        var run = new PipelineRun
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Project = project,
            Number = 1,
            State = PipelineStageState.Failed,
            Trigger = "audio-upload"
        };
        run.Stages = Enum.GetValues<WorkflowLane>()
            .Select(lane => new PipelineStage
            {
                PipelineRun = run,
                PipelineRunId = run.Id,
                Lane = lane,
                State = lane switch
                {
                    WorkflowLane.Preview => PipelineStageState.Failed,
                    WorkflowLane.FinalRender => PipelineStageState.WaitingUser,
                    _ => PipelineStageState.Succeeded
                },
                ProgressPercent = lane == WorkflowLane.Preview ? 65 : 100,
                ErrorCode = lane == WorkflowLane.Preview
                    ? "job.database_contract_invalid"
                    : null,
                BlockerCode = lane == WorkflowLane.FinalRender
                    ? "purchase.required"
                    : null,
                CurrentJobId = lane == WorkflowLane.Preview ? job.Id : null
            })
            .ToList();
        db.Projects.Add(project);
        db.CampaignPlanRevisions.Add(campaign);
        db.Jobs.Add(job);
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync();
        return new SeededPreview(workspaceId, project.Id, job.Id);
    }

    private static async Task Onboard(HttpClient client)
    {
        using var response = await client.PutAsJsonAsync(
            "/api/v1/account/onboarding",
            new
            {
                workspaceName = "PostgreSQL preview retry tests",
                acceptTerms = true,
                acceptPrivacy = true,
                termsVersion = "draft-2026-07-16",
                privacyVersion = "draft-2026-07-16",
                displayName = "Test artist"
            });
        response.EnsureSuccessStatusCode();
    }

    private static Hook2StreamDbContext CreateDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.EnableRetryOnFailure())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new Hook2StreamDbContext(options);
    }

    private sealed record SeededPreview(
        Guid WorkspaceId,
        Guid ProjectId,
        Guid JobId);

    private sealed class PostgresPreviewRetryApiFactory(
        string connectionString,
        PreviewRetrySaveBarrier saveBarrier)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Auth:Mode", "OAuth");
            builder.UseSetting(
                "Storage:AccessKey",
                "test-access-key");
            builder.UseSetting(
                "Storage:SecretKey",
                "test-secret-key");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<
                    DbContextOptions<Hook2StreamDbContext>>();
                services.RemoveAll<
                    IDbContextOptionsConfiguration<Hook2StreamDbContext>>();
                services.RemoveAll<Hook2StreamDbContext>();
                services.AddSingleton(saveBarrier);
                services.AddDbContext<Hook2StreamDbContext>(
                    (serviceProvider, options) =>
                    {
                        options.UseNpgsql(
                                connectionString,
                                npgsql => npgsql.EnableRetryOnFailure())
                            .UseSnakeCaseNamingConvention()
                            .AddInterceptors(
                                serviceProvider.GetRequiredService<
                                    PreviewRetrySaveBarrier>());
                    });

                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage, FakeObjectStorage>();

                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme =
                            TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme =
                            TestAuthHandler.SchemeName;
                    })
                    .AddScheme<
                        AuthenticationSchemeOptions,
                        TestAuthHandler>(
                        TestAuthHandler.SchemeName,
                        _ => { });
            });
        }
    }

    private sealed class PreviewRetrySaveBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _bothRequestsArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivalCount;

        public int ArrivalCount => Volatile.Read(ref _arrivalCount);

        public override async ValueTask<InterceptionResult<int>>
            SavingChangesAsync(
                DbContextEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
        {
            var isPreviewRetry = eventData.Context?.ChangeTracker
                .Entries<ApiIdempotencyRecord>()
                .Any(entry =>
                    entry.State == EntityState.Added &&
                    entry.Entity.Scope == "preview.retry") == true;
            if (!isPreviewRetry)
            {
                return result;
            }

            if (Interlocked.Increment(ref _arrivalCount) == 2)
            {
                _bothRequestsArrived.TrySetResult();
            }

            await _bothRequestsArrived.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken);
            return result;
        }
    }
}
