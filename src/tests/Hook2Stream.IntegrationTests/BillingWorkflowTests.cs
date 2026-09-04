using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Billing;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hook2Stream.IntegrationTests;

public sealed class BillingWorkflowTests
{
    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Disabled_billing_keeps_summary_available_and_rejects_commands_before_body_or_provider_work()
    {
        await using var factory = new Hook2StreamApiFactory(services =>
        {
            services.PostConfigure<StripeOptions>(options =>
            {
                options.Mode = PaymentGatewayMode.Disabled;
                options.SecretKey = string.Empty;
                options.WebhookSecret = string.Empty;
                options.PriceIds.Clear();
            });
            services.RemoveAll<IPaymentGateway>();
            services.AddTransient<IPaymentGateway>(_ =>
                throw new InvalidOperationException("A disabled endpoint must not resolve the payment provider."));
        });
        using var client = factory.CreateClient();
        await Onboard(client);

        var summary = await client.GetFromJsonAsync<JsonElement>("/api/v1/billing/summary");
        Assert.False(summary.GetProperty("checkoutEnabled").GetBoolean());
        Assert.Equal(0, summary.GetProperty("workspaceArtworkCredits").GetInt32());
        Assert.Empty(summary.GetProperty("entitlements").EnumerateArray());

        using var malformedCheckout = new StringContent("{not-json", Encoding.UTF8, "application/json");
        var checkout = await client.PostAsync("/api/v1/billing/checkouts", malformedCheckout);
        await AssertBillingDisabled(checkout);

        using var oversizedWebhook = new ByteArrayContent(new byte[1024 * 1024 + 1]);
        oversizedWebhook.Headers.ContentType = new("application/json");
        var webhook = await client.PostAsync("/api/v1/billing/stripe/webhook", oversizedWebhook);
        await AssertBillingDisabled(webhook);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Empty(await db.BillingCheckouts.AsNoTracking().ToListAsync());
        Assert.Empty(await db.InboxMessages.AsNoTracking().ToListAsync());
    }

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
        Assert.True(summary.GetProperty("checkoutEnabled").GetBoolean());
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
            Disposition: PaymentWebhookDisposition.Refunded,
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
            Disposition: PaymentWebhookDisposition.CheckoutFailed,
            OccurredAt: DateTimeOffset.UtcNow,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "checkout-expired")).StatusCode);

        gateway.NextEvent = gateway.NextEvent with
        {
            EventId = "evt-checkout-late-success",
            Type = "checkout.session.async_payment_succeeded",
            Disposition = PaymentWebhookDisposition.Paid,
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
    public async Task Release_pack_checkout_and_webhook_keep_eighteen_item_snapshot_out_of_provider_metadata()
    {
        const string webhookSecret = "whsec_release_pack_snapshot_integration";
        var stripeHandler = new CapturingStripeCheckoutHandler();
        using var stripeClient = new HttpClient(stripeHandler);
        var gateway = new StripePaymentGateway(
            stripeClient,
            Options.Create(new StripeOptions
            {
                ApiBaseUrl = "https://api.stripe.test",
                SecretKey = "sk_test_release_pack",
                WebhookSecret = webhookSecret,
                WebhookToleranceSeconds = 300,
                PriceIds = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [BillingProducts.ReleasePack] = "price_release_pack"
                }
            }));
        await using var factory = new Hook2StreamApiFactory(services =>
        {
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(gateway);
        });
        using var client = factory.CreateClient();
        await Onboard(client);
        var seeded = await SeedCampaign(factory);

        var checkoutResponse = await Checkout(client, "release-pack-snapshot", new
        {
            productCode = BillingProducts.ReleasePack,
            projectId = seeded.ProjectId,
            itemIds = seeded.ItemIds,
            returnPath = $"/releases/{seeded.ProjectId}/campaign"
        });

        Assert.Equal(HttpStatusCode.Created, checkoutResponse.StatusCode);
        var checkoutJson = await checkoutResponse.Content.ReadFromJsonAsync<JsonElement>();
        var checkoutId = checkoutJson.GetProperty("checkoutId").GetGuid();
        Assert.DoesNotContain(stripeHandler.Fields.Keys, key => key.Contains("item_ids", StringComparison.Ordinal));
        Assert.Equal(checkoutId.ToString("N"), stripeHandler.Fields["metadata[checkout_id]"]);
        Assert.Equal(seeded.WorkspaceId.ToString("N"), stripeHandler.Fields["metadata[workspace_id]"]);
        Assert.Equal(seeded.ProjectId.ToString("N"), stripeHandler.Fields["metadata[project_id]"]);
        Assert.Equal(BillingProducts.ReleasePack, stripeHandler.Fields["metadata[product_code]"]);

        var webhookMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["checkout_id"] = checkoutId.ToString("N"),
            ["workspace_id"] = seeded.WorkspaceId.ToString("N"),
            ["project_id"] = seeded.ProjectId.ToString("N"),
            ["product_code"] = BillingProducts.ReleasePack
        };
        Assert.DoesNotContain("item_ids", webhookMetadata.Keys);
        var webhook = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "evt_release_pack_snapshot",
            type = "checkout.session.completed",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = "cs_test_release_pack",
                    customer = "cus_release_pack",
                    payment_intent = "pi_release_pack",
                    payment_status = "paid",
                    amount_total = BillingProducts.AmountCents(BillingProducts.ReleasePack),
                    currency = "usd",
                    metadata = webhookMetadata
                }
            }
        });

        Assert.Equal(
            HttpStatusCode.OK,
            (await PostSignedStripeWebhook(client, webhook, webhookSecret)).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var checkout = await db.BillingCheckouts.AsNoTracking().SingleAsync(value => value.Id == checkoutId);
        var entitlement = await db.Entitlements.AsNoTracking().SingleAsync(value => value.CheckoutId == checkoutId);
        Assert.Equal(seeded.ItemIds, JsonSerializer.Deserialize<Guid[]>(checkout.ItemIdsJson, StoredJson));
        Assert.Equal(seeded.ItemIds, JsonSerializer.Deserialize<Guid[]>(entitlement.ItemIdsJson, StoredJson));
        Assert.Equal(18, entitlement.IncludedItemCount);
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
            ExternalSessionId: $"fixture-{checkoutId:N}",
            ProductCode: BillingProducts.CleanCover,
            WorkspaceId: seeded.WorkspaceId,
            ProjectId: seeded.ProjectId,
            ExternalCustomerId: null,
            ExternalSubscriptionId: null,
            ExternalPaymentIntentId: null,
            ExternalInvoiceId: null,
            ExternalChargeId: "ch-cover-refund",
            Disposition: PaymentWebhookDisposition.Refunded,
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
            Disposition = PaymentWebhookDisposition.Paid,
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
            "evt-period-1-paid", checkoutId, workspaceId, "sub_subscription", "in_period_1",
            paid: true, refunded: false, period1Start.AddHours(1), period1Start, period1End);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "period-1-paid")).StatusCode);

        gateway.NextEvent = SubscriptionEvent(
            "evt-period-1-refund", checkoutId, workspaceId, "sub_subscription", "in_period_1",
            paid: false, refunded: true, period1Start.AddHours(2), period1Start, period1End);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "period-1-refund")).StatusCode);

        gateway.NextEvent = SubscriptionEvent(
            "evt-period-1-late-paid", checkoutId, workspaceId, "sub_subscription", "in_period_1",
            paid: true, refunded: false, period1Start.AddHours(3), period1Start, period1End);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "period-1-late-paid")).StatusCode);

        var period2Start = period1End;
        var period2End = period2Start.AddMonths(1);
        gateway.NextEvent = SubscriptionEvent(
            "evt-period-2-paid", checkoutId, workspaceId, "sub_subscription", "in_period_2",
            paid: true, refunded: false, period2Start.AddHours(1), period2Start, period2End);
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

    [Theory]
    [InlineData("customer.subscription.deleted")]
    [InlineData("customer.subscription.paused")]
    [InlineData("customer.subscription.updated")]
    public async Task Subscription_access_end_is_idempotent_preserves_history_and_late_paid_does_not_restore_the_period(
        string eventType)
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
        Guid firstEntitlementId;
        var periodStart = DateTimeOffset.UtcNow.AddDays(-10);
        var periodEnd = periodStart.AddMonths(1);
        var revokedAt = periodStart.AddDays(5);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            workspaceId = (await db.Workspaces.SingleAsync()).Id;
            var checkout = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProductCode = BillingProducts.ActiveArtist,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                IdempotencyKey = $"subscription-revoke-{eventType}",
                RequestHash = new string('2', 64),
                State = CheckoutState.Completed,
                ExternalSessionId = $"cs_{eventType}",
                ExternalSubscriptionId = "sub_lifecycle",
                CheckoutUrl = "https://payments.example.test/subscription-lifecycle",
                CompletedAt = periodStart
            };
            var entitlement = new Entitlement
            {
                WorkspaceId = workspaceId,
                CheckoutId = checkout.Id,
                ProductCode = BillingProducts.ActiveArtist,
                ProviderPeriodKey = "invoice:in_lifecycle_1",
                ExternalSubscriptionId = "sub_lifecycle",
                ExternalInvoiceId = "in_lifecycle_1",
                ProviderEventOccurredAt = periodStart.AddHours(1),
                PeriodStartsAt = periodStart,
                ValidUntil = periodEnd,
                IncludedItemCount = 18,
                RemainingContentRerenders = 18
            };
            checkoutId = checkout.Id;
            firstEntitlementId = entitlement.Id;
            db.BillingCheckouts.Add(checkout);
            db.Entitlements.Add(entitlement);
            await db.SaveChangesAsync();
        }

        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: $"evt-{eventType}-revoked",
            Type: eventType,
            CheckoutId: null,
            ExternalSessionId: null,
            ProductCode: BillingProducts.ActiveArtist,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: "cus_subscription",
            ExternalSubscriptionId: "sub_lifecycle",
            ExternalPaymentIntentId: null,
            ExternalInvoiceId: null,
            ExternalChargeId: null,
            Disposition: PaymentWebhookDisposition.SubscriptionAccessEnded,
            OccurredAt: revokedAt,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "subscription-access-revoked")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "subscription-access-revoked")).StatusCode);

        gateway.NextEvent = SubscriptionEvent(
            $"evt-{eventType}-late-paid",
            checkoutId,
            workspaceId,
            "sub_lifecycle",
            "in_lifecycle_1",
            paid: true,
            refunded: false,
            revokedAt.AddSeconds(-1),
            periodStart,
            periodEnd);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "subscription-late-paid")).StatusCode);

        var nextPeriodStart = periodEnd;
        var nextPeriodEnd = nextPeriodStart.AddMonths(1);
        gateway.NextEvent = SubscriptionEvent(
            $"evt-{eventType}-next-paid",
            checkoutId,
            workspaceId,
            "sub_lifecycle",
            "in_lifecycle_2",
            paid: true,
            refunded: false,
            revokedAt.AddSeconds(1),
            nextPeriodStart,
            nextPeriodEnd);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "subscription-next-paid")).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var entitlements = await verifyDb.Entitlements.OrderBy(value => value.PeriodStartsAt).ToListAsync();
        Assert.Equal(2, entitlements.Count);
        Assert.Equal(firstEntitlementId, entitlements[0].Id);
        Assert.Equal(EntitlementState.Active, entitlements[0].State);
        Assert.Null(entitlements[0].RevokedAt);
        Assert.Equal(revokedAt, entitlements[0].ValidUntil);
        Assert.Equal("in_lifecycle_1", entitlements[0].ExternalInvoiceId);
        Assert.Equal(EntitlementState.Active, entitlements[1].State);
        Assert.Equal("in_lifecycle_2", entitlements[1].ExternalInvoiceId);
        Assert.Equal(1, await verifyDb.AuditEvents.CountAsync(value => value.Action == "billing.provider_subscription_access_ended"));
        Assert.Equal(3, await verifyDb.InboxMessages.CountAsync());
    }

    [Fact]
    public async Task Out_of_order_subscription_end_blocks_an_older_event_but_allows_overdue_payment_created_later()
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
        var revokedAt = DateTimeOffset.UtcNow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            workspaceId = (await db.Workspaces.SingleAsync()).Id;
            var checkout = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProductCode = BillingProducts.ActiveArtist,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                IdempotencyKey = "subscription-out-of-order-revoke",
                RequestHash = new string('5', 64),
                ExternalSubscriptionId = "sub_out_of_order",
                CheckoutUrl = "https://payments.example.test/subscription-out-of-order"
            };
            checkoutId = checkout.Id;
            db.BillingCheckouts.Add(checkout);
            await db.SaveChangesAsync();
        }

        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-out-of-order-revoked",
            Type: "customer.subscription.deleted",
            CheckoutId: null,
            ExternalSessionId: null,
            ProductCode: BillingProducts.ActiveArtist,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: "cus_subscription",
            ExternalSubscriptionId: "sub_out_of_order",
            ExternalPaymentIntentId: null,
            ExternalInvoiceId: null,
            ExternalChargeId: null,
            Disposition: PaymentWebhookDisposition.SubscriptionAccessEnded,
            OccurredAt: revokedAt,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "out-of-order-revoked")).StatusCode);

        var oldStart = revokedAt.AddDays(-20);
        gateway.NextEvent = SubscriptionEvent(
            "evt-out-of-order-old-paid",
            checkoutId,
            workspaceId,
            "sub_out_of_order",
            "in_out_of_order_old",
            paid: true,
            refunded: false,
            revokedAt.AddSeconds(-1),
            oldStart,
            oldStart.AddMonths(1));
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "out-of-order-old-paid")).StatusCode);

        var overdueStart = revokedAt.AddDays(-2);
        gateway.NextEvent = SubscriptionEvent(
            "evt-out-of-order-new-paid",
            checkoutId,
            workspaceId,
            "sub_out_of_order",
            "in_out_of_order_new",
            paid: true,
            refunded: false,
            revokedAt.AddSeconds(1),
            overdueStart,
            overdueStart.AddMonths(1));
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "out-of-order-new-paid")).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var entitlement = await verifyDb.Entitlements.SingleAsync();
        Assert.Equal("in_out_of_order_new", entitlement.ExternalInvoiceId);
        Assert.Equal(EntitlementState.Active, entitlement.State);
        Assert.Equal(revokedAt.AddSeconds(1), entitlement.ProviderEventOccurredAt);
    }

    [Fact]
    public async Task Older_subscription_end_delivered_after_newer_payment_does_not_clamp_the_new_period()
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
            workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
            var checkout = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProductCode = BillingProducts.ActiveArtist,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                IdempotencyKey = "subscription-reverse-delivery",
                RequestHash = new string('6', 64),
                ExternalSubscriptionId = "sub_reverse_delivery",
                CheckoutUrl = "https://payments.example.test/subscription-reverse-delivery"
            };
            checkoutId = checkout.Id;
            db.BillingCheckouts.Add(checkout);
            await db.SaveChangesAsync();
        }

        var accessEndedAt = DateTimeOffset.UtcNow;
        var paidOccurredAt = accessEndedAt.AddMinutes(1);
        var overduePeriodStart = accessEndedAt.AddDays(-3);
        var periodEnd = accessEndedAt.AddDays(27);
        gateway.NextEvent = SubscriptionEvent(
            "evt-reverse-delivery-paid",
            checkoutId,
            workspaceId,
            "sub_reverse_delivery",
            "in_reverse_delivery",
            paid: true,
            refunded: false,
            paidOccurredAt,
            overduePeriodStart,
            periodEnd);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "reverse-delivery-paid")).StatusCode);

        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-reverse-delivery-ended",
            Type: "customer.subscription.deleted",
            CheckoutId: null,
            ExternalSessionId: null,
            ProductCode: BillingProducts.ActiveArtist,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: "cus_subscription",
            ExternalSubscriptionId: "sub_reverse_delivery",
            ExternalPaymentIntentId: null,
            ExternalInvoiceId: null,
            ExternalChargeId: null,
            Disposition: PaymentWebhookDisposition.SubscriptionAccessEnded,
            OccurredAt: accessEndedAt,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "reverse-delivery-ended")).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var entitlement = await verifyDb.Entitlements.SingleAsync();
        Assert.Equal(EntitlementState.Active, entitlement.State);
        Assert.Null(entitlement.RevokedAt);
        Assert.Equal(periodEnd, entitlement.ValidUntil);
        Assert.Equal(paidOccurredAt, entitlement.ProviderEventOccurredAt);
        Assert.Equal(
            accessEndedAt,
            await verifyDb.BillingCheckouts.Where(value => value.Id == checkoutId)
                .Select(value => value.SubscriptionAccessEndedAt)
                .SingleAsync());
    }

    [Fact]
    public async Task Subscription_payment_failure_is_audited_without_premature_revocation()
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
                IdempotencyKey = "subscription-payment-failed",
                RequestHash = new string('3', 64),
                State = CheckoutState.Completed,
                ExternalSubscriptionId = "sub_payment_failed",
                CheckoutUrl = "https://payments.example.test/subscription-payment-failed"
            };
            checkoutId = checkout.Id;
            db.BillingCheckouts.Add(checkout);
            db.Entitlements.Add(new Entitlement
            {
                WorkspaceId = workspaceId,
                CheckoutId = checkout.Id,
                ProductCode = BillingProducts.ActiveArtist,
                ProviderPeriodKey = "invoice:in_paid_before_retry",
                ExternalSubscriptionId = "sub_payment_failed",
                ExternalInvoiceId = "in_paid_before_retry",
                PeriodStartsAt = DateTimeOffset.UtcNow.AddDays(-5),
                ValidUntil = DateTimeOffset.UtcNow.AddDays(25)
            });
            await db.SaveChangesAsync();
        }

        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-invoice-payment-failed",
            Type: "invoice.payment_failed",
            CheckoutId: checkoutId,
            ExternalSessionId: null,
            ProductCode: BillingProducts.ActiveArtist,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: "cus_payment_failed",
            ExternalSubscriptionId: "sub_payment_failed",
            ExternalPaymentIntentId: "pi_payment_failed",
            ExternalInvoiceId: "in_payment_failed",
            ExternalChargeId: null,
            Disposition: PaymentWebhookDisposition.PaymentFailed,
            OccurredAt: DateTimeOffset.UtcNow,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "invoice-payment-failed")).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Equal(EntitlementState.Active, (await verifyDb.Entitlements.SingleAsync()).State);
        Assert.Equal(CheckoutState.Completed, (await verifyDb.BillingCheckouts.SingleAsync()).State);
        Assert.Equal(1, await verifyDb.AuditEvents.CountAsync(value => value.Action == "billing.provider_payment_failed"));
    }

    [Fact]
    public async Task One_time_dispute_then_refund_is_idempotent_and_reconciles_revocation_once()
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
        Guid projectId;
        Guid entitlementId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            workspaceId = (await db.Workspaces.SingleAsync()).Id;
            var project = new ReleaseProject
            {
                WorkspaceId = workspaceId,
                ProjectLabel = "Release pack dispute",
                ArtistName = "Disputed artist",
                TrackTitle = "Disputed track"
            };
            var checkout = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProjectId = project.Id,
                ProductCode = BillingProducts.ReleasePack,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ReleasePack),
                IdempotencyKey = "release-pack-dispute",
                RequestHash = new string('4', 64),
                State = CheckoutState.Completed,
                ExternalPaymentIntentId = "pi_disputed",
                CheckoutUrl = "https://payments.example.test/release-pack-dispute"
            };
            var entitlement = new Entitlement
            {
                WorkspaceId = workspaceId,
                CheckoutId = checkout.Id,
                ProjectId = project.Id,
                ProductCode = BillingProducts.ReleasePack,
                ProviderPeriodKey = "purchase",
                ExternalPaymentIntentId = "pi_disputed",
                IncludedItemCount = 18,
                RemainingContentRerenders = 18
            };
            checkoutId = checkout.Id;
            projectId = project.Id;
            entitlementId = entitlement.Id;
            db.Projects.Add(project);
            db.BillingCheckouts.Add(checkout);
            db.Entitlements.Add(entitlement);
            await db.SaveChangesAsync();
        }

        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-charge-disputed",
            Type: "charge.dispute.created",
            CheckoutId: null,
            ExternalSessionId: null,
            ProductCode: null,
            WorkspaceId: null,
            ProjectId: null,
            ExternalCustomerId: null,
            ExternalSubscriptionId: null,
            ExternalPaymentIntentId: "pi_disputed",
            ExternalInvoiceId: null,
            ExternalChargeId: "ch_disputed",
            Disposition: PaymentWebhookDisposition.Disputed,
            OccurredAt: DateTimeOffset.UtcNow,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "charge-disputed")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "charge-disputed")).StatusCode);

        gateway.NextEvent = gateway.NextEvent with
        {
            EventId = "evt-charge-refunded-after-dispute",
            Type = "charge.refunded",
            Disposition = PaymentWebhookDisposition.Refunded,
            OccurredAt = gateway.NextEvent.OccurredAt.AddMinutes(1),
            PayloadHash = string.Empty
        };
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "charge-refunded-after-dispute")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "charge-refunded-after-dispute")).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Equal(EntitlementState.Revoked, (await verifyDb.Entitlements.SingleAsync()).State);
        var verifiedCheckout = await verifyDb.BillingCheckouts.SingleAsync(value => value.Id == checkoutId);
        Assert.Equal(CheckoutState.Refunded, verifiedCheckout.State);
        Assert.NotNull(verifiedCheckout.RefundedAt);
        Assert.Equal(1, await verifyDb.AuditEvents.CountAsync(value => value.Action == "billing.provider_access_revoked"));
        Assert.Equal(
            1,
            await verifyDb.OutboxMessages.CountAsync(value =>
                value.DedupeKey == $"pipeline.reconcile:{projectId:N}:entitlement.revoked:{entitlementId:N}"));
    }

    [Fact]
    public async Task Subscription_dispute_then_refund_revokes_only_the_period_and_reconciles_once()
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
        Guid projectId;
        Guid disputedEntitlementId;
        Guid retainedEntitlementId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
            var project = new ReleaseProject
            {
                WorkspaceId = workspaceId,
                ProjectLabel = "Subscription period dispute",
                ArtistName = "Subscription artist",
                TrackTitle = "Subscription track"
            };
            var checkout = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProjectId = project.Id,
                ProductCode = BillingProducts.ActiveArtist,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                IdempotencyKey = "subscription-period-dispute",
                RequestHash = new string('7', 64),
                State = CheckoutState.Completed,
                ExternalCustomerId = "cus_subscription_dispute",
                ExternalSubscriptionId = "sub_subscription_dispute",
                CheckoutUrl = "https://payments.example.test/subscription-period-dispute"
            };
            var disputed = new Entitlement
            {
                WorkspaceId = workspaceId,
                CheckoutId = checkout.Id,
                ProjectId = project.Id,
                ProductCode = BillingProducts.ActiveArtist,
                ProviderPeriodKey = "invoice:in_disputed_period",
                ExternalSubscriptionId = "sub_subscription_dispute",
                ExternalPaymentIntentId = "pi_disputed_period",
                ExternalInvoiceId = "in_disputed_period",
                ProviderEventOccurredAt = DateTimeOffset.UtcNow.AddMonths(-1),
                PeriodStartsAt = DateTimeOffset.UtcNow.AddMonths(-1),
                ValidUntil = DateTimeOffset.UtcNow
            };
            var retained = new Entitlement
            {
                WorkspaceId = workspaceId,
                CheckoutId = checkout.Id,
                ProjectId = project.Id,
                ProductCode = BillingProducts.ActiveArtist,
                ProviderPeriodKey = "invoice:in_retained_period",
                ExternalSubscriptionId = "sub_subscription_dispute",
                ExternalPaymentIntentId = "pi_retained_period",
                ExternalInvoiceId = "in_retained_period",
                ProviderEventOccurredAt = DateTimeOffset.UtcNow,
                PeriodStartsAt = DateTimeOffset.UtcNow,
                ValidUntil = DateTimeOffset.UtcNow.AddMonths(1)
            };
            checkoutId = checkout.Id;
            projectId = project.Id;
            disputedEntitlementId = disputed.Id;
            retainedEntitlementId = retained.Id;
            db.Projects.Add(project);
            db.BillingCheckouts.Add(checkout);
            db.Entitlements.AddRange(disputed, retained);
            await db.SaveChangesAsync();
        }

        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-subscription-period-dispute",
            Type: "charge.dispute.created",
            CheckoutId: checkoutId,
            ExternalSessionId: null,
            ProductCode: BillingProducts.ActiveArtist,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: "cus_subscription_dispute",
            ExternalSubscriptionId: "sub_subscription_dispute",
            ExternalPaymentIntentId: "pi_disputed_period",
            ExternalInvoiceId: "in_disputed_period",
            ExternalChargeId: "ch_disputed_period",
            Disposition: PaymentWebhookDisposition.Disputed,
            OccurredAt: DateTimeOffset.UtcNow,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "subscription-period-dispute")).StatusCode);

        gateway.NextEvent = gateway.NextEvent with
        {
            EventId = "evt-subscription-period-refund-after-dispute",
            Type = "charge.refunded",
            Disposition = PaymentWebhookDisposition.Refunded,
            OccurredAt = gateway.NextEvent.OccurredAt.AddMinutes(1),
            PayloadHash = string.Empty
        };
        Assert.Equal(
            HttpStatusCode.OK,
            (await PostWebhook(client, "subscription-period-refund-after-dispute")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await PostWebhook(client, "subscription-period-refund-after-dispute")).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Equal(
            EntitlementState.Revoked,
            await verifyDb.Entitlements.Where(value => value.Id == disputedEntitlementId)
                .Select(value => value.State)
                .SingleAsync());
        Assert.Equal(
            EntitlementState.Active,
            await verifyDb.Entitlements.Where(value => value.Id == retainedEntitlementId)
                .Select(value => value.State)
                .SingleAsync());
        var verifiedCheckout = await verifyDb.BillingCheckouts.SingleAsync(value => value.Id == checkoutId);
        Assert.Equal(CheckoutState.Completed, verifiedCheckout.State);
        Assert.Null(verifiedCheckout.ProviderAccessRevokedAt);
        Assert.NotNull(verifiedCheckout.RefundedAt);
        Assert.Equal(
            1,
            await verifyDb.OutboxMessages.CountAsync(value =>
                value.DedupeKey == $"pipeline.reconcile:{projectId:N}:entitlement.revoked:{disputedEntitlementId:N}"));
    }

    [Theory]
    [InlineData("charge.refunded")]
    [InlineData("charge.dispute.created")]
    public async Task Invoice_payments_array_intent_is_stored_and_scopes_later_access_revocation(
        string accessEventType)
    {
        const string webhookSecret = "whsec_invoice_payments_integration";
        var gateway = new StripePaymentGateway(
            new HttpClient(),
            Options.Create(new StripeOptions
            {
                WebhookSecret = webhookSecret,
                WebhookToleranceSeconds = 300
            }));
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
            workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
            var checkout = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProductCode = BillingProducts.ActiveArtist,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                IdempotencyKey = $"invoice-payments-{accessEventType}",
                RequestHash = new string('8', 64),
                ExternalCustomerId = "cus_invoice_payments",
                ExternalSubscriptionId = "sub_invoice_payments",
                CheckoutUrl = "https://payments.example.test/invoice-payments"
            };
            checkoutId = checkout.Id;
            db.BillingCheckouts.Add(checkout);
            await db.SaveChangesAsync();
        }

        var now = DateTimeOffset.UtcNow;
        var firstPeriodStart = now.AddMonths(-2);
        var firstPaid = StripeInvoicePaidPayload(
            "evt_invoice_payments_first",
            checkoutId,
            workspaceId,
            "in_invoice_payments_first",
            "pi_invoice_payments_first",
            now.AddMinutes(-3),
            firstPeriodStart,
            firstPeriodStart.AddMonths(1));
        Assert.Equal(
            HttpStatusCode.OK,
            (await PostSignedStripeWebhook(client, firstPaid, webhookSecret)).StatusCode);

        var secondPeriodStart = firstPeriodStart.AddMonths(1);
        var secondPaid = StripeInvoicePaidPayload(
            "evt_invoice_payments_second",
            checkoutId,
            workspaceId,
            "in_invoice_payments_second",
            "pi_invoice_payments_second",
            now.AddMinutes(-2),
            secondPeriodStart,
            secondPeriodStart.AddMonths(1));
        Assert.Equal(
            HttpStatusCode.OK,
            (await PostSignedStripeWebhook(client, secondPaid, webhookSecret)).StatusCode);

        await using (var paidScope = factory.Services.CreateAsyncScope())
        {
            var paidDb = paidScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            Assert.Equal(
                new[] { "pi_invoice_payments_first", "pi_invoice_payments_second" },
                await paidDb.Entitlements.OrderBy(value => value.PeriodStartsAt)
                    .Select(value => value.ExternalPaymentIntentId)
                    .ToArrayAsync());
        }

        var accessPayload = StripeAccessRevocationPayload(
            accessEventType,
            "pi_invoice_payments_first",
            now.AddMinutes(-1));
        Assert.Equal(
            HttpStatusCode.OK,
            (await PostSignedStripeWebhook(client, accessPayload, webhookSecret)).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var entitlements = await verifyDb.Entitlements.OrderBy(value => value.PeriodStartsAt).ToListAsync();
        Assert.Equal(2, entitlements.Count);
        Assert.Equal(EntitlementState.Revoked, entitlements[0].State);
        Assert.Equal("pi_invoice_payments_first", entitlements[0].ExternalPaymentIntentId);
        Assert.Equal(EntitlementState.Active, entitlements[1].State);
        Assert.Equal("pi_invoice_payments_second", entitlements[1].ExternalPaymentIntentId);
    }

    [Fact]
    public async Task One_time_dispute_tombstone_arriving_before_payment_blocks_late_credit_grant()
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
            workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
            var checkout = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProductCode = BillingProducts.ArtworkCredits5,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ArtworkCredits5),
                IdempotencyKey = "credit-dispute-before-paid",
                RequestHash = new string('8', 64),
                ExternalPaymentIntentId = "pi_credit_dispute",
                CheckoutUrl = "https://payments.example.test/credit-dispute-before-paid"
            };
            checkoutId = checkout.Id;
            db.BillingCheckouts.Add(checkout);
            await db.SaveChangesAsync();
        }

        var disputedAt = DateTimeOffset.UtcNow;
        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-credit-dispute-before-paid",
            Type: "charge.dispute.created",
            CheckoutId: checkoutId,
            ExternalSessionId: null,
            ProductCode: BillingProducts.ArtworkCredits5,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: null,
            ExternalSubscriptionId: null,
            ExternalPaymentIntentId: "pi_credit_dispute",
            ExternalInvoiceId: null,
            ExternalChargeId: "ch_credit_dispute",
            Disposition: PaymentWebhookDisposition.Disputed,
            OccurredAt: disputedAt,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "credit-dispute-before-paid")).StatusCode);

        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-credit-late-paid",
            Type: "checkout.session.completed",
            CheckoutId: checkoutId,
            ExternalSessionId: null,
            ProductCode: BillingProducts.ArtworkCredits5,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: null,
            ExternalSubscriptionId: null,
            ExternalPaymentIntentId: "pi_credit_dispute",
            ExternalInvoiceId: null,
            ExternalChargeId: null,
            Disposition: PaymentWebhookDisposition.Paid,
            OccurredAt: disputedAt.AddMinutes(1),
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "credit-late-paid")).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Empty(await verifyDb.ArtworkCreditGrants.ToListAsync());
        Assert.Empty(await verifyDb.WorkspaceArtworkCredits.ToListAsync());
        var checkoutState = await verifyDb.BillingCheckouts.SingleAsync(value => value.Id == checkoutId);
        Assert.Equal(CheckoutState.Pending, checkoutState.State);
        Assert.Equal(disputedAt, checkoutState.ProviderAccessRevokedAt);
    }

    [Fact]
    public async Task Malformed_historical_audit_json_is_not_used_as_security_state()
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
            workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
            var checkout = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProductCode = BillingProducts.ArtworkCredits5,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ArtworkCredits5),
                IdempotencyKey = "malformed-audit-evidence",
                RequestHash = new string('d', 64),
                ExternalPaymentIntentId = "pi_malformed_audit",
                CheckoutUrl = "https://payments.example.test/malformed-audit-evidence"
            };
            checkoutId = checkout.Id;
            db.BillingCheckouts.Add(checkout);
            db.AuditEvents.Add(new AuditEvent
            {
                WorkspaceId = workspaceId,
                Action = "billing.provider_access_revoked",
                ResourceType = "billing_checkout",
                ResourceId = checkout.Id,
                DataJson = "{malformed"
            });
            await db.SaveChangesAsync();
        }

        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-paid-after-malformed-audit",
            Type: "checkout.session.completed",
            CheckoutId: checkoutId,
            ExternalSessionId: null,
            ProductCode: BillingProducts.ArtworkCredits5,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: null,
            ExternalSubscriptionId: null,
            ExternalPaymentIntentId: "pi_malformed_audit",
            ExternalInvoiceId: null,
            ExternalChargeId: null,
            Disposition: PaymentWebhookDisposition.Paid,
            OccurredAt: DateTimeOffset.UtcNow,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);

        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "paid-after-malformed-audit")).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Equal(5, (await verifyDb.WorkspaceArtworkCredits.SingleAsync()).Balance);
        Assert.Null((await verifyDb.BillingCheckouts.SingleAsync(value => value.Id == checkoutId)).ProviderAccessRevokedAt);
    }

    [Fact]
    public async Task Unknown_provider_event_is_persisted_as_ignored()
    {
        var gateway = new MutablePaymentGateway
        {
            NextEvent = new PaymentWebhookEvent(
                EventId: "evt-unknown-provider-event",
                Type: "customer.subscription.resumed",
                CheckoutId: null,
                ExternalSessionId: null,
                ProductCode: null,
                WorkspaceId: null,
                ProjectId: null,
                ExternalCustomerId: null,
                ExternalSubscriptionId: "sub_unknown",
                ExternalPaymentIntentId: null,
                ExternalInvoiceId: null,
                ExternalChargeId: null,
                Disposition: PaymentWebhookDisposition.Unknown,
                OccurredAt: DateTimeOffset.UtcNow,
                PeriodStartsAt: null,
                PeriodEndsAt: null,
                PayloadHash: string.Empty)
        };
        await using var factory = new Hook2StreamApiFactory(services =>
        {
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(gateway);
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "unknown-provider-event")).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var inbox = await db.InboxMessages.SingleAsync();
        Assert.Equal("ignored", inbox.State);
        Assert.Equal("billing.event_not_actionable", inbox.LastError);
    }

    [Fact]
    public async Task Actionable_provider_event_without_checkout_fails_closed_for_retry()
    {
        var gateway = new MutablePaymentGateway
        {
            NextEvent = new PaymentWebhookEvent(
                EventId: "evt-unresolved-payment-failure",
                Type: "invoice.payment_failed",
                CheckoutId: null,
                ExternalSessionId: null,
                ProductCode: null,
                WorkspaceId: null,
                ProjectId: null,
                ExternalCustomerId: null,
                ExternalSubscriptionId: "sub_unresolved",
                ExternalPaymentIntentId: null,
                ExternalInvoiceId: "in_unresolved",
                ExternalChargeId: null,
                Disposition: PaymentWebhookDisposition.PaymentFailed,
                OccurredAt: DateTimeOffset.UtcNow,
                PeriodStartsAt: null,
                PeriodEndsAt: null,
                PayloadHash: string.Empty)
        };
        await using var factory = new Hook2StreamApiFactory(services =>
        {
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(gateway);
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Conflict, (await PostWebhook(client, "unresolved-payment-failure")).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Empty(await db.InboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData("session")]
    [InlineData("customer")]
    [InlineData("subscription")]
    [InlineData("payment_intent")]
    public async Task Provider_identity_mismatch_returns_conflict_before_any_mutation(string mismatch)
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
        var oneTime = mismatch == "payment_intent";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
            var checkout = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProductCode = oneTime ? BillingProducts.ReleasePack : BillingProducts.ActiveArtist,
                AmountCents = BillingProducts.AmountCents(
                    oneTime ? BillingProducts.ReleasePack : BillingProducts.ActiveArtist),
                IdempotencyKey = $"provider-correlation-{mismatch}",
                RequestHash = new string('9', 64),
                ExternalSessionId = "cs_expected",
                ExternalCustomerId = "cus_expected",
                ExternalSubscriptionId = oneTime ? null : "sub_expected",
                ExternalPaymentIntentId = oneTime ? "pi_expected" : null,
                CheckoutUrl = "https://payments.example.test/provider-correlation"
            };
            checkoutId = checkout.Id;
            db.BillingCheckouts.Add(checkout);
            await db.SaveChangesAsync();
        }

        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: $"evt-provider-correlation-{mismatch}",
            Type: "invoice.payment_failed",
            CheckoutId: checkoutId,
            ExternalSessionId: mismatch == "session" ? "cs_wrong" : "cs_expected",
            ProductCode: oneTime ? BillingProducts.ReleasePack : BillingProducts.ActiveArtist,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: mismatch == "customer" ? "cus_wrong" : "cus_expected",
            ExternalSubscriptionId: oneTime
                ? null
                : mismatch == "subscription" ? "sub_wrong" : "sub_expected",
            ExternalPaymentIntentId: oneTime
                ? mismatch == "payment_intent" ? "pi_wrong" : "pi_expected"
                : null,
            ExternalInvoiceId: "in_correlation",
            ExternalChargeId: null,
            Disposition: PaymentWebhookDisposition.PaymentFailed,
            OccurredAt: DateTimeOffset.UtcNow,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);

        var response = await PostWebhook(client, $"provider-correlation-{mismatch}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("billing.webhook_correlation_mismatch", problem.GetProperty("code").GetString());
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Empty(await verifyDb.InboxMessages.ToListAsync());
        Assert.DoesNotContain(
            await verifyDb.AuditEvents.ToListAsync(),
            value => value.Action.StartsWith("billing.provider_", StringComparison.Ordinal));
        Assert.Equal(CheckoutState.Pending, (await verifyDb.BillingCheckouts.SingleAsync()).State);
    }

    [Fact]
    public async Task Provider_subscription_owned_by_another_checkout_cannot_fill_a_null_correlation()
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
        Guid targetCheckoutId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
            var target = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProductCode = BillingProducts.ActiveArtist,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                IdempotencyKey = "provider-owner-target",
                RequestHash = new string('b', 64),
                CheckoutUrl = "https://payments.example.test/provider-owner-target"
            };
            var owner = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProductCode = BillingProducts.ActiveArtist,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                IdempotencyKey = "provider-owner-existing",
                RequestHash = new string('c', 64),
                ExternalSubscriptionId = "sub_owned_by_other_checkout",
                CheckoutUrl = "https://payments.example.test/provider-owner-existing"
            };
            targetCheckoutId = target.Id;
            db.BillingCheckouts.AddRange(target, owner);
            await db.SaveChangesAsync();
        }

        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-provider-owned-subscription",
            Type: "invoice.payment_failed",
            CheckoutId: targetCheckoutId,
            ExternalSessionId: null,
            ProductCode: BillingProducts.ActiveArtist,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: null,
            ExternalSubscriptionId: "sub_owned_by_other_checkout",
            ExternalPaymentIntentId: null,
            ExternalInvoiceId: "in_provider_owner_conflict",
            ExternalChargeId: null,
            Disposition: PaymentWebhookDisposition.PaymentFailed,
            OccurredAt: DateTimeOffset.UtcNow,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);

        var response = await PostWebhook(client, "provider-owned-subscription");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("billing.webhook_correlation_mismatch", problem.GetProperty("code").GetString());
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Null((await verifyDb.BillingCheckouts.SingleAsync(value => value.Id == targetCheckoutId)).ExternalSubscriptionId);
        Assert.Empty(await verifyDb.InboxMessages.ToListAsync());
        Assert.DoesNotContain(
            await verifyDb.AuditEvents.ToListAsync(),
            value => value.Action.StartsWith("billing.provider_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Actionable_event_concurrency_conflict_is_atomic_and_retry_preserves_terminal_ordering()
    {
        var gateway = new MutablePaymentGateway();
        var concurrency = new InjectedWebhookConcurrencyInterceptor();
        await using var factory = new Hook2StreamApiFactory(
            services =>
            {
                services.RemoveAll<IPaymentGateway>();
                services.AddSingleton<IPaymentGateway>(gateway);
            },
            options => options.AddInterceptors(concurrency));
        using var client = factory.CreateClient();
        await Onboard(client);
        Guid workspaceId;
        Guid checkoutId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
            var checkout = new BillingCheckout
            {
                WorkspaceId = workspaceId,
                ProductCode = BillingProducts.ActiveArtist,
                AmountCents = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                IdempotencyKey = "concurrent-provider-events",
                RequestHash = new string('a', 64),
                ExternalCustomerId = "cus_subscription",
                ExternalSubscriptionId = "sub_concurrent",
                CheckoutUrl = "https://payments.example.test/concurrent-provider-events"
            };
            checkoutId = checkout.Id;
            db.BillingCheckouts.Add(checkout);
            await db.SaveChangesAsync();
        }

        var accessEndedAt = DateTimeOffset.UtcNow;
        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-concurrent-payment-failed",
            Type: "invoice.payment_failed",
            CheckoutId: checkoutId,
            ExternalSessionId: null,
            ProductCode: BillingProducts.ActiveArtist,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: "cus_subscription",
            ExternalSubscriptionId: "sub_concurrent",
            ExternalPaymentIntentId: null,
            ExternalInvoiceId: "in_concurrent_failed",
            ExternalChargeId: null,
            Disposition: PaymentWebhookDisposition.PaymentFailed,
            OccurredAt: accessEndedAt.AddMinutes(-1),
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        concurrency.Arm();

        var conflicted = await PostWebhook(client, "concurrent-payment-failed");

        Assert.Equal(HttpStatusCode.Conflict, conflicted.StatusCode);
        Assert.True(concurrency.ObservedCheckoutFence);
        await using (var conflictScope = factory.Services.CreateAsyncScope())
        {
            var conflictDb = conflictScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            Assert.Empty(await conflictDb.InboxMessages.ToListAsync());
            Assert.Equal(
                0,
                await conflictDb.AuditEvents.CountAsync(
                    value => value.Action == "billing.provider_payment_failed"));
            Assert.Null((await conflictDb.BillingCheckouts.SingleAsync(value => value.Id == checkoutId)).SubscriptionAccessEndedAt);
        }

        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "concurrent-payment-failed-retry")).StatusCode);
        gateway.NextEvent = new PaymentWebhookEvent(
            EventId: "evt-concurrent-ended",
            Type: "customer.subscription.deleted",
            CheckoutId: checkoutId,
            ExternalSessionId: null,
            ProductCode: BillingProducts.ActiveArtist,
            WorkspaceId: workspaceId,
            ProjectId: null,
            ExternalCustomerId: "cus_subscription",
            ExternalSubscriptionId: "sub_concurrent",
            ExternalPaymentIntentId: null,
            ExternalInvoiceId: null,
            ExternalChargeId: null,
            Disposition: PaymentWebhookDisposition.SubscriptionAccessEnded,
            OccurredAt: accessEndedAt,
            PeriodStartsAt: null,
            PeriodEndsAt: null,
            PayloadHash: string.Empty);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "concurrent-ended")).StatusCode);
        var periodStart = accessEndedAt.AddDays(-7);
        gateway.NextEvent = SubscriptionEvent(
            "evt-concurrent-old-paid",
            checkoutId,
            workspaceId,
            "sub_concurrent",
            "in_concurrent",
            paid: true,
            refunded: false,
            accessEndedAt.AddSeconds(-1),
            periodStart,
            periodStart.AddMonths(1));
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(client, "concurrent-old-paid")).StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var checkoutState = await verifyDb.BillingCheckouts.SingleAsync(value => value.Id == checkoutId);
        Assert.Equal(accessEndedAt, checkoutState.SubscriptionAccessEndedAt);
        Assert.Empty(await verifyDb.Entitlements.ToListAsync());
        Assert.Equal(3, await verifyDb.InboxMessages.CountAsync());
        Assert.Equal(
            1,
            await verifyDb.AuditEvents.CountAsync(value => value.Action == "billing.provider_payment_failed"));
        Assert.Equal(
            1,
            await verifyDb.AuditEvents.CountAsync(
                value => value.Action == "billing.provider_subscription_access_ended"));
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

    private static async Task AssertBillingDisabled(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("billing.disabled", problem.GetProperty("code").GetString());
        Assert.Equal("billing.disabled", problem.GetProperty("title").GetString());
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

    private static async Task<HttpResponseMessage> PostSignedStripeWebhook(
        HttpClient client,
        byte[] payload,
        string webhookSecret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{Encoding.UTF8.GetString(payload)}");
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(webhookSecret), signedPayload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/stripe/webhook")
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TryAddWithoutValidation(
            "Stripe-Signature",
            $"t={timestamp},v1={Convert.ToHexStringLower(signature)}");
        return await client.SendAsync(request);
    }

    private static byte[] StripeInvoicePaidPayload(
        string eventId,
        Guid checkoutId,
        Guid workspaceId,
        string invoiceId,
        string paymentIntentId,
        DateTimeOffset occurredAt,
        DateTimeOffset periodStartsAt,
        DateTimeOffset periodEndsAt) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = eventId,
            type = "invoice.paid",
            created = occurredAt.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = invoiceId,
                    customer = "cus_invoice_payments",
                    amount_paid = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                    currency = "usd",
                    metadata = new { },
                    parent = new
                    {
                        subscription_details = new
                        {
                            subscription = "sub_invoice_payments",
                            metadata = new Dictionary<string, string>
                            {
                                ["workspace_id"] = workspaceId.ToString("N"),
                                ["checkout_id"] = checkoutId.ToString("N"),
                                ["product_code"] = BillingProducts.ActiveArtist
                            }
                        }
                    },
                    payments = new
                    {
                        data = new[]
                        {
                            new
                            {
                                status = "open",
                                payment = new { payment_intent = $"pi_old_open_{invoiceId}" }
                            },
                            new
                            {
                                status = "canceled",
                                payment = new { payment_intent = $"pi_old_canceled_{invoiceId}" }
                            },
                            new
                            {
                                status = "paid",
                                payment = new { payment_intent = paymentIntentId }
                            }
                        }
                    },
                    lines = new
                    {
                        data = new[]
                        {
                            new
                            {
                                period = new
                                {
                                    start = periodStartsAt.ToUnixTimeSeconds(),
                                    end = periodEndsAt.ToUnixTimeSeconds()
                                }
                            }
                        }
                    }
                }
            }
        });

    private static byte[] StripeAccessRevocationPayload(
        string eventType,
        string paymentIntentId,
        DateTimeOffset occurredAt) => eventType == "charge.refunded"
        ? JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "evt_invoice_payments_refund",
            type = eventType,
            created = occurredAt.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = "ch_invoice_payments_refund",
                    payment_intent = paymentIntentId,
                    amount = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                    amount_captured = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                    amount_refunded = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                    refunded = true,
                    metadata = new { }
                }
            }
        })
        : JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "evt_invoice_payments_dispute",
            type = eventType,
            created = occurredAt.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = "dp_invoice_payments_dispute",
                    charge = "ch_invoice_payments_dispute",
                    payment_intent = paymentIntentId,
                    metadata = new { }
                }
            }
        });

    private static PaymentWebhookEvent SubscriptionEvent(
        string eventId,
        Guid checkoutId,
        Guid workspaceId,
        string subscriptionId,
        string invoiceId,
        bool paid,
        bool refunded,
        DateTimeOffset occurredAt,
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
            ExternalSubscriptionId: subscriptionId,
            ExternalPaymentIntentId: $"pi_{invoiceId}",
            ExternalInvoiceId: invoiceId,
            ExternalChargeId: refunded ? $"ch_{invoiceId}" : null,
            Disposition: paid ? PaymentWebhookDisposition.Paid : PaymentWebhookDisposition.Refunded,
            OccurredAt: occurredAt,
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
            termsVersion = "2026-09-04",
            privacyVersion = "2026-09-04",
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

    private sealed class CapturingStripeCheckoutHandler : HttpMessageHandler
    {
        public IReadOnlyDictionary<string, string> Fields { get; private set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Fields = (await request.Content!.ReadAsStringAsync(cancellationToken))
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(field => field.Split('=', 2))
                .ToDictionary(
                    field => Decode(field[0]),
                    field => field.Length == 2 ? Decode(field[1]) : string.Empty,
                    StringComparer.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"cs_test_release_pack\",\"url\":\"https://checkout.stripe.test/release-pack\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        private static string Decode(string value) =>
            Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    private sealed class InjectedWebhookConcurrencyInterceptor : SaveChangesInterceptor
    {
        private int _remainingFailures;

        public bool ObservedCheckoutFence { get; private set; }

        public void Arm() => Interlocked.Exchange(ref _remainingFailures, 1);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var context = eventData.Context;
            var isActionableWebhook = context?.ChangeTracker.Entries<InboxMessage>().Any(entry =>
                    entry.State == EntityState.Added && entry.Entity.State == "processed") == true &&
                context.ChangeTracker.Entries<BillingCheckout>().Any(entry => entry.State == EntityState.Modified);
            if (!isActionableWebhook)
                return new ValueTask<InterceptionResult<int>>(result);

            ObservedCheckoutFence = true;
            if (Interlocked.Exchange(ref _remainingFailures, 0) == 1)
                throw new DbUpdateConcurrencyException("Injected checkout serialization conflict.");
            return new ValueTask<InterceptionResult<int>>(result);
        }
    }
}
