using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Jobs;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class AssetCleanupLifecycleTests
{
    [Fact]
    public async Task Deleting_selected_cover_cancels_transitive_campaign_render()
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"asset-cover-fence-{Guid.NewGuid():N}")
            .Options;
        await using var db = new Hook2StreamDbContext(options);
        var workspaceId = Guid.NewGuid();
        var project = NewProject(workspaceId);
        var cover = NewAsset(project, AssetPurpose.ApprovedCover, "cover.png", "image/png");
        var pack = new ArtworkPackRevision
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Number = 1,
            OperationNumber = 1,
            SelectedAssetId = cover.Id,
            State = RevisionState.Approved
        };
        var campaign = new CampaignPlanRevision
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Number = 1,
            TranscriptRevisionId = Guid.NewGuid(),
            ArtworkPackRevisionId = pack.Id,
            HookSetRevisionId = Guid.NewGuid(),
            State = RevisionState.Approved
        };
        var leaseToken = Guid.NewGuid();
        var render = NewJob(
            project,
            JobType.FinalRender,
            JobRoutingRegistry.Render,
            JsonSerializer.Serialize(new { projectId = project.Id, campaignRevisionId = campaign.Id }),
            JobState.Running,
            leaseToken);
        var attempt = NewAttempt(render);
        db.AddRange(project, cover, pack, campaign, render, attempt);
        await db.SaveChangesAsync();

        await AssetDeletionCoordinator.FenceAsync(
            db,
            cover,
            DateTimeOffset.UtcNow,
            "asset.deleted",
            CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(JobState.Cancelled, render.State);
        Assert.Equal(JobState.Cancelled, attempt.State);
        Assert.Equal("asset.deleted", render.ErrorCode);
    }

    [Fact]
    public async Task Deleting_rendered_video_cancels_export_for_its_batch()
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"asset-video-fence-{Guid.NewGuid():N}")
            .Options;
        await using var db = new Hook2StreamDbContext(options);
        var workspaceId = Guid.NewGuid();
        var project = NewProject(workspaceId);
        var batchId = Guid.NewGuid();
        var video = NewAsset(project, AssetPurpose.CampaignVideo, "video.mp4", "video/mp4");
        video.RenderBatchId = batchId;
        video.CampaignItemId = Guid.NewGuid();
        var export = NewJob(
            project,
            JobType.ExportBundle,
            JobRoutingRegistry.Export,
            JsonSerializer.Serialize(new { projectId = project.Id, renderBatchId = batchId }),
            JobState.Queued);
        db.AddRange(project, video, export);
        await db.SaveChangesAsync();

        await AssetDeletionCoordinator.FenceAsync(
            db,
            video,
            DateTimeOffset.UtcNow,
            "asset.deleted",
            CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(JobState.Cancelled, export.State);
        Assert.Equal("asset.deleted", export.ErrorCode);
    }

    [Fact]
    public void Paid_retention_starts_at_the_later_of_asset_and_entitlement_creation()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var policy = new OperationalPolicyOptions
        {
            PaidSourceDays = 90,
            PaidOutputDays = 365,
            SupersededArtworkDays = 30
        };
        var source = new MediaAsset
        {
            WorkspaceId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Purpose = AssetPurpose.Source,
            ObjectKey = "source.mp3",
            DeclaredContentType = "audio/mpeg",
            OriginalFileName = "source.mp3",
            CreatedAt = now.AddDays(-400)
        };

        Assert.False(RetentionSweepService.IsAssetPastRetention(
            source,
            now.AddDays(-1),
            isProtectedArtwork: false,
            now,
            policy));
        Assert.True(RetentionSweepService.IsAssetPastRetention(
            source,
            now.AddDays(-91),
            isProtectedArtwork: false,
            now,
            policy));
    }

    [Fact]
    public void Protected_campaign_artwork_is_not_removed_by_the_unselected_artwork_policy()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var background = new MediaAsset
        {
            WorkspaceId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Purpose = AssetPurpose.CampaignBackground,
            ObjectKey = "background.png",
            DeclaredContentType = "image/png",
            OriginalFileName = "background.png",
            CreatedAt = now.AddDays(-400)
        };

        Assert.False(RetentionSweepService.IsAssetPastRetention(
            background,
            now.AddDays(-400),
            isProtectedArtwork: true,
            now,
            new OperationalPolicyOptions()));
    }

    [Fact]
    public async Task Project_purge_preserves_current_attempt_until_queue_completion()
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"asset-cleanup-lifecycle-{Guid.NewGuid():N}")
            .ConfigureWarnings(value => value.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new Hook2StreamDbContext(options);
        var now = DateTimeOffset.UtcNow;
        var workspaceId = Guid.CreateVersion7();
        var project = new ReleaseProject
        {
            WorkspaceId = workspaceId,
            ProjectLabel = "Delete me",
            ArtistName = "Artist",
            TrackTitle = "Track",
            FlowKind = FlowKind.Mp3First,
            DeletedAt = now.AddHours(-1)
        };
        var tombstone = new ProjectDeletionTombstone
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            RequestedAt = now.AddHours(-1),
            PurgeDueAt = now.AddMinutes(-1),
            State = "queued"
        };
        var leaseToken = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new AssetCleanupPayload(project.Id, tombstone.Id));
        var cleanupJob = new Job
        {
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Type = JobType.AssetCleanup,
            RequiredCapability = JobRoutingRegistry.Control,
            HandlerVersion = "asset-cleanup-v1",
            PayloadJson = payload,
            State = JobState.Running,
            AttemptCount = 1,
            LeaseOwner = "cleanup-worker",
            LeaseToken = leaseToken,
            LeaseExpiresAt = now.AddMinutes(1)
        };
        var attempt = new JobAttempt
        {
            JobId = cleanupJob.Id,
            Number = 1,
            WorkerId = "cleanup-worker",
            State = JobState.Running,
            StartedAt = now
        };
        db.AddRange(project, tombstone, cleanupJob, attempt);
        await db.SaveChangesAsync();
        var leased = new LeasedJob(
            cleanupJob.Id,
            workspaceId,
            project.Id,
            null,
            JobType.AssetCleanup,
            payload,
            1,
            cleanupJob.MaxAttempts,
            JobRoutingRegistry.Control,
            cleanupJob.HandlerVersion,
            null,
            1,
            "cleanup-worker",
            cleanupJob.LeaseExpiresAt!.Value,
            leaseToken);
        var handler = new AssetCleanupJobHandler(
            db,
            new RecordingStorage(),
            new FixedTimeProvider(now),
            Options.Create(new OperationalPolicyOptions { DeletionFenceMinutes = 15 }));

        await handler.ProcessAsync(leased, CancellationToken.None);

        Assert.Equal("purged", tombstone.State);
        Assert.NotNull(tombstone.ContentPurgedAt);
        Assert.Null(attempt.DeletedAt);
        Assert.Equal(payload, cleanupJob.PayloadJson);

        await new PostgresJobQueue(db).CompleteAsync(
            cleanupJob.Id,
            "cleanup-worker",
            leaseToken,
            CancellationToken.None);

        Assert.Equal(JobState.Succeeded, cleanupJob.State);
        Assert.Equal(JobState.Succeeded, attempt.State);
        Assert.NotNull(attempt.CompletedAt);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ReleaseProject NewProject(Guid workspaceId) => new()
    {
        WorkspaceId = workspaceId,
        ProjectLabel = "Lifecycle project",
        ArtistName = "Artist",
        TrackTitle = "Track",
        FlowKind = FlowKind.Mp3First
    };

    private static MediaAsset NewAsset(
        ReleaseProject project,
        AssetPurpose purpose,
        string fileName,
        string contentType) => new()
    {
        WorkspaceId = project.WorkspaceId,
        ProjectId = project.Id,
        Purpose = purpose,
        State = AssetState.Ready,
        OriginalFileName = fileName,
        DeclaredContentType = contentType,
        ObjectKey = $"workspaces/{project.WorkspaceId:N}/projects/{project.Id:N}/assets/{Guid.NewGuid():N}/{fileName}",
        IsActive = true
    };

    private static Job NewJob(
        ReleaseProject project,
        JobType type,
        string capability,
        string payloadJson,
        JobState state,
        Guid? leaseToken = null) => new()
    {
        WorkspaceId = project.WorkspaceId,
        ProjectId = project.Id,
        Type = type,
        RequiredCapability = capability,
        PayloadJson = payloadJson,
        State = state,
        AttemptCount = state == JobState.Running ? 1 : 0,
        LeaseOwner = state == JobState.Running ? "worker" : null,
        LeaseToken = leaseToken,
        LeaseExpiresAt = state == JobState.Running ? DateTimeOffset.UtcNow.AddMinutes(1) : null
    };

    private static JobAttempt NewAttempt(Job job) => new()
    {
        JobId = job.Id,
        Number = job.AttemptCount,
        WorkerId = job.LeaseOwner!,
        State = JobState.Running,
        StartedAt = DateTimeOffset.UtcNow
    };

    private sealed class RecordingStorage : IObjectStorage
    {
        public Task EnsureBucketAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Uri> CreateUploadUrlAsync(string objectKey, string contentType, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Uri> CreateReadUrlAsync(string objectKey, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MultipartUpload> CreateMultipartUploadAsync(string objectKey, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Uri> CreateMultipartPartUploadUrlAsync(string objectKey, string uploadId, int partNumber, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteMultipartUploadAsync(string objectKey, string uploadId, IReadOnlyList<MultipartPart> parts, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortMultipartUploadAsync(string objectKey, string uploadId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StorageObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken) => Task.FromResult<StorageObjectInfo?>(null);
        public Task DownloadAsync(string objectKey, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UploadAsync(string objectKey, string sourcePath, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteProjectObjectsAsync(ProjectStorageScope scope, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAssetObjectsAsync(AssetStorageScope scope, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
