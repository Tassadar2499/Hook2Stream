using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Billing;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Api;

public static class BillingEndpoints
{
    private const int WebhookMaxBytes = 1024 * 1024;
    private static readonly TimeSpan DownloadUrlLifetime = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapBillingApi(
        this IEndpointRouteBuilder endpoints,
        RouteGroupBuilder authenticatedApi)
    {
        authenticatedApi.MapGet("/billing/summary", GetSummary)
            .Produces<BillingSummaryResponse>();
        authenticatedApi.MapPost("/billing/checkouts", CreateCheckout)
            .Produces<CheckoutResponse>(StatusCodes.Status201Created);
        authenticatedApi.MapPost("/releases/{projectId:guid}/renders", StartRender)
            .Produces<RenderBatchResponse>(StatusCodes.Status202Accepted);
        authenticatedApi.MapGet("/releases/{projectId:guid}/renders/{batchId:guid}", GetRenderBatch)
            .Produces<RenderBatchStatusResponse>();
        authenticatedApi.MapGet("/releases/{projectId:guid}/artwork/clean-cover/download-url", GetCleanCoverDownload)
            .Produces<DownloadGrantResponse>();
        endpoints.MapPost("/api/v1/billing/stripe/webhook", HandleStripeWebhook)
            .AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> GetSummary(
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var wallet = await db.WorkspaceArtworkCredits.AsNoTracking()
            .SingleOrDefaultAsync(value => value.WorkspaceId == context.Workspace.Id, cancellationToken);
        var entitlements = await db.Entitlements.AsNoTracking()
            .Where(value => value.WorkspaceId == context.Workspace.Id)
            .OrderByDescending(value => value.CreatedAt)
            .ToListAsync(cancellationToken);
        var responses = entitlements.Select(value => new EntitlementResponse(
            value.Id,
            value.ProductCode,
            value.ProjectId,
            EntitlementStatus(value, now),
            value.IncludedItemCount,
            Deserialize<IReadOnlyList<Guid>>(value.ItemIdsJson) ?? [],
            value.RemainingContentRerenders,
            value.ValidUntil)).ToList();
        var subscription = entitlements.FirstOrDefault(value =>
            value.ProductCode == BillingProducts.ActiveArtist && IsActive(value, now))?.ProductCode;
        return Results.Ok(new BillingSummaryResponse(wallet?.Balance ?? 0, subscription, responses));
    }

    private static async Task<IResult> CreateCheckout(
        CreateCheckoutRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        IPaymentGateway paymentGateway,
        IOptions<StripeOptions> stripeOptions,
        TimeProvider timeProvider,
        HttpRequest httpRequest,
        CancellationToken cancellationToken)
    {
        if (!BillingProducts.All.Contains(request.ProductCode))
            throw Problem(422, "billing.product_invalid", "Choose a supported billing product.");
        ValidateReturnPath(request.ReturnPath);
        var key = RequireIdempotencyKey(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var requestedItemIds = request.ItemIds?.Order().ToArray() ?? [];
        var requestHash = Hash($"{request.ProductCode}\n{request.ProjectId:N}\n{string.Join(',', requestedItemIds)}\n{request.ReturnPath}");
        var existing = await db.BillingCheckouts.SingleOrDefaultAsync(
            value => value.WorkspaceId == context.Workspace.Id && value.IdempotencyKey == key,
            cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                throw Problem(409, "idempotency.payload_mismatch", "This idempotency key was used with a different checkout.");
            if (!string.IsNullOrWhiteSpace(existing.CheckoutUrl))
                return Results.Ok(ToCheckout(existing));
        }

        IReadOnlyList<Guid> itemIds;
        Guid? artworkPackRevisionId = null;
        string? artworkCompositionHash = null;
        Guid? campaignPlanRevisionId = null;
        string? artistNameSnapshot = null;
        string? trackTitleSnapshot = null;
        DateOnly? scheduleAnchorSnapshot = null;
        ReleaseMode? releaseModeSnapshot = null;
        Guid? audioAssetIdSnapshot = null;
        string? audioFingerprintSnapshot = null;
        if (existing is null)
        {
            itemIds = await ValidateAndNormalizeCheckoutItems(
                db, context.Workspace.Id, request.ProductCode, request.ProjectId, request.ItemIds, cancellationToken);
            if (request.ProductCode == BillingProducts.CleanCover && request.ProjectId is { } coverProjectId)
            {
                var coverProject = await db.Projects.AsNoTracking()
                    .Where(value => value.Id == coverProjectId && value.WorkspaceId == context.Workspace.Id)
                    .Select(value => new
                    {
                        value.CurrentArtworkPackRevisionId,
                        value.ArtistName,
                        value.TrackTitle,
                        value.ReleaseDate,
                        value.CampaignStartDate,
                        value.Mode,
                        value.CreatedAt
                    })
                    .SingleAsync(cancellationToken);
                var pack = await db.ArtworkPackRevisions.AsNoTracking().SingleAsync(
                    value => value.Id == coverProject.CurrentArtworkPackRevisionId && value.SelectedAssetId == itemIds.Single(),
                    cancellationToken);
                artworkPackRevisionId = pack.Id;
                artworkCompositionHash = Hash(pack.CompositionJson);
                artistNameSnapshot = coverProject.ArtistName;
                trackTitleSnapshot = coverProject.TrackTitle;
                scheduleAnchorSnapshot = ScheduleAnchor(
                    coverProject.Mode,
                    coverProject.ReleaseDate,
                    coverProject.CampaignStartDate,
                    coverProject.CreatedAt);
                releaseModeSnapshot = coverProject.Mode;
            }
            else if (BillingProducts.IncludedVideoCount(request.ProductCode) > 0 &&
                     request.ProductCode != BillingProducts.ActiveArtist &&
                     request.ProjectId is { } videoProjectId)
            {
                var videoProject = await db.Projects.AsNoTracking()
                    .Where(value => value.Id == videoProjectId && value.WorkspaceId == context.Workspace.Id)
                    .Select(value => new
                    {
                        value.ArtistName,
                        value.TrackTitle,
                        value.ReleaseDate,
                        value.CampaignStartDate,
                        value.Mode,
                        value.CreatedAt
                    })
                    .SingleAsync(cancellationToken);
                var revisions = await db.CampaignPlanRevisions.AsNoTracking()
                    .Where(value => value.ProjectId == videoProjectId && value.WorkspaceId == context.Workspace.Id)
                    .OrderByDescending(value => value.CreatedAt)
                    .ToListAsync(cancellationToken);
                campaignPlanRevisionId = revisions.FirstOrDefault(value =>
                {
                    var revisionItems = Deserialize<List<CampaignItemRequest>>(value.ItemsJson) ?? [];
                    return revisionItems.Count == 18 && itemIds.All(id => revisionItems.Any(item => item.Id == id));
                })?.Id ?? throw Problem(
                    409,
                    "campaign.snapshot_unavailable",
                    "The selected campaign revision changed before checkout. Reload and try again.");
                artistNameSnapshot = videoProject.ArtistName;
                trackTitleSnapshot = videoProject.TrackTitle;
                scheduleAnchorSnapshot = ScheduleAnchor(
                    videoProject.Mode,
                    videoProject.ReleaseDate,
                    videoProject.CampaignStartDate,
                    videoProject.CreatedAt);
                releaseModeSnapshot = videoProject.Mode;
                var audioSnapshot = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(
                    value => value.ProjectId == videoProjectId &&
                             value.WorkspaceId == context.Workspace.Id &&
                             value.Kind == AssetKind.Audio &&
                             value.IsActive &&
                             value.State == AssetState.Ready &&
                             value.Sha256 != null,
                    cancellationToken) ?? throw Problem(
                    409,
                    "render.audio_not_ready",
                    "A ready audio master is required before checkout.");
                audioAssetIdSnapshot = audioSnapshot.Id;
                audioFingerprintSnapshot = audioSnapshot.Sha256;
            }
        }
        else
        {
            itemIds = Deserialize<List<Guid>>(existing.ItemIdsJson) ?? [];
            artworkPackRevisionId = existing.ArtworkPackRevisionId;
            artworkCompositionHash = existing.ArtworkCompositionHash;
            campaignPlanRevisionId = existing.CampaignPlanRevisionId;
            artistNameSnapshot = existing.ArtistNameSnapshot;
            trackTitleSnapshot = existing.TrackTitleSnapshot;
            scheduleAnchorSnapshot = existing.ScheduleAnchorSnapshot;
            releaseModeSnapshot = existing.ReleaseModeSnapshot;
            audioAssetIdSnapshot = existing.AudioAssetIdSnapshot;
            audioFingerprintSnapshot = existing.AudioFingerprintSnapshot;
        }

        var checkout = existing ?? new BillingCheckout
        {
            WorkspaceId = context.Workspace.Id,
            ProjectId = request.ProjectId,
            ProductCode = request.ProductCode,
            AmountCents = BillingProducts.AmountCents(request.ProductCode),
            ItemIdsJson = JsonSerializer.Serialize(itemIds, StoredJson),
            ArtworkPackRevisionId = artworkPackRevisionId,
            ArtworkCompositionHash = artworkCompositionHash,
            CampaignPlanRevisionId = campaignPlanRevisionId,
            ArtistNameSnapshot = artistNameSnapshot,
            TrackTitleSnapshot = trackTitleSnapshot,
            ScheduleAnchorSnapshot = scheduleAnchorSnapshot,
            ReleaseModeSnapshot = releaseModeSnapshot,
            AudioAssetIdSnapshot = audioAssetIdSnapshot,
            AudioFingerprintSnapshot = audioFingerprintSnapshot,
            IdempotencyKey = key,
            RequestHash = requestHash
        };
        if (existing is null)
        {
            db.BillingCheckouts.Add(checkout);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                var concurrent = await db.BillingCheckouts.SingleOrDefaultAsync(
                    value => value.WorkspaceId == context.Workspace.Id && value.IdempotencyKey == key,
                    cancellationToken);
                if (concurrent is null || concurrent.RequestHash != requestHash) throw;
                checkout = concurrent;
                itemIds = Deserialize<List<Guid>>(checkout.ItemIdsJson) ?? [];
                if (!string.IsNullOrWhiteSpace(checkout.CheckoutUrl)) return Results.Ok(ToCheckout(checkout));
            }
        }

        var baseUrl = new Uri(stripeOptions.Value.PublicWebBaseUrl.TrimEnd('/') + "/");
        var returnUri = new Uri(baseUrl, request.ReturnPath.TrimStart('/'));
        var separator = returnUri.Query.Length == 0 ? '?' : '&';
        var successUrl = $"{returnUri}{separator}billing=success";
        var cancelUrl = $"{returnUri}{separator}billing=cancelled";
        PaymentCheckoutResult payment;
        try
        {
            payment = await paymentGateway.CreateCheckoutAsync(new PaymentCheckoutCommand(
                checkout.Id,
                checkout.WorkspaceId,
                checkout.ProductCode,
                checkout.ProjectId,
                itemIds,
                context.User.Email ?? $"{context.User.ExternalSubject}@users.invalid",
                successUrl,
                cancelUrl,
                $"checkout:{checkout.Id:N}"), cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw Problem(502, "billing.provider_unavailable", "The payment provider is temporarily unavailable.");
        }

        checkout.ExternalSessionId = payment.ExternalSessionId;
        checkout.CheckoutUrl = payment.CheckoutUrl.ToString();
        if (payment.CompletedSynchronously)
            await FulfillCheckout(
                db,
                checkout,
                new FulfillmentContext(
                    $"fixture:{checkout.Id:N}",
                    null,
                    null,
                    null,
                    null,
                    timeProvider.GetUtcNow(),
                    BillingProducts.IsSubscription(checkout.ProductCode) ? timeProvider.GetUtcNow().AddMonths(1) : null),
                timeProvider,
                cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/billing/checkouts/{checkout.Id}", ToCheckout(checkout));
    }

    private static async Task<IResult> HandleStripeWebhook(
        HttpRequest request,
        Hook2StreamDbContext db,
        IPaymentGateway paymentGateway,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > WebhookMaxBytes)
            throw Problem(413, "billing.webhook_too_large", "The webhook payload is too large.");
        await using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > WebhookMaxBytes)
            throw Problem(413, "billing.webhook_too_large", "The webhook payload is too large.");
        var payload = buffer.ToArray();
        PaymentWebhookEvent paymentEvent;
        try
        {
            paymentEvent = paymentGateway.ParseAndVerifyWebhook(
                payload,
                request.Headers["Stripe-Signature"].ToString(),
                timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or InvalidOperationException)
        {
            throw Problem(400, "billing.webhook_invalid", "The payment webhook could not be verified.");
        }

        var existing = await db.InboxMessages.SingleOrDefaultAsync(
            value => value.Source == "stripe" && value.MessageId == paymentEvent.EventId,
            cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.PayloadHash, paymentEvent.PayloadHash, StringComparison.Ordinal))
                throw Problem(409, "billing.webhook_conflict", "A different payload used this provider event ID.");
            return Results.Ok(new { received = true, duplicate = true });
        }

        var inbox = new InboxMessage
        {
            Source = "stripe",
            MessageId = paymentEvent.EventId,
            PayloadHash = paymentEvent.PayloadHash,
            State = "processing"
        };
        db.InboxMessages.Add(inbox);
        var checkout = await ResolveCheckout(db, paymentEvent, cancellationToken);
        if (checkout is null && (paymentEvent.Paid || paymentEvent.Refunded))
            throw Problem(409, "billing.webhook_unresolved", "The payment event is not yet linked to a checkout; retry it later.");
        if (checkout is null)
        {
            inbox.State = "ignored";
            inbox.ProcessedAt = timeProvider.GetUtcNow();
            inbox.LastError = "billing.event_not_actionable";
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { received = true, ignored = true });
        }
        if (paymentEvent.WorkspaceId is { } eventWorkspaceId && checkout.WorkspaceId != eventWorkspaceId ||
            paymentEvent.ProductCode is { } eventProductCode && checkout.ProductCode != eventProductCode ||
            paymentEvent.ProjectId is { } eventProjectId && checkout.ProjectId != eventProjectId)
            throw Problem(409, "billing.webhook_metadata_mismatch", "The payment metadata does not match its checkout.");

        checkout.ExternalSessionId ??= paymentEvent.ExternalSessionId;
        checkout.ExternalCustomerId ??= paymentEvent.ExternalCustomerId;
        checkout.ExternalSubscriptionId ??= paymentEvent.ExternalSubscriptionId;
        checkout.ExternalPaymentIntentId ??= paymentEvent.ExternalPaymentIntentId;
        var checkoutFailed = paymentEvent.Type is
            "checkout.session.expired" or
            "checkout.session.async_payment_failed";
        if (checkoutFailed && checkout.State == CheckoutState.Pending)
            checkout.State = CheckoutState.Failed;
        else if (paymentEvent.Refunded)
            await RevokeCheckout(db, checkout, paymentEvent, cancellationToken);
        else if (paymentEvent.Paid &&
                 checkout.State != CheckoutState.Failed &&
                 !(checkout.ProductCode == BillingProducts.ActiveArtist &&
                   paymentEvent.Type.StartsWith("checkout.session.", StringComparison.Ordinal)))
            await FulfillCheckout(
                db,
                checkout,
                new FulfillmentContext(
                    checkout.ProductCode == BillingProducts.ActiveArtist
                        ? $"invoice:{paymentEvent.ExternalInvoiceId ?? paymentEvent.EventId}"
                        : "purchase",
                    paymentEvent.ExternalCustomerId,
                    paymentEvent.ExternalSubscriptionId,
                    paymentEvent.ExternalPaymentIntentId,
                    paymentEvent.ExternalInvoiceId,
                    paymentEvent.PeriodStartsAt ?? paymentEvent.OccurredAt,
                    paymentEvent.PeriodEndsAt),
                timeProvider,
                cancellationToken);

        inbox.State = "processed";
        inbox.ProcessedAt = timeProvider.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var duplicate = await db.InboxMessages.AsNoTracking().SingleOrDefaultAsync(
                value => value.Source == "stripe" && value.MessageId == paymentEvent.EventId,
                cancellationToken);
            if (duplicate is null || duplicate.PayloadHash != paymentEvent.PayloadHash) throw;
            return Results.Ok(new { received = true, duplicate = true });
        }
        return Results.Ok(new { received = true });
    }

    private static async Task<IResult> StartRender(
        Guid projectId,
        StartRenderRequest request,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        TimeProvider timeProvider,
        HttpRequest httpRequest,
        CancellationToken cancellationToken)
    {
        var key = RequireIdempotencyKey(httpRequest);
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var requestedIds = request.ItemIds?.Order().ToArray() ?? [];
        var requestHash = Hash($"{projectId:N}\n{request.EntitlementId:N}\n{request.Kind}\n{string.Join(',', requestedIds)}");
        var existing = await db.RenderBatches.AsNoTracking().SingleOrDefaultAsync(
            value => value.WorkspaceId == context.Workspace.Id && value.IdempotencyKey == key,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.RequestHash != requestHash)
                throw Problem(409, "idempotency.payload_mismatch", "This idempotency key was used with a different render request.");
            return Results.Accepted(
                $"/api/v1/releases/{existing.ProjectId}/renders/{existing.Id}",
                ToRenderBatch(existing));
        }
        var project = await db.Projects.SingleOrDefaultAsync(
            value => value.Id == projectId && value.WorkspaceId == context.Workspace.Id,
            cancellationToken) ?? throw NotFound();
        var entitlement = await db.Entitlements.SingleOrDefaultAsync(
            value => value.Id == request.EntitlementId &&
                     value.WorkspaceId == context.Workspace.Id &&
                     (value.ProjectId == projectId ||
                      value.ProductCode == BillingProducts.ActiveArtist && value.ProjectId == null),
            cancellationToken) ?? throw NotFound();
        if (!IsActive(entitlement, timeProvider.GetUtcNow()) || BillingProducts.IncludedVideoCount(entitlement.ProductCode) == 0)
            throw Problem(402, "render.entitlement_required", "An active video entitlement is required.");
        if (request.ItemIds is null || request.ItemIds.Count == 0 || request.ItemIds.Distinct().Count() != request.ItemIds.Count)
            throw Problem(422, "render.items_invalid", "Choose one or more distinct campaign items.");
        var entitledItems = Deserialize<List<Guid>>(entitlement.ItemIdsJson) ?? [];
        CampaignPlanRevision? currentCampaign = null;
        List<CampaignItemRequest> currentCampaignItems = [];
        if (project.CurrentCampaignPlanRevisionId is { } currentCampaignId)
        {
            currentCampaign = await db.CampaignPlanRevisions.AsNoTracking().SingleOrDefaultAsync(
                value => value.Id == currentCampaignId && value.ProjectId == project.Id,
                cancellationToken);
            currentCampaignItems = Deserialize<List<CampaignItemRequest>>(currentCampaign?.ItemsJson ?? "[]") ?? [];
        }
        if (entitlement.ProductCode == BillingProducts.ActiveArtist && entitlement.ProjectId is null)
        {
            if (request.Kind != RenderRequestKind.Initial || currentCampaign is null || currentCampaignItems.Count != 18 ||
                request.ItemIds.Count != 18 || request.ItemIds.Any(value => currentCampaignItems.All(item => item.Id != value)))
                throw Problem(422, "render.subscription_binding_invalid", "Bind the period entitlement by starting all 18 items of one current campaign.");
            entitlement.ProjectId = project.Id;
            entitlement.CampaignPlanRevisionId = currentCampaign.Id;
            entitlement.ArtistNameSnapshot = project.ArtistName;
            entitlement.TrackTitleSnapshot = project.TrackTitle;
            entitlement.ScheduleAnchorSnapshot = ScheduleAnchor(
                project.Mode,
                project.ReleaseDate,
                project.CampaignStartDate,
                project.CreatedAt);
            entitlement.ReleaseModeSnapshot = project.Mode;
            var subscriptionAudio = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(
                value => value.ProjectId == project.Id &&
                         value.WorkspaceId == context.Workspace.Id &&
                         value.Kind == AssetKind.Audio &&
                         value.IsActive &&
                         value.State == AssetState.Ready &&
                         value.Sha256 != null,
                cancellationToken) ?? throw Problem(
                409,
                "render.audio_not_ready",
                "A ready active audio master is required before binding the subscription.");
            entitlement.AudioAssetIdSnapshot = subscriptionAudio.Id;
            entitlement.AudioFingerprintSnapshot = subscriptionAudio.Sha256;
            entitledItems = currentCampaignItems.Select(value => value.Id).ToList();
            entitlement.ItemIdsJson = JsonSerializer.Serialize(entitledItems, StoredJson);
        }
        if (request.ItemIds.Any(value => !entitledItems.Contains(value)))
            throw Problem(403, "render.item_not_entitled", "The entitlement does not include every requested campaign item.");
        if (request.Kind == RenderRequestKind.Initial &&
            request.ItemIds.Count != entitledItems.Count)
            throw Problem(422, "render.initial_set_incomplete", "The initial paid render must include the entitlement's complete item set.");
        var retrySources = request.Kind == RenderRequestKind.TechnicalRetry
            ? await FindTechnicalRetrySources(db, entitlement.Id, project.Id, request.ItemIds, cancellationToken)
            : new Dictionary<Guid, TechnicalRetrySource>();

        CampaignPlanRevision? campaign = null;
        List<CampaignItemRequest> campaignItems = [];
        if (request.Kind == RenderRequestKind.TechnicalRetry)
        {
            var retryCampaignIds = retrySources.Values.Select(value => value.CampaignRevisionId).Distinct().ToArray();
            if (retryCampaignIds.Length != 1)
                throw Problem(409, "render.retry_snapshot_mismatch", "Retry the items from one immutable campaign batch at a time.");
            campaign = await db.CampaignPlanRevisions.AsNoTracking().SingleOrDefaultAsync(
                value => value.Id == retryCampaignIds[0] && value.ProjectId == project.Id,
                cancellationToken);
            campaignItems = Deserialize<List<CampaignItemRequest>>(campaign?.ItemsJson ?? "[]") ?? [];
        }
        else if (request.Kind == RenderRequestKind.ContentChange &&
                 currentCampaign is not null &&
                 request.ItemIds.All(id => currentCampaignItems.Any(item => item.Id == id)))
        {
            // Preserve the paid item identity while allowing the included
            // content rerender to use the user's latest revision of those items.
            campaign = currentCampaign;
            campaignItems = currentCampaignItems;
        }
        else if (entitlement.CampaignPlanRevisionId is { } purchasedCampaignId)
        {
            campaign = await db.CampaignPlanRevisions.AsNoTracking().SingleOrDefaultAsync(
                value => value.Id == purchasedCampaignId && value.ProjectId == project.Id,
                cancellationToken);
            campaignItems = Deserialize<List<CampaignItemRequest>>(campaign?.ItemsJson ?? "[]") ?? [];
        }
        else if (currentCampaign is not null &&
                 request.ItemIds.All(id => currentCampaignItems.Any(item => item.Id == id)))
        {
            // Compatibility path for entitlements created before immutable
            // campaign snapshots were persisted.
            campaign = currentCampaign;
            campaignItems = currentCampaignItems;
        }

        if (campaign is null || request.ItemIds.Any(id => campaignItems.All(item => item.Id != id)))
        {
            var revisions = await db.CampaignPlanRevisions.AsNoTracking()
                .Where(value => value.ProjectId == project.Id)
                .OrderByDescending(value => value.CreatedAt)
                .ToListAsync(cancellationToken);
            campaign = revisions.FirstOrDefault(value =>
            {
                var items = Deserialize<List<CampaignItemRequest>>(value.ItemsJson) ?? [];
                return request.ItemIds.All(id => items.Any(item => item.Id == id));
            });
            campaignItems = Deserialize<List<CampaignItemRequest>>(campaign?.ItemsJson ?? "[]") ?? [];
        }
        if (campaign is null || request.ItemIds.Any(id => campaignItems.All(item => item.Id != id)))
            throw Problem(409, "campaign.snapshot_unavailable", "The paid campaign snapshot is no longer available.");

        MediaAsset? renderAudio = null;
        if (request.Kind != RenderRequestKind.TechnicalRetry)
        {
            renderAudio = entitlement.AudioAssetIdSnapshot is { } purchasedAudioId
                ? await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(
                    value => value.Id == purchasedAudioId &&
                             value.WorkspaceId == context.Workspace.Id &&
                             value.ProjectId == project.Id &&
                             value.Kind == AssetKind.Audio &&
                             value.State == AssetState.Ready &&
                             value.Sha256 == entitlement.AudioFingerprintSnapshot,
                    cancellationToken)
                : await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(
                    value => value.WorkspaceId == context.Workspace.Id &&
                             value.ProjectId == project.Id &&
                             value.Kind == AssetKind.Audio &&
                             value.IsActive &&
                             value.State == AssetState.Ready &&
                             value.Sha256 != null,
                    cancellationToken);
            if (renderAudio is null)
                throw Problem(409, "render.audio_snapshot_unavailable", "The paid audio snapshot is no longer available for final rendering.");
        }

        var usages = await db.RenderItemUsages
            .Where(value => value.EntitlementId == entitlement.Id && request.ItemIds.Contains(value.CampaignItemId))
            .ToDictionaryAsync(value => value.CampaignItemId, cancellationToken);
        foreach (var itemId in request.ItemIds)
        {
            usages.TryGetValue(itemId, out var usage);
            if (request.Kind == RenderRequestKind.Initial && usage?.InitialRenderCount > 0)
                throw Problem(409, "render.initial_already_used", "Use the included content rerender for edited content.");
            if (request.Kind == RenderRequestKind.ContentChange && (usage?.ContentRerenderCount ?? 0) >= 1)
                throw Problem(402, "render.content_rerender_used", "The included content rerender has already been used for this item.");
            if (request.Kind == RenderRequestKind.TechnicalRetry &&
                (usage?.InitialRenderCount ?? 0) + (usage?.ContentRerenderCount ?? 0) == 0)
                throw Problem(409, "render.retry_without_render", "A technical retry requires an earlier render attempt.");
        }

        var renderAudioIds = request.Kind == RenderRequestKind.TechnicalRetry
            ? retrySources.Values.Select(value => value.AudioAssetId).Distinct().ToArray()
            : [renderAudio!.Id];
        var renderAudios = await db.MediaAssets.AsNoTracking()
            .Where(value => renderAudioIds.Contains(value.Id) &&
                            value.WorkspaceId == context.Workspace.Id &&
                            value.ProjectId == project.Id &&
                            value.Kind == AssetKind.Audio &&
                            value.State == AssetState.Ready &&
                            value.Sha256 != null)
            .ToListAsync(cancellationToken);
        var rights = await db.RightsAttestations.AsNoTracking().SingleOrDefaultAsync(
            value => value.ProjectId == project.Id,
            cancellationToken);
        if (renderAudios.Count != renderAudioIds.Length || renderAudios.Any(audio => !HasContentRights(project, audio, rights)))
            throw Problem(409, "rights.required", "Confirm rights for the exact audio snapshot before rendering.");

        var artworkPack = await db.ArtworkPackRevisions.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == campaign.ArtworkPackRevisionId && value.ProjectId == project.Id,
            cancellationToken) ?? throw Problem(409, "artwork.snapshot_unavailable", "The campaign artwork snapshot is unavailable.");
        var visualIds = campaignItems
            .Where(value => request.ItemIds.Contains(value.Id))
            .Select(value => value.BackgroundAssetId)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Append(artworkPack.SelectedAssetId ?? Guid.Empty)
            .Where(value => value != Guid.Empty)
            .Distinct()
            .ToArray();
        var usesUploadedVisual = await db.MediaAssets.AsNoTracking().AnyAsync(
            value => visualIds.Contains(value.Id) &&
                     value.ProjectId == project.Id &&
                     value.Origin == AssetOrigin.Uploaded,
            cancellationToken);
        if (usesUploadedVisual && rights?.OwnsVisualRights != true)
            throw Problem(409, "rights.visual_required", "Confirm rights to the selected uploaded cover or video before rendering.");

        foreach (var itemId in request.ItemIds)
        {
            if (!usages.TryGetValue(itemId, out var usage))
            {
                usage = new RenderItemUsage
                {
                    WorkspaceId = context.Workspace.Id,
                    EntitlementId = entitlement.Id,
                    ProjectId = project.Id,
                    CampaignItemId = itemId
                };
                db.RenderItemUsages.Add(usage);
                usages[itemId] = usage;
            }

            switch (request.Kind)
            {
                case RenderRequestKind.Initial when usage.InitialRenderCount > 0:
                    throw Problem(409, "render.initial_already_used", "Use the included content rerender for edited content.");
                case RenderRequestKind.Initial:
                    usage.InitialRenderCount++;
                    break;
                case RenderRequestKind.ContentChange when usage.ContentRerenderCount >= 1:
                    throw Problem(402, "render.content_rerender_used", "The included content rerender has already been used for this item.");
                case RenderRequestKind.ContentChange:
                    usage.ContentRerenderCount++;
                    entitlement.RemainingContentRerenders--;
                    break;
                case RenderRequestKind.TechnicalRetry when usage.InitialRenderCount + usage.ContentRerenderCount == 0:
                    throw Problem(409, "render.retry_without_render", "A technical retry requires an earlier render attempt.");
                case RenderRequestKind.TechnicalRetry:
                    usage.TechnicalRetryCount++;
                    break;
                default:
                    throw Problem(422, "render.kind_invalid", "Choose a supported render request kind.");
            }
        }

        var batch = new RenderBatch
        {
            WorkspaceId = context.Workspace.Id,
            ProjectId = project.Id,
            EntitlementId = entitlement.Id,
            Kind = request.Kind,
            ItemIdsJson = JsonSerializer.Serialize(request.ItemIds, StoredJson),
            IdempotencyKey = key,
            RequestHash = requestHash
        };
        var jobs = request.ItemIds.Select(itemId =>
        {
            var retrySource = retrySources.GetValueOrDefault(itemId);
            return NewJob(
                context.Workspace.Id,
                project.Id,
                JobType.FinalRender,
                "render",
                "deterministic-render-v1",
                $"final-render:{batch.Id:N}:{itemId:N}",
                new
                {
                    projectId,
                    campaignRevisionId = retrySource?.CampaignRevisionId ?? campaign.Id,
                    campaignItemId = itemId,
                    renderBatchId = batch.Id,
                    audioAssetId = retrySource?.AudioAssetId ?? renderAudio!.Id,
                    audioFingerprint = retrySource?.AudioFingerprint ?? renderAudio!.Sha256,
                    request.Kind,
                    retryOfJobId = retrySource?.JobId
                });
        }).ToList();
        var exportJob = NewJob(
            context.Workspace.Id,
            project.Id,
            JobType.ExportBundle,
            "render",
            "export-v1",
            $"export:{batch.Id:N}",
            new
            {
                projectId,
                renderBatchId = batch.Id,
                renderJobIds = jobs.Select(value => value.Id).ToArray(),
                campaignRevisionId = campaign.Id,
                scheduleAnchor = entitlement.ScheduleAnchorSnapshot ?? ScheduleAnchor(
                    project.Mode,
                    project.ReleaseDate,
                    project.CampaignStartDate,
                    project.CreatedAt),
                artistName = entitlement.ArtistNameSnapshot ?? project.ArtistName,
                trackTitle = entitlement.TrackTitleSnapshot ?? project.TrackTitle,
                releaseMode = entitlement.ReleaseModeSnapshot ?? project.Mode
            });
        exportJob.MaxAttempts = 100;
        jobs.Add(exportJob);
        batch.JobIdsJson = JsonSerializer.Serialize(jobs.Select(value => value.Id), StoredJson);
        db.RenderBatches.Add(batch);
        db.Jobs.AddRange(jobs);
        db.JobEvents.AddRange(jobs.Select(value => NewQueuedEvent(value)));
        project.State = ProjectState.Rendering;
        db.ProjectEvents.Add(new ProjectEvent
        {
            WorkspaceId = context.Workspace.Id,
            ProjectId = project.Id,
            EventType = "render.queued",
            DataJson = JsonSerializer.Serialize(new { projectId, renderBatchId = batch.Id, request.Kind })
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var currentEntitlement = await db.Entitlements.AsNoTracking().SingleOrDefaultAsync(
                value => value.Id == request.EntitlementId && value.WorkspaceId == context.Workspace.Id,
                cancellationToken);
            if (currentEntitlement?.ProductCode == BillingProducts.ActiveArtist &&
                currentEntitlement.ProjectId is { } boundProjectId && boundProjectId != project.Id)
                throw Problem(409, "render.subscription_already_bound", "This billing-period entitlement was already bound to another release.");
            throw;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var concurrent = await db.RenderBatches.AsNoTracking().SingleOrDefaultAsync(
                value => value.WorkspaceId == context.Workspace.Id && value.IdempotencyKey == key,
                cancellationToken);
            if (concurrent is null || concurrent.RequestHash != requestHash) throw;
            return Results.Accepted(
                $"/api/v1/releases/{concurrent.ProjectId}/renders/{concurrent.Id}",
                ToRenderBatch(concurrent));
        }
        return Results.Accepted(
            $"/api/v1/releases/{projectId}/renders/{batch.Id}",
            ToRenderBatch(batch));
    }

    private static async Task<IResult> GetRenderBatch(
        Guid projectId,
        Guid batchId,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        IObjectStorage storage,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        if (!await db.Projects.AsNoTracking().AnyAsync(
                value => value.Id == projectId && value.WorkspaceId == context.Workspace.Id,
                cancellationToken))
            throw NotFound();
        var batch = await db.RenderBatches.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == batchId && value.ProjectId == projectId && value.WorkspaceId == context.Workspace.Id,
            cancellationToken) ?? throw NotFound();
        var entitlement = await db.Entitlements.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == batch.EntitlementId && value.WorkspaceId == context.Workspace.Id,
            cancellationToken) ?? throw NotFound();
        // Period expiry prevents new renders, but already purchased/generated
        // outputs remain in the owner's history. Refund/revocation still closes access.
        if (entitlement.State != EntitlementState.Active || entitlement.RevokedAt is not null)
            throw Problem(402, "render.entitlement_required", "The render entitlement was revoked.");

        var itemIds = Deserialize<List<Guid>>(batch.ItemIdsJson) ?? [];
        var jobIds = Deserialize<List<Guid>>(batch.JobIdsJson) ?? [];
        var jobs = await db.Jobs.AsNoTracking()
            .Where(value => jobIds.Contains(value.Id))
            .ToListAsync(cancellationToken);
        var assets = await db.MediaAssets.AsNoTracking()
            .Where(value => value.RenderBatchId == batch.Id && value.State == AssetState.Ready)
            .ToListAsync(cancellationToken);
        var expiresAt = timeProvider.GetUtcNow().Add(DownloadUrlLifetime);
        var items = new List<RenderItemStatusResponse>(itemIds.Count);
        foreach (var itemId in itemIds)
        {
            var job = jobs.FirstOrDefault(value =>
                value.Type == JobType.FinalRender && PayloadGuid(value.PayloadJson, "campaignItemId") == itemId);
            var asset = assets.FirstOrDefault(value =>
                value.Purpose == AssetPurpose.CampaignVideo && value.CampaignItemId == itemId);
            var download = asset is null
                ? null
                : await CreateDownloadGrant(asset, storage, expiresAt, cancellationToken);
            items.Add(new RenderItemStatusResponse(
                itemId,
                asset is not null ? "succeeded" : JobStatus(job),
                job?.Id,
                job?.ErrorCode,
                download));
        }

        var exportAsset = assets.FirstOrDefault(value => value.Purpose == AssetPurpose.ExportBundle);
        var export = exportAsset is null
            ? null
            : await CreateDownloadGrant(exportAsset, storage, expiresAt, cancellationToken);
        return Results.Ok(new RenderBatchStatusResponse(
            batch.Id,
            batch.EntitlementId,
            batch.State.ToString().ToLowerInvariant(),
            batch.Kind,
            items,
            export,
            batch.CompletedAt));
    }

    private static async Task<IResult> GetCleanCoverDownload(
        Guid projectId,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        IObjectStorage storage,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        if (!await db.Projects.AsNoTracking().AnyAsync(
                value => value.Id == projectId && value.WorkspaceId == context.Workspace.Id,
                cancellationToken))
            throw NotFound();
        var entitlements = await db.Entitlements.AsNoTracking()
            .Where(value => value.WorkspaceId == context.Workspace.Id &&
                            value.ProjectId == projectId &&
                            value.ProductCode == BillingProducts.CleanCover)
            .OrderByDescending(value => value.CreatedAt)
            .ToListAsync(cancellationToken);
        var entitlement = entitlements.FirstOrDefault(value => IsActive(value, timeProvider.GetUtcNow()))
            ?? throw Problem(402, "cover.entitlement_required", "Purchase the clean cover before downloading it.");
        var selectedAssetId = (Deserialize<List<Guid>>(entitlement.ItemIdsJson) ?? []).FirstOrDefault();
        if (selectedAssetId == Guid.Empty)
            throw Problem(409, "cover.output_not_ready", "The purchased clean cover is still being prepared.");
        var coverQuery = db.MediaAssets.AsNoTracking().Where(value =>
            value.SupersedesAssetId == selectedAssetId &&
            value.ArtworkPackRevisionId == entitlement.ArtworkPackRevisionId &&
            value.WorkspaceId == context.Workspace.Id &&
            value.ProjectId == projectId &&
            value.Origin == AssetOrigin.Generated &&
            value.Purpose == AssetPurpose.CleanCover &&
            value.State == AssetState.Ready);
        if (!string.IsNullOrWhiteSpace(entitlement.ArtistNameSnapshot) &&
            !string.IsNullOrWhiteSpace(entitlement.TrackTitleSnapshot))
        {
            var metadataHashPrefix = Hash(
                $"{entitlement.ArtistNameSnapshot}\n{entitlement.TrackTitleSnapshot}")[..16];
            coverQuery = coverQuery.Where(value => value.ObjectKey.Contains(
                $"clean-cover-{metadataHashPrefix}-"));
        }
        var asset = await coverQuery
            .OrderByDescending(value => value.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw Problem(409, "cover.output_not_ready", "The purchased clean cover is still being prepared.");
        var expiresAt = timeProvider.GetUtcNow().Add(DownloadUrlLifetime);
        return Results.Ok(await CreateDownloadGrant(asset, storage, expiresAt, cancellationToken));
    }

    private static async Task<IReadOnlyList<Guid>> ValidateAndNormalizeCheckoutItems(
        Hook2StreamDbContext db,
        Guid workspaceId,
        string productCode,
        Guid? projectId,
        IReadOnlyList<Guid>? requestedItems,
        CancellationToken cancellationToken)
    {
        if (productCode == BillingProducts.ArtworkCredits5)
        {
            if (projectId is not null || requestedItems is { Count: > 0 })
                throw Problem(422, "billing.project_not_allowed", "Artwork credits are purchased for the workspace.");
            return [];
        }
        if (productCode == BillingProducts.ActiveArtist)
        {
            if (projectId is not null || requestedItems is { Count: > 0 })
                throw Problem(422, "billing.subscription_scope_invalid", "Active Artist is a workspace billing-period entitlement and is bound when its first render starts.");
            return [];
        }
        if (projectId is null)
            throw Problem(422, "billing.project_required", "Choose a release for this product.");
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == projectId && value.WorkspaceId == workspaceId,
            cancellationToken) ?? throw NotFound();
        if (productCode == BillingProducts.CleanCover)
        {
            if (project.CurrentArtworkPackRevisionId is not { } artId)
                throw Problem(409, "artwork.approval_required", "Approve a cover before purchasing the clean artwork.");
            var artwork = await db.ArtworkPackRevisions.AsNoTracking().SingleOrDefaultAsync(
                value => value.Id == artId && value.State == RevisionState.Approved,
                cancellationToken);
            if (artwork?.SelectedAssetId is not { } selectedAssetId)
                throw Problem(409, "artwork.approval_required", "Approve a ready cover before purchasing the clean artwork.");
            var selectedAsset = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(value =>
                    value.Id == selectedAssetId &&
                    value.ProjectId == project.Id &&
                    value.Purpose == AssetPurpose.ApprovedCover &&
                    value.State == AssetState.Ready,
                cancellationToken);
            if (selectedAsset is null)
                throw Problem(409, "artwork.approval_required", "Approve a ready cover before purchasing the clean artwork.");
            if (selectedAsset.Origin == AssetOrigin.Uploaded &&
                !await db.RightsAttestations.AsNoTracking().AnyAsync(
                    value => value.ProjectId == project.Id && value.OwnsVisualRights,
                    cancellationToken))
                throw Problem(409, "rights.visual_required", "Confirm rights to the uploaded cover before purchasing its clean export.");
            return [selectedAssetId];
        }
        if (project.CurrentCampaignPlanRevisionId is not { } campaignId)
            throw Problem(409, "campaign.required", "Generate the campaign before checkout.");
        var campaign = await db.CampaignPlanRevisions.AsNoTracking().SingleAsync(value => value.Id == campaignId, cancellationToken);
        var allItems = (Deserialize<List<CampaignItemRequest>>(campaign.ItemsJson) ?? []).Select(value => value.Id).ToList();
        if (allItems.Count != 18 || allItems.Distinct().Count() != 18)
            throw Problem(409, "campaign.incomplete", "The campaign must contain exactly 18 items.");
        var requested = requestedItems?.Distinct().ToList() ?? [];
        var count = BillingProducts.IncludedVideoCount(productCode);
        if (count == 6 && requested.Count != 6)
            throw Problem(422, "billing.items_invalid", "Mini Release requires exactly six selected campaign items.");
        if (count == 18)
            requested = requested.Count == 0 ? allItems : requested;
        if (requested.Count != count || requested.Any(value => !allItems.Contains(value)))
            throw Problem(422, "billing.items_invalid", $"This product requires exactly {count} current campaign items.");
        return requested;
    }

    private static async Task FulfillCheckout(
        Hook2StreamDbContext db,
        BillingCheckout checkout,
        FulfillmentContext fulfillment,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (checkout.State == CheckoutState.Refunded && !BillingProducts.IsSubscription(checkout.ProductCode)) return;
        checkout.ExternalCustomerId ??= fulfillment.ExternalCustomerId;
        checkout.ExternalSubscriptionId ??= fulfillment.ExternalSubscriptionId;
        checkout.ExternalPaymentIntentId ??= fulfillment.ExternalPaymentIntentId;
        checkout.State = CheckoutState.Completed;
        checkout.CompletedAt ??= timeProvider.GetUtcNow();
        if (checkout.ProductCode == BillingProducts.ArtworkCredits5)
        {
            if (await db.ArtworkCreditGrants.AnyAsync(value => value.CheckoutId == checkout.Id, cancellationToken)) return;
            var wallet = await db.WorkspaceArtworkCredits.SingleOrDefaultAsync(
                value => value.WorkspaceId == checkout.WorkspaceId, cancellationToken);
            if (wallet is null)
            {
                wallet = new WorkspaceArtworkCredit { WorkspaceId = checkout.WorkspaceId };
                db.WorkspaceArtworkCredits.Add(wallet);
            }
            wallet.Balance += 5;
            var grant = new ArtworkCreditGrant
            {
                WorkspaceId = checkout.WorkspaceId,
                CheckoutId = checkout.Id,
                Granted = 5,
                Remaining = 5
            };
            db.ArtworkCreditGrants.Add(grant);
            db.ArtworkCreditTransactions.Add(new ArtworkCreditTransaction
            {
                WorkspaceId = checkout.WorkspaceId,
                GrantId = grant.Id,
                Delta = 5,
                BalanceAfter = wallet.Balance,
                Reason = "purchase",
                Reference = $"checkout:{checkout.Id:N}:grant"
            });
            return;
        }
        if (checkout.ProductCode != BillingProducts.ActiveArtist &&
            await db.Entitlements.AnyAsync(value => value.CheckoutId == checkout.Id, cancellationToken)) return;
        if (await db.Entitlements.AnyAsync(
                value => value.CheckoutId == checkout.Id && value.ProviderPeriodKey == fulfillment.ProviderPeriodKey,
                cancellationToken)) return;
        var included = BillingProducts.IncludedVideoCount(checkout.ProductCode);
        var entitlement = new Entitlement
        {
            WorkspaceId = checkout.WorkspaceId,
            CheckoutId = checkout.Id,
            ProjectId = checkout.ProjectId,
            ProductCode = checkout.ProductCode,
            ItemIdsJson = checkout.ItemIdsJson,
            IncludedItemCount = included,
            RemainingContentRerenders = included,
            ProviderPeriodKey = fulfillment.ProviderPeriodKey,
            ExternalSubscriptionId = checkout.ExternalSubscriptionId,
            ExternalPaymentIntentId = fulfillment.ExternalPaymentIntentId,
            ExternalInvoiceId = fulfillment.ExternalInvoiceId,
            ArtworkPackRevisionId = checkout.ArtworkPackRevisionId,
            ArtworkCompositionHash = checkout.ArtworkCompositionHash,
            CampaignPlanRevisionId = checkout.CampaignPlanRevisionId,
            ArtistNameSnapshot = checkout.ArtistNameSnapshot,
            TrackTitleSnapshot = checkout.TrackTitleSnapshot,
            ScheduleAnchorSnapshot = checkout.ScheduleAnchorSnapshot,
            ReleaseModeSnapshot = checkout.ReleaseModeSnapshot,
            AudioAssetIdSnapshot = checkout.AudioAssetIdSnapshot,
            AudioFingerprintSnapshot = checkout.AudioFingerprintSnapshot,
            PeriodStartsAt = BillingProducts.IsSubscription(checkout.ProductCode)
                ? fulfillment.PeriodStartsAt
                : null,
            ValidUntil = BillingProducts.IsSubscription(checkout.ProductCode)
                ? fulfillment.PeriodEndsAt ?? fulfillment.PeriodStartsAt.AddMonths(1)
                : null
        };
        db.Entitlements.Add(entitlement);
        if (checkout.ProductCode == BillingProducts.CleanCover && checkout.ProjectId is { } projectId)
        {
            var selectedAssetId = (Deserialize<List<Guid>>(checkout.ItemIdsJson) ?? []).SingleOrDefault();
            var artworkPack = await db.ArtworkPackRevisions
                .Where(value => value.Id == checkout.ArtworkPackRevisionId &&
                                value.ProjectId == projectId &&
                                value.SelectedAssetId == selectedAssetId &&
                                value.ApprovedAt != null)
                .OrderByDescending(value => value.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw Problem(409, "cover.snapshot_invalid", "The purchased cover snapshot is unavailable.");
            if (!string.Equals(Hash(artworkPack.CompositionJson), checkout.ArtworkCompositionHash, StringComparison.Ordinal))
                throw Problem(409, "cover.snapshot_invalid", "The purchased cover composition no longer matches its immutable snapshot.");
            var renderJob = NewJob(
                checkout.WorkspaceId,
                projectId,
                JobType.CleanCoverRender,
                "render",
                "local-cover-v1",
                $"clean-cover:{entitlement.Id:N}",
                new
                {
                    projectId,
                    entitlementId = entitlement.Id,
                    artworkPackRevisionId = artworkPack.Id,
                    selectedAssetId,
                    artistName = checkout.ArtistNameSnapshot,
                    trackTitle = checkout.TrackTitleSnapshot
                });
            db.Jobs.Add(renderJob);
            db.JobEvents.Add(NewQueuedEvent(renderJob));
        }
    }

    private static async Task RevokeCheckout(
        Hook2StreamDbContext db,
        BillingCheckout checkout,
        PaymentWebhookEvent paymentEvent,
        CancellationToken cancellationToken)
    {
        if (!BillingProducts.IsSubscription(checkout.ProductCode) &&
            checkout.State == CheckoutState.Refunded && checkout.RefundedAt >= paymentEvent.OccurredAt) return;
        if (!BillingProducts.IsSubscription(checkout.ProductCode)) checkout.State = CheckoutState.Refunded;
        checkout.RefundedAt = checkout.RefundedAt is { } refundedAt && refundedAt > paymentEvent.OccurredAt
            ? refundedAt
            : paymentEvent.OccurredAt;
        var entitlements = await db.Entitlements.Where(value => value.CheckoutId == checkout.Id).ToListAsync(cancellationToken);
        var referenced = paymentEvent.ExternalInvoiceId is { } invoiceId
            ? entitlements.Where(value => value.ExternalInvoiceId == invoiceId).ToList()
            : paymentEvent.ExternalPaymentIntentId is { } paymentIntentId
                ? entitlements.Where(value => value.ExternalPaymentIntentId == paymentIntentId).ToList()
                : [];
        if (BillingProducts.IsSubscription(checkout.ProductCode) && referenced.Count == 0)
            throw Problem(409, "billing.refund_period_unresolved", "The subscription refund is not linked to a granted billing period yet.");
        foreach (var entitlement in referenced.Count > 0 ? referenced : entitlements)
        {
            entitlement.State = EntitlementState.Revoked;
            entitlement.RevokedAt = checkout.RefundedAt;
        }
        var grant = await db.ArtworkCreditGrants.SingleOrDefaultAsync(value => value.CheckoutId == checkout.Id, cancellationToken);
        if (grant is null) return;
        var wallet = await db.WorkspaceArtworkCredits.SingleAsync(value => value.WorkspaceId == checkout.WorkspaceId, cancellationToken);
        var revoked = Math.Min(wallet.Balance, grant.Remaining);
        wallet.Balance -= revoked;
        grant.Remaining -= revoked;
        grant.RevokedAt = checkout.RefundedAt;
        var refundReference = $"checkout:{checkout.Id:N}:refund";
        if (!await db.ArtworkCreditTransactions.AnyAsync(
                value => value.WorkspaceId == checkout.WorkspaceId && value.Reference == refundReference,
                cancellationToken))
        {
            db.ArtworkCreditTransactions.Add(new ArtworkCreditTransaction
            {
                WorkspaceId = checkout.WorkspaceId,
                GrantId = grant.Id,
                Delta = -revoked,
                BalanceAfter = wallet.Balance,
                Reason = "refund",
                Reference = refundReference
            });
        }
    }

    private static async Task<BillingCheckout?> ResolveCheckout(
        Hook2StreamDbContext db,
        PaymentWebhookEvent paymentEvent,
        CancellationToken cancellationToken)
    {
        if (paymentEvent.CheckoutId is { } checkoutId)
        {
            var byId = await db.BillingCheckouts.SingleOrDefaultAsync(value => value.Id == checkoutId, cancellationToken);
            if (byId is not null) return byId;
        }
        if (paymentEvent.ExternalSessionId is { Length: > 0 } sessionId)
        {
            var bySession = await db.BillingCheckouts.SingleOrDefaultAsync(
                value => value.ExternalSessionId == sessionId,
                cancellationToken);
            if (bySession is not null) return bySession;
        }
        if (paymentEvent.ExternalSubscriptionId is { Length: > 0 } subscriptionId)
        {
            var bySubscription = await db.BillingCheckouts
                .OrderByDescending(value => value.CreatedAt)
                .FirstOrDefaultAsync(value => value.ExternalSubscriptionId == subscriptionId, cancellationToken);
            if (bySubscription is not null) return bySubscription;
        }
        if (paymentEvent.ExternalPaymentIntentId is { Length: > 0 } paymentIntentId)
        {
            var byPayment = await db.BillingCheckouts.SingleOrDefaultAsync(
                value => value.ExternalPaymentIntentId == paymentIntentId,
                cancellationToken);
            if (byPayment is not null) return byPayment;
            var entitlementCheckoutId = await db.Entitlements
                .Where(value => value.ExternalPaymentIntentId == paymentIntentId)
                .Select(value => (Guid?)value.CheckoutId)
                .FirstOrDefaultAsync(cancellationToken);
            if (entitlementCheckoutId is { } linkedCheckoutId)
                return await db.BillingCheckouts.SingleOrDefaultAsync(value => value.Id == linkedCheckoutId, cancellationToken);
        }
        if (paymentEvent.ExternalInvoiceId is { Length: > 0 } invoiceId)
        {
            var entitlementCheckoutId = await db.Entitlements
                .Where(value => value.ExternalInvoiceId == invoiceId)
                .Select(value => (Guid?)value.CheckoutId)
                .FirstOrDefaultAsync(cancellationToken);
            if (entitlementCheckoutId is { } linkedCheckoutId)
                return await db.BillingCheckouts.SingleOrDefaultAsync(value => value.Id == linkedCheckoutId, cancellationToken);
        }
        return null;
    }

    private static async Task<Dictionary<Guid, TechnicalRetrySource>> FindTechnicalRetrySources(
        Hook2StreamDbContext db,
        Guid entitlementId,
        Guid projectId,
        IReadOnlyList<Guid> requestedItemIds,
        CancellationToken cancellationToken)
    {
        var batches = await db.RenderBatches.AsNoTracking()
            .Where(value => value.EntitlementId == entitlementId && value.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        var jobIds = batches
            .SelectMany(value => Deserialize<List<Guid>>(value.JobIdsJson) ?? [])
            .Distinct()
            .ToArray();
        var jobs = await db.Jobs.AsNoTracking()
            .Where(value => jobIds.Contains(value.Id) && value.Type == JobType.FinalRender)
            .OrderByDescending(value => value.UpdatedAt)
            .ToListAsync(cancellationToken);
        var consumedFailures = jobs
            .Select(value => PayloadGuid(value.PayloadJson, "retryOfJobId"))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToHashSet();
        var result = new Dictionary<Guid, TechnicalRetrySource>();
        foreach (var itemId in requestedItemIds)
        {
            var failed = jobs.FirstOrDefault(value =>
                value.State == JobState.Failed &&
                !consumedFailures.Contains(value.Id) &&
                PayloadGuid(value.PayloadJson, "campaignItemId") == itemId);
            if (failed is null || PayloadGuid(failed.PayloadJson, "campaignRevisionId") is not { } campaignRevisionId)
                throw Problem(409, "render.retry_not_available", "A free technical retry requires an unconsumed failed render of the same item revision.");
            if (PayloadGuid(failed.PayloadJson, "audioAssetId") is not { } audioAssetId ||
                PayloadString(failed.PayloadJson, "audioFingerprint") is not { Length: > 0 } audioFingerprint)
                throw Problem(409, "render.retry_snapshot_invalid", "The failed render does not contain an immutable audio snapshot and cannot be retried safely.");
            result[itemId] = new TechnicalRetrySource(failed.Id, campaignRevisionId, audioAssetId, audioFingerprint);
        }
        if (result.Values.Select(value => value.CampaignRevisionId).Distinct().Count() != 1)
            throw Problem(409, "render.retry_mixed_revisions", "Retry failed items from one immutable campaign revision at a time.");
        return result;
    }

    private static Job NewJob(
        Guid workspaceId,
        Guid projectId,
        JobType type,
        string capability,
        string handlerVersion,
        string idempotencyKey,
        object payload) => new()
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Type = type,
            RequiredCapability = capability,
            HandlerVersion = handlerVersion,
            PayloadJson = JsonSerializer.Serialize(payload, StoredJson),
            IdempotencyKey = idempotencyKey,
            State = JobState.Queued,
            AvailableAt = DateTimeOffset.UtcNow
        };

    private static JobEvent NewQueuedEvent(Job job) => new()
    {
        JobId = job.Id,
        EventType = "queued",
        DataJson = JsonSerializer.Serialize(new { job.Type, job.RequiredCapability, job.HandlerVersion })
    };

    private static CheckoutResponse ToCheckout(BillingCheckout value) => new(
        value.Id,
        value.ProductCode,
        value.State.ToString().ToLowerInvariant(),
        value.CheckoutUrl ?? string.Empty);

    private static RenderBatchResponse ToRenderBatch(RenderBatch value) => new(
        value.Id,
        value.State.ToString().ToLowerInvariant(),
        Deserialize<IReadOnlyList<Guid>>(value.JobIdsJson) ?? []);

    private static async Task<DownloadGrantResponse> CreateDownloadGrant(
        MediaAsset asset,
        IObjectStorage storage,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var url = await storage.CreateReadUrlAsync(asset.ObjectKey, DownloadUrlLifetime, cancellationToken);
        return new DownloadGrantResponse(
            asset.Id,
            asset.OriginalFileName,
            asset.DetectedContentType ?? asset.DeclaredContentType,
            asset.ActualBytes ?? asset.DeclaredBytes,
            asset.Width,
            asset.Height,
            url.ToString(),
            expiresAt);
    }

    private static Guid? PayloadGuid(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String &&
                   value.TryGetGuid(out var parsed)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? PayloadString(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string JobStatus(Job? job) => job?.State.ToString().ToLowerInvariant() ?? "queued";

    private static bool IsActive(Entitlement value, DateTimeOffset now) =>
        value.State == EntitlementState.Active && (value.ValidUntil is null || value.ValidUntil > now);

    private static bool HasContentRights(
        ReleaseProject project,
        MediaAsset audio,
        RightsAttestation? rights) =>
        rights?.OwnsAudioRights == true &&
        (project.IsInstrumental && project.IsInstrumentalConfirmed || rights.OwnsLyricsRights) &&
        rights.AudioAssetId == audio.Id &&
        !string.IsNullOrWhiteSpace(audio.Sha256) &&
        string.Equals(rights.AudioFingerprint, audio.Sha256, StringComparison.Ordinal);

    private static DateOnly ScheduleAnchor(
        ReleaseMode mode,
        DateOnly? releaseDate,
        DateOnly? campaignStartDate,
        DateTimeOffset createdAt) =>
        mode == ReleaseMode.Released
            ? campaignStartDate ?? releaseDate ?? DateOnly.FromDateTime(createdAt.UtcDateTime)
            : releaseDate ?? campaignStartDate ?? DateOnly.FromDateTime(createdAt.UtcDateTime);

    private static string EntitlementStatus(Entitlement value, DateTimeOffset now) =>
        value.State == EntitlementState.Active && value.ValidUntil <= now
            ? "expired"
            : value.State.ToString().ToLowerInvariant();

    private static string RequireIdempotencyKey(HttpRequest request)
    {
        var key = request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(key)) throw Problem(428, "idempotency.key_required", "Send Idempotency-Key for this command.");
        if (key.Length > 255) throw Problem(400, "idempotency.key_invalid", "Idempotency-Key must not exceed 255 characters.");
        return key;
    }

    private static void ValidateReturnPath(string returnPath)
    {
        if (string.IsNullOrWhiteSpace(returnPath) || returnPath.Length > 1_000 ||
            !returnPath.StartsWith('/') || returnPath.StartsWith("//", StringComparison.Ordinal) ||
            returnPath.Contains('\\'))
            throw Problem(422, "billing.return_path_invalid", "ReturnPath must be a local application path.");
    }

    private static T? Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, StoredJson);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ApiProblemException NotFound() => Problem(404, "resource.not_found", "The requested resource was not found.");
    private static ApiProblemException Problem(int status, string code, string message) => new(status, code, message);

    private sealed record FulfillmentContext(
        string ProviderPeriodKey,
        string? ExternalCustomerId,
        string? ExternalSubscriptionId,
        string? ExternalPaymentIntentId,
        string? ExternalInvoiceId,
        DateTimeOffset PeriodStartsAt,
        DateTimeOffset? PeriodEndsAt);

    private sealed record TechnicalRetrySource(
        Guid JobId,
        Guid CampaignRevisionId,
        Guid AudioAssetId,
        string AudioFingerprint);
}
