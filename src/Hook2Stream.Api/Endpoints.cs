using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Api;

public static class Endpoints
{
    private static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromMinutes(60);

    public static IEndpointRouteBuilder MapHook2StreamApi(this IEndpointRouteBuilder endpoints)
    {
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
            .Produces(StatusCodes.Status204NoContent);
        releases.MapGet("/{projectId:guid}/readiness", GetReadiness)
            .Produces<ReadinessResponse>();
        releases.MapPut("/{projectId:guid}/rights", UpdateRights)
            .Produces<RightsAttestationResponse>();

        releases.MapGet("/{projectId:guid}/assets", ListAssets)
            .Produces<IReadOnlyList<AssetResponse>>();
        releases.MapPost("/{projectId:guid}/uploads", CreateUpload)
            .Produces<UploadSessionResponse>(StatusCodes.Status201Created);
        releases.MapPut("/{projectId:guid}/assets/reorder", ReorderAssets)
            .Produces<IReadOnlyList<AssetResponse>>();
        releases.MapDelete("/{projectId:guid}/assets/{assetId:guid}", DeleteAsset)
            .Produces(StatusCodes.Status204NoContent);

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
            user.ClerkSubject,
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
                user.ClerkSubject,
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
            ActorSubject = user.ClerkSubject,
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
                user.ClerkSubject,
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
        CancellationToken cancellationToken)
    {
        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(request);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, false, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expectedVersion, project.Version);
        project.Archive();
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
        CancellationToken cancellationToken)
    {
        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(request);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, false, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expectedVersion, project.Version);
        project.Restore();
        await dbContext.SaveChangesAsync(cancellationToken);
        ApiEndpointHelpers.SetEtag(response, project.Version);
        return Results.Ok(ToResponse(project, Array.Empty<MediaAsset>()));
    }

    private static async Task<IResult> DeleteRelease(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(request);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, true, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expectedVersion, project.Version);

        var now = DateTimeOffset.UtcNow;
        project.DeletedAt = now;
        foreach (var asset in project.Assets)
        {
            asset.DeletedAt = now;
            asset.State = AssetState.Deleted;
            asset.IsActive = false;
        }

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
        return Results.NoContent();
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
        if (string.IsNullOrWhiteSpace(request.PolicyVersion) || request.PolicyVersion.Length > 64)
        {
            var errors = new ValidationErrors();
            errors.Add("policyVersion", "Policy version is required.");
            ApiEndpointHelpers.RequireValid(errors);
        }

        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var project = await FindProject(dbContext, context.Workspace.Id, projectId, false, cancellationToken);
        ApiEndpointHelpers.EnsureVersion(expectedVersion, project.Version);

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
        attestation.PolicyVersion = request.PolicyVersion;
        attestation.OwnsAudioRights = request.OwnsAudioRights;
        attestation.OwnsLyricsRights = request.OwnsLyricsRights;
        attestation.OwnsVisualRights = request.OwnsVisualRights;
        attestation.SyntheticContentStatus = request.SyntheticContentStatus;
        attestation.AcceptedAt = DateTimeOffset.UtcNow;
        dbContext.Entry(project).Property(value => value.Version).IsModified = true;
        await dbContext.SaveChangesAsync(cancellationToken);

        ApiEndpointHelpers.SetEtag(response, project.Version);
        return Results.Ok(new RightsAttestationResponse(
            attestation.Id,
            attestation.OwnsAudioRights,
            attestation.OwnsLyricsRights,
            attestation.OwnsVisualRights,
            attestation.SyntheticContentStatus,
            attestation.PolicyVersion,
            attestation.AcceptedAt,
            project.Version));
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
                UploadUrlLifetime,
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
            ExpiresAt = DateTimeOffset.UtcNow.Add(UploadUrlLifetime)
        };
        dbContext.UploadSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

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
                session.ExpiresAt));
    }

    private static async Task<IResult> ResumeUpload(
        Guid sessionId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        IObjectStorage objectStorage,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var session = await FindUpload(dbContext, context.Workspace.Id, sessionId, cancellationToken);

        if (session.State is UploadState.Completed or UploadState.Aborted or UploadState.Expired)
        {
            throw new ApiProblemException(
                StatusCodes.Status409Conflict,
                "upload.not_resumable",
                "This upload session cannot be resumed.");
        }

        session.ExpiresAt = DateTimeOffset.UtcNow.Add(UploadUrlLifetime);
        Uri? uploadUrl = null;
        if (!session.IsMultipart)
        {
            uploadUrl = await objectStorage.CreateUploadUrlAsync(
                session.ObjectKey,
                session.Asset.DeclaredContentType,
                UploadUrlLifetime,
                cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
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
            session.ExpiresAt));
    }

    private static async Task<IResult> SignUploadPart(
        Guid sessionId,
        UploadPartRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        IObjectStorage objectStorage,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var session = await FindUpload(dbContext, context.Workspace.Id, sessionId, cancellationToken);
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

        var expiresAt = DateTimeOffset.UtcNow.Add(UploadUrlLifetime);
        var url = await objectStorage.CreateMultipartPartUploadUrlAsync(
            session.ObjectKey,
            session.MultipartUploadId,
            request.PartNumber,
            UploadUrlLifetime,
            cancellationToken);
        session.State = UploadState.Uploading;
        session.ExpiresAt = expiresAt;
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
        session.CompletedAt = DateTimeOffset.UtcNow;
        session.Asset.State = AssetState.Uploaded;
        await dbContext.SaveChangesAsync(cancellationToken);

        var payload = JsonSerializer.Serialize(new { assetId = session.AssetId });
        var jobId = await jobQueue.EnqueueAsync(
            session.WorkspaceId,
            session.ProjectId,
            session.AssetId,
            JobType.MediaIngest,
            payload,
            $"media-ingest:{session.AssetId:N}:r{session.Asset.Revision}",
            cancellationToken);
        return Results.Accepted(
            $"/api/v1/jobs/{jobId}",
            new CompleteUploadResponse(session.AssetId, jobId));
    }

    private static async Task<IResult> AbortUpload(
        Guid sessionId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        IObjectStorage objectStorage,
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

        if (session.IsMultipart && !string.IsNullOrWhiteSpace(session.MultipartUploadId))
        {
            await objectStorage.AbortMultipartUploadAsync(
                session.ObjectKey,
                session.MultipartUploadId,
                cancellationToken);
        }
        else
        {
            await objectStorage.DeleteAsync(session.ObjectKey, cancellationToken);
        }

        session.State = UploadState.Aborted;
        session.AbortedAt = DateTimeOffset.UtcNow;
        session.Asset.State = AssetState.Rejected;
        session.Asset.FailureCode = "upload.aborted";
        session.Asset.FailureMessage = "Upload cancelled by the user.";
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsset(
        Guid projectId,
        Guid assetId,
        CurrentUserService currentUser,
        Hook2StreamDbContext dbContext,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var expectedVersion = ApiEndpointHelpers.RequireIfMatch(request);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        _ = await FindProject(dbContext, context.Workspace.Id, projectId, false, cancellationToken);
        var asset = await dbContext.MediaAssets
            .SingleOrDefaultAsync(
                value => value.ProjectId == projectId && value.Id == assetId,
                cancellationToken)
            ?? throw NotFound();
        ApiEndpointHelpers.EnsureVersion(expectedVersion, asset.Version);

        asset.State = AssetState.Deleted;
        asset.IsActive = false;
        asset.DeletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
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
            value.Mode,
            value.ReleaseDate,
            value.CampaignStartDate,
            value.State,
            value.IsArchived,
            value.Version,
            value.CreatedAt,
            assets.OrderBy(asset => asset.Kind).ThenBy(asset => asset.SortOrder).Select(ToResponse).ToList());

    private static AssetResponse ToResponse(MediaAsset value) =>
        new(
            value.Id,
            value.Kind,
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
