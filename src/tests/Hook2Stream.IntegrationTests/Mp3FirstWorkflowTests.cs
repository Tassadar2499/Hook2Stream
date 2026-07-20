using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hook2Stream.IntegrationTests;

public sealed class Mp3FirstWorkflowTests
{
    [Fact]
    public async Task Quick_audio_intake_requires_explicit_rights_and_external_ai_consent()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);

        using var noRights = new HttpRequestMessage(HttpMethod.Post, "/api/v1/releases/audio-uploads")
        {
            Content = JsonContent.Create(new
            {
                fileName = "song.mp3",
                contentType = "audio/mpeg",
                sizeBytes = 1024,
                confirmsContentRights = false,
                allowsExternalAiProcessing = true
            })
        };
        noRights.Headers.TryAddWithoutValidation("Idempotency-Key", "quick-no-rights");
        var rightsResponse = await client.SendAsync(noRights);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rightsResponse.StatusCode);

        using var noAi = new HttpRequestMessage(HttpMethod.Post, "/api/v1/releases/audio-uploads")
        {
            Content = JsonContent.Create(new
            {
                fileName = "song.mp3",
                contentType = "audio/mpeg",
                sizeBytes = 1024,
                confirmsContentRights = true,
                allowsExternalAiProcessing = false
            })
        };
        noAi.Headers.TryAddWithoutValidation("Idempotency-Key", "quick-no-ai");
        var aiResponse = await client.SendAsync(noAi);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, aiResponse.StatusCode);
    }

    [Fact]
    public async Task Quick_audio_intake_is_mp3_only_and_idempotent()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);

        var missingKey = await client.PostAsJsonAsync(
            "/api/v1/releases/audio-uploads",
            new
            {
                fileName = "song.mp3",
                contentType = "audio/mpeg",
                sizeBytes = 1024,
                confirmsContentRights = true,
                allowsExternalAiProcessing = true
            });
        Assert.Equal((HttpStatusCode)428, missingKey.StatusCode);

        var first = await QuickUpload(client, "quick-1", "song.mp3", 1024);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = firstJson.GetProperty("project").GetProperty("id").GetGuid();
        Assert.Equal("mp3First", firstJson.GetProperty("workflow").GetProperty("flowKind").GetString());
        Assert.Equal("uploadAudio", firstJson.GetProperty("workflow").GetProperty("nextAction").GetString());

        var repeated = await QuickUpload(client, "quick-1", "song.mp3", 1024);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var repeatedJson = await repeated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(projectId, repeatedJson.GetProperty("project").GetProperty("id").GetGuid());

        var mismatched = await QuickUpload(client, "quick-1", "other.mp3", 1024);
        Assert.Equal(HttpStatusCode.Conflict, mismatched.StatusCode);
        var problem = await mismatched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency.payload_mismatch", problem.GetProperty("code").GetString());

        var wav = await QuickUpload(client, "quick-wav", "song.wav", 1024, "audio/wav");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wav.StatusCode);
    }

    [Fact]
    public async Task Instrumental_setup_requires_confirmation_and_creates_an_approved_empty_transcript()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var quick = await QuickUpload(client, "instrumental-1", "instrumental.mp3", 1024);
        var json = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = json.GetProperty("project").GetProperty("id").GetGuid();
        var projectEtag = quick.Headers.ETag!.Tag;

        var rejected = await PutSetup(client, projectId, projectEtag, instrumentalConfirmed: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        var accepted = await PutSetup(client, projectId, projectEtag, instrumentalConfirmed: true);
        accepted.EnsureSuccessStatusCode();
        var transcript = await client.GetAsync($"/api/v1/releases/{projectId}/transcript");
        transcript.EnsureSuccessStatusCode();
        var transcriptJson = await transcript.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("instrumental", transcriptJson.GetProperty("source").GetString());
        Assert.Equal("approved", transcriptJson.GetProperty("state").GetString());
        Assert.Empty(transcriptJson.GetProperty("phrases").EnumerateArray());
    }

    [Fact]
    public async Task Instrumental_confirmation_supersedes_racing_automatic_transcript_and_dependants()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var quick = await QuickUpload(client, "instrumental-race-1", "race.mp3", 1024);
        var quickJson = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = quickJson.GetProperty("project").GetProperty("id").GetGuid();
        Guid automaticId;
        Guid hooksId;
        Guid campaignId;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var project = await db.Projects.SingleAsync(value => value.Id == projectId);
            var automatic = new TranscriptRevision
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Number = 1,
                Source = TranscriptSource.Automatic,
                State = RevisionState.ReadyForReview,
                Language = "en",
                PhrasesJson = JsonSerializer.Serialize(new[] { new { text = "stale lyrics" } }),
                SourceFingerprint = new string('9', 64)
            };
            var hooks = new HookSetRevision
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Number = 1,
                State = RevisionState.Approved,
                TranscriptRevisionId = automatic.Id,
                HooksJson = "[]",
                SourceFingerprint = new string('8', 64)
            };
            var campaign = new CampaignPlanRevision
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                Number = 1,
                State = RevisionState.ReadyForReview,
                TranscriptRevisionId = automatic.Id,
                HookSetRevisionId = hooks.Id,
                ArtworkPackRevisionId = Guid.CreateVersion7(),
                ItemsJson = "[]",
                SourceFingerprint = new string('7', 64)
            };
            automaticId = automatic.Id;
            hooksId = hooks.Id;
            campaignId = campaign.Id;
            project.CurrentTranscriptRevisionId = automatic.Id;
            project.CurrentHookSetRevisionId = hooks.Id;
            project.CurrentCampaignPlanRevisionId = campaign.Id;
            db.TranscriptRevisions.Add(automatic);
            db.HookSetRevisions.Add(hooks);
            db.CampaignPlanRevisions.Add(campaign);
            await db.SaveChangesAsync();
        }

        var refreshed = await client.GetAsync($"/api/v1/releases/{projectId}");
        refreshed.EnsureSuccessStatusCode();
        var setup = await PutSetup(client, projectId, refreshed.Headers.ETag!.Tag, instrumentalConfirmed: true);
        setup.EnsureSuccessStatusCode();

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var projectAfter = await verifyDb.Projects.SingleAsync(value => value.Id == projectId);
        var current = await verifyDb.TranscriptRevisions.SingleAsync(value => value.Id == projectAfter.CurrentTranscriptRevisionId);
        Assert.Equal(TranscriptSource.Instrumental, current.Source);
        Assert.Equal(RevisionState.Approved, current.State);
        Assert.Equal("[]", current.PhrasesJson);
        Assert.Equal(RevisionState.Superseded, (await verifyDb.TranscriptRevisions.SingleAsync(value => value.Id == automaticId)).State);
        Assert.Equal(RevisionState.Superseded, (await verifyDb.HookSetRevisions.SingleAsync(value => value.Id == hooksId)).State);
        Assert.Equal(RevisionState.Superseded, (await verifyDb.CampaignPlanRevisions.SingleAsync(value => value.Id == campaignId)).State);
        Assert.Null(projectAfter.CurrentHookSetRevisionId);
        Assert.Null(projectAfter.CurrentCampaignPlanRevisionId);
    }

    [Fact]
    public async Task Transcript_approval_rejects_unacknowledged_low_confidence_phrases()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var quick = await QuickUpload(client, "transcript-1", "lyrics.mp3", 1024);
        var quickJson = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = quickJson.GetProperty("project").GetProperty("id").GetGuid();

        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/releases/{projectId}/transcript")
        {
            Content = JsonContent.Create(Transcript(warningAcknowledged: false))
        };
        put.Headers.TryAddWithoutValidation("If-Match", quick.Headers.ETag!.Tag);
        var created = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>();
        var revisionId = createdJson.GetProperty("revisionId").GetGuid();

        using var approve = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/releases/{projectId}/transcript/approve")
        {
            Content = JsonContent.Create(new { revisionId })
        };
        approve.Headers.TryAddWithoutValidation("If-Match", created.Headers.ETag!.Tag);
        var response = await client.SendAsync(approve);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("transcript.warnings_unresolved", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Artwork_requires_external_ai_consent_after_setup_and_rights()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var quick = await QuickUpload(client, "artwork-1", "cover-me.mp3", 1024);
        var quickJson = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = quickJson.GetProperty("project").GetProperty("id").GetGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var asset = await db.MediaAssets.SingleAsync(value => value.ProjectId == projectId);
            asset.State = AssetState.Ready;
            asset.IsActive = true;
            asset.Sha256 = new string('a', 64);
            asset.DurationMilliseconds = 180_000;
            await db.SaveChangesAsync();
        }

        var setup = await PutSetup(
            client,
            projectId,
            quick.Headers.ETag!.Tag,
            instrumentalConfirmed: true,
            mode: "upcoming",
            releaseDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        setup.EnsureSuccessStatusCode();
        using var rights = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/releases/{projectId}/rights")
        {
            Content = JsonContent.Create(new
            {
                ownsAudioRights = true,
                ownsLyricsRights = false,
                ownsVisualRights = true,
                // A legacy artwork-only flag cannot authorize broader AI processing.
                allowsExternalAiArtwork = true,
                allowsExternalAiProcessing = false,
                syntheticContentStatus = "none",
                policyVersion = "2026-07"
            })
        };
        rights.Headers.TryAddWithoutValidation("If-Match", setup.Headers.ETag!.Tag);
        var rightsResponse = await client.SendAsync(rights);
        rightsResponse.EnsureSuccessStatusCode();

        using var artwork = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/releases/{projectId}/artwork")
        {
            Content = JsonContent.Create(new { prompt = "Midnight city lights", style = "editorial" })
        };
        artwork.Headers.TryAddWithoutValidation("Idempotency-Key", "artwork-no-consent");
        var response = await client.SendAsync(artwork);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rights.external_ai_processing_required", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Artwork_rejects_unscheduled_release_until_timing_is_confirmed()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var quick = await QuickUpload(client, "artwork-timing-1", "timing.mp3", 1024);
        var quickJson = await quick.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = quickJson.GetProperty("project").GetProperty("id").GetGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var asset = await db.MediaAssets.SingleAsync(value => value.ProjectId == projectId);
            asset.State = AssetState.Ready;
            asset.IsActive = true;
            asset.Sha256 = new string('b', 64);
            asset.DurationMilliseconds = 180_000;
            await db.SaveChangesAsync();
        }

        var setup = await PutSetup(client, projectId, quick.Headers.ETag!.Tag, instrumentalConfirmed: true);
        setup.EnsureSuccessStatusCode();
        using var rights = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/releases/{projectId}/rights")
        {
            Content = JsonContent.Create(new
            {
                ownsAudioRights = true,
                ownsLyricsRights = false,
                ownsVisualRights = true,
                allowsExternalAiArtwork = true,
                allowsExternalAiProcessing = true,
                syntheticContentStatus = "none",
                policyVersion = "2026-07"
            })
        };
        rights.Headers.TryAddWithoutValidation("If-Match", setup.Headers.ETag!.Tag);
        (await client.SendAsync(rights)).EnsureSuccessStatusCode();

        using var artwork = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/releases/{projectId}/artwork")
        {
            Content = JsonContent.Create(new { prompt = "A future-facing cover", style = "editorial" })
        };
        artwork.Headers.TryAddWithoutValidation("Idempotency-Key", "artwork-no-timing");
        var response = await client.SendAsync(artwork);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("release.timing_required", problem.GetProperty("code").GetString());
    }

    private static async Task<HttpResponseMessage> QuickUpload(
        HttpClient client,
        string key,
        string fileName,
        long sizeBytes,
        string contentType = "audio/mpeg")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/releases/audio-uploads")
        {
            Content = JsonContent.Create(new
            {
                fileName,
                contentType,
                sizeBytes,
                confirmsContentRights = true,
                allowsExternalAiProcessing = true
            })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PutSetup(
        HttpClient client,
        Guid projectId,
        string etag,
        bool instrumentalConfirmed,
        string mode = "unscheduled",
        DateOnly? releaseDate = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/releases/{projectId}/setup")
        {
            Content = JsonContent.Create(new
            {
                projectLabel = "Instrumental draft",
                artistName = "Test artist",
                trackTitle = "Test track",
                language = "en",
                mode,
                releaseDate,
                campaignStartDate = (DateOnly?)null,
                isInstrumental = true,
                isInstrumentalConfirmed = instrumentalConfirmed,
                internalNotes = (string?)null
            })
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }

    private static object Transcript(bool warningAcknowledged) => new
    {
        source = "manual",
        language = "en",
        isInstrumental = false,
        phrases = new[]
        {
            new
            {
                id = "phrase-1",
                order = 0,
                text = "A low confidence line",
                startMilliseconds = 1000,
                endMilliseconds = 5000,
                confidence = .5,
                warningAcknowledged,
                words = Array.Empty<object>()
            }
        }
    };

    private static async Task Onboard(HttpClient client)
    {
        var response = await client.PutAsJsonAsync("/api/v1/account/onboarding", new
        {
            workspaceName = "MP3 workflow tests",
            acceptTerms = true,
            acceptPrivacy = true,
            termsVersion = "draft-2026-07-16",
            privacyVersion = "draft-2026-07-16",
            displayName = "Test artist"
        });
        response.EnsureSuccessStatusCode();
    }
}
