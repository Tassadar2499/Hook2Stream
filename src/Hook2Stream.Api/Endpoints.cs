using System.Text.Json;
using Hook2Stream.Api.Authentication;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Api;

public static class Endpoints
{
    private const string ExternalAiPolicyVersion = "external-ai-zdr-v1";

    public static IEndpointRouteBuilder MapHook2StreamApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAuthApi();

        var api = endpoints.MapGroup("/api/v1")
            .RequireAuthorization();

        var account = api.MapGroup("/account");
        account.MapGet("/me", GetAccount)
            .Produces<AccountResponse>();
        account.MapPut("/onboarding", CompleteOnboarding)
            .Produces<AccountResponse>()
            .Produces<AccountResponse>(StatusCodes.Status201Created);

        var brandKit = api.MapGroup("/brand-kit");
        brandKit.MapGet("/", GetBrandKit)
            .Produces<BrandKitResponse>();
        brandKit.MapPut("/", UpdateBrandKit)
            .Produces<BrandKitResponse>();

        var releases = api.MapGroup("/releases");
        api.MapMp3FirstApi();
        endpoints.MapBillingApi(api);
        releases.MapGet("/", ListReleases)
            .Produces<IReadOnlyList<ReleaseResponse>>();
        releases.MapPost("/", CreateRelease)
            .Produces<ReleaseResponse>(StatusCodes.Status201Created);
        releases.MapGet("/{projectId:guid}", GetRelease)
            .Produces<ReleaseResponse>();
        releases.MapPut("/{projectId:guid}", UpdateRelease)
            .Produces<ReleaseResponse>();
        releases.MapPost("/{projectId:guid}/archive", ArchiveRelease)
            .Produces<ReleaseResponse>();
        releases.MapPost("/{projectId:guid}/restore", RestoreRelease)
            .Produces<ReleaseResponse>();
        releases.MapDelete("/{projectId:guid}", DeleteRelease)
            .Produces<DeletionStatusResponse>(StatusCodes.Status202Accepted);
        releases.MapGet("/{projectId:guid}/readiness", GetReadiness)
            .Produces<ReadinessResponse>();
        releases.MapGet("/{projectId:guid}/rights", GetRights)
            .Produces<RightsAttestationResponse>()
            .Produces(StatusCodes.Status404NotFound);
        releases.MapPut("/{projectId:guid}/rights", UpdateRights)
            .Produces<RightsAttestationResponse>();

        releases.MapGet("/{projectId:guid}/assets", ListAssets)
            .Produces<IReadOnlyList<AssetResponse>>();
        releases.MapPost("/{projectId:guid}/uploads", CreateUpload)
            .Produces<UploadSessionResponse>(StatusCodes.Status201Created);
        releases.MapPut("/{projectId:guid}/assets/reorder", ReorderAssets)
            .Produces<IReadOnlyList<AssetResponse>>();
        releases.MapDelete("/{projectId:guid}/assets/{assetId:guid}", DeleteAsset)
            .Produces<AssetDeletionResponse>(StatusCodes.Status202Accepted);

        var uploads = api.MapGroup("/uploads");
        uploads.MapGet("/{sessionId:guid}", ResumeUpload)
            .Produces<UploadSessionResponse>();
        uploads.MapPost("/{sessionId:guid}/parts", SignUploadPart)
            .Produces<UploadPartResponse>();
        uploads.MapPost("/{sessionId:guid}/complete", CompleteUpload)
            .Produces<CompleteUploadResponse>(StatusCodes.Status202Accepted);
        uploads.MapPost("/{sessionId:guid}/abort", AbortUpload)
            .Produces(StatusCodes.Status204NoContent);

        var jobs = api.MapGroup("/jobs");
        jobs.MapGet("/{jobId:guid}", GetJob)
            .Produces<JobResponse>();
        jobs.MapGet("/{jobId:guid}/events", StreamJobEvents)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream");

        return endpoints;
    }

    private static async Task<IResult> GetAccount(
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var user = await currentUser.EnsureUserAsync(cancellationToken);
        var workspace = await dbContext.Workspaces
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.OwnerUserId == user.Id, cancellationToken);

        if (workspace is not null)
        {
            ApiEndpointHelpers.SetEtag(response, workspace.Version);
        }

        return Results.Ok(new AccountResponse(
            user.Id,
            user.ExternalSubject,
            user.Email,
            user.DisplayName,
            workspace is null,
            workspace?.Id,
            workspace?.Name,
            workspace?.Version));
    }

    private static async Task<IResult> CompleteOnboarding(
        CompleteOnboardingRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        IConfiguration configuration,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(request.WorkspaceName) || request.WorkspaceName.Trim().Length > 160)
        {
            errors.Add("workspaceName", "Workspace name is required and must not exceed 160 characters.");
        }

        if (!request.AcceptTerms)
        {
            errors.Add("acceptTerms", "Terms must be accepted.");
        }

        if (!request.AcceptPrivacy)
        {
            errors.Add("acceptPrivacy", "Privacy policy must be accepted.");
        }

        var termsVersion = configuration["Legal:TermsVersion"] ?? "draft-2026-07-16";
        var privacyVersion = configuration["Legal:PrivacyVersion"] ?? "draft-2026-07-16";
        if (!string.Equals(request.TermsVersion, termsVersion, StringComparison.Ordinal))
        {
            errors.Add("termsVersion", "Accept the current Terms version.");
        }

        if (!string.Equals(request.PrivacyVersion, privacyVersion, StringComparison.Ordinal))
        {
            errors.Add("privacyVersion", "Accept the current Privacy version.");
        }

        ApiEndpointHelpers.RequireValid(errors);

        var user = await currentUser.EnsureUserAsync(cancellationToken);
        var existing = await dbContext.Workspaces
            .Include(value => value.BrandKit)
            .SingleOrDefaultAsync(value => value.OwnerUserId == user.Id, cancellationToken);

        if (existing is not null)
        {
            ApiEndpointHelpers.SetEtag(response, existing.Version);
            return Results.Ok(new AccountResponse(
                user.Id,
                user.ExternalSubject,
                user.Email,
                user.DisplayName,
                false,
                existing.Id,
                existing.Name,
                existing.Version));
        }

        var now = DateTimeOffset.UtcNow;
        var workspace = new Workspace
        {
            OwnerUserId = user.Id,
            Name = request.WorkspaceName.Trim(),
            TermsVersion = termsVersion,
            PrivacyVersion = privacyVersion,
            TermsAcceptedAt = now,
            PrivacyAcceptedAt = now
        };
        workspace.BrandKit = new BrandKit
        {
            Workspace = workspace,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? user.DisplayName ?? request.WorkspaceName.Trim()
                : request.DisplayName.Trim()
        };

        dbContext.Workspaces.Add(workspace);
        dbContext.AuditEvents.Add(new AuditEvent
        {
            WorkspaceId = workspace.Id,
            ActorSubject = user.ExternalSubject,
            Action = "workspace.created",
            ResourceType = "workspace",
            ResourceId = workspace.Id,
            DataJson = JsonSerializer.Serialize(new { termsVersion, privacyVersion })
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        ApiEndpointHelpers.SetEtag(response, workspace.Version);
        return Results.Created(
            $"/api/v1/account/me",
            new AccountResponse(
                user.Id,
                user.ExternalSubject,
                user.Email,
                user.DisplayName,
                false,
                workspace.Id,
                workspace.Name,
                workspace.Version));
    }

    private static async Task<IResult> GetBrandKit(
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var brandKit = await dbContext.BrandKits
            .AsNoTracking()
            .SingleAsync(value => value.WorkspaceId == context.Workspace.Id, cancellationToken);
        ApiEndpointHelpers.SetEtag(response, brandKit.Version);
        return Results.Ok(ToResponse(brandKit));
    }

    private static async Task<IResult> UpdateBrandKit(
        UpdateBrandKitRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        ApiEndpointHelpers.RequireValid(BrandKitRules.Validate(request));
        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var brandKit = await dbContext.BrandKits
            .SingleAsync(value => value.WorkspaceId == context.Workspace.Id, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expectedVersion, brandKit.Version);

        brandKit.DisplayName = request.DisplayName.Trim();
        brandKit.PrimaryColor = request.PrimaryColor.ToUpperInvariant();
        brandKit.SecondaryColor = request.SecondaryColor.ToUpperInvariant();
        brandKit.AccentColor = request.AccentColor.ToUpperInvariant();
        brandKit.HeadingFont = request.HeadingFont;
        brandKit.BodyFont = request.BodyFont;
        brandKit.DefaultCta = request.DefaultCta.Trim();
        brandKit.SmartLink = request.SmartLink?.Trim();
        brandKit.ToneRestrictions = request.ToneRestrictions?.Trim();
        brandKit.CharacterLayerEnabled = request.CharacterLayerEnabled;

        var nextBrandVersion = brandKit.Version + 1;
        var affectedProjects = await dbContext.Projects
            .Where(value => value.WorkspaceId == context.Workspace.Id &&
                            value.FlowKind == FlowKind.Mp3First &&
                            !value.IsArchived)
            .ToListAsync(cancellationToken);
        foreach (var project in affectedProjects)
        {
            project.BrandKitVersion = nextBrandVersion;
            if (project.CurrentCampaignPlanRevisionId is { } campaignId)
            {
                var campaign = await dbContext.CampaignPlanRevisions.SingleOrDefaultAsync(
                    value => value.Id == campaignId,
                    cancellationToken);
                if (campaign is not null) campaign.State = RevisionState.Superseded;
                project.CurrentCampaignPlanRevisionId = null;
            }
            dbContext.OutboxMessages.Add(new OutboxMessage
            {
                WorkspaceId = project.WorkspaceId,
                AggregateId = project.Id,
                Destination = "pipeline",
                MessageType = "pipeline.reconcile",
                DedupeKey = $"pipeline.reconcile:{project.Id:N}:brand:{nextBrandVersion}:{Guid.CreateVersion7():N}",
                PayloadJson = JsonSerializer.Serialize(new { projectId = project.Id, reason = "brand.updated" })
            });
            dbContext.ProjectEvents.Add(new ProjectEvent
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                EventType = "brand.updated",
                DataJson = JsonSerializer.Serialize(new { projectId = project.Id, brandKitVersion = nextBrandVersion })
            });
        }
        var affectedProjectIds = affectedProjects.Select(value => value.Id).ToArray();
        var staleBrandJobs = await dbContext.Jobs
            .Where(value => value.ProjectId != null &&
                            affectedProjectIds.Contains(value.ProjectId.Value) &&
                            (value.Type == JobType.CampaignGeneration || value.Type == JobType.PreviewRender) &&
                            (value.State == JobState.Queued || value.State == JobState.Running))
            .ToListAsync(cancellationToken);
        foreach (var staleJob in staleBrandJobs)
        {
            staleJob.State = JobState.Cancelled;
            staleJob.ErrorCode = "brand.changed";
            staleJob.ErrorMessage = "The job was cancelled because the brand kit changed.";
            staleJob.CompletedAt = DateTimeOffset.UtcNow;
            staleJob.LeaseOwner = null;
            staleJob.LeaseToken = null;
            staleJob.LeaseExpiresAt = null;
            dbContext.JobEvents.Add(new JobEvent
            {
                JobId = staleJob.Id,
                EventType = "cancelled",
                DataJson = "{\"code\":\"brand.changed\"}"
            });
        }

        dbContext.AuditEvents.Add(new AuditEvent
        {
            WorkspaceId = context.Workspace.Id,
            ActorSubject = currentUser.Subject,
            Action = "brand_kit.updated",
            ResourceType = "brand_kit",
            ResourceId = brandKit.Id,
            DataJson = "{}"
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        ApiEndpointHelpers.SetEtag(response, brandKit.Version);
        return Results.Ok(ToResponse(brandKit));
    }

    private static async Task<IResult> ListReleases(
        bool? includeArchived,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var query = dbContext.Projects
            .AsNoTracking()
            .Where(value => value.WorkspaceId == context.Workspace.Id);

        if (includeArchived != true)
        {
            query = query.Where(value => !value.IsArchived);
        }

        var projects = await query
            .OrderByDescending(value => value.CreatedAt)
            .ToListAsync(cancellationToken);
        return Results.Ok(projects.Select(value => ToResponse(value, Array.Empty<MediaAsset>())));
    }

    private static async Task<IResult> CreateRelease(
        CreateReleaseRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        ApiEndpointHelpers.RequireValid(ReleaseRules.Validate(request, UtcToday()));
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var brandKitVersion = await dbContext.BrandKits
            .Where(value => value.WorkspaceId == context.Workspace.Id)
            .Select(value => value.Version)
            .SingleAsync(cancellationToken);

        var project = new ReleaseProject
        {
            WorkspaceId = context.Workspace.Id,
            ProjectLabel = request.ProjectLabel.Trim(),
            ArtistName = request.ArtistName.Trim(),
            TrackTitle = request.TrackTitle.Trim(),
            Language = request.Language.Trim(),
            InternalNotes = request.InternalNotes?.Trim(),
            LyricsText = request.LyricsText?.Trim(),
            IsInstrumental = request.IsInstrumental,
            Mode = request.Mode,
            ReleaseDate = request.ReleaseDate,
            CampaignStartDate = request.CampaignStartDate,
            BrandKitVersion = brandKitVersion
        };

        dbContext.Projects.Add(project);
        dbContext.AuditEvents.Add(new AuditEvent
        {
            WorkspaceId = context.Workspace.Id,
            ActorSubject = currentUser.Subject,
            Action = "release.created",
            ResourceType = "release_project",
            ResourceId = project.Id,
            DataJson = JsonSerializer.Serialize(new { project.Mode })
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        ApiEndpointHelpers.SetEtag(response, project.Version);
        return Results.Created(
            $"/api/v1/releases/{project.Id}",
            ToResponse(project, Array.Empty<MediaAsset>()));
    }

    private static async Task<IResult> GetRelease(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, includeAssets: true, cancellationToken);
        ApiEndpointHelpers.SetEtag(response, project.Version);
        return Results.Ok(ToResponse(project, project.Assets));
    }

    private static async Task<IResult> UpdateRelease(
        Guid projectId,
        UpdateReleaseRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        ApiEndpointHelpers.RequireValid(ReleaseRules.Validate(request, UtcToday()));
        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, includeAssets: true, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expectedVersion, project.Version);

        project.ProjectLabel = request.ProjectLabel.Trim();
        project.ArtistName = request.ArtistName.Trim();
        project.TrackTitle = request.TrackTitle.Trim();
        project.Language = request.Language.Trim();
        project.InternalNotes = request.InternalNotes?.Trim();
        project.LyricsText = request.LyricsText?.Trim();
        project.IsInstrumental = request.IsInstrumental;
        project.Mode = request.Mode;
        project.ReleaseDate = request.ReleaseDate;
        project.CampaignStartDate = request.CampaignStartDate;
        await dbContext.SaveChangesAsync(cancellationToken);

        ApiEndpointHelpers.SetEtag(response, project.Version);
        return Results.Ok(ToResponse(project, project.Assets));
    }

    private static async Task<IResult> ArchiveRelease(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpRequest request,
        HttpResponse response,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(request);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, false, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expectedVersion, project.Version);

        var now = timeProvider.GetUtcNow();
        project.Archive();
        await ProjectArchiveCoordinator.PauseAsync(dbContext, project, now, cancellationToken);
        dbContext.ProjectEvents.Add(new ProjectEvent
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            EventType = "release.archived",
            DataJson = JsonSerializer.Serialize(new { projectId = project.Id })
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        ApiEndpointHelpers.SetEtag(response, project.Version);
        return Results.Ok(ToResponse(project, Array.Empty<MediaAsset>()));
    }

    private static async Task<IResult> RestoreRelease(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpRequest request,
        HttpResponse response,
        IOptions<OperationalPolicyOptions> policyOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(request);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, false, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expectedVersion, project.Version);
        var now = timeProvider.GetUtcNow();
        project.Restore();
        await ProjectArchiveCoordinator.ResumeAsync(
            dbContext,
            project,
            now,
            TimeSpan.FromMinutes(policyOptions.Value.DeletionFenceMinutes),
            cancellationToken);
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            WorkspaceId = project.WorkspaceId,
            AggregateId = project.Id,
            Destination = "pipeline",
            MessageType = "pipeline.reconcile",
            DedupeKey = $"pipeline.reconcile:{project.Id:N}:restored:{Guid.CreateVersion7():N}",
            PayloadJson = JsonSerializer.Serialize(new { projectId = project.Id, reason = "release.restored" })
        });
        dbContext.ProjectEvents.Add(new ProjectEvent
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            EventType = "release.restored",
            DataJson = JsonSerializer.Serialize(new { projectId = project.Id })
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        ApiEndpointHelpers.SetEtag(response, project.Version);
        return Results.Ok(ToResponse(project, Array.Empty<MediaAsset>()));
    }

    private static async Task<IResult> DeleteRelease(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpRequest request,
        IOptions<OperationalPolicyOptions> policyOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(request);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, true, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expectedVersion, project.Version);

        var now = timeProvider.GetUtcNow();
        var pendingCheckouts = await dbContext.BillingCheckouts
            .Where(value => value.ProjectId == project.Id && value.State == CheckoutState.Pending)
            .ToListAsync(cancellationToken);
        if (pendingCheckouts.Count > 0)
        {
            throw new ApiProblemException(
                StatusCodes.Status409Conflict,
                "billing.checkout_pending",
                "Wait for the open payment session to complete or expire before deleting this project.");
        }

        await ProjectDeletionCoordinator.FenceAsync(
            dbContext,
            project,
            now,
            "project.deleted",
            cancellationToken);

        var cleanupAvailableAt = now.AddMinutes(policyOptions.Value.DeletionFenceMinutes);
        var configuredPurgeDueAt = now.AddDays(policyOptions.Value.ExplicitDeletionDays);

        var tombstone = new ProjectDeletionTombstone
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            ActorSubject = currentUser.Subject,
            PolicyVersion = "retention-v1",
            RequestedAt = now,
            PurgeDueAt = configuredPurgeDueAt > cleanupAvailableAt
                ? configuredPurgeDueAt
                : cleanupAvailableAt,
            State = "queued"
        };
        dbContext.ProjectDeletionTombstones.Add(tombstone);
        var cleanupJob = new Job
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Type = JobType.AssetCleanup,
            RequiredCapability = JobRoutingRegistry.GetRequiredCapability(JobType.AssetCleanup),
            HandlerVersion = "v1",
            PayloadSchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new
            {
                projectId = project.Id,
                deletionId = tombstone.Id
            }),
            IdempotencyKey = $"retention:project:{tombstone.Id:N}",
            State = JobState.Queued,
            AvailableAt = cleanupAvailableAt
        };
        dbContext.Jobs.Add(cleanupJob);
        dbContext.JobEvents.Add(new JobEvent
        {
            JobId = cleanupJob.Id,
            EventType = "queued",
            DataJson = JsonSerializer.Serialize(new
            {
                cleanupJob.Type,
                cleanupJob.RequiredCapability,
                deletionId = tombstone.Id,
                purgeDueAt = tombstone.PurgeDueAt
            })
        });

        dbContext.AuditEvents.Add(new AuditEvent
        {
            WorkspaceId = context.Workspace.Id,
            ActorSubject = currentUser.Subject,
            Action = "release.deleted",
            ResourceType = "release_project",
            ResourceId = project.Id,
            DataJson = "{}"
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Accepted(
            $"/api/v1/jobs/{cleanupJob.Id}",
            new DeletionStatusResponse(
                tombstone.Id,
                project.Id,
                now,
                tombstone.PurgeDueAt,
                tombstone.State));
    }

    private static async Task<IResult> GetRights(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, false, cancellationToken);
        var attestation = await dbContext.RightsAttestations
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProjectId == project.Id, cancellationToken);
        if (attestation is null)
        {
            throw new ApiProblemException(
                StatusCodes.Status404NotFound,
                "rights.not_found",
                "No rights attestation has been saved for this release.");
        }

        ApiEndpointHelpers.SetEtag(response, project.Version);
        return Results.Ok(ToResponse(attestation, project.Version));
    }

    private static async Task<IResult> UpdateRights(
        Guid projectId,
        RightsAttestationRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, false, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expectedVersion, project.Version);

        if (project.FlowKind != FlowKind.Mp3First &&
            (string.IsNullOrWhiteSpace(request.PolicyVersion) || request.PolicyVersion.Length > 64))
        {
            var errors = new ValidationErrors();
            errors.Add("policyVersion", "Policy version is required.");
            ApiEndpointHelpers.RequireValid(errors);
        }

        MediaAsset? attestedAudio = null;
        if (project.FlowKind == FlowKind.Mp3First)
        {
            attestedAudio = await dbContext.MediaAssets.AsNoTracking()
                .Where(value => value.ProjectId == project.Id && value.Kind == AssetKind.Audio)
                .OrderByDescending(value => value.IsActive)
                .ThenByDescending(value => value.Revision)
                .FirstOrDefaultAsync(cancellationToken);
            if (request.AllowsExternalAiProcessing &&
                (attestedAudio is null ||
                 attestedAudio.State != AssetState.Ready ||
                 string.IsNullOrWhiteSpace(attestedAudio.Sha256)))
            {
                throw new ApiProblemException(
                    StatusCodes.Status409Conflict,
                    "audio.not_ready",
                    "Wait for the audio master to finish processing before granting external AI access.");
            }
        }

        var attestation = await dbContext.RightsAttestations
            .SingleOrDefaultAsync(value => value.ProjectId == project.Id, cancellationToken);
        if (attestation is null)
        {
            attestation = new RightsAttestation
            {
                ProjectId = project.Id,
                ActorSubject = currentUser.Subject,
                PolicyVersion = request.PolicyVersion,
                AcceptedAt = DateTimeOffset.UtcNow
            };
            dbContext.RightsAttestations.Add(attestation);
        }

        attestation.ActorSubject = currentUser.Subject;
        attestation.PolicyVersion = project.FlowKind == FlowKind.Mp3First
            ? ExternalAiPolicyVersion
            : request.PolicyVersion.Trim();
        attestation.OwnsAudioRights = request.OwnsAudioRights;
        attestation.OwnsLyricsRights = request.OwnsLyricsRights;
        attestation.OwnsVisualRights = request.OwnsVisualRights;
        attestation.AllowsExternalAiArtwork = request.AllowsExternalAiArtwork;
        attestation.AllowsExternalAiProcessing = request.AllowsExternalAiProcessing;
        if (attestedAudio is not null)
        {
            attestation.AudioAssetId = attestedAudio.Id;
            if (!string.IsNullOrWhiteSpace(attestedAudio.Sha256))
                attestation.AudioFingerprint = attestedAudio.Sha256;
        }
        attestation.SyntheticContentStatus = request.SyntheticContentStatus;
        attestation.AcceptedAt = DateTimeOffset.UtcNow;
        dbContext.AuditEvents.Add(new AuditEvent
        {
            WorkspaceId = project.WorkspaceId,
            ActorSubject = currentUser.Subject,
            Action = request.AllowsExternalAiProcessing
                ? "rights.external_ai_processing_accepted"
                : "rights.external_ai_processing_revoked",
            ResourceType = "rights_attestation",
            ResourceId = attestation.Id,
            DataJson = JsonSerializer.Serialize(new
            {
                attestation.PolicyVersion,
                attestation.AudioAssetId,
                attestation.OwnsAudioRights,
                attestation.OwnsLyricsRights,
                attestation.OwnsVisualRights,
                attestation.AllowsExternalAiProcessing
            })
        });
        dbContext.Entry(project).Property(value => value.Version).IsModified = true;
        if (project.FlowKind == FlowKind.Mp3First)
        {
            var contentRightsReady = attestedAudio is not null &&
                                     request.OwnsAudioRights &&
                                     (project.IsInstrumental && project.IsInstrumentalConfirmed || request.OwnsLyricsRights);
            var pausedTypes = new HashSet<JobType>();
            if (!contentRightsReady)
            {
                pausedTypes.UnionWith([
                    JobType.Transcription,
                    JobType.ArtworkGeneration,
                    JobType.CampaignGeneration,
                    JobType.PreviewRender,
                    JobType.FinalRender,
                    JobType.ExportBundle
                ]);
            }
            else if (!request.AllowsExternalAiProcessing)
            {
                pausedTypes.UnionWith([
                    JobType.Transcription,
                    JobType.ArtworkGeneration,
                    JobType.CampaignGeneration
                ]);
            }

            Guid[] uploadedVisualAssetIds = request.OwnsVisualRights
                ? []
                : await dbContext.MediaAssets.AsNoTracking()
                    .Where(value => value.ProjectId == project.Id &&
                                    value.Origin == AssetOrigin.Uploaded &&
                                    value.State != AssetState.Rejected &&
                                    value.State != AssetState.Deleted &&
                                    (value.Kind == AssetKind.Cover || value.Kind == AssetKind.Visual))
                    .Select(value => value.Id)
                    .ToArrayAsync(cancellationToken);
            var hasUploadedVisuals = uploadedVisualAssetIds.Length > 0;
            if (hasUploadedVisuals)
            {
                pausedTypes.UnionWith([
                    JobType.ArtworkGeneration,
                    JobType.CampaignGeneration,
                    JobType.PreviewRender,
                    JobType.FinalRender,
                    JobType.CleanCoverRender,
                    JobType.ExportBundle
                ]);
            }
            var now = DateTimeOffset.UtcNow;

            // User-blocked jobs do not poll. Resume the same immutable command
            // only when this attestation satisfies the blocker that stopped it.
            var blockedJobs = await dbContext.Jobs
                .Where(value => value.ProjectId == project.Id &&
                                value.State == JobState.Cancelled &&
                                (value.ErrorCode == "rights.required" ||
                                 value.ErrorCode == "rights.external_ai_processing_required" ||
                                 value.ErrorCode == "rights.visual_required"))
                .ToListAsync(cancellationToken);
            foreach (var blockedJob in blockedJobs)
            {
                var needsAiConsent = blockedJob.Type is
                    JobType.Transcription or
                    JobType.ArtworkGeneration or
                    JobType.CampaignGeneration;
                var needsContentRights = blockedJob.Type is not (JobType.CleanCoverRender or JobType.MediaIngest);
                var needsVisualRights = blockedJob.ErrorCode == "rights.visual_required" ||
                                        hasUploadedVisuals && blockedJob.Type is
                                            JobType.ArtworkGeneration or
                                            JobType.CampaignGeneration or
                                            JobType.PreviewRender or
                                            JobType.FinalRender or
                                            JobType.CleanCoverRender or
                                            JobType.ExportBundle or
                                            JobType.MediaIngest;
                var canResume = (!needsContentRights || contentRightsReady) &&
                                (!needsAiConsent || request.AllowsExternalAiProcessing) &&
                                (!needsVisualRights || request.OwnsVisualRights);
                if (!canResume) continue;
                blockedJob.State = JobState.Queued;
                blockedJob.AvailableAt = now;
                blockedJob.CompletedAt = null;
                blockedJob.ProgressStage = "queued";
                blockedJob.ErrorCode = null;
                blockedJob.ErrorMessage = null;
                dbContext.JobEvents.Add(new JobEvent
                {
                    JobId = blockedJob.Id,
                    EventType = "resumed",
                    DataJson = "{\"reason\":\"rights.updated\"}"
                });
            }
            if (pausedTypes.Count > 0 || uploadedVisualAssetIds.Length > 0)
            {
                var pausedJobs = await dbContext.Jobs
                    .Where(value => value.ProjectId == project.Id &&
                                    (pausedTypes.Contains(value.Type) ||
                                     value.Type == JobType.MediaIngest &&
                                     value.AssetId != null &&
                                     uploadedVisualAssetIds.Contains(value.AssetId.Value)) &&
                                    (value.State == JobState.Queued || value.State == JobState.Running))
                    .ToListAsync(cancellationToken);
                foreach (var pausedJob in pausedJobs)
                {
                    var isExternalAiJob = pausedJob.Type is
                        JobType.Transcription or
                        JobType.ArtworkGeneration or
                        JobType.CampaignGeneration;
                    var needsVisualRights = pausedJob.Type == JobType.MediaIngest ||
                                            hasUploadedVisuals && pausedJob.Type is
                                                JobType.ArtworkGeneration or
                                                JobType.CampaignGeneration or
                                                JobType.PreviewRender or
                                                JobType.FinalRender or
                                                JobType.CleanCoverRender or
                                                JobType.ExportBundle;
                    var blockerCode = !contentRightsReady
                        ? "rights.required"
                        : needsVisualRights && !request.OwnsVisualRights
                            ? "rights.visual_required"
                            : isExternalAiJob && !request.AllowsExternalAiProcessing
                                ? "rights.external_ai_processing_required"
                                : "rights.required";
                    pausedJob.State = JobState.Cancelled;
                    pausedJob.CompletedAt = now;
                    pausedJob.ProgressStage = "waiting_user";
                    pausedJob.LeaseOwner = null;
                    pausedJob.LeaseToken = null;
                    pausedJob.LeaseExpiresAt = null;
                    pausedJob.ErrorCode = blockerCode;
                    pausedJob.ErrorMessage = "The job is paused until the required rights are confirmed.";
                    dbContext.JobEvents.Add(new JobEvent
                    {
                        JobId = pausedJob.Id,
                        EventType = "waiting_user",
                        DataJson = JsonSerializer.Serialize(new { code = blockerCode })
                    });
                }
                var pausedIds = pausedJobs.Select(value => value.Id).ToArray();
                var runningAttempts = await dbContext.JobAttempts
                    .Where(value => pausedIds.Contains(value.JobId) && value.State == JobState.Running)
                    .ToListAsync(cancellationToken);
                foreach (var attempt in runningAttempts)
                {
                    attempt.State = JobState.Cancelled;
                    attempt.CompletedAt = now;
                    attempt.ErrorCode = pausedJobs
                        .Where(value => value.Id == attempt.JobId)
                        .Select(value => value.ErrorCode)
                        .SingleOrDefault() ?? "rights.required";
                    attempt.ErrorMessage = "The job is waiting for updated rights.";
                }
            }
            dbContext.OutboxMessages.Add(new OutboxMessage
            {
                WorkspaceId = project.WorkspaceId,
                AggregateId = project.Id,
                Destination = "pipeline",
                MessageType = "pipeline.reconcile",
                DedupeKey = $"pipeline.reconcile:{project.Id:N}:rights:{attestation.Id:N}:{Guid.CreateVersion7():N}",
                PayloadJson = JsonSerializer.Serialize(new { projectId = project.Id, reason = "rights.updated" })
            });
            dbContext.ProjectEvents.Add(new ProjectEvent
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                EventType = "rights.updated",
                DataJson = JsonSerializer.Serialize(new { projectId = project.Id })
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        ApiEndpointHelpers.SetEtag(response, project.Version);
        return Results.Ok(ToResponse(attestation, project.Version));
    }

    private static async Task<IResult> GetReadiness(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, true, cancellationToken);
        var rights = await dbContext.RightsAttestations
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProjectId == project.Id, cancellationToken);

        var readyAssets = project.Assets.Where(value => value.IsActive && value.State == AssetState.Ready).ToList();
        var hasAudio = readyAssets.Count(value => value.Kind == AssetKind.Audio) == 1;
        var hasCover = readyAssets.Count(value => value.Kind == AssetKind.Cover) == 1;
        var visualCount = readyAssets.Count(value => value.Kind == AssetKind.Visual);
        var hasLyrics = project.IsInstrumental || !string.IsNullOrWhiteSpace(project.LyricsText);
        var hasRights = rights is
        {
            OwnsAudioRights: true,
            OwnsLyricsRights: true,
            OwnsVisualRights: true
        };

        var missing = new List<string>();
        if (!hasAudio) missing.Add("Upload and process one MP3 or WAV.");
        if (!hasCover) missing.Add("Upload and process one cover image.");
        if (visualCount < MediaPolicy.MinVisualCount) missing.Add("Upload at least three ready visual assets.");
        if (!hasLyrics) missing.Add("Provide lyrics or mark the track as instrumental.");
        if (!hasRights) missing.Add("Confirm rights to the uploaded materials.");

        return Results.Ok(new ReadinessResponse(
            missing.Count == 0,
            missing,
            visualCount,
            hasAudio,
            hasCover,
            hasLyrics,
            hasRights));
    }

    private static async Task<IResult> ListAssets(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        _ = await FindProject(dbContext, context.Workspace.Id, projectId, false, cancellationToken);
        var assets = await dbContext.MediaAssets
            .AsNoTracking()
            .Where(value => value.ProjectId == projectId)
            .OrderBy(value => value.Kind)
            .ThenBy(value => value.SortOrder)
            .ThenByDescending(value => value.Revision)
            .ToListAsync(cancellationToken);
        return Results.Ok(assets.Select(ToResponse));
    }

    private static async Task<IResult> CreateUpload(
        Guid projectId,
        CreateUploadRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        IObjectStorage objectStorage,
        IOptions<OperationalPolicyOptions> policyOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, true, cancellationToken);

        var activeVisuals = project.Assets
            .Where(value => value.Kind == AssetKind.Visual && value.IsActive)
            .ToList();
        ApiEndpointHelpers.RequireValid(MediaPolicy.ValidateReservation(
            request,
            activeVisuals.Count,
            activeVisuals.Sum(value => value.ActualBytes ?? value.DeclaredBytes)));

        MediaAsset? replacement = null;
        if (request.ReplacesAssetId is not null)
        {
            replacement = project.Assets.SingleOrDefault(value => value.Id == request.ReplacesAssetId)
                ?? throw NotFound();
            if (replacement.Kind != request.Kind)
            {
                throw new ApiProblemException(
                    StatusCodes.Status422UnprocessableEntity,
                    "asset.replacement_kind_mismatch",
                    "A replacement must use the same asset role.");
            }
        }

        var revision = replacement?.Revision + 1 ??
            (request.Kind is AssetKind.Audio or AssetKind.Cover
                ? project.Assets.Where(value => value.Kind == request.Kind).Select(value => value.Revision).DefaultIfEmpty(0).Max() + 1
                : 1);
        var asset = new MediaAsset
        {
            WorkspaceId = context.Workspace.Id,
            ProjectId = project.Id,
            Kind = request.Kind,
            OriginalFileName = Path.GetFileName(request.FileName),
            DeclaredContentType = request.ContentType,
            DeclaredBytes = request.SizeBytes,
            Revision = revision,
            SortOrder = request.Kind == AssetKind.Visual
                ? replacement?.SortOrder ?? activeVisuals.Count
                : 0,
            SupersedesAssetId = replacement?.Id,
            ObjectKey = ""
        };
        asset.ObjectKey = ObjectKeyFactory.Original(
            context.Workspace.Id,
            project.Id,
            asset.Id,
            asset.Revision);

        var now = timeProvider.GetUtcNow();
        var uploadPolicy = policyOptions.Value;
        var sessionExpiresAt = now.AddHours(uploadPolicy.UploadSessionHours);
        var urlLifetime = GetUploadUrlLifetime(now, sessionExpiresAt, uploadPolicy);
        var urlExpiresAt = now.Add(urlLifetime);
        var multipart = request.SizeBytes >= MediaPolicy.MultipartThresholdBytes;
        MultipartUpload? multipartUpload = null;
        Uri? uploadUrl = null;
        if (multipart)
        {
            multipartUpload = await objectStorage.CreateMultipartUploadAsync(
                asset.ObjectKey,
                asset.DeclaredContentType,
                cancellationToken);
        }
        else
        {
            uploadUrl = await objectStorage.CreateUploadUrlAsync(
                asset.ObjectKey,
                asset.DeclaredContentType,
                urlLifetime,
                cancellationToken);
        }

        var session = new UploadSession
        {
            WorkspaceId = context.Workspace.Id,
            ProjectId = project.Id,
            AssetId = asset.Id,
            Asset = asset,
            ObjectKey = asset.ObjectKey,
            IsMultipart = multipart,
            MultipartUploadId = multipartUpload?.UploadId,
            PartSizeBytes = multipart ? MediaPolicy.MultipartPartSizeBytes : request.SizeBytes,
            ExpiresAt = sessionExpiresAt
        };
        dbContext.UploadSessions.Add(session);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (multipartUpload is not null)
            {
                try
                {
                    await objectStorage.AbortMultipartUploadAsync(
                        asset.ObjectKey,
                        multipartUpload.UploadId,
                        CancellationToken.None);
                }
                catch
                {
                    // The bucket lifecycle rule is the final safety net if the
                    // best-effort compensation cannot reach object storage.
                }
            }

            throw;
        }

        var partCount = multipart
            ? (int)Math.Ceiling(request.SizeBytes / (double)MediaPolicy.MultipartPartSizeBytes)
            : 1;
        return Results.Created(
            $"/api/v1/uploads/{session.Id}",
            new UploadSessionResponse(
                session.Id,
                asset.Id,
                multipart,
                uploadUrl?.ToString(),
                multipartUpload?.UploadId,
                session.PartSizeBytes,
                partCount,
                urlExpiresAt,
                session.ExpiresAt));
    }

    private static async Task<IResult> ResumeUpload(
        Guid sessionId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        IObjectStorage objectStorage,
        IOptions<OperationalPolicyOptions> policyOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var session = await FindUpload(dbContext, context.Workspace.Id, sessionId, cancellationToken);

        await RejectExpiredUploadAsync(session, dbContext, timeProvider, cancellationToken);

        if (session.State is UploadState.Completed or UploadState.Aborted or UploadState.Expired)
        {
            throw new ApiProblemException(
                StatusCodes.Status409Conflict,
                "upload.not_resumable",
                "This upload session cannot be resumed.");
        }

        var now = timeProvider.GetUtcNow();
        var urlLifetime = GetUploadUrlLifetime(now, session.ExpiresAt, policyOptions.Value);
        var urlExpiresAt = now.Add(urlLifetime);
        Uri? uploadUrl = null;
        if (!session.IsMultipart)
        {
            uploadUrl = await objectStorage.CreateUploadUrlAsync(
                session.ObjectKey,
                session.Asset.DeclaredContentType,
                urlLifetime,
                cancellationToken);
        }

        var partCount = session.IsMultipart
            ? (int)Math.Ceiling(session.Asset.DeclaredBytes / (double)session.PartSizeBytes)
            : 1;
        return Results.Ok(new UploadSessionResponse(
            session.Id,
            session.AssetId,
            session.IsMultipart,
            uploadUrl?.ToString(),
            session.MultipartUploadId,
            session.PartSizeBytes,
            partCount,
            urlExpiresAt,
            session.ExpiresAt));
    }

    private static async Task<IResult> SignUploadPart(
        Guid sessionId,
        UploadPartRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        IObjectStorage objectStorage,
        IOptions<OperationalPolicyOptions> policyOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var session = await FindUpload(dbContext, context.Workspace.Id, sessionId, cancellationToken);
        await RejectExpiredUploadAsync(session, dbContext, timeProvider, cancellationToken);
        if (session.State is UploadState.Completed or UploadState.Aborted or UploadState.Expired)
        {
            throw new ApiProblemException(
                StatusCodes.Status409Conflict,
                "upload.not_resumable",
                "This upload session cannot accept more parts.");
        }

        if (!session.IsMultipart || string.IsNullOrWhiteSpace(session.MultipartUploadId))
        {
            throw new ApiProblemException(
                StatusCodes.Status409Conflict,
                "upload.not_multipart",
                "This upload does not use multipart transfer.");
        }

        var partCount = (int)Math.Ceiling(session.Asset.DeclaredBytes / (double)session.PartSizeBytes);
        if (request.PartNumber < 1 || request.PartNumber > partCount)
        {
            throw new ApiProblemException(
                StatusCodes.Status422UnprocessableEntity,
                "upload.part_out_of_range",
                "Part number is outside this upload.");
        }

        var now = timeProvider.GetUtcNow();
        var urlLifetime = GetUploadUrlLifetime(now, session.ExpiresAt, policyOptions.Value);
        var expiresAt = now.Add(urlLifetime);
        var url = await objectStorage.CreateMultipartPartUploadUrlAsync(
            session.ObjectKey,
            session.MultipartUploadId,
            request.PartNumber,
            urlLifetime,
            cancellationToken);
        session.State = UploadState.Uploading;
        session.Asset.State = AssetState.Uploading;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Ok(new UploadPartResponse(request.PartNumber, url.ToString(), expiresAt));
    }

    private static async Task<IResult> CompleteUpload(
        Guid sessionId,
        CompleteUploadRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        IObjectStorage objectStorage,
        IJobQueue jobQueue,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var session = await FindUpload(dbContext, context.Workspace.Id, sessionId, cancellationToken);
        if (session.State == UploadState.Completed)
        {
            var existingJob = await dbContext.Jobs
                .AsNoTracking()
                .Where(value => value.AssetId == session.AssetId && value.Type == JobType.MediaIngest)
                .OrderByDescending(value => value.CreatedAt)
                .Select(value => value.Id)
                .FirstOrDefaultAsync(cancellationToken);
            return Results.Accepted(
                $"/api/v1/jobs/{existingJob}",
                new CompleteUploadResponse(session.AssetId, existingJob));
        }

        await RejectExpiredUploadAsync(session, dbContext, timeProvider, cancellationToken);

        if (session.State is UploadState.Aborted or UploadState.Expired)
        {
            throw new ApiProblemException(
                StatusCodes.Status409Conflict,
                "upload.not_completable",
                "This upload session cannot be completed.");
        }

        if (session.IsMultipart)
        {
            var expectedPartCount = (int)Math.Ceiling(
                session.Asset.DeclaredBytes / (double)session.PartSizeBytes);
            if (request.Parts.Count != expectedPartCount ||
                request.Parts.Select(value => value.PartNumber).Distinct().Count() != expectedPartCount ||
                request.Parts.Any(value => value.PartNumber < 1 ||
                                           value.PartNumber > expectedPartCount ||
                                           string.IsNullOrWhiteSpace(value.ETag)))
            {
                throw new ApiProblemException(
                    StatusCodes.Status422UnprocessableEntity,
                    "upload.parts_invalid",
                    "Provide one ETag for every multipart upload part.");
            }

            await objectStorage.CompleteMultipartUploadAsync(
                session.ObjectKey,
                session.MultipartUploadId!,
                request.Parts.Select(value => new MultipartPart(value.PartNumber, value.ETag)).ToList(),
                cancellationToken);
        }

        var objectInfo = await objectStorage.HeadAsync(session.ObjectKey, cancellationToken);
        if (objectInfo is null || objectInfo.SizeBytes != session.Asset.DeclaredBytes)
        {
            throw new ApiProblemException(
                StatusCodes.Status409Conflict,
                "upload.object_invalid",
                "The uploaded object is missing or its size does not match the reservation.");
        }

        session.State = UploadState.Completed;
        session.CompletedAt = timeProvider.GetUtcNow();
        session.Asset.State = AssetState.Uploaded;
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            WorkspaceId = session.WorkspaceId,
            AggregateId = session.ProjectId,
            Destination = "pipeline",
            MessageType = "pipeline.reconcile",
            DedupeKey = $"pipeline.reconcile:{session.ProjectId:N}:upload:{session.AssetId:N}:r{session.Asset.Revision}",
            PayloadJson = JsonSerializer.Serialize(new
            {
                projectId = session.ProjectId,
                assetId = session.AssetId,
                reason = "audio.upload_completed"
            })
        });
        dbContext.ProjectEvents.Add(new ProjectEvent
        {
            WorkspaceId = session.WorkspaceId,
            ProjectId = session.ProjectId,
            EventType = "audio.upload_completed",
            DataJson = JsonSerializer.Serialize(new { projectId = session.ProjectId, assetId = session.AssetId })
        });

        var payload = JsonSerializer.Serialize(new { assetId = session.AssetId });
        Guid jobId;
        try
        {
            jobId = await jobQueue.EnqueueAsync(
                session.WorkspaceId,
                session.ProjectId,
                session.AssetId,
                JobType.MediaIngest,
                payload,
                $"media-ingest:{session.AssetId:N}:r{session.Asset.Revision}",
                cancellationToken);
        }
        catch
        {
            // Multipart completion is an external side effect that precedes the
            // atomic DB transition. Remove the object if persistence fails so a
            // completed-but-uncommitted upload cannot be orphaned.
            try
            {
                if (session.IsMultipart && !string.IsNullOrWhiteSpace(session.MultipartUploadId))
                {
                    await objectStorage.AbortMultipartUploadAsync(
                        session.ObjectKey,
                        session.MultipartUploadId,
                        CancellationToken.None);
                }
                await objectStorage.DeleteAsync(session.ObjectKey, CancellationToken.None);
            }
            catch
            {
                // Fixed session expiry plus retention cleanup remains the
                // safety net when compensation cannot reach object storage.
            }
            throw;
        }
        return Results.Accepted(
            $"/api/v1/jobs/{jobId}",
            new CompleteUploadResponse(session.AssetId, jobId));
    }

    private static async Task<IResult> AbortUpload(
        Guid sessionId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        IObjectStorage objectStorage,
        IOptions<OperationalPolicyOptions> policyOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var session = await FindUpload(dbContext, context.Workspace.Id, sessionId, cancellationToken);
        if (session.State == UploadState.Completed)
        {
            throw new ApiProblemException(
                StatusCodes.Status409Conflict,
                "upload.already_completed",
                "A completed upload cannot be aborted.");
        }
        if (session.State is UploadState.Aborted or UploadState.Expired)
        {
            return Results.NoContent();
        }

        if (session.IsMultipart && !string.IsNullOrWhiteSpace(session.MultipartUploadId))
        {
            await objectStorage.AbortMultipartUploadAsync(
                session.ObjectKey,
                session.MultipartUploadId,
                cancellationToken);
        }
        await objectStorage.DeleteAsync(session.ObjectKey, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var cleanupAvailableAt = now.AddMinutes(policyOptions.Value.DeletionFenceMinutes);
        session.State = UploadState.Aborted;
        session.AbortedAt = now;
        session.Asset.State = AssetState.Rejected;
        session.Asset.FailureCode = "upload.aborted";
        session.Asset.FailureMessage = "Upload cancelled by the user.";
        var cleanupJob = new Job
        {
            WorkspaceId = session.WorkspaceId,
            ProjectId = session.ProjectId,
            AssetId = session.AssetId,
            Type = JobType.AssetCleanup,
            RequiredCapability = JobRoutingRegistry.Control,
            HandlerVersion = "v1",
            PayloadSchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new
            {
                projectId = session.ProjectId,
                uploadSessionId = session.Id,
                notBefore = cleanupAvailableAt
            }),
            IdempotencyKey = $"retention:upload:{session.Id:N}",
            State = JobState.Queued,
            AvailableAt = cleanupAvailableAt
        };
        dbContext.Jobs.Add(cleanupJob);
        dbContext.JobEvents.Add(new JobEvent
        {
            JobId = cleanupJob.Id,
            EventType = "queued",
            DataJson = JsonSerializer.Serialize(new
            {
                cleanupJob.RequiredCapability,
                cleanupJob.AvailableAt,
                reason = "upload.aborted"
            })
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsset(
        Guid projectId,
        Guid assetId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpRequest request,
        IOptions<OperationalPolicyOptions> policyOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(request);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, false, cancellationToken);
        var asset = await dbContext.MediaAssets
            .SingleOrDefaultAsync(
                value => value.ProjectId == projectId && value.Id == assetId,
                cancellationToken)
            ?? throw NotFound();
        ApiEndpointHelpers.EnsureVersion(expectedVersion, asset.Version);

        var now = timeProvider.GetUtcNow();
        await AssetDeletionCoordinator.FenceAsync(
            dbContext,
            asset,
            now,
            "asset.deleted",
            cancellationToken);

        if (project.FlowKind == FlowKind.Mp3First)
        {
            dbContext.OutboxMessages.Add(new OutboxMessage
            {
                WorkspaceId = project.WorkspaceId,
                AggregateId = project.Id,
                Destination = "pipeline",
                MessageType = "pipeline.reconcile",
                DedupeKey = $"pipeline.reconcile:{project.Id:N}:asset-deleted:{asset.Id:N}:{Guid.CreateVersion7():N}",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    projectId = project.Id,
                    assetId = asset.Id,
                    reason = "asset.deleted"
                })
            });
        }
        var cleanupAvailableAt = now.AddMinutes(policyOptions.Value.DeletionFenceMinutes);
        var cleanupJob = new Job
        {
            WorkspaceId = context.Workspace.Id,
            ProjectId = projectId,
            AssetId = asset.Id,
            Type = JobType.AssetCleanup,
            RequiredCapability = JobRoutingRegistry.GetRequiredCapability(JobType.AssetCleanup),
            HandlerVersion = "v1",
            PayloadSchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new
            {
                projectId,
                assetId = asset.Id,
                notBefore = cleanupAvailableAt
            }),
            IdempotencyKey = $"asset-cleanup:{asset.Id:N}",
            State = JobState.Queued,
            AvailableAt = cleanupAvailableAt
        };
        dbContext.Jobs.Add(cleanupJob);
        dbContext.JobEvents.Add(new JobEvent
        {
            JobId = cleanupJob.Id,
            EventType = "queued",
            DataJson = "{\"requiredCapability\":\"control\"}"
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.Accepted(
            $"/api/v1/jobs/{cleanupJob.Id}",
            new AssetDeletionResponse(asset.Id, cleanupJob.Id));
    }

    private static async Task<IResult> ReorderAssets(
        Guid projectId,
        ReorderAssetsRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, true, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expectedVersion, project.Version);

        var visuals = project.Assets
            .Where(value => value.Kind == AssetKind.Visual && value.IsActive)
            .ToList();
        if (request.AssetIds.Count != visuals.Count ||
            request.AssetIds.Distinct().Count() != visuals.Count ||
            request.AssetIds.Any(id => visuals.All(value => value.Id != id)))
        {
            throw new ApiProblemException(
                StatusCodes.Status422UnprocessableEntity,
                "assets.order_invalid",
                "The order must include every active visual asset exactly once.");
        }

        for (var index = 0; index < request.AssetIds.Count; index++)
        {
            visuals.Single(value => value.Id == request.AssetIds[index]).SortOrder = index;
        }

        dbContext.Entry(project).Property(value => value.Version).IsModified = true;
        await dbContext.SaveChangesAsync(cancellationToken);
        ApiEndpointHelpers.SetEtag(response, project.Version);
        return Results.Ok(project.Assets
            .Where(value => value.Kind == AssetKind.Visual && value.IsActive)
            .OrderBy(value => value.SortOrder)
            .Select(ToResponse));
    }

    private static async Task<IResult> GetJob(
        Guid jobId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var job = await dbContext.Jobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.Id == jobId && value.WorkspaceId == context.Workspace.Id,
                cancellationToken)
            ?? throw NotFound();
        ApiEndpointHelpers.SetEtag(response, job.Version);
        return Results.Ok(ToResponse(job));
    }

    private static async Task StreamJobEvents(
        Guid jobId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var job = await dbContext.Jobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.Id == jobId && value.WorkspaceId == context.Workspace.Id,
                cancellationToken)
            ?? throw NotFound();

        long afterSequence = 0;
        var lastEventId = httpContext.Request.Headers["Last-Event-ID"].FirstOrDefault()
            ?? httpContext.Request.Query["after"].FirstOrDefault();
        _ = long.TryParse(lastEventId, out afterSequence);

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";

        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await dbContext.JobEvents
                .AsNoTracking()
                .Where(value => value.JobId == jobId && value.Sequence > afterSequence)
                .OrderBy(value => value.Sequence)
                .Take(100)
                .ToListAsync(cancellationToken);

            foreach (var jobEvent in events)
            {
                await httpContext.Response.WriteAsync($"id: {jobEvent.Sequence}\n", cancellationToken);
                await httpContext.Response.WriteAsync($"event: {jobEvent.EventType}\n", cancellationToken);
                await httpContext.Response.WriteAsync($"data: {jobEvent.DataJson}\n\n", cancellationToken);
                afterSequence = jobEvent.Sequence;
            }

            if (events.Count > 0)
            {
                await httpContext.Response.Body.FlushAsync(cancellationToken);
            }

            var state = await dbContext.Jobs
                .AsNoTracking()
                .Where(value => value.Id == jobId)
                .Select(value => value.State)
                .SingleAsync(cancellationToken);
            if ((state is JobState.Succeeded or JobState.Failed or JobState.Cancelled) &&
                events.Count == 0)
            {
                break;
            }

            if (events.Count == 0)
            {
                await httpContext.Response.WriteAsync(": keepalive\n\n", cancellationToken);
                await httpContext.Response.Body.FlushAsync(cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static async Task<ReleaseProject> FindProject(
        Hook2StreamDbContext dbContext,
        Guid workspaceId,
        Guid projectId,
        bool includeAssets,
        CancellationToken cancellationToken)
    {
        IQueryable<ReleaseProject> query = dbContext.Projects;
        if (includeAssets)
        {
            query = query.Include(value => value.Assets);
        }

        return await query.SingleOrDefaultAsync(
                   value => value.Id == projectId && value.WorkspaceId == workspaceId,
                   cancellationToken)
               ?? throw NotFound();
    }

    private static async Task<UploadSession> FindUpload(
        Hook2StreamDbContext dbContext,
        Guid workspaceId,
        Guid sessionId,
        CancellationToken cancellationToken) =>
        await dbContext.UploadSessions
            .Include(value => value.Asset)
            .SingleOrDefaultAsync(
                value => value.Id == sessionId && value.WorkspaceId == workspaceId,
            cancellationToken)
        ?? throw NotFound();

    private static async Task RejectExpiredUploadAsync(
        UploadSession session,
        Hook2StreamDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if ((session.State is UploadState.Initiated or UploadState.Uploading) && session.ExpiresAt <= now)
        {
            session.State = UploadState.Expired;
            session.Asset.State = AssetState.Rejected;
            session.Asset.IsActive = false;
            session.Asset.FailureCode = "upload.session_expired";
            session.Asset.FailureMessage = "The upload session expired before it was completed.";
            dbContext.ProjectEvents.Add(new ProjectEvent
            {
                WorkspaceId = session.WorkspaceId,
                ProjectId = session.ProjectId,
                EventType = "upload.expired",
                DataJson = JsonSerializer.Serialize(new
                {
                    projectId = session.ProjectId,
                    uploadSessionId = session.Id,
                    assetId = session.AssetId
                })
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (session.State == UploadState.Expired)
        {
            throw new ApiProblemException(
                StatusCodes.Status410Gone,
                "upload.session_expired",
                "This upload session has expired. Start a new upload.");
        }
    }

    private static TimeSpan GetUploadUrlLifetime(
        DateTimeOffset now,
        DateTimeOffset sessionExpiresAt,
        OperationalPolicyOptions options)
    {
        var remaining = sessionExpiresAt - now;
        var configured = TimeSpan.FromMinutes(options.UploadUrlMinutes);
        return remaining < configured ? remaining : configured;
    }

    private static ApiProblemException NotFound() =>
        new(
            StatusCodes.Status404NotFound,
            "resource.not_found",
            "The requested resource was not found.");

    private static DateOnly UtcToday() => DateOnly.FromDateTime(DateTime.UtcNow);

    private static BrandKitResponse ToResponse(BrandKit value) =>
        new(
            value.Id,
            value.DisplayName,
            value.PrimaryColor,
            value.SecondaryColor,
            value.AccentColor,
            value.HeadingFont,
            value.BodyFont,
            value.DefaultCta,
            value.SmartLink,
            value.ToneRestrictions,
            value.CharacterLayerEnabled,
            value.Version);

    private static ReleaseResponse ToResponse(ReleaseProject value, IEnumerable<MediaAsset> assets) =>
        new(
            value.Id,
            value.ProjectLabel,
            value.ArtistName,
            value.TrackTitle,
            value.Language,
            value.InternalNotes,
            value.LyricsText,
            value.IsInstrumental,
            value.IsInstrumentalConfirmed,
            value.Mode,
            value.ReleaseDate,
            value.CampaignStartDate,
            value.State,
            value.IsArchived,
            value.Version,
            value.CreatedAt,
            assets.OrderBy(asset => asset.Kind).ThenBy(asset => asset.SortOrder).Select(ToResponse).ToList());

    private static RightsAttestationResponse ToResponse(RightsAttestation value, long projectVersion) =>
        new(
            value.Id,
            value.OwnsAudioRights,
            value.OwnsLyricsRights,
            value.OwnsVisualRights,
            value.AllowsExternalAiArtwork,
            value.AllowsExternalAiProcessing,
            value.SyntheticContentStatus,
            value.PolicyVersion,
            value.AcceptedAt,
            value.AudioAssetId,
            value.AudioFingerprint,
            projectVersion);

    private static AssetResponse ToResponse(MediaAsset value) =>
        new(
            value.Id,
            value.Kind,
            value.Origin,
            value.Purpose,
            value.State,
            value.OriginalFileName,
            value.DeclaredContentType,
            value.DeclaredBytes,
            value.ActualBytes,
            value.Revision,
            value.SortOrder,
            value.IsActive,
            value.FailureCode,
            value.FailureMessage,
            value.DurationMilliseconds,
            value.Width,
            value.Height,
            value.Version);

    private static JobResponse ToResponse(Job value) =>
        new(
            value.Id,
            value.Type,
            value.State,
            value.ProgressPercent,
            value.ProgressStage,
            value.ErrorCode,
            value.ErrorMessage,
            value.AttemptCount,
            value.CreatedAt,
            value.CompletedAt,
            value.Version);
}
