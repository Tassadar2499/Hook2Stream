using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Api;

public static class Mp3FirstEndpoints
{
    private static readonly TimeSpan ReadUrlLifetime = TimeSpan.FromMinutes(10);
    private const int TranscriptMaxPhraseCount = 2_000;
    private const int TranscriptMaxTotalTextLength = 200_000;
    private const string ExternalAiPolicyVersion = "external-ai-zdr-v1";
    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> CampaignTemplates = new HashSet<string>(
        ["kinetic-lyrics", "animated-cover", "visual-loop-a", "visual-loop-b", "teaser", "countdown", "out-now", "post-release-cta", "momentum"],
        StringComparer.Ordinal);

    public static RouteGroupBuilder MapMp3FirstApi(this RouteGroupBuilder api)
    {
        api.MapPost("/releases/audio-uploads", CreateQuickAudioUpload)
            .Produces<QuickAudioUploadResponse>(StatusCodes.Status201Created);
        api.MapPut("/releases/{projectId:guid}/setup", UpdateSetup)
            .Produces<ReleaseResponse>();
        api.MapGet("/releases/{projectId:guid}/workflow", GetWorkflow)
            .Produces<WorkflowResponse>();
        api.MapGet("/releases/{projectId:guid}/events", StreamProjectEvents)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream");

        api.MapGet("/releases/{projectId:guid}/transcript", GetTranscript)
            .Produces<TranscriptResponse>();
        api.MapPut("/releases/{projectId:guid}/transcript", PutTranscript)
            .Produces<TranscriptResponse>(StatusCodes.Status201Created);
        api.MapPost("/releases/{projectId:guid}/transcript/approve", ApproveTranscript)
            .Produces<TranscriptResponse>();
        api.MapPost("/releases/{projectId:guid}/transcript/regenerations", RegenerateTranscript)
            .Produces<JobAcceptedResponse>(StatusCodes.Status202Accepted);

        api.MapGet("/releases/{projectId:guid}/artwork", GetArtwork)
            .Produces<ArtworkPackResponse>();
        api.MapPost("/releases/{projectId:guid}/artwork", GenerateArtwork)
            .Produces<JobAcceptedResponse>(StatusCodes.Status202Accepted);
        api.MapPut("/releases/{projectId:guid}/artwork/selection", UpdateArtworkSelection)
            .Produces<ArtworkPackResponse>();
        api.MapPost("/releases/{projectId:guid}/artwork/cover-approval", ApproveCover)
            .Produces<ArtworkPackResponse>();

        api.MapGet("/releases/{projectId:guid}/hooks", GetHooks)
            .Produces<HookSetResponse>();
        api.MapPut("/releases/{projectId:guid}/hooks", PutHooks)
            .Produces<HookSetResponse>(StatusCodes.Status201Created);

        api.MapGet("/releases/{projectId:guid}/campaign", GetCampaign)
            .Produces<CampaignResponse>();
        api.MapPut("/releases/{projectId:guid}/campaign/items/{itemId:guid}", PutCampaignItem)
            .Produces<CampaignResponse>(StatusCodes.Status201Created);
        api.MapPost("/releases/{projectId:guid}/preview/retries", RetryPreview)
            .Produces<JobAcceptedResponse>(StatusCodes.Status202Accepted);

        api.MapGet("/releases/{projectId:guid}/assets/{assetId:guid}/view-url", GetAssetReadUrl)
            .Produces<AssetReadUrlResponse>();
        return api;
    }

    private static async Task<IResult> CreateQuickAudioUpload(
        QuickAudioUploadRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        IObjectStorage storage,
        IOptions<OperationalPolicyOptions> policyOptions,
        TimeProvider timeProvider,
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        CancellationToken cancellationToken)
    {
        ValidateQuickAudio(request);
        var now = timeProvider.GetUtcNow();
        var uploadUrlLifetime = TimeSpan.FromMinutes(policyOptions.Value.UploadUrlMinutes);
        var uploadSessionLifetime = TimeSpan.FromHours(policyOptions.Value.UploadSessionHours);
        var idempotencyKey = RequireIdempotencyKey(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var requestHash = Hash(
            $"{Path.GetFileName(request.FileName)}\n{request.ContentType.ToLowerInvariant()}\n{request.SizeBytes}\n{request.ConfirmsContentRights}\n{request.AllowsExternalAiProcessing}");

        var existing = await db.ApiIdempotencyRecords
            .SingleOrDefaultAsync(
                value => value.WorkspaceId == context.Workspace.Id &&
                         value.Scope == "release.audio-upload" &&
                         value.Key == idempotencyKey,
                cancellationToken);
        if (existing is not null && existing.ExpiresAt <= now)
        {
            db.ApiIdempotencyRecords.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
            existing = null;
        }

        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(existing.RequestHash),
                    Encoding.ASCII.GetBytes(requestHash)))
            {
                throw Problem(409, "idempotency.payload_mismatch", "This idempotency key was used with a different request.");
            }

            var existingProject = await Project(db, context.Workspace.Id, existing.ResourceId, true, cancellationToken);
            var existingSession = await db.UploadSessions
                .Include(value => value.Asset)
                .SingleAsync(value => value.Id == existing.SecondaryResourceId, cancellationToken);
            var upload = await RefreshUploadResponse(
                existingSession,
                storage,
                uploadUrlLifetime,
                now,
                cancellationToken);
            var workflow = await BuildWorkflow(db, existingProject, cancellationToken);
            ApiEndpointHelpers.SetEtag(httpResponse, existingProject.Version);
            return Results.Ok(new QuickAudioUploadResponse(ToRelease(existingProject), upload, workflow));
        }

        var brandVersion = await db.BrandKits
            .Where(value => value.WorkspaceId == context.Workspace.Id)
            .Select(value => value.Version)
            .SingleAsync(cancellationToken);
        var fileStem = Path.GetFileNameWithoutExtension(request.FileName).Trim();
        var project = new ReleaseProject
        {
            WorkspaceId = context.Workspace.Id,
            ProjectLabel = string.IsNullOrWhiteSpace(fileStem) ? "New release" : fileStem[..Math.Min(160, fileStem.Length)],
            ArtistName = string.Empty,
            // Ingest applies ID3 title first and uses the filename only as a
            // fallback. Keeping this blank also protects edits made while the
            // upload is processing: only a still-blank draft is suggested.
            TrackTitle = string.Empty,
            Language = "en",
            Mode = ReleaseMode.Unscheduled,
            FlowKind = FlowKind.Mp3First,
            BrandKitVersion = brandVersion
        };
        ProjectActivity.Touch(project, now);
        var asset = new MediaAsset
        {
            WorkspaceId = context.Workspace.Id,
            ProjectId = project.Id,
            Project = project,
            Kind = AssetKind.Audio,
            Origin = AssetOrigin.Uploaded,
            Purpose = AssetPurpose.AudioMaster,
            OriginalFileName = Path.GetFileName(request.FileName),
            DeclaredContentType = request.ContentType,
            DeclaredBytes = request.SizeBytes,
            ObjectKey = string.Empty
        };
        asset.ObjectKey = ObjectKeyFactory.Original(context.Workspace.Id, project.Id, asset.Id, 1);
        var rights = new RightsAttestation
        {
            ProjectId = project.Id,
            Project = project,
            ActorSubject = currentUser.Subject,
            PolicyVersion = ExternalAiPolicyVersion,
            OwnsAudioRights = true,
            OwnsLyricsRights = true,
            OwnsVisualRights = false,
            // The broad consent includes artwork. The legacy field is retained
            // only for backwards-compatible reads and never authorizes another
            // external-AI stage on its own.
            AllowsExternalAiArtwork = true,
            AllowsExternalAiProcessing = true,
            AudioAssetId = asset.Id,
            AudioFingerprint = null,
            SyntheticContentStatus = SyntheticContentStatus.Unknown,
            AcceptedAt = now
        };

        var multipart = request.SizeBytes >= MediaPolicy.MultipartThresholdBytes;
        MultipartUpload? multipartUpload = null;
        Uri? uploadUrl = null;
        if (multipart)
        {
            multipartUpload = await storage.CreateMultipartUploadAsync(
                asset.ObjectKey, asset.DeclaredContentType, cancellationToken);
        }
        else
        {
            uploadUrl = await storage.CreateUploadUrlAsync(
                asset.ObjectKey, asset.DeclaredContentType, uploadUrlLifetime, cancellationToken);
        }

        var session = new UploadSession
        {
            WorkspaceId = context.Workspace.Id,
            ProjectId = project.Id,
            Asset = asset,
            AssetId = asset.Id,
            ObjectKey = asset.ObjectKey,
            IsMultipart = multipart,
            MultipartUploadId = multipartUpload?.UploadId,
            PartSizeBytes = multipart ? MediaPolicy.MultipartPartSizeBytes : request.SizeBytes,
            ExpiresAt = now.Add(uploadSessionLifetime)
        };
        var pipelineRun = Mp3FirstPipelineFactory.CreateInitial(project, "audio-upload");

        db.Projects.Add(project);
        db.RightsAttestations.Add(rights);
        db.UploadSessions.Add(session);
        db.PipelineRuns.Add(pipelineRun);
        db.ApiIdempotencyRecords.Add(new ApiIdempotencyRecord
        {
            WorkspaceId = context.Workspace.Id,
            Scope = "release.audio-upload",
            Key = idempotencyKey,
            RequestHash = requestHash,
            ResourceId = project.Id,
            SecondaryResourceId = session.Id,
            ExpiresAt = now.AddDays(policyOptions.Value.IdempotencyDays)
        });
        db.AuditEvents.Add(new AuditEvent
        {
            WorkspaceId = context.Workspace.Id,
            ActorSubject = currentUser.Subject,
            Action = "release.mp3_first_created",
            ResourceType = "release_project",
            ResourceId = project.Id,
            DataJson = "{}"
        });
        db.AuditEvents.Add(new AuditEvent
        {
            WorkspaceId = context.Workspace.Id,
            ActorSubject = currentUser.Subject,
            Action = "rights.external_ai_processing_accepted",
            ResourceType = "rights_attestation",
            ResourceId = rights.Id,
            DataJson = JsonSerializer.Serialize(new
            {
                rights.PolicyVersion,
                rights.AudioAssetId,
                rights.AllowsExternalAiProcessing
            })
        });
        db.ProjectEvents.Add(NewProjectEvent(project, "release.created", new { project.FlowKind }));
        db.ProjectEvents.Add(NewProjectEvent(project, "rights.accepted", new
        {
            rights.PolicyVersion,
            rights.AudioAssetId,
            rights.AllowsExternalAiProcessing
        }));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await BestEffortAbortMultipartUploadAsync(storage, asset.ObjectKey, multipartUpload);
            db.ChangeTracker.Clear();
            var winner = await db.ApiIdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
                value => value.WorkspaceId == context.Workspace.Id &&
                         value.Scope == "release.audio-upload" &&
                         value.Key == idempotencyKey &&
                         value.ExpiresAt > now,
                cancellationToken);
            if (winner is null) throw;
            if (!string.Equals(winner.RequestHash, requestHash, StringComparison.Ordinal))
                throw Problem(409, "idempotency.payload_mismatch", "This idempotency key was used with a different request.");
            var winnerProject = await Project(db, context.Workspace.Id, winner.ResourceId, true, cancellationToken);
            var winnerSession = await db.UploadSessions.Include(value => value.Asset)
                .SingleAsync(value => value.Id == winner.SecondaryResourceId, cancellationToken);
            var winnerUpload = await RefreshUploadResponse(
                winnerSession,
                storage,
                uploadUrlLifetime,
                now,
                cancellationToken);
            ApiEndpointHelpers.SetEtag(httpResponse, winnerProject.Version);
            return Results.Ok(new QuickAudioUploadResponse(
                ToRelease(winnerProject), winnerUpload, await BuildWorkflow(db, winnerProject, cancellationToken)));
        }
        catch
        {
            await BestEffortAbortMultipartUploadAsync(storage, asset.ObjectKey, multipartUpload);
            throw;
        }

        var response = new UploadSessionResponse(
            session.Id,
            asset.Id,
            multipart,
            uploadUrl?.ToString(),
            multipartUpload?.UploadId,
            session.PartSizeBytes,
            multipart ? (int)Math.Ceiling(request.SizeBytes / (double)session.PartSizeBytes) : 1,
            Min(now.Add(uploadUrlLifetime), session.ExpiresAt),
            session.ExpiresAt);
        var initialWorkflow = await BuildWorkflow(db, project, cancellationToken);
        ApiEndpointHelpers.SetEtag(httpResponse, project.Version);
        return Results.Created(
            $"/api/v1/releases/{project.Id}",
            new QuickAudioUploadResponse(ToRelease(project), response, initialWorkflow));
    }

    private static async Task BestEffortAbortMultipartUploadAsync(
        IObjectStorage storage,
        string objectKey,
        MultipartUpload? multipartUpload)
    {
        if (multipartUpload is null)
        {
            return;
        }

        try
        {
            // Persistence cancellation must not strand an already-created multipart
            // upload, so cleanup deliberately does not reuse the request token.
            await storage.AbortMultipartUploadAsync(
                objectKey,
                multipartUpload.UploadId,
                CancellationToken.None);
        }
        catch
        {
            // Multipart lifecycle cleanup is also enforced by the bucket policy.
            // Never replace the persistence failure (or request cancellation) with
            // a secondary storage-cleanup exception.
        }
    }

    private static async Task<IResult> UpdateSetup(
        Guid projectId,
        SetupReleaseRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        TimeProvider timeProvider,
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        ApiEndpointHelpers.RequireValid(ReleaseRules.ValidateSetup(request, DateOnly.FromDateTime(now.UtcDateTime)));
        var expected = ApiEndpointHelpers.RequireIfMatch(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, true, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expected, project.Version);

        var transcriptInputsChanged =
            !string.Equals(project.Language, request.Language.Trim(), StringComparison.OrdinalIgnoreCase) ||
            project.IsInstrumental != request.IsInstrumental ||
            project.IsInstrumentalConfirmed != request.IsInstrumentalConfirmed;
        var artworkInputsChanged =
            transcriptInputsChanged ||
            !string.Equals(project.ArtistName, request.ArtistName.Trim(), StringComparison.Ordinal) ||
            !string.Equals(project.TrackTitle, request.TrackTitle.Trim(), StringComparison.Ordinal);
        var campaignInputsChanged =
            artworkInputsChanged ||
            project.Mode != request.Mode ||
            project.ReleaseDate != request.ReleaseDate ||
            project.CampaignStartDate != request.CampaignStartDate;

        project.ProjectLabel = request.ProjectLabel.Trim();
        project.ArtistName = request.ArtistName.Trim();
        project.TrackTitle = request.TrackTitle.Trim();
        project.Language = request.Language.Trim().ToLowerInvariant();
        project.Mode = request.Mode;
        project.ReleaseDate = request.ReleaseDate;
        project.CampaignStartDate = request.CampaignStartDate;
        project.IsInstrumental = request.IsInstrumental;
        project.IsInstrumentalConfirmed = request.IsInstrumentalConfirmed;
        project.InternalNotes = request.InternalNotes?.Trim();
        project.SetupCompletedAt = now;

        if (transcriptInputsChanged && !(project.IsInstrumental && project.IsInstrumentalConfirmed))
        {
            if (project.CurrentTranscriptRevisionId is { } transcriptId)
            {
                var transcript = await db.TranscriptRevisions.SingleOrDefaultAsync(
                    value => value.Id == transcriptId,
                    cancellationToken);
                if (transcript is not null) transcript.State = RevisionState.Superseded;
                project.CurrentTranscriptRevisionId = null;
            }
            project.LyricsText = null;
        }

        if (transcriptInputsChanged && project.CurrentHookSetRevisionId is { } setupHookId)
        {
            var hooks = await db.HookSetRevisions.SingleOrDefaultAsync(
                value => value.Id == setupHookId,
                cancellationToken);
            if (hooks is not null) hooks.State = RevisionState.Superseded;
            project.CurrentHookSetRevisionId = null;
        }

        if (artworkInputsChanged && project.CurrentArtworkPackRevisionId is { } setupArtworkId)
        {
            var artwork = await db.ArtworkPackRevisions.SingleOrDefaultAsync(
                value => value.Id == setupArtworkId,
                cancellationToken);
            if (artwork is not null)
            {
                await ArtworkCreditLedger.ReleaseReservationAsync(
                    db,
                    project.WorkspaceId,
                    artwork.Id,
                    cancellationToken);
                if (artwork.State != RevisionState.Failed)
                    artwork.State = RevisionState.Superseded;
            }
            project.CurrentArtworkPackRevisionId = null;
        }

        if (campaignInputsChanged && project.CurrentCampaignPlanRevisionId is { } setupCampaignId)
        {
            var campaign = await db.CampaignPlanRevisions.SingleOrDefaultAsync(
                value => value.Id == setupCampaignId,
                cancellationToken);
            if (campaign is not null) campaign.State = RevisionState.Superseded;
            project.CurrentCampaignPlanRevisionId = null;
        }

        if (artworkInputsChanged || campaignInputsChanged)
        {
            var generatedOutputs = await db.MediaAssets
                .Where(value => value.ProjectId == project.Id &&
                                value.Origin == AssetOrigin.Generated &&
                                value.IsActive &&
                                (value.Purpose == AssetPurpose.CampaignBackground ||
                                 value.Purpose == AssetPurpose.PreviewVideo))
                .ToListAsync(cancellationToken);
            foreach (var output in generatedOutputs) output.IsActive = false;
        }

        var staleJobTypes = new List<JobType>();
        if (transcriptInputsChanged) staleJobTypes.Add(JobType.Transcription);
        if (artworkInputsChanged) staleJobTypes.Add(JobType.ArtworkGeneration);
        if (campaignInputsChanged)
        {
            staleJobTypes.Add(JobType.CampaignGeneration);
            staleJobTypes.Add(JobType.PreviewRender);
        }
        if (staleJobTypes.Count > 0)
        {
            var staleJobs = await db.Jobs
                .Where(value => value.ProjectId == project.Id &&
                                staleJobTypes.Contains(value.Type) &&
                                (value.State == JobState.Queued || value.State == JobState.Running))
                .ToListAsync(cancellationToken);
            foreach (var staleJob in staleJobs)
            {
                staleJob.State = JobState.Cancelled;
                staleJob.ErrorCode = "setup.changed";
                staleJob.ErrorMessage = "The job was cancelled because release setup changed.";
                staleJob.CompletedAt = now;
                staleJob.LeaseOwner = null;
                staleJob.LeaseToken = null;
                staleJob.LeaseExpiresAt = null;
                db.JobEvents.Add(new JobEvent
                {
                    JobId = staleJob.Id,
                    EventType = "cancelled",
                    DataJson = "{\"code\":\"setup.changed\"}"
                });
            }
        }

        if (project.IsInstrumental && project.IsInstrumentalConfirmed)
        {
            var audio = await db.MediaAssets.AsNoTracking()
                .Where(value =>
                    value.ProjectId == project.Id &&
                    value.Kind == AssetKind.Audio &&
                    value.State == AssetState.Ready &&
                    value.IsActive &&
                    value.Sha256 != null)
                .OrderByDescending(value => value.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            await InstrumentalTranscriptCoordinator.EnsureAsync(
                db,
                project,
                audio,
                currentUser.Subject,
                now,
                cancellationToken);
        }

        AddReconcile(db, project, "setup.updated");
        ProjectActivity.Touch(project, now);
        await db.SaveChangesAsync(cancellationToken);
        ApiEndpointHelpers.SetEtag(httpResponse, project.Version);
        return Results.Ok(ToRelease(project));
    }

    private static async Task<IResult> GetWorkflow(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, true, cancellationToken);
        var workflow = await BuildWorkflow(db, project, cancellationToken);
        ApiEndpointHelpers.SetEtag(response, workflow.WorkflowVersion);
        return Results.Ok(workflow);
    }

    private static async Task StreamProjectEvents(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        _ = await Project(db, context.Workspace.Id, projectId, false, cancellationToken);
        long afterSequence = 0;
        var requestedSequence = httpContext.Request.Headers["Last-Event-ID"].FirstOrDefault()
            ?? httpContext.Request.Query["after"].FirstOrDefault();
        _ = long.TryParse(requestedSequence, out afterSequence);

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await db.ProjectEvents.AsNoTracking()
                .Where(value => value.WorkspaceId == context.Workspace.Id && value.ProjectId == projectId && value.Sequence > afterSequence)
                .OrderBy(value => value.Sequence)
                .Take(100)
                .ToListAsync(cancellationToken);
            foreach (var projectEvent in events)
            {
                await httpContext.Response.WriteAsync($"id: {projectEvent.Sequence}\n", cancellationToken);
                await httpContext.Response.WriteAsync($"event: {projectEvent.EventType}\n", cancellationToken);
                await httpContext.Response.WriteAsync($"data: {projectEvent.DataJson}\n\n", cancellationToken);
                afterSequence = projectEvent.Sequence;
            }
            if (events.Count == 0)
                await httpContext.Response.WriteAsync(": keepalive\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static async Task<IResult> GetTranscript(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, false, cancellationToken);
        var revision = project.CurrentTranscriptRevisionId is { } revisionId
            ? await db.TranscriptRevisions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == revisionId, cancellationToken)
            : null;
        if (revision is null) throw NotFound();
        ApiEndpointHelpers.SetEtag(response, revision.Version);
        return Results.Ok(ToTranscript(revision, project.IsInstrumental));
    }

    private static async Task<IResult> PutTranscript(
        Guid projectId,
        PutTranscriptRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        ValidateTranscript(request);
        var expectedProjectVersion = ApiEndpointHelpers.RequireIfMatch(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, true, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expectedProjectVersion, project.Version);
        var sourceAudio = project.Assets.SingleOrDefault(value => value.Kind == AssetKind.Audio && value.IsActive);

        var previous = project.CurrentTranscriptRevisionId is { } previousId
            ? await db.TranscriptRevisions.SingleOrDefaultAsync(value => value.Id == previousId, cancellationToken)
            : null;
        if (previous is not null) previous.State = RevisionState.Superseded;
        var number = await db.TranscriptRevisions
            .Where(value => value.ProjectId == project.Id)
            .Select(value => value.Number)
            .DefaultIfEmpty()
            .MaxAsync(cancellationToken) + 1;
        var revision = new TranscriptRevision
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Number = number,
            Source = request.IsInstrumental ? TranscriptSource.Instrumental : request.Source,
            State = RevisionState.ReadyForReview,
            Language = request.Language.Trim().ToLowerInvariant(),
            PhrasesJson = JsonSerializer.Serialize(request.Phrases, StoredJson),
            SourceFingerprint = sourceAudio?.Sha256 ?? $"asset:{sourceAudio?.Id:N}:r{sourceAudio?.Revision}",
            SupersedesRevisionId = previous?.Id
        };
        db.TranscriptRevisions.Add(revision);
        project.CurrentTranscriptRevisionId = revision.Id;
        project.IsInstrumental = request.IsInstrumental;
        project.IsInstrumentalConfirmed = request.IsInstrumental;
        project.LyricsText = request.IsInstrumental ? null : string.Join('\n', request.Phrases.OrderBy(value => value.Order).Select(value => value.Text));
        await CancelActiveJobsAsync(
            db,
            project.Id,
            JobType.Transcription,
            "transcript.manual_override",
            cancellationToken);
        await UpdateWorkflowStagesAsync(
            db,
            project.Id,
            [new WorkflowStageUpdate(
                WorkflowLane.Transcript,
                PipelineStageState.WaitingUser,
                "transcript.review_required")],
            cancellationToken);
        await InvalidateAfterTranscript(db, project, cancellationToken);
        AddReconcile(db, project, "transcript.updated");
        ProjectActivity.Touch(project, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        ApiEndpointHelpers.SetEtag(response, revision.Version);
        return Results.Created($"/api/v1/releases/{project.Id}/transcript", ToTranscript(revision, project.IsInstrumental));
    }

    private static async Task<IResult> ApproveTranscript(
        Guid projectId,
        ApproveRevisionRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var expected = ApiEndpointHelpers.RequireIfMatch(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, false, cancellationToken);
        if (project.CurrentTranscriptRevisionId != request.RevisionId) throw Problem(409, "transcript.revision_stale", "Only the current transcript can be approved.");
        var revision = await db.TranscriptRevisions.SingleAsync(value => value.Id == request.RevisionId, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expected, revision.Version);
        var phrases = Deserialize<IReadOnlyList<TranscriptPhraseRequest>>(revision.PhrasesJson) ?? [];
        if (revision.Source != TranscriptSource.Instrumental &&
            phrases.Any(value => string.IsNullOrWhiteSpace(value.Text) || value.Confidence < .75 && !value.WarningAcknowledged))
        {
            throw Problem(422, "transcript.warnings_unresolved", "Fix or acknowledge every low-confidence phrase before approval.");
        }

        revision.State = RevisionState.Approved;
        revision.ApprovedBySubject = currentUser.Subject;
        revision.ApprovedAt = DateTimeOffset.UtcNow;
        db.Entry(project).Property(value => value.Version).IsModified = true;
        AddReconcile(db, project, "transcript.approved");
        ProjectActivity.Touch(project, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        ApiEndpointHelpers.SetEtag(response, revision.Version);
        return Results.Ok(ToTranscript(revision, project.IsInstrumental));
    }

    private static async Task<IResult> RegenerateTranscript(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        IJobQueue jobs,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var key = RequireIdempotencyKey(request);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, true, cancellationToken);
        var audio = project.Assets.SingleOrDefault(value => value.Kind == AssetKind.Audio && value.IsActive && value.State == AssetState.Ready)
            ?? throw Problem(409, "audio.not_ready", "A processed audio master is required.");
        await RequireExternalAiProcessingConsent(db, project, audio, cancellationToken);
        ProjectActivity.Touch(project, DateTimeOffset.UtcNow);
        var jobId = await jobs.EnqueueAsync(new JobEnqueueRequest(
            project.WorkspaceId, project.Id, audio.Id, JobType.Transcription,
            JsonSerializer.Serialize(new { projectId, assetId = audio.Id }),
            $"transcript:{project.Id:N}:{key}", JobRoutingRegistry.Control, "openrouter-stt-v1", audio.Sha256), cancellationToken);
        return Results.Accepted($"/api/v1/jobs/{jobId}", new JobAcceptedResponse(jobId, null));
    }

    private static async Task<IResult> GetArtwork(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, false, cancellationToken);
        if (project.CurrentArtworkPackRevisionId is not { } id) throw NotFound();
        var revision = await db.ArtworkPackRevisions.AsNoTracking().SingleAsync(value => value.Id == id, cancellationToken);
        ApiEndpointHelpers.SetEtag(response, revision.Version);
        return Results.Ok(ToArtwork(revision));
    }

    private static async Task<IResult> GenerateArtwork(
        Guid projectId,
        GenerateArtworkRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        IJobQueue jobs,
        TimeProvider timeProvider,
        HttpRequest httpRequest,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Trim().Length > 2_000)
            throw Problem(422, "artwork.prompt_invalid", "Prompt is required and must not exceed 2000 characters.");
        var key = RequireIdempotencyKey(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, true, cancellationToken);
        await RequireArtworkGate(
            db,
            project,
            DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime),
            cancellationToken);
        var existingOperation = await db.ArtworkPackRevisions
            .SingleOrDefaultAsync(value => value.ProjectId == project.Id && value.SourceFingerprint == $"request:{key}", cancellationToken);
        if (existingOperation is not null)
        {
            var existingJobId = await db.Jobs.Where(value => value.ProjectId == project.Id && value.Type == JobType.ArtworkGeneration && value.InputFingerprint == $"request:{key}")
                .Select(value => value.Id).SingleAsync(cancellationToken);
            return Results.Accepted($"/api/v1/jobs/{existingJobId}", new JobAcceptedResponse(existingJobId, existingOperation.Id));
        }

        ArtworkPackRevision? previousPack = null;
        if (project.CurrentArtworkPackRevisionId is { } previousPackId)
        {
            previousPack = await db.ArtworkPackRevisions.SingleAsync(
                value => value.Id == previousPackId,
                cancellationToken);
            if (previousPack.State == RevisionState.Processing)
            {
                if (await ArtworkGenerationRecovery.TryFailProcessingCoverAsync(
                        db,
                        previousPack,
                        cancellationToken) is null)
                {
                    throw Problem(409, "artwork.generation_in_progress", "Wait for the current artwork generation to finish before starting another one.");
                }

                // The recovery and its credit compensation must be visible to
                // the SQL eligibility checks that create the replacement pack.
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        var operation = await db.ArtworkPackRevisions.CountAsync(value => value.ProjectId == project.Id, cancellationToken) + 1;
        var hasIncludedGeneration = await ArtworkCreditLedger.HasIncludedGenerationAsync(
            db,
            project.Id,
            cancellationToken);
        var revision = new ArtworkPackRevision
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Number = operation,
            OperationNumber = operation,
            State = RevisionState.Processing,
            Prompt = request.Prompt.Trim(),
            SourceFingerprint = $"request:{key}"
        };
        if (!hasIncludedGeneration && !await ArtworkCreditLedger.TryReserveAsync(
                db,
                project.WorkspaceId,
                revision.Id,
                cancellationToken))
        {
            throw Problem(402, "artwork.credit_required", "The three included artwork operations have been used.");
        }
        if (previousPack is not null)
        {
            if (previousPack.State != RevisionState.Failed)
                previousPack.State = RevisionState.Superseded;
        }
        if (project.CurrentCampaignPlanRevisionId is { } previousCampaignId)
        {
            var previousCampaign = await db.CampaignPlanRevisions.SingleAsync(value => value.Id == previousCampaignId, cancellationToken);
            previousCampaign.State = RevisionState.Superseded;
            project.CurrentCampaignPlanRevisionId = null;
        }
        db.ArtworkPackRevisions.Add(revision);
        project.CurrentArtworkPackRevisionId = revision.Id;
        ProjectActivity.Touch(project, timeProvider);
        var jobId = await jobs.EnqueueAsync(new JobEnqueueRequest(
            project.WorkspaceId, project.Id, null, JobType.ArtworkGeneration,
            JsonSerializer.Serialize(new { projectId, artworkPackRevisionId = revision.Id, prompt = revision.Prompt, request.Style }),
            $"artwork:{project.Id:N}:{key}", JobRoutingRegistry.Control, "openrouter-image-v1", $"request:{key}"), cancellationToken);
        return Results.Accepted($"/api/v1/jobs/{jobId}", new JobAcceptedResponse(jobId, revision.Id));
    }

    private static async Task<IResult> UpdateArtworkSelection(
        Guid projectId,
        UpdateArtworkSelectionRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        _ = ParseJson(request.CompositionJson, "compositionJson");
        var expected = ApiEndpointHelpers.RequireIfMatch(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, false, cancellationToken);
        if (project.CurrentArtworkPackRevisionId != request.PackRevisionId) throw Problem(409, "artwork.revision_stale", "Only the current artwork pack can be edited.");
        var pack = await db.ArtworkPackRevisions.SingleAsync(value => value.Id == request.PackRevisionId, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expected, pack.Version);
        if (pack.State == RevisionState.Approved)
            throw Problem(409, "artwork.new_operation_required", "Create a new artwork operation before changing an approved cover.");
        var candidates = Deserialize<List<Guid>>(pack.CandidateAssetIdsJson) ?? [];
        var asset = await db.MediaAssets.SingleOrDefaultAsync(
            value => value.Id == request.SelectedAssetId &&
                     value.ProjectId == project.Id &&
                     value.Kind == AssetKind.Cover &&
                     value.State == AssetState.Ready &&
                     (value.Origin == AssetOrigin.Uploaded && value.Purpose == AssetPurpose.Source ||
                      value.Purpose == AssetPurpose.CoverCandidate && candidates.Contains(value.Id)),
            cancellationToken) ?? throw NotFound();
        if (!candidates.Contains(asset.Id)) candidates.Add(asset.Id);
        pack.CandidateAssetIdsJson = JsonSerializer.Serialize(candidates, StoredJson);
        pack.SelectedAssetId = asset.Id;
        pack.CompositionJson = request.CompositionJson;
        pack.State = RevisionState.ReadyForReview;
        ProjectActivity.Touch(project, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        ApiEndpointHelpers.SetEtag(response, pack.Version);
        return Results.Ok(ToArtwork(pack));
    }

    private static async Task<IResult> ApproveCover(
        Guid projectId,
        ApproveRevisionRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        IJobQueue jobs,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var expected = ApiEndpointHelpers.RequireIfMatch(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, false, cancellationToken);
        if (project.CurrentArtworkPackRevisionId != request.RevisionId) throw Problem(409, "artwork.revision_stale", "Only the current artwork pack can be approved.");
        var pack = await db.ArtworkPackRevisions.SingleAsync(value => value.Id == request.RevisionId, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expected, pack.Version);
        if (pack.SelectedAssetId is not { } selectedId) throw Problem(422, "artwork.selection_required", "Select a cover before approval.");
        var asset = await db.MediaAssets.SingleOrDefaultAsync(value => value.Id == selectedId && value.ProjectId == project.Id, cancellationToken) ?? throw NotFound();
        if (asset.State != AssetState.Ready) throw Problem(409, "artwork.asset_not_ready", "The selected cover is still processing.");
        var audio = await db.MediaAssets.SingleOrDefaultAsync(
            value => value.ProjectId == project.Id &&
                     value.Kind == AssetKind.Audio &&
                     value.IsActive &&
                     value.State == AssetState.Ready,
            cancellationToken) ?? throw Problem(409, "audio.not_ready", "A processed audio master is required.");
        await RequireExternalAiProcessingConsent(db, project, audio, cancellationToken);
        var assetPurposeChanges = asset.Purpose != AssetPurpose.ApprovedCover;
        var backgroundFingerprint = $"cover:{selectedId:N}:v{asset.Version + (assetPurposeChanges ? 1 : 0)}";
        var exactJobs = await ExactBackgroundJobsAsync(
            db,
            project,
            pack,
            selectedId,
            cancellationToken);
        var activeJob = exactJobs.FirstOrDefault(value => value.State is JobState.Queued or JobState.Running);
        var latestJob = exactJobs.FirstOrDefault();

        if (pack.State != RevisionState.Approved)
        {
            pack.State = RevisionState.Approved;
            pack.ApprovedBySubject = currentUser.Subject;
            pack.ApprovedAt = DateTimeOffset.UtcNow;
            db.Entry(project).Property(value => value.Version).IsModified = true;
            AddReconcile(db, project, "artwork.approved");
            ProjectActivity.Touch(project, DateTimeOffset.UtcNow);
        }
        if (assetPurposeChanges)
        {
            asset.Purpose = AssetPurpose.ApprovedCover;
        }

        Guid jobId;
        if (activeJob is not null)
        {
            // The immutable background command is already polling or leased.
            // Persist a first-time approval, but never enqueue a parallel copy.
            await db.SaveChangesAsync(cancellationToken);
            jobId = activeJob.Id;
        }
        else if (latestJob is { State: JobState.Failed } failedJob &&
                 (Deserialize<IReadOnlyList<Guid>>(pack.BackgroundAssetIdsJson) ?? []).Count == 0)
        {
            jobId = await EnqueueBackgroundGenerationAsync(
                jobs,
                project,
                pack,
                selectedId,
                backgroundFingerprint,
                $"artwork-backgrounds:{pack.Id:N}:retry:{failedJob.Id:N}",
                failedJob.Id,
                cancellationToken);
        }
        else if (latestJob is not null)
        {
            // A waiting-user command is resumed by the rights workflow. A
            // succeeded command remains authoritative even if its artifacts
            // need separate reconciliation. Neither case spends another call.
            await db.SaveChangesAsync(cancellationToken);
            jobId = latestJob.Id;
        }
        else
        {
            jobId = await EnqueueBackgroundGenerationAsync(
                jobs,
                project,
                pack,
                selectedId,
                backgroundFingerprint,
                $"artwork-backgrounds:{pack.Id:N}",
                retryOfJobId: null,
                cancellationToken);
        }
        ApiEndpointHelpers.SetEtag(response, pack.Version);
        response.Headers.Location = $"/api/v1/jobs/{jobId}";
        return Results.Ok(ToArtwork(pack));
    }

    private static async Task<IReadOnlyList<Job>> ExactBackgroundJobsAsync(
        Hook2StreamDbContext db,
        ReleaseProject project,
        ArtworkPackRevision pack,
        Guid selectedAssetId,
        CancellationToken cancellationToken)
    {
        var candidates = await db.Jobs.AsNoTracking()
            .Where(value => value.WorkspaceId == project.WorkspaceId &&
                            value.ProjectId == project.Id &&
                            value.Type == JobType.ArtworkGeneration &&
                            value.AssetId == selectedAssetId)
            .OrderByDescending(value => value.CreatedAt)
            .ThenByDescending(value => value.Id)
            .ToListAsync(cancellationToken);
        return candidates
            .Where(value => TargetsBackgroundPack(value.PayloadJson, pack.Id) &&
                            TargetsSelectedCoverFingerprint(value.InputFingerprint, selectedAssetId))
            .ToArray();
    }

    private static bool TargetsSelectedCoverFingerprint(string? inputFingerprint, Guid selectedAssetId)
    {
        var prefix = $"cover:{selectedAssetId:N}:v";
        return inputFingerprint is not null &&
               inputFingerprint.StartsWith(prefix, StringComparison.Ordinal) &&
               long.TryParse(
                   inputFingerprint.AsSpan(prefix.Length),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var assetVersion) &&
               assetVersion > 0;
    }

    private static bool TargetsBackgroundPack(string payloadJson, Guid packId)
    {
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            var root = payload.RootElement;
            return root.TryGetProperty("artworkPackRevisionId", out var revision) &&
                   revision.TryGetGuid(out var revisionId) &&
                   revisionId == packId &&
                   root.TryGetProperty("mode", out var mode) &&
                   mode.ValueKind == JsonValueKind.String &&
                   string.Equals(mode.GetString(), "backgrounds", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static Task<Guid> EnqueueBackgroundGenerationAsync(
        IJobQueue jobs,
        ReleaseProject project,
        ArtworkPackRevision pack,
        Guid selectedAssetId,
        string inputFingerprint,
        string idempotencyKey,
        Guid? retryOfJobId,
        CancellationToken cancellationToken)
    {
        var payloadJson = retryOfJobId is { } failedJobId
            ? JsonSerializer.Serialize(new
            {
                projectId = project.Id,
                artworkPackRevisionId = pack.Id,
                mode = "backgrounds",
                count = 3,
                retryOfJobId = failedJobId
            })
            : JsonSerializer.Serialize(new
            {
                projectId = project.Id,
                artworkPackRevisionId = pack.Id,
                mode = "backgrounds",
                count = 3
            });
        return jobs.EnqueueAsync(new JobEnqueueRequest(
            project.WorkspaceId,
            project.Id,
            selectedAssetId,
            JobType.ArtworkGeneration,
            payloadJson,
            idempotencyKey,
            JobRoutingRegistry.Control,
            "openrouter-image-v1",
            inputFingerprint), cancellationToken);
    }

    private static async Task<IResult> GetHooks(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, false, cancellationToken);
        if (project.CurrentHookSetRevisionId is not { } id) throw NotFound();
        var revision = await db.HookSetRevisions.AsNoTracking().SingleAsync(value => value.Id == id, cancellationToken);
        ApiEndpointHelpers.SetEtag(response, revision.Version);
        return Results.Ok(ToHooks(revision));
    }

    private static async Task<IResult> PutHooks(
        Guid projectId,
        PutHooksRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var expected = ApiEndpointHelpers.RequireIfMatch(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, true, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expected, project.Version);
        if (project.CurrentTranscriptRevisionId is not { } transcriptId ||
            !await db.TranscriptRevisions.AnyAsync(value => value.Id == transcriptId && value.State == RevisionState.Approved, cancellationToken))
            throw Problem(409, "transcript.approval_required", "Approve the transcript before editing hooks.");
        ValidateHooks(request, project.Assets.Where(value => value.Kind == AssetKind.Audio && value.IsActive).Select(value => value.DurationMilliseconds).FirstOrDefault());
        if (project.CurrentHookSetRevisionId is { } oldId)
        {
            var old = await db.HookSetRevisions.SingleAsync(value => value.Id == oldId, cancellationToken);
            old.State = RevisionState.Superseded;
        }
        var number = await db.HookSetRevisions.Where(value => value.ProjectId == project.Id).Select(value => value.Number).DefaultIfEmpty().MaxAsync(cancellationToken) + 1;
        var revision = new HookSetRevision
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Number = number,
            TranscriptRevisionId = transcriptId,
            HooksJson = JsonSerializer.Serialize(request.Hooks, StoredJson),
            State = RevisionState.Approved,
            SourceFingerprint = $"transcript:{transcriptId:N}"
        };
        db.HookSetRevisions.Add(revision);
        project.CurrentHookSetRevisionId = revision.Id;
        if (project.CurrentCampaignPlanRevisionId is { } campaignId)
        {
            var campaign = await db.CampaignPlanRevisions.SingleAsync(value => value.Id == campaignId, cancellationToken);
            campaign.State = RevisionState.Superseded;
            project.CurrentCampaignPlanRevisionId = null;
        }
        AddReconcile(db, project, "hooks.updated");
        ProjectActivity.Touch(project, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        ApiEndpointHelpers.SetEtag(response, revision.Version);
        return Results.Created($"/api/v1/releases/{project.Id}/hooks", ToHooks(revision));
    }

    private static async Task<IResult> GetCampaign(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, false, cancellationToken);
        if (project.CurrentCampaignPlanRevisionId is not { } id) throw NotFound();
        var revision = await db.CampaignPlanRevisions.AsNoTracking().SingleAsync(value => value.Id == id, cancellationToken);
        ApiEndpointHelpers.SetEtag(response, revision.Version);
        return Results.Ok(ToCampaign(revision));
    }

    private static async Task<IResult> PutCampaignItem(
        Guid projectId,
        Guid itemId,
        PutCampaignItemRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        _ = ParseJson(request.CompositionJson, "compositionJson");
        var expected = ApiEndpointHelpers.RequireIfMatch(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await Project(db, context.Workspace.Id, projectId, false, cancellationToken);
        if (project.CurrentCampaignPlanRevisionId is not { } currentId) throw NotFound();
        var current = await db.CampaignPlanRevisions.SingleAsync(value => value.Id == currentId, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expected, current.Version);
        if (string.IsNullOrWhiteSpace(request.Template) || !CampaignTemplates.Contains(request.Template) ||
            request.Text is null || request.Text.Length > 2_000 ||
            request.CompositionJson is null || request.CompositionJson.Length > 40_000)
            throw Problem(422, "campaign.item_invalid", "Choose a supported template and keep campaign text and composition within their limits.");
        var currentHooks = await db.HookSetRevisions.AsNoTracking().SingleAsync(value => value.Id == current.HookSetRevisionId, cancellationToken);
        var hookIds = (Deserialize<IReadOnlyList<HookRequest>>(currentHooks.HooksJson) ?? []).Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(request.HookId) && !hookIds.Contains(request.HookId))
            throw Problem(422, "campaign.hook_invalid", "The campaign item must reference a current hook.");
        if (request.BackgroundAssetId is { } backgroundId)
        {
            var background = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(
                value => value.Id == backgroundId &&
                         value.ProjectId == project.Id &&
                         value.State == AssetState.Ready,
                cancellationToken);
            var generatedOrCover = background?.Purpose is AssetPurpose.CampaignBackground or AssetPurpose.ApprovedCover;
            var uploadedOverride = background is
            {
                Kind: AssetKind.Visual,
                Origin: AssetOrigin.Uploaded,
                Purpose: AssetPurpose.Source,
                IsActive: true
            } && (background.DetectedContentType ?? background.DeclaredContentType).StartsWith(
                "image/",
                StringComparison.OrdinalIgnoreCase) ||
                background is
                {
                    Kind: AssetKind.Visual,
                    Origin: AssetOrigin.Uploaded,
                    Purpose: AssetPurpose.Source,
                    IsActive: true
                } && (background.DetectedContentType ?? background.DeclaredContentType).StartsWith(
                    "video/",
                    StringComparison.OrdinalIgnoreCase);
            if (!generatedOrCover && !uploadedOverride)
                throw Problem(422, "campaign.background_invalid", "Choose a ready background from this release.");
            if (uploadedOverride && !await db.RightsAttestations.AsNoTracking().AnyAsync(
                    value => value.ProjectId == project.Id && value.OwnsVisualRights,
                    cancellationToken))
                throw Problem(409, "rights.visual_required", "Confirm rights to uploaded visuals before using them in a campaign.");
        }
        var items = Deserialize<List<CampaignItemRequest>>(current.ItemsJson) ?? [];
        if (items.Count != 18) throw Problem(409, "campaign.incomplete", "The current campaign must contain exactly 18 items.");
        var index = items.FindIndex(value => value.Id == itemId);
        if (index < 0) throw NotFound();
        var existingItem = items[index];
        if (!string.Equals(request.Template, existingItem.Template, StringComparison.Ordinal) ||
            !string.Equals(request.HookId, existingItem.HookId, StringComparison.Ordinal))
        {
            throw Problem(
                422,
                "campaign.slot_assignment_immutable",
                "A campaign item keeps its canonical template and hook assignment. Edit its copy, background, or composition instead.");
        }

        items[index] = items[index] with
        {
            BackgroundAssetId = request.BackgroundAssetId,
            Text = request.Text,
            CompositionJson = request.CompositionJson
        };
        var storedHooks = Deserialize<IReadOnlyList<HookRequest>>(currentHooks.HooksJson) ?? [];
        var validation = CampaignPlanContractValidator.Validate(
            project.Mode == ReleaseMode.Released,
            storedHooks.Select(value => StableGuid(value.Id)).ToArray(),
            items.OrderBy(value => value.Slot).Select(ToCampaignPlan).ToArray());
        if (!validation.IsValid)
        {
            throw Problem(
                422,
                "campaign.recipe_invalid",
                string.Join(' ', validation.Errors.Select(value => value.Message).Distinct(StringComparer.Ordinal)));
        }

        current.State = RevisionState.Superseded;
        var revision = new CampaignPlanRevision
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Number = current.Number + 1,
            State = RevisionState.ReadyForReview,
            TranscriptRevisionId = current.TranscriptRevisionId,
            ArtworkPackRevisionId = current.ArtworkPackRevisionId,
            HookSetRevisionId = current.HookSetRevisionId,
            ItemsJson = JsonSerializer.Serialize(items, StoredJson),
            // This fingerprint describes upstream dependencies only. The immutable
            // revision id identifies edited campaign content and preview inputs.
            SourceFingerprint = current.SourceFingerprint
        };
        db.CampaignPlanRevisions.Add(revision);
        project.CurrentCampaignPlanRevisionId = revision.Id;
        await CancelActiveJobsAsync(
            db,
            project.Id,
            JobType.PreviewRender,
            "preview.revision_superseded",
            cancellationToken);
        var previewAllowanceConsumed = await db.Jobs.AsNoTracking().AnyAsync(
            value => value.ProjectId == project.Id &&
                     value.Type == JobType.PreviewRender &&
                     value.State == JobState.Succeeded,
            cancellationToken);
        await UpdateWorkflowStagesAsync(
            db,
            project.Id,
            [
                new WorkflowStageUpdate(WorkflowLane.Campaign, PipelineStageState.Succeeded),
                new WorkflowStageUpdate(
                    WorkflowLane.Preview,
                    previewAllowanceConsumed ? PipelineStageState.Stale : PipelineStageState.NotStarted,
                    previewAllowanceConsumed ? "preview.allowance_consumed" : null)
            ],
            cancellationToken);
        AddReconcile(db, project, "campaign.item_updated");
        ProjectActivity.Touch(project, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        ApiEndpointHelpers.SetEtag(response, revision.Version);
        return Results.Created($"/api/v1/releases/{project.Id}/campaign", ToCampaign(revision));
    }

    private static async Task<IResult> RetryPreview(
        Guid projectId,
        RetryPreviewRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        IOptions<OperationalPolicyOptions> policyOptions,
        TimeProvider timeProvider,
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        CancellationToken cancellationToken)
    {
        var key = RequireIdempotencyKey(httpRequest);
        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(httpRequest);
        var now = timeProvider.GetUtcNow();
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var requestHash = Hash($"{projectId:N}\n{request.FailedJobId:N}");
        var existingRequest = await db.ApiIdempotencyRecords.SingleOrDefaultAsync(
            value => value.WorkspaceId == context.Workspace.Id &&
                     value.Scope == "preview.retry" &&
                     value.Key == key,
            cancellationToken);
        if (existingRequest is not null && existingRequest.ExpiresAt <= now)
        {
            db.ApiIdempotencyRecords.Remove(existingRequest);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                existingRequest = null;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request may have removed the expired row first and
                // already claimed the same key. Re-read instead of exposing
                // the cleanup race as an unrelated ETag conflict.
                db.ChangeTracker.Clear();
                existingRequest = await db.ApiIdempotencyRecords
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        value => value.WorkspaceId == context.Workspace.Id &&
                                 value.Scope == "preview.retry" &&
                                 value.Key == key &&
                                 value.ExpiresAt > now,
                        cancellationToken);
            }
        }

        if (existingRequest is not null)
        {
            EnsurePreviewRetryIdempotencyMatch(
                existingRequest,
                requestHash,
                projectId,
                request.FailedJobId);

            return Results.Accepted(
                $"/api/v1/jobs/{request.FailedJobId}",
                new JobAcceptedResponse(request.FailedJobId, null));
        }

        var project = await Project(db, context.Workspace.Id, projectId, false, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expectedVersion, project.Version);
        if (project.FlowKind != FlowKind.Mp3First)
            throw Problem(409, "release.flow_endpoint_mismatch", "Preview retry is available only for the audio-first workflow.");
        if (project.CurrentCampaignPlanRevisionId is not { } campaignRevisionId)
            throw Problem(409, "campaign.required", "A current campaign is required before retrying preview.");

        var job = await db.Jobs.SingleOrDefaultAsync(
            value => value.Id == request.FailedJobId &&
                     value.WorkspaceId == context.Workspace.Id &&
                     value.ProjectId == project.Id &&
                     value.Type == JobType.PreviewRender,
            cancellationToken) ?? throw NotFound();
        if (job.State != JobState.Failed)
            throw Problem(409, "preview.retry_not_allowed", "Only a failed preview job can be retried.");

        Guid payloadCampaignRevisionId;
        try
        {
            using var payload = JsonDocument.Parse(job.PayloadJson);
            payloadCampaignRevisionId = payload.RootElement
                .GetProperty("campaignRevisionId")
                .GetGuid();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw Problem(409, "preview.job_invalid", "The failed preview does not contain a valid campaign snapshot.");
        }
        if (payloadCampaignRevisionId != campaignRevisionId)
            throw Problem(409, "preview.revision_stale", "Only the failed preview for the current campaign can be retried.");

        var successfulPreviewExists = await db.Jobs.AsNoTracking().AnyAsync(
            value => value.ProjectId == project.Id &&
                     value.Type == JobType.PreviewRender &&
                     value.State == JobState.Succeeded,
            cancellationToken);
        if (successfulPreviewExists)
            throw Problem(409, "preview.allowance_consumed", "The free project preview has already been rendered.");

        var run = await db.PipelineRuns
            .Include(value => value.Stages)
            .Where(value => value.ProjectId == project.Id)
            .OrderByDescending(value => value.Number)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw Problem(409, "workflow.not_initialized", "The release workflow has not been initialized yet.");
        var stage = run.Stages.Single(value => value.Lane == WorkflowLane.Preview);
        if (stage.CurrentJobId != job.Id || stage.State != PipelineStageState.Failed)
            throw Problem(409, "preview.job_stale", "The selected preview job is no longer the current failed operation.");

        job.State = JobState.Queued;
        job.AvailableAt = now;
        job.MaxAttempts = Math.Max(job.MaxAttempts, job.AttemptCount + 3);
        job.CompletedAt = null;
        job.ProgressPercent = 0;
        job.ProgressStage = "manual_retry";
        job.ErrorCode = null;
        job.ErrorMessage = null;
        job.LeaseOwner = null;
        job.LeaseToken = null;
        job.LeaseExpiresAt = null;
        db.JobEvents.Add(new JobEvent
        {
            JobId = job.Id,
            EventType = "requeued",
            DataJson = JsonSerializer.Serialize(new
            {
                reason = "preview.manual_retry",
                actorSubject = currentUser.Subject,
                availableAt = now
            })
        });

        stage.State = PipelineStageState.Queued;
        stage.ProgressPercent = 0;
        stage.BlockerCode = null;
        stage.ErrorCode = null;
        stage.CurrentJobId = job.Id;
        run.State = PipelineStageState.Running;
        run.CompletedAt = null;
        ProjectActivity.Touch(project, now);
        db.ApiIdempotencyRecords.Add(new ApiIdempotencyRecord
        {
            WorkspaceId = context.Workspace.Id,
            Scope = "preview.retry",
            Key = key,
            RequestHash = requestHash,
            ResourceId = project.Id,
            SecondaryResourceId = job.Id,
            ExpiresAt = now.AddDays(policyOptions.Value.IdempotencyDays)
        });
        db.ProjectEvents.Add(NewProjectEvent(project, "preview.retry_requested", new
        {
            jobId = job.Id,
            campaignRevisionId
        }));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent requests can both observe an unused key. The unique
            // idempotency row and entity concurrency tokens choose one winner;
            // the loser must replay that result instead of leaking a database
            // conflict as a 409/500.
            db.ChangeTracker.Clear();
            var winner = await db.ApiIdempotencyRecords
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    value => value.WorkspaceId == context.Workspace.Id &&
                             value.Scope == "preview.retry" &&
                             value.Key == key &&
                             value.ExpiresAt > now,
                    cancellationToken);
            if (winner is null)
            {
                throw;
            }

            EnsurePreviewRetryIdempotencyMatch(
                winner,
                requestHash,
                projectId,
                request.FailedJobId);
            return Results.Accepted(
                $"/api/v1/jobs/{winner.SecondaryResourceId}",
                new JobAcceptedResponse(winner.SecondaryResourceId!.Value, null));
        }

        ApiEndpointHelpers.SetEtag(httpResponse, project.Version);
        return Results.Accepted(
            $"/api/v1/jobs/{job.Id}",
            new JobAcceptedResponse(job.Id, null));
    }

    private static void EnsurePreviewRetryIdempotencyMatch(
        ApiIdempotencyRecord record,
        string requestHash,
        Guid projectId,
        Guid failedJobId)
    {
        if (string.Equals(
                record.RequestHash,
                requestHash,
                StringComparison.Ordinal) &&
            record.ResourceId == projectId &&
            record.SecondaryResourceId == failedJobId)
        {
            return;
        }

        throw Problem(
            409,
            "idempotency.payload_mismatch",
            "This idempotency key was used with a different preview retry.");
    }

    private static async Task<IResult> GetAssetReadUrl(
        Guid projectId,
        Guid assetId,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        IObjectStorage storage,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        _ = await Project(db, context.Workspace.Id, projectId, false, cancellationToken);
        var asset = await db.MediaAssets.AsNoTracking().Include(value => value.Derivatives).SingleOrDefaultAsync(
            value => value.Id == assetId && value.ProjectId == projectId && value.State == AssetState.Ready,
            cancellationToken) ?? throw NotFound();
        if (asset.Origin == AssetOrigin.Generated && asset.Purpose is AssetPurpose.CampaignVideo or AssetPurpose.ExportBundle)
            throw Problem(402, "purchase.required", "Purchase the corresponding output before requesting a clean download.");
        var objectKey = asset.ObjectKey;
        if (asset.Origin == AssetOrigin.Generated && asset.Purpose != AssetPurpose.PreviewVideo)
        {
            objectKey = asset.Derivatives
                .Where(value => value.DeletedAt == null && value.Kind is DerivativeKind.ImageProxy or DerivativeKind.Thumbnail)
                .OrderBy(value => value.Kind == DerivativeKind.ImageProxy ? 0 : 1)
                .Select(value => value.ObjectKey)
                .FirstOrDefault()
                ?? throw Problem(409, "asset.preview_not_ready", "The protected preview is still being prepared.");
        }
        var expiresAt = DateTimeOffset.UtcNow.Add(ReadUrlLifetime);
        var url = await storage.CreateReadUrlAsync(objectKey, ReadUrlLifetime, cancellationToken);
        return Results.Ok(new AssetReadUrlResponse(asset.Id, url.ToString(), expiresAt));
    }

    private static async Task<WorkflowResponse> BuildWorkflow(
        Hook2StreamDbContext db,
        ReleaseProject project,
        CancellationToken cancellationToken)
    {
        var run = await db.PipelineRuns.AsNoTracking()
            .Include(value => value.Stages)
            .Where(value => value.ProjectId == project.Id && value.WorkspaceId == project.WorkspaceId)
            .OrderByDescending(value => value.Number)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw Problem(409, "workflow.not_initialized", "The release workflow has not been initialized yet.");
        var stages = run.Stages.ToDictionary(value => value.Lane);
        var laneResponses = Enum.GetValues<WorkflowLane>()
            .Select(lane => stages.TryGetValue(lane, out var stage)
                ? new WorkflowLaneResponse(
                    lane,
                    stage.State,
                    stage.ProgressPercent,
                    stage.BlockerCode,
                    stage.ErrorCode,
                    stage.CurrentJobId)
                : new WorkflowLaneResponse(
                    lane,
                    PipelineStageState.NotStarted,
                    0,
                    "workflow.stage_missing",
                    null,
                    null))
            .ToArray();
        var blockers = laneResponses
            .Where(value => value.BlockerCode is not null)
            .Select(value => value.BlockerCode!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var nextAction = NextAction(laneResponses);
        var currentRenderBatchId = stages.TryGetValue(WorkflowLane.FinalRender, out var finalRenderStage)
            ? finalRenderStage.CurrentRenderBatchId
            : null;
        return new WorkflowResponse(
            project.Id,
            project.FlowKind,
            project.Version,
            run.Version,
            blockers,
            nextAction,
            laneResponses,
            currentRenderBatchId);
    }

    private static WorkflowNextAction? NextAction(IReadOnlyList<WorkflowLaneResponse> lanes)
    {
        WorkflowLaneResponse Lane(WorkflowLane lane) => lanes.Single(value => value.Lane == lane);
        if (lanes.Any(value => WorkflowBlockerCodes.RequiresRightsConfirmation(value.BlockerCode)))
            return WorkflowNextAction.ConfirmRights;
        var audio = Lane(WorkflowLane.Audio);
        if (audio.State != PipelineStageState.Succeeded) return WorkflowNextAction.UploadAudio;
        if (lanes.Any(value => value.BlockerCode == "setup.required")) return WorkflowNextAction.CompleteSetup;

        var transcript = Lane(WorkflowLane.Transcript);
        if (transcript.State != PipelineStageState.Succeeded) return WorkflowNextAction.ReviewTranscript;
        var artwork = Lane(WorkflowLane.Artwork);
        if (artwork.State != PipelineStageState.Succeeded) return WorkflowNextAction.ReviewArtwork;
        var hooks = Lane(WorkflowLane.Hooks);
        if (hooks.State != PipelineStageState.Succeeded) return WorkflowNextAction.ReviewHooks;
        var campaign = Lane(WorkflowLane.Campaign);
        if (campaign.State != PipelineStageState.Succeeded) return WorkflowNextAction.WaitForCampaign;
        var preview = Lane(WorkflowLane.Preview);
        if (preview.State == PipelineStageState.Failed)
            return WorkflowNextAction.RetryPreview;
        if (preview.State != PipelineStageState.Succeeded &&
            !(preview.State == PipelineStageState.Stale && preview.BlockerCode == "preview.allowance_consumed"))
            return WorkflowNextAction.WaitForPreview;

        var finalRender = Lane(WorkflowLane.FinalRender);
        return finalRender.State switch
        {
            PipelineStageState.WaitingUser when finalRender.BlockerCode == "purchase.required" => WorkflowNextAction.PurchaseRender,
            PipelineStageState.WaitingUser when finalRender.BlockerCode == "render.start_required" => WorkflowNextAction.StartFinalRender,
            PipelineStageState.Queued or PipelineStageState.Running or PipelineStageState.Retrying => WorkflowNextAction.WaitForFinalRender,
            PipelineStageState.Succeeded => WorkflowNextAction.DownloadExport,
            PipelineStageState.Degraded or PipelineStageState.Failed => WorkflowNextAction.RetryFailedRenders,
            _ => null
        };
    }

    private static async Task RequireArtworkGate(
        Hook2StreamDbContext db,
        ReleaseProject project,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        if (project.SetupCompletedAt is null) throw Problem(409, "setup.required", "Complete release setup before generating artwork.");
        var timingConfirmed = project.Mode switch
        {
            ReleaseMode.Upcoming => project.ReleaseDate > today,
            ReleaseMode.Released => project.ReleaseDate <= today,
            _ => false
        };
        if (!timingConfirmed)
            throw Problem(409, "release.timing_required", "Confirm whether the release is upcoming or already released before generating artwork.");
        if (!project.Assets.Any(value => value.Kind == AssetKind.Audio && value.IsActive && value.State == AssetState.Ready))
            throw Problem(409, "audio.not_ready", "A processed audio master is required.");
        var rights = await db.RightsAttestations.AsNoTracking().SingleOrDefaultAsync(value => value.ProjectId == project.Id, cancellationToken);
        if (rights?.OwnsAudioRights != true ||
            !(project.IsInstrumental && project.IsInstrumentalConfirmed) && !rights.OwnsLyricsRights ||
            !rights.AllowsExternalAiProcessing)
            throw Problem(409, "rights.external_ai_processing_required", "Confirm the required rights and external AI processing consent before generating artwork.");
        var audio = project.Assets.Single(value => value.Kind == AssetKind.Audio && value.IsActive && value.State == AssetState.Ready);
        if (rights.AudioAssetId != audio.Id || string.IsNullOrWhiteSpace(audio.Sha256) ||
            !string.Equals(rights.AudioFingerprint, audio.Sha256, StringComparison.Ordinal))
            throw Problem(409, "rights.stale", "Confirm rights again for the current audio revision.");
    }

    private static async Task RequireExternalAiProcessingConsent(
        Hook2StreamDbContext db,
        ReleaseProject project,
        MediaAsset audio,
        CancellationToken cancellationToken)
    {
        var rights = await db.RightsAttestations.AsNoTracking().SingleOrDefaultAsync(
            value => value.ProjectId == project.Id,
            cancellationToken);
        if (rights?.AllowsExternalAiProcessing != true ||
            !rights.OwnsAudioRights ||
            !(project.IsInstrumental && project.IsInstrumentalConfirmed) && !rights.OwnsLyricsRights)
        {
            throw Problem(
                409,
                "rights.external_ai_processing_required",
                "Confirm the required rights and external AI processing consent before sending content to an external provider.");
        }

        if (rights.AudioAssetId != audio.Id ||
            string.IsNullOrWhiteSpace(audio.Sha256) ||
            !string.Equals(rights.AudioFingerprint, audio.Sha256, StringComparison.Ordinal))
        {
            throw Problem(409, "rights.stale", "Confirm rights again for the current audio revision.");
        }
    }

    private static void ValidateQuickAudio(QuickAudioUploadRequest request)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 255 || !string.Equals(Path.GetExtension(request.FileName), ".mp3", StringComparison.OrdinalIgnoreCase))
            errors.Add("fileName", "Choose an MP3 file with a valid file name.");
        if (request.ContentType is not ("audio/mpeg" or "audio/mp3")) errors.Add("contentType", "The MP3 upload must use audio/mpeg or audio/mp3.");
        if (request.SizeBytes <= 0 || request.SizeBytes > MediaPolicy.AudioMaxBytes) errors.Add("sizeBytes", "The MP3 must be between 1 byte and 250 MB.");
        if (!request.ConfirmsContentRights) errors.Add("confirmsContentRights", "Confirm that you control the rights required to process this audio and its lyrics.");
        if (!request.AllowsExternalAiProcessing) errors.Add("allowsExternalAiProcessing", "Allow external AI processing under the current zero-data-retention policy.");
        ApiEndpointHelpers.RequireValid(errors);
    }

    private static void ValidateTranscript(PutTranscriptRequest request)
    {
        var errors = new ValidationErrors();
        if (request.Source == TranscriptSource.Automatic)
            errors.Add("source", "Automatic transcript revisions can only be created by the transcription worker.");
        if (request.Source == TranscriptSource.Instrumental && !request.IsInstrumental)
            errors.Add("source", "Instrumental source requires an explicitly confirmed instrumental transcript.");
        if (request.Language is not ("en" or "ru")) errors.Add("language", "Automatic workflow supports English and Russian.");
        if (request.Phrases is null)
        {
            errors.Add("phrases", "Provide a transcript phrase list.");
            ApiEndpointHelpers.RequireValid(errors);
            return;
        }
        if (request.IsInstrumental && request.Phrases.Count != 0) errors.Add("phrases", "Instrumental transcripts must not contain phrases.");
        if (!request.IsInstrumental && request.Phrases.Count == 0) errors.Add("phrases", "Add at least one phrase.");
        if (request.Phrases.Count > TranscriptMaxPhraseCount)
            errors.Add("phrases", $"A transcript can contain at most {TranscriptMaxPhraseCount:N0} phrases.");
        if (request.Phrases.Sum(value => (long)(value.Text?.Length ?? 0)) > TranscriptMaxTotalTextLength)
            errors.Add("phrases", $"Transcript text can contain at most {TranscriptMaxTotalTextLength:N0} characters.");
        if (request.Phrases.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != request.Phrases.Count) errors.Add("phrases", "Phrase IDs must be unique.");
        if (request.Phrases.Select(value => value.Order).Distinct().Count() != request.Phrases.Count ||
            !request.Phrases.OrderBy(value => value.Order).Select(value => value.Order).SequenceEqual(Enumerable.Range(0, request.Phrases.Count)))
            errors.Add("phrases", "Phrase order must be a contiguous zero-based sequence.");
        foreach (var phrase in request.Phrases)
        {
            if (string.IsNullOrWhiteSpace(phrase.Id) || phrase.Id.Length > 64 || string.IsNullOrWhiteSpace(phrase.Text) || phrase.Text.Length > 2_000 || phrase.StartMilliseconds < 0 || phrase.EndMilliseconds <= phrase.StartMilliseconds || phrase.Confidence is < 0 or > 1)
                errors.Add("phrases", "Every phrase needs a stable ID, text, valid timing, and confidence between 0 and 1.");
        }
        var orderedPhrases = request.Phrases.OrderBy(value => value.Order).ToArray();
        for (var index = 1; index < orderedPhrases.Length; index++)
        {
            if (orderedPhrases[index].StartMilliseconds < orderedPhrases[index - 1].EndMilliseconds)
            {
                errors.Add("phrases", "Phrase timings must be chronological and must not overlap.");
                break;
            }
        }
        ApiEndpointHelpers.RequireValid(errors);
    }

    private static async Task CancelActiveJobsAsync(
        Hook2StreamDbContext db,
        Guid projectId,
        JobType type,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var jobs = await db.Jobs
            .Where(value => value.ProjectId == projectId &&
                            value.Type == type &&
                            (value.State == JobState.Queued || value.State == JobState.Running))
            .ToListAsync(cancellationToken);
        if (jobs.Count == 0) return;

        var jobIds = jobs.Select(value => value.Id).ToArray();
        var attempts = await db.JobAttempts
            .Where(value => jobIds.Contains(value.JobId) && value.State == JobState.Running)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var job in jobs)
        {
            job.State = JobState.Cancelled;
            job.CompletedAt = now;
            job.ProgressStage = "cancelled";
            job.ErrorCode = reasonCode;
            job.ErrorMessage = "The job was cancelled because a user supplied a newer revision.";
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.LeaseToken = null;
            db.JobEvents.Add(new JobEvent
            {
                JobId = job.Id,
                EventType = "cancelled",
                DataJson = JsonSerializer.Serialize(new { reasonCode })
            });
        }

        foreach (var attempt in attempts)
        {
            attempt.State = JobState.Cancelled;
            attempt.CompletedAt = now;
            attempt.ErrorCode = reasonCode;
        }
    }

    private static async Task UpdateWorkflowStagesAsync(
        Hook2StreamDbContext db,
        Guid projectId,
        IReadOnlyList<WorkflowStageUpdate> updates,
        CancellationToken cancellationToken)
    {
        var run = await db.PipelineRuns
            .Include(value => value.Stages)
            .Where(value => value.ProjectId == projectId)
            .OrderByDescending(value => value.Number)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null) return;

        foreach (var update in updates)
        {
            var stage = run.Stages.SingleOrDefault(value => value.Lane == update.Lane);
            if (stage is null) continue;
            stage.State = update.State;
            stage.ProgressPercent = update.State == PipelineStageState.Succeeded ? 100 : 0;
            stage.BlockerCode = update.BlockerCode;
            stage.ErrorCode = null;
            stage.CurrentJobId = null;
        }

        var states = run.Stages.Select(value => value.State).ToArray();
        run.State = states.Any(value => value == PipelineStageState.Failed)
            ? PipelineStageState.Failed
            : states.Any(value => value == PipelineStageState.Degraded)
                ? PipelineStageState.Degraded
                : states.Any(value => value is PipelineStageState.Running or PipelineStageState.Queued or PipelineStageState.Retrying)
                    ? PipelineStageState.Running
                    : states.All(value => value == PipelineStageState.Succeeded)
                        ? PipelineStageState.Succeeded
                        : PipelineStageState.WaitingUser;
        run.CompletedAt = run.State == PipelineStageState.Succeeded
            ? run.CompletedAt ?? DateTimeOffset.UtcNow
            : null;
        run.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void ValidateHooks(PutHooksRequest request, long? audioDuration)
    {
        var errors = new ValidationErrors();
        if (request.Hooks.Count != 3 || request.Hooks.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != 3)
            errors.Add("hooks", "Provide exactly three hooks with unique IDs.");
        var allowedKinds = new HashSet<string>(["chorus", "emotional", "energy"], StringComparer.OrdinalIgnoreCase);
        if (request.Hooks.Select(value => value.Kind).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3 ||
            request.Hooks.Any(value => !allowedKinds.Contains(value.Kind)))
            errors.Add("hooks", "Provide one chorus, emotional, and energy hook.");
        foreach (var hook in request.Hooks)
        {
            var duration = hook.EndMilliseconds - hook.StartMilliseconds;
            if (hook.StartMilliseconds < 0 || duration is < 10_000 or > 30_000 || audioDuration is not null && hook.EndMilliseconds > audioDuration)
                errors.Add("hooks", "Each hook must be a 10–30 second range inside the audio duration.");
        }
        ApiEndpointHelpers.RequireValid(errors);
    }

    private static async Task InvalidateAfterTranscript(Hook2StreamDbContext db, ReleaseProject project, CancellationToken cancellationToken)
    {
        if (project.CurrentHookSetRevisionId is { } hookId)
        {
            var hook = await db.HookSetRevisions.SingleAsync(value => value.Id == hookId, cancellationToken);
            hook.State = RevisionState.Superseded;
            project.CurrentHookSetRevisionId = null;
        }
        if (project.CurrentCampaignPlanRevisionId is { } campaignId)
        {
            var campaign = await db.CampaignPlanRevisions.SingleAsync(value => value.Id == campaignId, cancellationToken);
            campaign.State = RevisionState.Superseded;
            project.CurrentCampaignPlanRevisionId = null;
        }
    }

    private static void AddReconcile(Hook2StreamDbContext db, ReleaseProject project, string reason)
    {
        db.OutboxMessages.Add(new OutboxMessage
        {
            WorkspaceId = project.WorkspaceId,
            AggregateId = project.Id,
            Destination = "pipeline",
            MessageType = "pipeline.reconcile",
            DedupeKey = $"pipeline.reconcile:{project.Id:N}:{project.Version}:{reason}:{Guid.CreateVersion7():N}",
            PayloadJson = JsonSerializer.Serialize(new { projectId = project.Id, reason })
        });
        db.ProjectEvents.Add(NewProjectEvent(project, reason, new { projectId = project.Id }));
    }

    private static ProjectEvent NewProjectEvent(ReleaseProject project, string eventType, object data) => new()
    {
        WorkspaceId = project.WorkspaceId,
        ProjectId = project.Id,
        EventType = eventType,
        DataJson = JsonSerializer.Serialize(data, StoredJson)
    };

    private static string RequireIdempotencyKey(HttpRequest request)
    {
        var key = request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(key)) throw Problem(428, "idempotency.key_required", "Send Idempotency-Key for this command.");
        if (key.Length > 255) throw Problem(400, "idempotency.key_invalid", "Idempotency-Key must not exceed 255 characters.");
        return key;
    }

    private static async Task<UploadSessionResponse> RefreshUploadResponse(
        UploadSession session,
        IObjectStorage storage,
        TimeSpan configuredUrlLifetime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (session.ExpiresAt <= now)
            throw Problem(410, "upload.session_expired", "The upload session expired. Start a new upload.");
        var urlLifetime = session.ExpiresAt - now < configuredUrlLifetime
            ? session.ExpiresAt - now
            : configuredUrlLifetime;
        Uri? uploadUrl = null;
        if (!session.IsMultipart && session.State is UploadState.Initiated or UploadState.Uploading)
            uploadUrl = await storage.CreateUploadUrlAsync(
                session.ObjectKey,
                session.Asset.DeclaredContentType,
                urlLifetime,
                cancellationToken);
        var partCount = session.IsMultipart ? (int)Math.Ceiling(session.Asset.DeclaredBytes / (double)session.PartSizeBytes) : 1;
        return new UploadSessionResponse(
            session.Id,
            session.AssetId,
            session.IsMultipart,
            uploadUrl?.ToString(),
            session.MultipartUploadId,
            session.PartSizeBytes,
            partCount,
            now.Add(urlLifetime),
            session.ExpiresAt);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private static async Task<ReleaseProject> Project(Hook2StreamDbContext db, Guid workspaceId, Guid projectId, bool assets, CancellationToken cancellationToken)
    {
        IQueryable<ReleaseProject> query = db.Projects;
        if (assets) query = query.Include(value => value.Assets);
        return await query.SingleOrDefaultAsync(value => value.Id == projectId && value.WorkspaceId == workspaceId, cancellationToken) ?? throw NotFound();
    }

    private static ReleaseResponse ToRelease(ReleaseProject value) => new(
        value.Id, value.FlowKind, value.ProjectLabel, value.ArtistName, value.TrackTitle, value.Language, value.InternalNotes,
        value.LyricsText, value.IsInstrumental, value.IsInstrumentalConfirmed, value.Mode, value.ReleaseDate, value.CampaignStartDate,
        value.State, value.IsArchived, value.Version, value.CreatedAt,
        value.Assets.OrderBy(asset => asset.Kind).ThenBy(asset => asset.SortOrder).Select(asset => new AssetResponse(
            asset.Id, asset.Kind, asset.Origin, asset.Purpose, asset.State, asset.OriginalFileName, asset.DeclaredContentType, asset.DeclaredBytes,
            asset.ActualBytes, asset.Revision, asset.SortOrder, asset.IsActive, asset.FailureCode, asset.FailureMessage,
            asset.DurationMilliseconds, asset.Width, asset.Height, asset.Version)).ToList());

    private static TranscriptResponse ToTranscript(TranscriptRevision value, bool instrumental) => new(
        value.Id, value.Number, value.Source, value.State, value.Language, instrumental,
        Deserialize<IReadOnlyList<TranscriptPhraseRequest>>(value.PhrasesJson) ?? [], value.ApprovedAt, value.Version);

    private static ArtworkPackResponse ToArtwork(ArtworkPackRevision value) => new(
        value.Id, value.Number, value.OperationNumber, value.State, value.Prompt,
        Deserialize<IReadOnlyList<Guid>>(value.CandidateAssetIdsJson) ?? [],
        Deserialize<IReadOnlyList<Guid>>(value.BackgroundAssetIdsJson) ?? [], value.SelectedAssetId,
        value.CompositionJson, value.ApprovedAt, value.Version);

    private static HookSetResponse ToHooks(HookSetRevision value) => new(
        value.Id, value.Number, value.TranscriptRevisionId,
        Deserialize<IReadOnlyList<HookRequest>>(value.HooksJson) ?? [], value.Version);

    private static CampaignResponse ToCampaign(CampaignPlanRevision value) => new(
        value.Id, value.Number, value.State,
        Deserialize<IReadOnlyList<CampaignItemRequest>>(value.ItemsJson) ?? [], value.Version);

    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, StoredJson);

    private static JsonDocument ParseJson(string json, string field)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw Problem(422, "validation.failed", $"{field} must contain valid JSON.");
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { throw Problem(422, "validation.failed", $"{field} must contain valid JSON."); }
    }

    private static CampaignItemPlan ToCampaignPlan(CampaignItemRequest item)
    {
        try
        {
            using var document = JsonDocument.Parse(item.CompositionJson);
            var root = document.RootElement;
            var relativeDay = root.TryGetProperty("relativeDay", out var relativeDayValue) &&
                              relativeDayValue.TryGetInt32(out var parsedRelativeDay)
                ? parsedRelativeDay
                : int.MinValue;
            var duration = root.TryGetProperty("durationMilliseconds", out var durationValue) &&
                           durationValue.TryGetInt64(out var parsedDuration)
                ? parsedDuration
                : 0;
            var headline = root.TryGetProperty("headline", out var headlineValue) &&
                           headlineValue.ValueKind == JsonValueKind.String
                ? headlineValue.GetString() ?? item.Text
                : item.Text;
            var callToAction = root.TryGetProperty("cta", out var ctaValue) &&
                               ctaValue.ValueKind == JsonValueKind.String
                ? ctaValue.GetString() ?? string.Empty
                : string.Empty;
            return new CampaignItemPlan(
                item.Id,
                item.Slot,
                relativeDay,
                item.Template,
                string.IsNullOrWhiteSpace(item.HookId) ? null : StableGuid(item.HookId),
                headline,
                item.Text,
                callToAction,
                item.BackgroundAssetId,
                duration,
                item.CompositionJson);
        }
        catch (JsonException)
        {
            return new CampaignItemPlan(
                item.Id,
                item.Slot,
                int.MinValue,
                item.Template,
                string.IsNullOrWhiteSpace(item.HookId) ? null : StableGuid(item.HookId),
                item.Text,
                item.Text,
                string.Empty,
                item.BackgroundAssetId,
                0,
                item.CompositionJson);
        }
    }

    private static Guid StableGuid(string value)
    {
        if (Guid.TryParse(value, out var parsed)) return parsed;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private sealed record WorkflowStageUpdate(
        WorkflowLane Lane,
        PipelineStageState State,
        string? BlockerCode = null);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static ApiProblemException NotFound() => Problem(404, "resource.not_found", "The requested resource was not found.");
    private static ApiProblemException Problem(int status, string code, string message) => new(status, code, message);
}
