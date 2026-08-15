using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hook2Stream.IntegrationTests;

public sealed class BillingWorkflowTests
{
    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Fixture_artwork_credit_checkout_is_idempotent_and_grants_exactly_five()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);

        var first = await Checkout(client, "credits-1", new
        {
            productCode = BillingProducts.ArtworkCredits5,
            projectId = (Guid?)null,
            itemIds = (Guid[]?)null,
            returnPath = "/dashboard"
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", firstJson.GetProperty("status").GetString());

        var replay = await Checkout(client, "credits-1", new
        {
            productCode = BillingProducts.ArtworkCredits5,
            projectId = (Guid?)null,
            itemIds = (Guid[]?)null,
            returnPath = "/dashboard"
        });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var summary = await client.GetFromJsonAsync<JsonElement>("/api/v1/billing/summary");
        Assert.Equal(5, summary.GetProperty("workspaceArtworkCredits").GetInt32());
        Assert.Empty(summary.GetProperty("entitlements").EnumerateArray());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Equal(1, await db.ArtworkCreditGrants.CountAsync());
        Assert.Equal(1, await db.ArtworkCreditTransactions.CountAsync());
    }

    [Fact]
    public async Task Refund_revokes_a_fully_reserved_artwork_grant_and_failure_cannot_restore_it()
    {
        var gateway = new MutablePaymentGateway();
        await using var factory = new Hook2StreamApiFactory(services =>
        {
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(gateway);
        });
        using var client = factory.CreateClient();
        await Onboard(client);
        Guid workspaceId;
        Guid checkoutId;
        Guid grantId;
        var revisionId = Guid.CreateVersion7();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
            var checkout = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProductCode = BillingProducts.ArtworkCredits5,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ArtworkCredits5),
                IdempotencyKey = "fully-reserved-refund",
                RequestHash = new string('a', 64),
                State = CheckoutState.Completed,
                ExternalSessionId = "cs_fully_reserved",
                CheckoutUrl = "https://payments.example.test/fully-reserved",
                CompletedAt = DateTimeOffset.UtcNow
            };
            var grant = new ArtworkCreditGrant
            {
                WorkspaceId = workspaceId,
                CheckoutId = checkout.Id,
                Granted = 1,
                Remaining = 0
            };
            checkoutId = checkout.Id;
            grantId = grant.Id;
            db.BillingCheckouts.Add(checkout);
            db.WorkspaceArtworkCredits.Add(new WorkspaceArtworkCredit
            {
                WorkspaceId = workspaceId,
                Balance = 0
            });
            db.ArtworkCreditGrants.Add(grant);
            db.ArtworkCreditTransactions.Add(new ArtworkCreditTransaction
            {
                WorkspaceId = workspaceId,
                GrantId = grant.Id,
                Delta = -1,
                BalanceAfter = 0,
                Reason = "artwork_generation_reserved",
                Reference = $"artwork:{revisionId:N}:reserve"
            });
            await db.SaveChangesAsync();
        }

        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-fully-reserved-refund",
            Type: "charge.refunded",
            CheckoutId: checkoutId,
            ExternalSessionId: "cs_fully_reserved",
            ProductCode: BillingProducts.ArtworkCredits5,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: null,
            ExternalSubscriptionId: null,
            ExternalPaymentIntentId: null,
            ExternalInvoiceId: null,
            ExternalChargeId: "ch_fully_reserved",
            Paid: false,
            Refunded: true,
            OccurredAt: DateTimeOffset.UtcNow,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        (await PostWebhook(client, "fully-reserved-refund")).EnsureSuccessStatusCode();

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var revokedGrant = await verifyDb.ArtworkCreditGrants.SingleAsync(value => value.Id == grantId);
        Assert.NotNull(revokedGrant.RevokedAt);
        Assert.True(await ArtworkCreditLedger.ReleaseReservationAsync(
            verifyDb,
            workspaceId,
            revisionId,
            default));
        await verifyDb.SaveChangesAsync();
        Assert.Equal(0, (await verifyDb.WorkspaceArtworkCredits.SingleAsync()).Balance);
        Assert.Contains(
            await verifyDb.ArtworkCreditTransactions.ToListAsync(),
            value => value.Reason == "artwork_generation_release_revoked" && value.Delta == 0);
    }

    [Fact]
    public async Task Failed_checkout_is_terminal_and_late_success_does_not_grant_value()
    {
        var gateway = new MutablePaymentGateway();
        await using var factory = new Hook2StreamApiFactory(services =>
        {
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(gateway);
        });
        using var client = factory.CreateClient();
        await Onboard(client);
        Guid workspaceId;
        Guid checkoutId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            workspaceId = (await db.Workspaces.SingleAsync()).Id;
            var checkout = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProductCode = BillingProducts.ArtworkCredits5,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ArtworkCredits5),
                IdempotencyKey = "failed-checkout",
                RequestHash = new string('f', 64),
                ExternalSessionId = "cs_failed",
                CheckoutUrl = "https://payments.example.test/failed"
            };
            checkoutId = checkout.Id;
            db.BillingCheckouts.Add(checkout);
            await db.SaveChangesAsync();
        }

        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-checkout-expired",
            Type: "checkout.session.expired",
            CheckoutId: checkoutId,
            ExternalSessionId: "cs_failed",
            ProductCode: BillingProducts.ArtworkCredits5,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: null,
            ExternalSubscriptionId: null,
            ExternalPaymentIntentId: null,
            ExternalInvoiceId: null,
            ExternalChargeId: null,
            Paid: false,
            Refunded: false,
            OccurredAt: DateTimeOffset.UtcNow,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "checkout-expired")).StatusCode);

        gateway.NextEvent = gateway.NextEvent with
        {
            EventId = "evt-checkout-late-success",
            Type = "checkout.session.async_payment_succeeded",
            Paid = true,
            OccurredAt = gateway.NextEvent.OccurredAt.AddMinutes(1),
            PayloadHash = string.Empty
        };
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "checkout-late-success")).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Equal(
            CheckoutState.Failed,
            await verifyDb.BillingCheckouts.Where(value => value.Id == checkoutId).Select(value => value.State).SingleAsync());
        Assert.Empty(await verifyDb.ArtworkCreditGrants.ToListAsync());
        Assert.Empty(await verifyDb.Entitlements.ToListAsync());
    }

    [Fact]
    public async Task Mini_checkout_preserves_selection_and_render_status_only_exposes_entitled_outputs()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var seeded = await SeedCampaign(factory);
        var selected = seeded.ItemIds.Take(6).ToArray();

        var checkout = await Checkout(client, "mini-1", new
        {
            productCode = BillingProducts.MiniRelease,
            projectId = seeded.ProjectId,
            itemIds = selected,
            returnPath = $"/releases/{seeded.ProjectId}/campaign"
        });
        Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);

        var summary = await client.GetFromJsonAsync<JsonElement>("/api/v1/billing/summary");
        var entitlement = Assert.Single(summary.GetProperty("entitlements").EnumerateArray());
        var entitlementId = entitlement.GetProperty("id").GetGuid();
        Assert.Equal(6, entitlement.GetProperty("includedItemCount").GetInt32());
        Assert.Equal(selected, entitlement.GetProperty("itemIds").EnumerateArray().Select(value => value.GetGuid()));

        var incomplete = await StartRender(client, seeded.ProjectId, "render-incomplete", entitlementId, selected.Take(5).ToArray(), "initial");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, incomplete.StatusCode);

        var started = await StartRender(client, seeded.ProjectId, "render-initial-1", entitlementId, selected, "initial");
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var startedJson = await started.Content.ReadFromJsonAsync<JsonElement>();
        var batchId = startedJson.GetProperty("batchId").GetGuid();
        Assert.Equal(7, startedJson.GetProperty("jobIds").GetArrayLength());
        await AssertFinalRenderAudioSnapshots(
            factory,
            batchId,
            seeded.AudioAssetId,
            seeded.AudioFingerprint);

        var replay = await StartRender(client, seeded.ProjectId, "render-initial-1", entitlementId, selected, "initial");
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        await MaterializeRenderOutputs(factory, seeded.WorkspaceId, seeded.ProjectId, batchId, selected);

        var status = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/releases/{seeded.ProjectId}/renders/{batchId}");
        Assert.Equal("succeeded", status.GetProperty("state").GetString());
        Assert.Equal(6, status.GetProperty("items").GetArrayLength());
        Assert.All(status.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal("succeeded", item.GetProperty("state").GetString());
            Assert.Contains($"/api/v1/releases/{seeded.ProjectId}/downloads/", item.GetProperty("download").GetProperty("url").GetString());
        });
        Assert.Contains($"/api/v1/releases/{seeded.ProjectId}/downloads/", status.GetProperty("export").GetProperty("url").GetString());

        var contentChange = await StartRender(client, seeded.ProjectId, "render-content-1", entitlementId, selected, "contentChange");
        Assert.Equal(HttpStatusCode.Accepted, contentChange.StatusCode);
        var contentChangeJson = await contentChange.Content.ReadFromJsonAsync<JsonElement>();
        await MarkFinalRenderJobsFailed(factory, contentChangeJson.GetProperty("batchId").GetGuid());
        await ReplaceActiveAudio(factory, seeded.WorkspaceId, seeded.ProjectId);
        var secondContentChange = await StartRender(client, seeded.ProjectId, "render-content-2", entitlementId, selected, "contentChange");
        Assert.Equal(HttpStatusCode.PaymentRequired, secondContentChange.StatusCode);
        var technicalRetry = await StartRender(client, seeded.ProjectId, "render-retry-1", entitlementId, selected, "technicalRetry");
        Assert.Equal(HttpStatusCode.Accepted, technicalRetry.StatusCode);
        var technicalRetryJson = await technicalRetry.Content.ReadFromJsonAsync<JsonElement>();
        await AssertFinalRenderAudioSnapshots(
            factory,
            technicalRetryJson.GetProperty("batchId").GetGuid(),
            seeded.AudioAssetId,
            seeded.AudioFingerprint);

        var finalSummary = await client.GetFromJsonAsync<JsonElement>("/api/v1/billing/summary");
        var finalEntitlement = Assert.Single(finalSummary.GetProperty("entitlements").EnumerateArray());
        Assert.Equal(0, finalEntitlement.GetProperty("remainingContentRerenders").GetInt32());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Equal(21, await db.Jobs.CountAsync(value => value.ProjectId == seeded.ProjectId));
        Assert.Equal(6, await db.RenderItemUsages.CountAsync(value => value.EntitlementId == entitlementId));
        var storedEntitlement = await db.Entitlements.SingleAsync(value => value.Id == entitlementId);
        storedEntitlement.State = EntitlementState.Revoked;
        storedEntitlement.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var canonicalReplay = await StartRender(client, seeded.ProjectId, "render-initial-1", entitlementId, selected, "initial");
        Assert.Equal(HttpStatusCode.Accepted, canonicalReplay.StatusCode);
    }

    [Fact]
    public async Task Paid_checkout_can_render_its_campaign_snapshot_after_regeneration()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var seeded = await SeedCampaign(factory);
        var selected = seeded.ItemIds.Take(6).ToArray();

        var checkout = await Checkout(client, "snapshot-mini", new
        {
            productCode = BillingProducts.MiniRelease,
            projectId = seeded.ProjectId,
            itemIds = selected,
            returnPath = $"/releases/{seeded.ProjectId}/campaign"
        });
        Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);
        var summary = await client.GetFromJsonAsync<JsonElement>("/api/v1/billing/summary");
        var entitlementId = Assert.Single(summary.GetProperty("entitlements").EnumerateArray())
            .GetProperty("id").GetGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var project = await db.Projects.SingleAsync(value => value.Id == seeded.ProjectId);
            var previous = await db.CampaignPlanRevisions.SingleAsync(value => value.Id == seeded.CampaignRevisionId);
            previous.State = RevisionState.Superseded;
            // Manual campaign edits preserve item IDs. Initial paid output must
            // still use the checkout revision rather than this newer content.
            var replacementIds = seeded.ItemIds;
            var replacement = new CampaignPlanRevision
            {
                WorkspaceId = previous.WorkspaceId,
                ProjectId = previous.ProjectId,
                Number = previous.Number + 1,
                State = RevisionState.ReadyForReview,
                TranscriptRevisionId = previous.TranscriptRevisionId,
                ArtworkPackRevisionId = previous.ArtworkPackRevisionId,
                HookSetRevisionId = previous.HookSetRevisionId,
                SourceFingerprint = new string('9', 64),
                ItemsJson = JsonSerializer.Serialize(replacementIds.Select((id, index) => new CampaignItemRequest(
                    id, index + 1, "teaser", $"hook-{index % 3}", null, $"Replacement {index + 1}", "{}")), StoredJson)
            };
            db.CampaignPlanRevisions.Add(replacement);
            project.CurrentCampaignPlanRevisionId = replacement.Id;
            project.ArtistName = "Artist changed after checkout";
            project.TrackTitle = "Title changed after checkout";
            project.ReleaseDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
            await db.SaveChangesAsync();
        }
        await ReplaceActiveAudio(factory, seeded.WorkspaceId, seeded.ProjectId);

        var started = await StartRender(
            client,
            seeded.ProjectId,
            "snapshot-render",
            entitlementId,
            selected,
            "initial");
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var startedJson = await started.Content.ReadFromJsonAsync<JsonElement>();
        await AssertFinalRenderAudioSnapshots(
            factory,
            startedJson.GetProperty("batchId").GetGuid(),
            seeded.AudioAssetId,
            seeded.AudioFingerprint);
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var batchId = startedJson.GetProperty("batchId").GetGuid();
        var batch = await verifyDb.RenderBatches.AsNoTracking().SingleAsync(value => value.Id == batchId);
        var jobIds = JsonSerializer.Deserialize<List<Guid>>(batch.JobIdsJson, StoredJson) ?? [];
        var payloads = await verifyDb.Jobs.AsNoTracking()
            .Where(value => jobIds.Contains(value.Id) && value.Type == JobType.FinalRender)
            .Select(value => value.PayloadJson)
            .ToListAsync();
        Assert.All(payloads, payload =>
        {
            using var document = JsonDocument.Parse(payload);
            Assert.Equal(seeded.CampaignRevisionId, document.RootElement.GetProperty("campaignRevisionId").GetGuid());
        });
        var exportPayload = await verifyDb.Jobs.AsNoTracking()
            .Where(value => jobIds.Contains(value.Id) && value.Type == JobType.ExportBundle)
            .Select(value => value.PayloadJson)
            .SingleAsync();
        using (var document = JsonDocument.Parse(exportPayload))
        {
            Assert.Equal("Billing artist", document.RootElement.GetProperty("artistName").GetString());
            Assert.Equal("Billing track", document.RootElement.GetProperty("trackTitle").GetString());
            Assert.Equal(
                seeded.ScheduleAnchor,
                DateOnly.Parse(document.RootElement.GetProperty("scheduleAnchor").GetString()!));
            Assert.Equal((int)ReleaseMode.Upcoming, document.RootElement.GetProperty("releaseMode").GetInt32());
        }
    }

    [Fact]
    public async Task Completed_render_history_survives_period_expiry_but_not_revocation()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var seeded = await SeedCampaign(factory);
        var selected = seeded.ItemIds.Take(6).ToArray();
        await Checkout(client, "history-mini", new
        {
            productCode = BillingProducts.MiniRelease,
            projectId = seeded.ProjectId,
            itemIds = selected,
            returnPath = $"/releases/{seeded.ProjectId}/campaign"
        });
        var summary = await client.GetFromJsonAsync<JsonElement>("/api/v1/billing/summary");
        var entitlementId = Assert.Single(summary.GetProperty("entitlements").EnumerateArray())
            .GetProperty("id").GetGuid();
        var started = await StartRender(client, seeded.ProjectId, "history-render", entitlementId, selected, "initial");
        var batchId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("batchId").GetGuid();
        await MaterializeRenderOutputs(factory, seeded.WorkspaceId, seeded.ProjectId, batchId, selected);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var entitlement = await db.Entitlements.SingleAsync(value => value.Id == entitlementId);
            entitlement.ValidUntil = DateTimeOffset.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }
        var history = await client.GetAsync($"/api/v1/releases/{seeded.ProjectId}/renders/{batchId}");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var entitlement = await db.Entitlements.SingleAsync(value => value.Id == entitlementId);
            entitlement.State = EntitlementState.Revoked;
            entitlement.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        var revoked = await client.GetAsync($"/api/v1/releases/{seeded.ProjectId}/renders/{batchId}");
        Assert.Equal(HttpStatusCode.PaymentRequired, revoked.StatusCode);
    }

    [Fact]
    public async Task Clean_cover_download_requires_bound_entitlement_and_refund_revokes_it_idempotently()
    {
        var gateway = new MutablePaymentGateway();
        await using var factory = new Hook2StreamApiFactory(services =>
        {
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(gateway);
        });
        using var client = factory.CreateClient();
        await Onboard(client);
        var seeded = await SeedApprovedCover(factory);

        var beforePurchase = await client.GetAsync(
            $"/api/v1/releases/{seeded.ProjectId}/artwork/clean-cover/download-url");
        Assert.Equal(HttpStatusCode.PaymentRequired, beforePurchase.StatusCode);

        var checkout = await Checkout(client, "cover-1", new
        {
            productCode = BillingProducts.CleanCover,
            projectId = seeded.ProjectId,
            itemIds = (Guid[]?)null,
            returnPath = $"/releases/{seeded.ProjectId}/artwork"
        });
        Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);
        var checkoutJson = await checkout.Content.ReadFromJsonAsync<JsonElement>();
        var checkoutId = checkoutJson.GetProperty("checkoutId").GetGuid();

        var download = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/releases/{seeded.ProjectId}/artwork/clean-cover/download-url");
        Assert.Equal(seeded.CleanAssetId, download.GetProperty("assetId").GetGuid());
        Assert.Equal(3000, download.GetProperty("width").GetInt32());
        Assert.Equal(3000, download.GetProperty("height").GetInt32());
        Assert.Equal($"/api/v1/releases/{seeded.ProjectId}/downloads/{seeded.CleanAssetId}", download.GetProperty("url").GetString());

        var summary = await client.GetFromJsonAsync<JsonElement>("/api/v1/billing/summary");
        var entitlement = Assert.Single(summary.GetProperty("entitlements").EnumerateArray());
        Assert.Equal(seeded.SourceAssetId, Assert.Single(entitlement.GetProperty("itemIds").EnumerateArray()).GetGuid());

        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-cover-refund",
            Type: "charge.refunded",
            CheckoutId: checkoutId,
            ExternalSessionId: "fixture-cover-1",
            ProductCode: BillingProducts.CleanCover,
            WorkspaceId: seeded.WorkspaceId,
            ProjectId: seeded.ProjectId,
            ExternalCustomerId: null,
            ExternalSubscriptionId: null,
            ExternalPaymentIntentId: null,
            ExternalInvoiceId: null,
            ExternalChargeId: "ch-cover-refund",
            Paid: false,
            Refunded: true,
            OccurredAt: DateTimeOffset.UtcNow,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        using var webhookBody = JsonContent.Create(new { eventId = "evt-cover-refund" });
        var refund = await client.PostAsync("/api/v1/billing/stripe/webhook", webhookBody);
        Assert.Equal(HttpStatusCode.OK, refund.StatusCode);
        using var duplicateBody = JsonContent.Create(new { eventId = "evt-cover-refund" });
        var duplicate = await client.PostAsync("/api/v1/billing/stripe/webhook", duplicateBody);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);

        gateway.NextEvent = gateway.NextEvent with
        {
            EventId = "evt-cover-late-paid",
            Type = "checkout.session.completed",
            Paid = true,
            Refunded = false,
            OccurredAt = gateway.NextEvent.OccurredAt.AddMinutes(-5),
            PayloadHash = string.Empty
        };
        using var latePaidBody = JsonContent.Create(new { eventId = "evt-cover-late-paid" });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/billing/stripe/webhook", latePaidBody)).StatusCode);

        var afterRefund = await client.GetAsync(
            $"/api/v1/releases/{seeded.ProjectId}/artwork/clean-cover/download-url");
        Assert.Equal(HttpStatusCode.PaymentRequired, afterRefund.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Equal(EntitlementState.Revoked, (await db.Entitlements.SingleAsync()).State);
        Assert.Equal(2, await db.InboxMessages.CountAsync());
    }

    [Fact]
    public async Task Subscription_period_refund_does_not_block_a_later_paid_renewal()
    {
        var gateway = new MutablePaymentGateway();
        await using var factory = new Hook2StreamApiFactory(services =>
        {
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(gateway);
        });
        using var client = factory.CreateClient();
        await Onboard(client);
        Guid workspaceId;
        Guid checkoutId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            workspaceId = (await db.Workspaces.SingleAsync()).Id;
            var checkout = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProductCode = BillingProducts.ActiveArtist,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                IdempotencyKey = "subscription-checkout",
                RequestHash = new string('1', 64),
                ExternalSessionId = "cs_subscription",
                ExternalSubscriptionId = "sub_subscription",
                CheckoutUrl = "https://payments.example.test/subscription"
            };
            checkoutId = checkout.Id;
            db.BillingCheckouts.Add(checkout);
            await db.SaveChangesAsync();
        }

        var period1Start = DateTimeOffset.UtcNow.AddDays(-20);
        var period1End = period1Start.AddMonths(1);
        gateway.NextEvent = SubscriptionEvent(
            "evt-period-1-paid", checkoutId, workspaceId, "in_period_1", paid: true, refunded: false,
            period1Start, period1End);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "period-1-paid")).StatusCode);

        gateway.NextEvent = SubscriptionEvent(
            "evt-period-1-refund", checkoutId, workspaceId, "in_period_1", paid: false, refunded: true,
            period1Start, period1End);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "period-1-refund")).StatusCode);

        gateway.NextEvent = SubscriptionEvent(
            "evt-period-1-late-paid", checkoutId, workspaceId, "in_period_1", paid: true, refunded: false,
            period1Start, period1End);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "period-1-late-paid")).StatusCode);

        var period2Start = period1End;
        var period2End = period2Start.AddMonths(1);
        gateway.NextEvent = SubscriptionEvent(
            "evt-period-2-paid", checkoutId, workspaceId, "in_period_2", paid: true, refunded: false,
            period2Start, period2End);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "period-2-paid")).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var checkoutState = await verifyDb.BillingCheckouts.Where(value => value.Id == checkoutId).Select(value => value.State).SingleAsync();
        Assert.Equal(CheckoutState.Completed, checkoutState);
        var entitlements = await verifyDb.Entitlements.OrderBy(value => value.PeriodStartsAt).ToListAsync();
        Assert.Equal(2, entitlements.Count);
        Assert.Equal(EntitlementState.Revoked, entitlements[0].State);
        Assert.Equal("in_period_1", entitlements[0].ExternalInvoiceId);
        Assert.Equal(EntitlementState.Active, entitlements[1].State);
        Assert.Equal("in_period_2", entitlements[1].ExternalInvoiceId);
    }

    private static async Task<CampaignSeed> SeedCampaign(Hook2StreamApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var workspace = await db.Workspaces.SingleAsync();
        var project = new ReleaseProject
        {
            WorkspaceId = workspace.Id,
            ProjectLabel = "Billing campaign",
            ArtistName = "Billing artist",
            TrackTitle = "Billing track",
            FlowKind = FlowKind.Mp3First,
            Mode = ReleaseMode.Upcoming,
            ReleaseDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
            BrandKitVersion = 1,
            State = ProjectState.CampaignReady
        };
        var audioFingerprint = new string('a', 64);
        var audio = new MediaAsset
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Kind = AssetKind.Audio,
            Origin = AssetOrigin.Uploaded,
            Purpose = AssetPurpose.AudioMaster,
            State = AssetState.Ready,
            OriginalFileName = "billing-track.mp3",
            DeclaredContentType = "audio/mpeg",
            DetectedContentType = "audio/mpeg",
            DeclaredBytes = 1_000,
            ActualBytes = 1_000,
            ObjectKey = $"tests/{project.Id:N}/billing-track.mp3",
            IsActive = true,
            Sha256 = audioFingerprint,
            DurationMilliseconds = 180_000
        };
        var itemIds = Enumerable.Range(1, 18).Select(_ => Guid.CreateVersion7()).ToArray();
        var approvedCover = new MediaAsset
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Kind = AssetKind.Cover,
            Origin = AssetOrigin.Generated,
            Purpose = AssetPurpose.ApprovedCover,
            State = AssetState.Ready,
            OriginalFileName = "billing-cover.png",
            DeclaredContentType = "image/png",
            DetectedContentType = "image/png",
            DeclaredBytes = 1_000,
            ActualBytes = 1_000,
            ObjectKey = $"tests/{project.Id:N}/billing-cover.png",
            IsActive = true,
            Sha256 = new string('d', 64),
            Width = 1024,
            Height = 1024
        };
        var artwork = new ArtworkPackRevision
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Number = 1,
            OperationNumber = 1,
            State = RevisionState.Approved,
            CandidateAssetIdsJson = JsonSerializer.Serialize(new[] { approvedCover.Id }, StoredJson),
            SelectedAssetId = approvedCover.Id,
            CompositionJson = "{}",
            SourceFingerprint = new string('e', 64),
            ApprovedAt = DateTimeOffset.UtcNow
        };
        var campaign = new CampaignPlanRevision
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Number = 1,
            State = RevisionState.ReadyForReview,
            TranscriptRevisionId = Guid.CreateVersion7(),
            ArtworkPackRevisionId = artwork.Id,
            HookSetRevisionId = Guid.CreateVersion7(),
            SourceFingerprint = new string('c', 64),
            ItemsJson = JsonSerializer.Serialize(itemIds.Select((id, index) => new CampaignItemRequest(
                id,
                index + 1,
                "teaser",
                $"hook-{index % 3}",
                null,
                $"Campaign item {index + 1}",
                "{}")), StoredJson)
        };
        project.CurrentCampaignPlanRevisionId = campaign.Id;
        project.CurrentArtworkPackRevisionId = artwork.Id;
        var rights = new RightsAttestation
        {
            ProjectId = project.Id,
            ActorSubject = "billing-test-user",
            PolicyVersion = "test-v1",
            OwnsAudioRights = true,
            OwnsLyricsRights = true,
            OwnsVisualRights = true,
            AllowsExternalAiArtwork = true,
            AudioAssetId = audio.Id,
            AudioFingerprint = audioFingerprint,
            AcceptedAt = DateTimeOffset.UtcNow
        };
        db.Projects.Add(project);
        db.MediaAssets.AddRange(audio, approvedCover);
        db.ArtworkPackRevisions.Add(artwork);
        db.CampaignPlanRevisions.Add(campaign);
        db.RightsAttestations.Add(rights);
        await db.SaveChangesAsync();
        return new CampaignSeed(
            workspace.Id,
            project.Id,
            campaign.Id,
            project.ReleaseDate!.Value,
            itemIds,
            audio.Id,
            audioFingerprint);
    }

    private static async Task<CoverSeed> SeedApprovedCover(Hook2StreamApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var workspace = await db.Workspaces.SingleAsync();
        var project = new ReleaseProject
        {
            WorkspaceId = workspace.Id,
            ProjectLabel = "Cover purchase",
            ArtistName = "Cover artist",
            TrackTitle = "Cover track",
            FlowKind = FlowKind.Mp3First,
            Mode = ReleaseMode.Released,
            ReleaseDate = DateOnly.FromDateTime(DateTime.UtcNow),
            BrandKitVersion = 1
        };
        var source = new MediaAsset
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Kind = AssetKind.Cover,
            Origin = AssetOrigin.Generated,
            Purpose = AssetPurpose.ApprovedCover,
            State = AssetState.Ready,
            OriginalFileName = "approved-source.png",
            DeclaredContentType = "image/png",
            DetectedContentType = "image/png",
            DeclaredBytes = 10,
            ActualBytes = 10,
            ObjectKey = $"tests/{project.Id:N}/approved-source.png",
            Width = 2048,
            Height = 2048,
            IsActive = true,
            Sha256 = new string('d', 64)
        };
        var clean = new MediaAsset
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Kind = AssetKind.Cover,
            Origin = AssetOrigin.Generated,
            Purpose = AssetPurpose.CleanCover,
            State = AssetState.Ready,
            OriginalFileName = "cover-3000x3000.png",
            DeclaredContentType = "image/png",
            DetectedContentType = "image/png",
            DeclaredBytes = 20,
            ActualBytes = 20,
            ObjectKey = $"tests/{project.Id:N}/clean-cover-{Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{project.ArtistName}\n{project.TrackTitle}")))[..16]}-3000.png",
            Width = 3000,
            Height = 3000,
            IsActive = true,
            SupersedesAssetId = source.Id,
            Sha256 = new string('e', 64)
        };
        var artwork = new ArtworkPackRevision
        {
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Number = 1,
            OperationNumber = 1,
            State = RevisionState.Approved,
            Prompt = "Approved cover",
            CandidateAssetIdsJson = JsonSerializer.Serialize(new[] { source.Id }, StoredJson),
            SelectedAssetId = source.Id,
            CompositionJson = "{}",
            SourceFingerprint = new string('f', 64),
            ApprovedBySubject = "user-a",
            ApprovedAt = DateTimeOffset.UtcNow
        };
        clean.ArtworkPackRevisionId = artwork.Id;
        project.CurrentArtworkPackRevisionId = artwork.Id;
        db.Projects.Add(project);
        db.MediaAssets.AddRange(source, clean);
        db.ArtworkPackRevisions.Add(artwork);
        await db.SaveChangesAsync();
        return new CoverSeed(workspace.Id, project.Id, source.Id, clean.Id);
    }

    private static async Task MaterializeRenderOutputs(
        Hook2StreamApiFactory factory,
        Guid workspaceId,
        Guid projectId,
        Guid batchId,
        IReadOnlyList<Guid> itemIds)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var batch = await db.RenderBatches.SingleAsync(value => value.Id == batchId);
        batch.State = RenderBatchState.Succeeded;
        batch.CompletedAt = DateTimeOffset.UtcNow;
        var jobIds = JsonSerializer.Deserialize<List<Guid>>(batch.JobIdsJson, StoredJson) ?? [];
        var jobs = await db.Jobs.Where(value => jobIds.Contains(value.Id)).ToListAsync();
        foreach (var job in jobs)
        {
            job.State = JobState.Succeeded;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }

        foreach (var itemId in itemIds)
        {
            db.MediaAssets.Add(new MediaAsset
            {
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Kind = AssetKind.Visual,
                Origin = AssetOrigin.Generated,
                Purpose = AssetPurpose.CampaignVideo,
                State = AssetState.Ready,
                OriginalFileName = $"{itemId:N}.mp4",
                DeclaredContentType = "video/mp4",
                DetectedContentType = "video/mp4",
                DeclaredBytes = 100,
                ActualBytes = 100,
                ObjectKey = $"tests/{projectId:N}/{batchId:N}/{itemId:N}.mp4",
                CampaignItemId = itemId,
                RenderBatchId = batchId,
                IsActive = true
            });
        }

        db.MediaAssets.Add(new MediaAsset
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Kind = AssetKind.Visual,
            Origin = AssetOrigin.Generated,
            Purpose = AssetPurpose.ExportBundle,
            State = AssetState.Ready,
            OriginalFileName = "campaign.zip",
            DeclaredContentType = "application/zip",
            DetectedContentType = "application/zip",
            DeclaredBytes = 1_000,
            ActualBytes = 1_000,
            ObjectKey = $"tests/{projectId:N}/{batchId:N}/campaign.zip",
            RenderBatchId = batchId,
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    private static async Task MarkFinalRenderJobsFailed(Hook2StreamApiFactory factory, Guid batchId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var batch = await db.RenderBatches.SingleAsync(value => value.Id == batchId);
        var jobIds = JsonSerializer.Deserialize<List<Guid>>(batch.JobIdsJson, StoredJson) ?? [];
        var jobs = await db.Jobs
            .Where(value => jobIds.Contains(value.Id) && value.Type == JobType.FinalRender)
            .ToListAsync();
        foreach (var job in jobs)
        {
            job.State = JobState.Failed;
            job.ErrorCode = "render.fixture_failure";
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
        batch.State = RenderBatchState.Failed;
        batch.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static async Task AssertFinalRenderAudioSnapshots(
        Hook2StreamApiFactory factory,
        Guid batchId,
        Guid audioAssetId,
        string audioFingerprint)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var batch = await db.RenderBatches.AsNoTracking().SingleAsync(value => value.Id == batchId);
        var jobIds = JsonSerializer.Deserialize<List<Guid>>(batch.JobIdsJson, StoredJson) ?? [];
        var payloads = await db.Jobs.AsNoTracking()
            .Where(value => jobIds.Contains(value.Id) && value.Type == JobType.FinalRender)
            .Select(value => value.PayloadJson)
            .ToListAsync();
        Assert.NotEmpty(payloads);
        Assert.All(payloads, payloadJson =>
        {
            using var payload = JsonDocument.Parse(payloadJson);
            Assert.Equal(audioAssetId, payload.RootElement.GetProperty("audioAssetId").GetGuid());
            Assert.Equal(audioFingerprint, payload.RootElement.GetProperty("audioFingerprint").GetString());
        });
    }

    private static async Task ReplaceActiveAudio(
        Hook2StreamApiFactory factory,
        Guid workspaceId,
        Guid projectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var previous = await db.MediaAssets.SingleAsync(value =>
            value.ProjectId == projectId && value.Kind == AssetKind.Audio && value.IsActive);
        previous.IsActive = false;
        db.MediaAssets.Add(new MediaAsset
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Kind = AssetKind.Audio,
            Origin = AssetOrigin.Uploaded,
            Purpose = AssetPurpose.AudioMaster,
            State = AssetState.Ready,
            OriginalFileName = "replacement.mp3",
            DeclaredContentType = "audio/mpeg",
            DetectedContentType = "audio/mpeg",
            DeclaredBytes = 2_000,
            ActualBytes = 2_000,
            ObjectKey = $"tests/{projectId:N}/replacement.mp3",
            IsActive = true,
            Sha256 = new string('b', 64),
            DurationMilliseconds = 181_000,
            SupersedesAssetId = previous.Id,
            Revision = previous.Revision + 1
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> Checkout(HttpClient client, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/checkouts")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> StartRender(
        HttpClient client,
        Guid projectId,
        string key,
        Guid entitlementId,
        IReadOnlyList<Guid> itemIds,
        string kind)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/releases/{projectId}/renders")
        {
            Content = JsonContent.Create(new { entitlementId, itemIds, kind })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostWebhook(HttpClient client, string marker) =>
        client.PostAsJsonAsync("/api/v1/billing/stripe/webhook", new { marker });

    private static PaymentWebhookEvent SubscriptionEvent(
        string eventId,
        Guid checkoutId,
        Guid workspaceId,
        string invoiceId,
        bool paid,
        bool refunded,
        DateTimeOffset periodStartsAt,
        DateTimeOffset periodEndsAt) => new(
            EventId: eventId,
            Type: paid ? "invoice.paid" : "charge.refunded",
            CheckoutId: checkoutId,
            ExternalSessionId: null,
            ProductCode: BillingProducts.ActiveArtist,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: "cus_subscription",
            ExternalSubscriptionId: "sub_subscription",
            ExternalPaymentIntentId: $"pi_{invoiceId}",
            ExternalInvoiceId: invoiceId,
            ExternalChargeId: refunded ? $"ch_{invoiceId}" : null,
            Paid: paid,
            Refunded: refunded,
            OccurredAt: periodStartsAt.AddHours(1),
            PeriodStartsAt: periodStartsAt,
            PeriodEndsAt: periodEndsAt,
            PayloadHash: string.Empty);

    private static async Task Onboard(HttpClient client)
    {
        var response = await client.PutAsJsonAsync("/api/v1/account/onboarding", new
        {
            workspaceName = "Billing tests",
            acceptTerms = true,
            acceptPrivacy = true,
            termsVersion = "draft-2026-07-16",
            privacyVersion = "draft-2026-07-16",
            displayName = "Billing artist"
        });
        response.EnsureSuccessStatusCode();
    }

    private sealed record CampaignSeed(
        Guid WorkspaceId,
        Guid ProjectId,
        Guid CampaignRevisionId,
        DateOnly ScheduleAnchor,
        Guid[] ItemIds,
        Guid AudioAssetId,
        string AudioFingerprint);
    private sealed record CoverSeed(Guid WorkspaceId, Guid ProjectId, Guid SourceAssetId, Guid CleanAssetId);

    private sealed class MutablePaymentGateway : IPaymentGateway
    {
        public PaymentWebhookEvent? NextEvent { get; set; }

        public Task<PaymentCheckoutResult> CreateCheckoutAsync(
            PaymentCheckoutCommand command,
            CancellationToken cancellationToken) => Task.FromResult(new PaymentCheckoutResult(
            $"fixture-{command.CheckoutId:N}",
            new Uri($"https://payments.example.test/{command.CheckoutId:N}"),
            CompletedSynchronously: true));

        public PaymentWebhookEvent ParseAndVerifyWebhook(
            ReadOnlySpan<byte> payload,
            string signatureHeader,
            DateTimeOffset now)
        {
            var value = NextEvent ?? throw new InvalidOperationException("No webhook was configured.");
            return value with { PayloadHash = Convert.ToHexStringLower(SHA256.HashData(payload)) };
        }
    }
}
