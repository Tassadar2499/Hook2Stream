using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Jobs;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hook2Stream.IntegrationTests;

public sealed class DataLifecycleTests
{
    [Fact]
    public async Task Reads_do_not_extend_retention_but_archive_does()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var quick = await QuickUpload(client, "lifecycle-activity-1");
        quick.EnsureSuccessStatusCode();
        var body = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = body.GetProperty("project").GetProperty("id").GetGuid();
        var oldActivity = DateTimeOffset.UtcNow.AddDays(-29);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var project = await db.Projects.SingleAsync(value => value.Id == projectId);
            project.LastActivityAt = oldActivity;
            await db.SaveChangesAsync();
        }

        var read = await client.GetAsync($"/api/v1/releases/{projectId}");
        read.EnsureSuccessStatusCode();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            Assert.Equal(
                oldActivity,
                (await db.Projects.SingleAsync(value => value.Id == projectId)).LastActivityAt);
        }

        using var archive = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/releases/{projectId}/archive");
        archive.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.Tag);
        var archived = await client.SendAsync(archive);
        archived.EnsureSuccessStatusCode();

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.True(
            (await verifyDb.Projects.SingleAsync(value => value.Id == projectId)).LastActivityAt >
            oldActivity);
    }

    [Fact]
    public async Task Expired_idempotency_key_can_start_a_new_command()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);

        var first = await QuickUpload(client, "lifecycle-idempotency-1");
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstProjectId = firstBody.GetProperty("project").GetProperty("id").GetGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var record = await db.ApiIdempotencyRecords.SingleAsync(value =>
                value.Scope == "release.audio-upload" && value.Key == "lifecycle-idempotency-1");
            record.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var second = await QuickUpload(client, "lifecycle-idempotency-1", "new-command.mp3");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(
            firstProjectId,
            secondBody.GetProperty("project").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Upload_session_has_a_fixed_deadline_and_expiry_is_enforced()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);

        var quick = await QuickUpload(client, "lifecycle-upload-1");
        Assert.Equal(HttpStatusCode.Created, quick.StatusCode);
        var body = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var upload = body.GetProperty("upload");
        var sessionId = upload.GetProperty("sessionId").GetGuid();
        var urlExpiresAt = upload.GetProperty("urlExpiresAt").GetDateTimeOffset();
        var sessionExpiresAt = upload.GetProperty("sessionExpiresAt").GetDateTimeOffset();
        Assert.True(sessionExpiresAt > urlExpiresAt);
        Assert.Equal(urlExpiresAt, upload.GetProperty("expiresAt").GetDateTimeOffset());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var session = await db.UploadSessions.SingleAsync(value => value.Id == sessionId);
            session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var expired = await client.GetAsync($"/api/v1/uploads/{sessionId}");
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        var problem = await expired.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("upload.session_expired", problem.GetProperty("code").GetString());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var expiredSession = await verifyDb.UploadSessions.SingleAsync(value => value.Id == sessionId);
        var asset = await verifyDb.MediaAssets.SingleAsync(value => value.Id == expiredSession.AssetId);
        Assert.Equal(UploadState.Expired, expiredSession.State);
        Assert.Equal(AssetState.Rejected, asset.State);
        Assert.False(asset.IsActive);
    }

    [Fact]
    public async Task Explicit_delete_hides_project_fences_work_and_queues_control_purge()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var quick = await QuickUpload(client, "lifecycle-delete-1");
        var body = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = body.GetProperty("project").GetProperty("id").GetGuid();
        var uploadUrlExpiresAt = body.GetProperty("upload").GetProperty("urlExpiresAt").GetDateTimeOffset();

        Guid queuedJobId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var project = await db.Projects.SingleAsync(value => value.Id == projectId);
            var queued = new Job
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Type = JobType.AudioAnalysis,
                RequiredCapability = "analysis",
                PayloadJson = "{}",
                State = JobState.Queued
            };
            queuedJobId = queued.Id;
            db.Jobs.Add(queued);
            await db.SaveChangesAsync();
        }

        var projectResponse = await client.GetAsync($"/api/v1/releases/{projectId}");
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/releases/{projectId}");
        delete.Headers.TryAddWithoutValidation("If-Match", projectResponse.Headers.ETag!.Tag);
        var deleted = await client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);
        var deletion = await deleted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(projectId, deletion.GetProperty("projectId").GetGuid());
        Assert.Equal("queued", deletion.GetProperty("state").GetString());
        Assert.True(
            deletion.GetProperty("purgeDueAt").GetDateTimeOffset() <=
            deletion.GetProperty("deletedAt").GetDateTimeOffset().AddDays(7));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/releases/{projectId}")).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var hidden = await verifyDb.Projects.IgnoreQueryFilters().SingleAsync(value => value.Id == projectId);
        Assert.NotNull(hidden.DeletedAt);
        var cancelled = await verifyDb.Jobs.SingleAsync(value => value.Id == queuedJobId);
        Assert.Equal(JobState.Cancelled, cancelled.State);
        var cleanup = await verifyDb.Jobs.SingleAsync(value =>
            value.ProjectId == projectId && value.Type == JobType.AssetCleanup);
        Assert.Equal("control", cleanup.RequiredCapability);
        Assert.Equal(JobState.Queued, cleanup.State);
        Assert.True(cleanup.AvailableAt > uploadUrlExpiresAt);
        Assert.Equal(
            UploadState.Expired,
            (await verifyDb.UploadSessions.SingleAsync(value => value.ProjectId == projectId)).State);
        var tombstone = await verifyDb.ProjectDeletionTombstones.SingleAsync(value => value.ProjectId == projectId);
        Assert.Equal("queued", tombstone.State);
    }

    [Fact]
    public async Task Archive_restore_preserves_attempt_sequence_for_the_next_lease()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var quick = await QuickUpload(client, "lifecycle-archive-running-1");
        quick.EnsureSuccessStatusCode();
        var body = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = body.GetProperty("project").GetProperty("id").GetGuid();
        Guid jobId;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var project = await db.Projects.SingleAsync(value => value.Id == projectId);
            var leaseToken = Guid.CreateVersion7();
            var running = new Job
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Type = JobType.AudioAnalysis,
                RequiredCapability = "analysis",
                HandlerVersion = "test-v1",
                PayloadJson = "{}",
                State = JobState.Running,
                AttemptCount = 1,
                MaxAttempts = 1,
                LeaseOwner = "analysis-worker",
                LeaseToken = leaseToken,
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
            };
            jobId = running.Id;
            db.Jobs.Add(running);
            db.JobAttempts.Add(new JobAttempt
            {
                JobId = running.Id,
                Number = 1,
                WorkerId = "analysis-worker",
                State = JobState.Running,
                StartedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var archive = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/releases/{projectId}/archive");
        archive.Headers.TryAddWithoutValidation("If-Match", quick.Headers.ETag!.Tag);
        var archived = await client.SendAsync(archive);
        archived.EnsureSuccessStatusCode();
        using var restore = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/releases/{projectId}/restore");
        restore.Headers.TryAddWithoutValidation("If-Match", archived.Headers.ETag!.Tag);
        var restored = await client.SendAsync(restore);
        restored.EnsureSuccessStatusCode();

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var restoredJob = await verifyDb.Jobs.SingleAsync(value => value.Id == jobId);
        Assert.Equal(JobState.Queued, restoredJob.State);
        Assert.Equal(1, restoredJob.AttemptCount);
        Assert.True(restoredJob.MaxAttempts >= 2);
        Assert.Equal(
            JobState.Cancelled,
            (await verifyDb.JobAttempts.SingleAsync(value => value.JobId == jobId && value.Number == 1)).State);

        // Mirror the queue's atomic lease increment. The retained attempt #1
        // must make the restored lease use #2, not violate (JobId, Number).
        var nextLeaseToken = Guid.CreateVersion7();
        restoredJob.State = JobState.Running;
        restoredJob.AttemptCount++;
        restoredJob.LeaseOwner = "analysis-worker-restored";
        restoredJob.LeaseToken = nextLeaseToken;
        restoredJob.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        verifyDb.JobAttempts.Add(new JobAttempt
        {
            JobId = restoredJob.Id,
            Number = restoredJob.AttemptCount,
            WorkerId = restoredJob.LeaseOwner,
            State = JobState.Running,
            StartedAt = DateTimeOffset.UtcNow
        });
        await verifyDb.SaveChangesAsync();

        await new PostgresJobQueue(verifyDb).CompleteAsync(
            restoredJob.Id,
            "analysis-worker-restored",
            nextLeaseToken,
            CancellationToken.None);

        Assert.Equal(JobState.Succeeded, restoredJob.State);
        var attemptNumbers = await verifyDb.JobAttempts
            .Where(value => value.JobId == restoredJob.Id)
            .OrderBy(value => value.Number)
            .Select(value => value.Number)
            .ToArrayAsync();
        Assert.Equal(new[] { 1, 2 }, attemptNumbers);
    }

    private static async Task Onboard(HttpClient client)
    {
        var response = await client.PutAsJsonAsync(
            "/api/v1/account/onboarding",
            new
            {
                workspaceName = "Lifecycle workspace",
                acceptTerms = true,
                acceptPrivacy = true,
                termsVersion = "2026-09-04",
                privacyVersion = "2026-09-04",
                displayName = "Lifecycle artist"
            });
        response.EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> QuickUpload(
        HttpClient client,
        string idempotencyKey,
        string fileName = "lifecycle.mp3")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/releases/audio-uploads")
        {
            Content = JsonContent.Create(new
            {
                fileName,
                contentType = "audio/mpeg",
                sizeBytes = 1024,
                confirmsContentRights = true,
                allowsExternalAiProcessing = true
            })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }
}
