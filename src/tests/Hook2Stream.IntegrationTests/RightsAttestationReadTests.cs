using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hook2Stream.IntegrationTests;

public sealed class RightsAttestationReadTests
{
    [Fact]
    public async Task Current_attestation_and_instrumental_confirmation_are_hydratable_and_tenant_safe()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", "rights-owner");
        await Onboard(client, "Rights owner");

        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/v1/releases/audio-uploads")
        {
            Content = JsonContent.Create(new
            {
                fileName = "rights.mp3",
                contentType = "audio/mpeg",
                sizeBytes = 4_000_000,
                confirmsContentRights = true,
                allowsExternalAiProcessing = true
            })
        };
        upload.Headers.TryAddWithoutValidation("Idempotency-Key", "rights-hydration");
        var uploaded = await client.SendAsync(upload);
        uploaded.EnsureSuccessStatusCode();
        var uploadedJson = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = uploadedJson.GetProperty("project").GetProperty("id").GetGuid();
        var audioAssetId = uploadedJson.GetProperty("upload").GetProperty("assetId").GetGuid();
        var audioFingerprint = new string('c', 64);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var audio = await db.MediaAssets.SingleAsync(value => value.Id == audioAssetId);
            audio.State = AssetState.Ready;
            audio.IsActive = true;
            audio.Sha256 = audioFingerprint;
            audio.DurationMilliseconds = 180_000;
            await db.SaveChangesAsync();
        }

        using var setup = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/releases/{projectId}/setup")
        {
            Content = JsonContent.Create(new
            {
                projectLabel = "Rights hydration",
                artistName = "Test artist",
                trackTitle = "Test instrumental",
                language = "en",
                mode = "upcoming",
                releaseDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)),
                campaignStartDate = (DateOnly?)null,
                isInstrumental = true,
                isInstrumentalConfirmed = true,
                internalNotes = (string?)null
            })
        };
        setup.Headers.TryAddWithoutValidation("If-Match", uploaded.Headers.ETag!.Tag);
        var setupResponse = await client.SendAsync(setup);
        setupResponse.EnsureSuccessStatusCode();

        var reservedRightsResponse = await client.GetAsync($"/api/v1/releases/{projectId}/rights");
        reservedRightsResponse.EnsureSuccessStatusCode();
        var reservedRights = await reservedRightsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(reservedRights.GetProperty("ownsAudioRights").GetBoolean());
        Assert.True(reservedRights.GetProperty("ownsLyricsRights").GetBoolean());
        Assert.True(reservedRights.GetProperty("allowsExternalAiProcessing").GetBoolean());
        Assert.Equal("external-ai-zdr-v1", reservedRights.GetProperty("policyVersion").GetString());
        Assert.Equal(audioAssetId, reservedRights.GetProperty("audioAssetId").GetGuid());
        Assert.Equal(JsonValueKind.Null, reservedRights.GetProperty("audioFingerprint").ValueKind);

        using var saveRights = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/releases/{projectId}/rights")
        {
            Content = JsonContent.Create(new
            {
                ownsAudioRights = true,
                ownsLyricsRights = false,
                ownsVisualRights = true,
                allowsExternalAiArtwork = true,
                allowsExternalAiProcessing = true,
                syntheticContentStatus = "assisted",
                policyVersion = "mp3-first-test"
            })
        };
        saveRights.Headers.TryAddWithoutValidation("If-Match", setupResponse.Headers.ETag!.Tag);
        var saved = await client.SendAsync(saveRights);
        saved.EnsureSuccessStatusCode();

        var releaseResponse = await client.GetAsync($"/api/v1/releases/{projectId}");
        releaseResponse.EnsureSuccessStatusCode();
        var release = await releaseResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(release.GetProperty("isInstrumentalConfirmed").GetBoolean());

        var currentResponse = await client.GetAsync($"/api/v1/releases/{projectId}/rights");
        currentResponse.EnsureSuccessStatusCode();
        Assert.Equal(releaseResponse.Headers.ETag, currentResponse.Headers.ETag);
        var current = await currentResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(current.GetProperty("ownsAudioRights").GetBoolean());
        Assert.False(current.GetProperty("ownsLyricsRights").GetBoolean());
        Assert.True(current.GetProperty("ownsVisualRights").GetBoolean());
        Assert.True(current.GetProperty("allowsExternalAiArtwork").GetBoolean());
        Assert.True(current.GetProperty("allowsExternalAiProcessing").GetBoolean());
        Assert.Equal("assisted", current.GetProperty("syntheticContentStatus").GetString());
        Assert.Equal("external-ai-zdr-v1", current.GetProperty("policyVersion").GetString());
        Assert.Equal(audioAssetId, current.GetProperty("audioAssetId").GetGuid());
        Assert.Equal(audioFingerprint, current.GetProperty("audioFingerprint").GetString());
        Assert.Equal(release.GetProperty("version").GetInt64(), current.GetProperty("projectVersion").GetInt64());

        var runningJobId = Guid.CreateVersion7();
        var queuedJobId = Guid.CreateVersion7();
        var visualIngestJobId = Guid.CreateVersion7();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            db.MediaAssets.Add(new MediaAsset
            {
                WorkspaceId = await db.Projects.Where(value => value.Id == projectId).Select(value => value.WorkspaceId).SingleAsync(),
                ProjectId = projectId,
                Kind = AssetKind.Cover,
                Origin = AssetOrigin.Uploaded,
                State = AssetState.Ready,
                OriginalFileName = "artist-cover.png",
                DeclaredContentType = "image/png",
                DetectedContentType = "image/png",
                DeclaredBytes = 1024,
                ActualBytes = 1024,
                ObjectKey = $"tests/{projectId:N}/artist-cover.png",
                IsActive = true,
                Sha256 = new string('d', 64)
            });
            var processingVisual = new MediaAsset
            {
                WorkspaceId = await db.Projects.Where(value => value.Id == projectId).Select(value => value.WorkspaceId).SingleAsync(),
                ProjectId = projectId,
                Kind = AssetKind.Visual,
                Origin = AssetOrigin.Uploaded,
                State = AssetState.Processing,
                OriginalFileName = "artist-loop.mp4",
                DeclaredContentType = "video/mp4",
                DeclaredBytes = 2048,
                ObjectKey = $"tests/{projectId:N}/artist-loop.mp4"
            };
            db.MediaAssets.Add(processingVisual);
            var workspaceId = await db.Projects.Where(value => value.Id == projectId).Select(value => value.WorkspaceId).SingleAsync();
            var runningJob = new Job
            {
                Id = runningJobId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Type = JobType.FinalRender,
                State = JobState.Running,
                RequiredCapability = JobRoutingRegistry.Render,
                PayloadJson = "{}",
                AttemptCount = 1,
                LeaseOwner = "rights-test-worker",
                LeaseToken = Guid.CreateVersion7(),
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
            };
            runningJob.Attempts.Add(new JobAttempt
            {
                Number = 1,
                WorkerId = "rights-test-worker",
                State = JobState.Running,
                StartedAt = DateTimeOffset.UtcNow
            });
            db.Jobs.Add(runningJob);
            db.Jobs.Add(new Job
            {
                Id = queuedJobId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Type = JobType.ExportBundle,
                State = JobState.Queued,
                RequiredCapability = JobRoutingRegistry.Export,
                PayloadJson = "{}"
            });
            var visualIngestJob = new Job
            {
                Id = visualIngestJobId,
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                AssetId = processingVisual.Id,
                Type = JobType.MediaIngest,
                State = JobState.Running,
                RequiredCapability = JobRoutingRegistry.Media,
                PayloadJson = "{}",
                AttemptCount = 1,
                LeaseOwner = "rights-test-worker",
                LeaseToken = Guid.CreateVersion7(),
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
            };
            visualIngestJob.Attempts.Add(new JobAttempt
            {
                Number = 1,
                WorkerId = "rights-test-worker",
                State = JobState.Running,
                StartedAt = DateTimeOffset.UtcNow
            });
            db.Jobs.Add(visualIngestJob);
            await db.SaveChangesAsync();
        }

        using var revokeVisualRights = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/releases/{projectId}/rights")
        {
            Content = JsonContent.Create(new
            {
                ownsAudioRights = true,
                ownsLyricsRights = false,
                ownsVisualRights = false,
                allowsExternalAiArtwork = true,
                allowsExternalAiProcessing = true,
                syntheticContentStatus = "assisted",
                policyVersion = "mp3-first-test"
            })
        };
        revokeVisualRights.Headers.TryAddWithoutValidation("If-Match", currentResponse.Headers.ETag!.Tag);
        var visualRevoked = await client.SendAsync(revokeVisualRights);
        visualRevoked.EnsureSuccessStatusCode();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var cancelledJobs = await db.Jobs
                .Where(value => value.Id == runningJobId ||
                                value.Id == queuedJobId ||
                                value.Id == visualIngestJobId)
                .OrderBy(value => value.Id)
                .ToListAsync();
            Assert.All(cancelledJobs, value =>
            {
                Assert.Equal(JobState.Cancelled, value.State);
                Assert.Equal("waiting_user", value.ProgressStage);
                Assert.Equal("rights.visual_required", value.ErrorCode);
                Assert.Null(value.LeaseToken);
            });
            var cancelledAttempts = await db.JobAttempts
                .Where(value => value.JobId == runningJobId || value.JobId == visualIngestJobId)
                .ToListAsync();
            Assert.Equal(2, cancelledAttempts.Count);
            Assert.All(cancelledAttempts, value =>
            {
                Assert.Equal(JobState.Cancelled, value.State);
                Assert.Equal("rights.visual_required", value.ErrorCode);
            });
        }

        var allRightsRestored = await PutRights(
            client,
            projectId,
            visualRevoked.Headers.ETag!.Tag,
            ownsAudioRights: true,
            ownsVisualRights: true);
        allRightsRestored.EnsureSuccessStatusCode();
        var allRightsRevoked = await PutRights(
            client,
            projectId,
            allRightsRestored.Headers.ETag!.Tag,
            ownsAudioRights: false,
            ownsVisualRights: false);
        allRightsRevoked.EnsureSuccessStatusCode();
        var onlyContentRestored = await PutRights(
            client,
            projectId,
            allRightsRevoked.Headers.ETag!.Tag,
            ownsAudioRights: true,
            ownsVisualRights: false);
        onlyContentRestored.EnsureSuccessStatusCode();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var export = await db.Jobs.SingleAsync(value => value.Id == queuedJobId);
            Assert.Equal(JobState.Cancelled, export.State);
            Assert.Equal("waiting_user", export.ProgressStage);
            Assert.Equal("rights.required", export.ErrorCode);
        }

        client.DefaultRequestHeaders.Remove("X-Test-Subject");
        client.DefaultRequestHeaders.Add("X-Test-Subject", "rights-stranger");
        await Onboard(client, "Rights stranger");
        var foreign = await client.GetAsync($"/api/v1/releases/{projectId}/rights");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Revoking_external_ai_consent_cancels_and_restoring_it_resumes_only_ai_jobs()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client, "AI consent owner");

        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/v1/releases/audio-uploads")
        {
            Content = JsonContent.Create(new
            {
                fileName = "consent.mp3",
                contentType = "audio/mpeg",
                sizeBytes = 4_000_000,
                confirmsContentRights = true,
                allowsExternalAiProcessing = true
            })
        };
        upload.Headers.TryAddWithoutValidation("Idempotency-Key", "external-ai-revocation");
        var uploaded = await client.SendAsync(upload);
        uploaded.EnsureSuccessStatusCode();
        var uploadJson = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = uploadJson.GetProperty("project").GetProperty("id").GetGuid();
        var audioAssetId = uploadJson.GetProperty("upload").GetProperty("assetId").GetGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var audio = await db.MediaAssets.SingleAsync(value => value.Id == audioAssetId);
            audio.State = AssetState.Ready;
            audio.IsActive = true;
            audio.Sha256 = new string('e', 64);
            audio.DurationMilliseconds = 180_000;
            await db.SaveChangesAsync();
        }

        var bound = await PutExternalAiRights(client, projectId, uploaded.Headers.ETag!.Tag, allowsExternalAiProcessing: true);
        bound.EnsureSuccessStatusCode();

        Guid[] aiJobIds;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var workspaceId = await db.Projects.Where(value => value.Id == projectId).Select(value => value.WorkspaceId).SingleAsync();
            var jobs = new[]
            {
                new Job
                {
                    WorkspaceId = workspaceId,
                    ProjectId = projectId,
                    AssetId = audioAssetId,
                    Type = JobType.Transcription,
                    State = JobState.Running,
                    RequiredCapability = JobRoutingRegistry.Control,
                    HandlerVersion = "openrouter-stt-v1",
                    PayloadJson = "{}",
                    AttemptCount = 1,
                    LeaseOwner = "consent-test-worker",
                    LeaseToken = Guid.CreateVersion7(),
                    LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
                },
                new Job
                {
                    WorkspaceId = workspaceId,
                    ProjectId = projectId,
                    Type = JobType.ArtworkGeneration,
                    State = JobState.Queued,
                    RequiredCapability = JobRoutingRegistry.Control,
                    HandlerVersion = "openrouter-image-v1",
                    PayloadJson = "{}"
                },
                new Job
                {
                    WorkspaceId = workspaceId,
                    ProjectId = projectId,
                    Type = JobType.CampaignGeneration,
                    State = JobState.Queued,
                    RequiredCapability = JobRoutingRegistry.Control,
                    HandlerVersion = "openrouter-campaign-v1",
                    PayloadJson = "{}"
                }
            };
            jobs[0].Attempts.Add(new JobAttempt
            {
                Number = 1,
                WorkerId = "consent-test-worker",
                State = JobState.Running,
                StartedAt = DateTimeOffset.UtcNow
            });
            db.Jobs.AddRange(jobs);
            await db.SaveChangesAsync();
            aiJobIds = jobs.Select(value => value.Id).ToArray();
        }

        var revoked = await PutExternalAiRights(client, projectId, bound.Headers.ETag!.Tag, allowsExternalAiProcessing: false);
        revoked.EnsureSuccessStatusCode();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var jobs = await db.Jobs.Where(value => aiJobIds.Contains(value.Id)).ToListAsync();
            Assert.All(jobs, value =>
            {
                Assert.Equal(JobState.Cancelled, value.State);
                Assert.Equal("rights.external_ai_processing_required", value.ErrorCode);
                Assert.Null(value.LeaseToken);
            });
            var attempt = await db.JobAttempts.SingleAsync(value => value.JobId == aiJobIds[0]);
            Assert.Equal(JobState.Cancelled, attempt.State);
            Assert.Equal("rights.external_ai_processing_required", attempt.ErrorCode);
        }

        var restored = await PutExternalAiRights(client, projectId, revoked.Headers.ETag!.Tag, allowsExternalAiProcessing: true);
        restored.EnsureSuccessStatusCode();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var jobs = await db.Jobs.Where(value => aiJobIds.Contains(value.Id)).ToListAsync();
            Assert.All(jobs, value =>
            {
                Assert.Equal(JobState.Queued, value.State);
                Assert.Null(value.ErrorCode);
            });
        }
    }

    [Fact]
    public async Task Rights_read_requires_authentication()
    {
        await using var factory = new LocalAuthenticationApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/releases/{Guid.CreateVersion7()}/rights");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PutRights(
        HttpClient client,
        Guid projectId,
        string etag,
        bool ownsAudioRights,
        bool ownsVisualRights)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/releases/{projectId}/rights")
        {
            Content = JsonContent.Create(new
            {
                ownsAudioRights,
                ownsLyricsRights = false,
                ownsVisualRights,
                allowsExternalAiArtwork = true,
                allowsExternalAiProcessing = true,
                syntheticContentStatus = "assisted",
                policyVersion = "mp3-first-test"
            })
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PutExternalAiRights(
        HttpClient client,
        Guid projectId,
        string etag,
        bool allowsExternalAiProcessing)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/releases/{projectId}/rights")
        {
            Content = JsonContent.Create(new
            {
                ownsAudioRights = true,
                ownsLyricsRights = true,
                ownsVisualRights = true,
                // Keep the legacy consent true to prove it cannot override a
                // revocation of the broader processing permission.
                allowsExternalAiArtwork = true,
                allowsExternalAiProcessing,
                syntheticContentStatus = "none",
                policyVersion = "ignored-client-version"
            })
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }

    private static async Task Onboard(HttpClient client, string workspaceName)
    {
        var response = await client.PutAsJsonAsync("/api/v1/account/onboarding", new
        {
            workspaceName,
            acceptTerms = true,
            acceptPrivacy = true,
            termsVersion = "draft-2026-07-16",
            privacyVersion = "draft-2026-07-16",
            displayName = workspaceName
        });
        response.EnsureSuccessStatusCode();
    }
}
