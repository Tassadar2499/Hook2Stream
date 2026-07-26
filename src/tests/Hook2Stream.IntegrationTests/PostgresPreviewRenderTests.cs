extern alias worker;

using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using worker::Hook2Stream.Worker;

namespace Hook2Stream.IntegrationTests;

public sealed class PostgresPreviewRenderTests
{
    [Fact]
    public async Task Producer_job_lookup_is_valid_jsonb_sql_and_idempotent_on_postgresql()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            Assert.False(
                string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "CI must provide HOOK2STREAM_TEST_POSTGRES so preview idempotency is exercised on Npgsql.");
            return;
        }

        var databaseName = $"hook2stream_preview_{Guid.NewGuid():N}";
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
            await using var db = CreateDb(testConnection);
            await db.Database.MigrateAsync();
            var seeded = await SeedExistingPreview(db);
            var handler = new VideoRenderJobHandler(
                JobType.PreviewRender,
                db,
                null!,
                null!,
                null!);

            await handler.ProcessAsync(seeded.Job, CancellationToken.None);
            db.ChangeTracker.Clear();
            await handler.ProcessAsync(seeded.Job, CancellationToken.None);

            Assert.Equal(
                1,
                await db.MediaAssets.CountAsync(value =>
                    value.ProjectId == seeded.ProjectId &&
                    value.ProducerJobId == seeded.Job.Id));
        }
        finally
        {
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<SeededPreview> SeedExistingPreview(Hook2StreamDbContext db)
    {
        var user = new AppUser
        {
            ExternalSubject = $"preview-postgres-{Guid.NewGuid():N}"
        };
        var workspace = new Workspace
        {
            OwnerUserId = user.Id,
            OwnerUser = user,
            Name = "PostgreSQL preview tests",
            TermsVersion = "test",
            PrivacyVersion = "test",
            TermsAcceptedAt = DateTimeOffset.UtcNow,
            PrivacyAcceptedAt = DateTimeOffset.UtcNow
        };
        var project = new ReleaseProject
        {
            WorkspaceId = workspace.Id,
            Workspace = workspace,
            ProjectLabel = "Preview lookup",
            ArtistName = "Test artist",
            TrackTitle = "Test track",
            Language = "en",
            FlowKind = FlowKind.Mp3First,
            Mode = ReleaseMode.Unscheduled
        };
        var campaignItemId = Guid.CreateVersion7();
        var campaign = new CampaignPlanRevision
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Number = 1,
            State = RevisionState.ReadyForReview,
            TranscriptRevisionId = Guid.CreateVersion7(),
            ArtworkPackRevisionId = Guid.CreateVersion7(),
            HookSetRevisionId = Guid.CreateVersion7(),
            ItemsJson = "[]",
            SourceFingerprint = new string('c', 64)
        };
        var jobId = Guid.CreateVersion7();
        var preview = new MediaAsset
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Project = project,
            Kind = AssetKind.Visual,
            Origin = AssetOrigin.Generated,
            Purpose = AssetPurpose.PreviewVideo,
            State = AssetState.Ready,
            OriginalFileName = "preview.mp4",
            DeclaredContentType = "video/mp4",
            DetectedContentType = "video/mp4",
            DeclaredBytes = 128,
            ActualBytes = 128,
            ObjectKey = $"tests/previews/{Guid.NewGuid():N}.mp4",
            IsActive = true,
            CampaignItemId = campaignItemId,
            ProducerJobId = jobId,
            ProvenanceJson = JsonSerializer.Serialize(new { jobId = jobId.ToString("N") })
        };
        db.Users.Add(user);
        db.Workspaces.Add(workspace);
        db.Projects.Add(project);
        db.CampaignPlanRevisions.Add(campaign);
        db.MediaAssets.Add(preview);
        await db.SaveChangesAsync();

        var leaseToken = Guid.CreateVersion7();
        return new SeededPreview(
            project.Id,
            new LeasedJob(
                jobId,
                workspace.Id,
                project.Id,
                null,
                JobType.PreviewRender,
                JsonSerializer.Serialize(new
                {
                    projectId = project.Id,
                    campaignRevisionId = campaign.Id,
                    campaignItemId,
                    renderBatchId = (Guid?)null,
                    audioAssetId = (Guid?)null,
                    audioFingerprint = (string?)null
                }),
                1,
                3,
                JobRoutingRegistry.Render,
                "deterministic-render-v1",
                null,
                1,
                "postgres-preview-test",
                DateTimeOffset.UtcNow.AddMinutes(1),
                leaseToken));
    }

    private static Hook2StreamDbContext CreateDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new Hook2StreamDbContext(options);
    }

    private sealed record SeededPreview(Guid ProjectId, LeasedJob Job);
}
