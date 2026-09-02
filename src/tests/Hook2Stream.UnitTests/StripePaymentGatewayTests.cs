using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Billing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class StripePaymentGatewayTests
{
    private const string WebhookSecret = "whsec_acceptance_test_secret";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_784_505_600);
    private static readonly Guid CheckoutId = Guid.Parse("01981fee-6ac0-7000-8000-000000000000");
    private static readonly Guid WorkspaceId = Guid.Parse("01981fee-6ac0-7000-8000-000000000001");
    private static readonly Guid ProjectId = Guid.Parse("01981fee-6ac0-7000-8000-000000000002");

    [Fact]
    public void Billing_catalog_has_locked_products_and_video_entitlements()
    {
        Assert.Equal(
            new[]
            {
                BillingProducts.ActiveArtist,
                BillingProducts.ArtworkCredits5,
                BillingProducts.CleanCover,
                BillingProducts.MiniRelease,
                BillingProducts.ReleasePack
            },
            BillingProducts.All.Order(StringComparer.Ordinal));
        Assert.False(BillingProducts.IsSubscription(BillingProducts.ArtworkCredits5));
        Assert.False(BillingProducts.IsSubscription(BillingProducts.CleanCover));
        Assert.False(BillingProducts.IsSubscription(BillingProducts.MiniRelease));
        Assert.False(BillingProducts.IsSubscription(BillingProducts.ReleasePack));
        Assert.True(BillingProducts.IsSubscription(BillingProducts.ActiveArtist));
        Assert.Equal(0, BillingProducts.IncludedVideoCount(BillingProducts.ArtworkCredits5));
        Assert.Equal(0, BillingProducts.IncludedVideoCount(BillingProducts.CleanCover));
        Assert.Equal(6, BillingProducts.IncludedVideoCount(BillingProducts.MiniRelease));
        Assert.Equal(18, BillingProducts.IncludedVideoCount(BillingProducts.ReleasePack));
        Assert.Equal(18, BillingProducts.IncludedVideoCount(BillingProducts.ActiveArtist));
        Assert.Equal(100, BillingProducts.AmountCents(BillingProducts.ArtworkCredits5));
        Assert.Equal(200, BillingProducts.AmountCents(BillingProducts.CleanCover));
        Assert.Equal(500, BillingProducts.AmountCents(BillingProducts.MiniRelease));
        Assert.Equal(990, BillingProducts.AmountCents(BillingProducts.ReleasePack));
        Assert.Equal(2_900, BillingProducts.AmountCents(BillingProducts.ActiveArtist));
        Assert.DoesNotContain("MINI_RELEASE", BillingProducts.All);
    }

    [Fact]
    public async Task Release_pack_checkout_with_eighteen_items_uses_only_bounded_correlation_metadata()
    {
        var handler = new CapturingCheckoutHandler();
        var gateway = CheckoutGateway(handler);
        var itemIds = Enumerable.Range(1, 18)
            .Select(value => Guid.Parse($"01981fee-6ac0-7000-8000-{value:000000000000}"))
            .ToArray();
        Assert.True(string.Join(',', itemIds.Select(value => value.ToString("N"))).Length > 500);

        var result = await gateway.CreateCheckoutAsync(
            CheckoutCommand(BillingProducts.ReleasePack, itemIds, ProjectId),
            CancellationToken.None);

        Assert.Equal("cs_test_checkout", result.ExternalSessionId);
        Assert.False(result.CompletedSynchronously);
        Assert.Equal("payment", handler.Fields["mode"]);
        AssertCorrelationMetadata(
            handler.Fields,
            "payment_intent_data[metadata]",
            BillingProducts.ReleasePack,
            includeProject: true);
        Assert.DoesNotContain(handler.Fields.Keys, key => key.Contains("item_ids", StringComparison.Ordinal));
        Assert.All(
            BillingMetadata(handler.Fields),
            field => Assert.InRange(field.Value.Length, 1, 500));
    }

    [Fact]
    public async Task Subscription_checkout_does_not_copy_item_ids_into_subscription_metadata()
    {
        var handler = new CapturingCheckoutHandler();
        var gateway = CheckoutGateway(handler);
        var itemIds = Enumerable.Range(1, 18)
            .Select(value => Guid.Parse($"01981fee-6ac0-7000-8001-{value:000000000000}"))
            .ToArray();

        await gateway.CreateCheckoutAsync(
            CheckoutCommand(BillingProducts.ActiveArtist, itemIds, projectId: null),
            CancellationToken.None);

        Assert.Equal("subscription", handler.Fields["mode"]);
        AssertCorrelationMetadata(
            handler.Fields,
            "subscription_data[metadata]",
            BillingProducts.ActiveArtist,
            includeProject: false);
        Assert.DoesNotContain(handler.Fields.Keys, key => key.Contains("item_ids", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Fields.Keys, key => key.Contains("project_id", StringComparison.Ordinal));
    }

    [Fact]
    public void Valid_checkout_webhook_is_verified_and_parsed_from_metadata()
    {
        var payload = EventPayload("evt_paid", "checkout.session.completed", BillingProducts.ReleasePack);

        var result = Gateway().ParseAndVerifyWebhook(
            payload,
            Signature(payload, Now.ToUnixTimeSeconds()),
            Now);

        Assert.Equal("evt_paid", result.EventId);
        Assert.Equal("checkout.session.completed", result.Type);
        Assert.Equal(CheckoutId, result.CheckoutId);
        Assert.Equal("cs_test_acceptance", result.ExternalSessionId);
        Assert.Equal(BillingProducts.ReleasePack, result.ProductCode);
        Assert.Equal(WorkspaceId, result.WorkspaceId);
        Assert.Equal(ProjectId, result.ProjectId);
        Assert.Equal("cus_acceptance", result.ExternalCustomerId);
        Assert.Equal("sub_acceptance", result.ExternalSubscriptionId);
        Assert.Equal(PaymentWebhookDisposition.Paid, result.Disposition);
        Assert.Equal(Now.AddSeconds(-10), result.OccurredAt);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(payload)),
            result.PayloadHash);
    }

    [Fact]
    public void Delayed_checkout_success_is_treated_as_a_verified_payment()
    {
        var payload = EventPayload(
            "evt_async_paid",
            "checkout.session.async_payment_succeeded",
            BillingProducts.MiniRelease);

        var result = Gateway().ParseAndVerifyWebhook(
            payload,
            Signature(payload, Now.ToUnixTimeSeconds()),
            Now);

        Assert.Equal(PaymentWebhookDisposition.Paid, result.Disposition);
        Assert.Equal("cs_test_acceptance", result.ExternalSessionId);
        Assert.Equal(BillingProducts.MiniRelease, result.ProductCode);
    }

    [Theory]
    [InlineData("checkout.session.expired")]
    [InlineData("checkout.session.async_payment_failed")]
    public void Checkout_failures_are_classified_explicitly(string eventType)
    {
        var payload = EventPayload("evt_checkout_failed", eventType, BillingProducts.MiniRelease);

        var result = Gateway().ParseAndVerifyWebhook(payload, Signature(payload, Now.ToUnixTimeSeconds()), Now);

        Assert.Equal(PaymentWebhookDisposition.CheckoutFailed, result.Disposition);
    }

    [Fact]
    public void Refund_event_is_not_treated_as_payment()
    {
        var payload = EventPayload("evt_refund", "charge.refunded", BillingProducts.MiniRelease);

        var result = Gateway().ParseAndVerifyWebhook(
            payload,
            Signature(payload, Now.ToUnixTimeSeconds()),
            Now);

        Assert.Equal(PaymentWebhookDisposition.Refunded, result.Disposition);
    }

    [Fact]
    public void Renewal_invoice_reads_subscription_metadata_and_period_references()
    {
        var periodStart = Now.AddDays(-2);
        var periodEnd = Now.AddDays(28);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "evt_renewal",
            type = "invoice.paid",
            created = Now.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = "in_renewal",
                    customer = "cus_acceptance",
                    amount_paid = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                    currency = "usd",
                    metadata = new { },
                    parent = new
                    {
                        subscription_details = new
                        {
                            subscription = "sub_acceptance",
                            metadata = new Dictionary<string, string>
                            {
                                ["workspace_id"] = WorkspaceId.ToString("N"),
                                ["checkout_id"] = CheckoutId.ToString("N"),
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
                                payment = new { payment_intent = "pi_old_open_attempt" }
                            },
                            new
                            {
                                status = "canceled",
                                payment = new { payment_intent = "pi_old_canceled_attempt" }
                            },
                            new
                            {
                                status = "paid",
                                payment = new { payment_intent = "pi_renewal" }
                            },
                            new
                            {
                                status = "paid",
                                payment = new { payment_intent = "pi_renewal" }
                            }
                        }
                    },
                    lines = new
                    {
                        data = new[]
                        {
                            new { period = new { start = periodStart.ToUnixTimeSeconds(), end = periodEnd.ToUnixTimeSeconds() } }
                        }
                    }
                }
            }
        });

        var result = Gateway().ParseAndVerifyWebhook(payload, Signature(payload, Now.ToUnixTimeSeconds()), Now);

        Assert.Equal(PaymentWebhookDisposition.Paid, result.Disposition);
        Assert.Equal(CheckoutId, result.CheckoutId);
        Assert.Equal("sub_acceptance", result.ExternalSubscriptionId);
        Assert.Equal("pi_renewal", result.ExternalPaymentIntentId);
        Assert.Equal("in_renewal", result.ExternalInvoiceId);
        Assert.Equal(periodStart, result.PeriodStartsAt);
        Assert.Equal(periodEnd, result.PeriodEndsAt);
    }

    [Fact]
    public void Renewal_invoice_rejects_conflicting_paid_payment_intents()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "evt_conflicting_invoice_payments",
            type = "invoice.paid",
            created = Now.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = "in_conflicting_invoice_payments",
                    customer = "cus_acceptance",
                    amount_paid = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                    currency = "usd",
                    metadata = new Dictionary<string, string>
                    {
                        ["workspace_id"] = WorkspaceId.ToString("N"),
                        ["checkout_id"] = CheckoutId.ToString("N"),
                        ["product_code"] = BillingProducts.ActiveArtist
                    },
                    subscription = "sub_acceptance",
                    payments = new
                    {
                        data = new[]
                        {
                            new
                            {
                                status = "paid",
                                payment = new { payment_intent = "pi_paid_a" }
                            },
                            new
                            {
                                status = "paid",
                                payment = new { payment_intent = "pi_paid_b" }
                            }
                        }
                    }
                }
            }
        });

        var exception = Assert.Throws<JsonException>(() =>
            Gateway().ParseAndVerifyWebhook(payload, Signature(payload, Now.ToUnixTimeSeconds()), Now));

        Assert.Contains("conflicting paid payment intents", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Renewal_invoice_preserves_legacy_top_level_payment_intent_fallback()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "evt_legacy_invoice_payment",
            type = "invoice.paid",
            created = Now.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = "in_legacy_invoice_payment",
                    customer = "cus_acceptance",
                    subscription = "sub_acceptance",
                    payment_intent = "pi_legacy_invoice_payment",
                    amount_paid = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                    currency = "usd",
                    metadata = new Dictionary<string, string>
                    {
                        ["workspace_id"] = WorkspaceId.ToString("N"),
                        ["checkout_id"] = CheckoutId.ToString("N"),
                        ["product_code"] = BillingProducts.ActiveArtist
                    },
                    payments = new
                    {
                        data = new[]
                        {
                            new
                            {
                                status = "open",
                                payment = new { payment_intent = "pi_old_open_attempt" }
                            },
                            new
                            {
                                status = "canceled",
                                payment = new { payment_intent = "pi_old_canceled_attempt" }
                            }
                        }
                    }
                }
            }
        });

        var result = Gateway().ParseAndVerifyWebhook(payload, Signature(payload, Now.ToUnixTimeSeconds()), Now);

        Assert.Equal("pi_legacy_invoice_payment", result.ExternalPaymentIntentId);
    }

    [Theory]
    [InlineData("customer.subscription.deleted", "active")]
    [InlineData("customer.subscription.paused", "paused")]
    [InlineData("customer.subscription.updated", "canceled")]
    [InlineData("customer.subscription.updated", "unpaid")]
    [InlineData("customer.subscription.updated", "paused")]
    [InlineData("customer.subscription.updated", "incomplete_expired")]
    public void Terminal_subscription_events_end_current_access_without_becoming_disputes(string eventType, string status)
    {
        var payload = SubscriptionPayload("evt_subscription_revoked", eventType, status);

        var result = Gateway().ParseAndVerifyWebhook(payload, Signature(payload, Now.ToUnixTimeSeconds()), Now);

        Assert.Equal(PaymentWebhookDisposition.SubscriptionAccessEnded, result.Disposition);
        Assert.Equal("sub_acceptance", result.ExternalSubscriptionId);
        Assert.Equal(CheckoutId, result.CheckoutId);
    }

    [Theory]
    [InlineData("invoice.payment_failed")]
    [InlineData("invoice.finalization_failed")]
    [InlineData("invoice.voided")]
    [InlineData("invoice.marked_uncollectible")]
    public void Invoice_failures_are_actionable_without_immediate_revocation(string eventType)
    {
        var payload = EventPayload("evt_invoice_failure", eventType, BillingProducts.ActiveArtist);

        var result = Gateway().ParseAndVerifyWebhook(payload, Signature(payload, Now.ToUnixTimeSeconds()), Now);

        Assert.Equal(PaymentWebhookDisposition.PaymentFailed, result.Disposition);
    }

    [Fact]
    public void Dispute_event_carries_payment_correlation_and_is_classified_separately()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "evt_dispute",
            type = "charge.dispute.created",
            created = Now.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = "dp_acceptance",
                    charge = "ch_acceptance",
                    payment_intent = "pi_acceptance",
                    metadata = new { }
                }
            }
        });

        var result = Gateway().ParseAndVerifyWebhook(payload, Signature(payload, Now.ToUnixTimeSeconds()), Now);

        Assert.Equal(PaymentWebhookDisposition.Disputed, result.Disposition);
        Assert.Equal("ch_acceptance", result.ExternalChargeId);
        Assert.Equal("pi_acceptance", result.ExternalPaymentIntentId);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("past_due")]
    [InlineData("trialing")]
    public void Non_terminal_subscription_update_is_unknown(string status)
    {
        var payload = SubscriptionPayload("evt_subscription_non_terminal", "customer.subscription.updated", status);

        var result = Gateway().ParseAndVerifyWebhook(payload, Signature(payload, Now.ToUnixTimeSeconds()), Now);

        Assert.Equal(PaymentWebhookDisposition.Unknown, result.Disposition);
    }

    [Fact]
    public void Full_charge_refund_can_be_correlated_without_top_level_metadata()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "evt_refund_without_metadata",
            type = "charge.refunded",
            created = Now.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = "ch_refunded",
                    payment_intent = "pi_acceptance",
                    amount = 500,
                    amount_refunded = 500,
                    refunded = true,
                    metadata = new { }
                }
            }
        });

        var result = Gateway().ParseAndVerifyWebhook(payload, Signature(payload, Now.ToUnixTimeSeconds()), Now);

        Assert.Equal(PaymentWebhookDisposition.Refunded, result.Disposition);
        Assert.Null(result.CheckoutId);
        Assert.Null(result.ProductCode);
        Assert.Equal("pi_acceptance", result.ExternalPaymentIntentId);
    }

    [Fact]
    public void Refunded_flag_is_authoritative_for_a_fully_refunded_partial_capture()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "evt_partial_capture_fully_refunded",
            type = "charge.refunded",
            created = Now.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = "ch_partial_capture",
                    payment_intent = "pi_partial_capture",
                    amount = 1_000,
                    amount_captured = 500,
                    amount_refunded = 500,
                    refunded = true,
                    metadata = new { }
                }
            }
        });

        var result = Gateway().ParseAndVerifyWebhook(payload, Signature(payload, Now.ToUnixTimeSeconds()), Now);

        Assert.Equal(PaymentWebhookDisposition.Refunded, result.Disposition);
        Assert.Equal("pi_partial_capture", result.ExternalPaymentIntentId);
    }

    [Fact]
    public void Partial_refund_does_not_revoke_access_before_stripe_marks_the_charge_refunded()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = "evt_partial_refund",
            type = "charge.refunded",
            created = Now.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = "ch_partial_refund",
                    payment_intent = "pi_partial_refund",
                    amount = 1_000,
                    amount_captured = 1_000,
                    amount_refunded = 500,
                    refunded = false,
                    metadata = new { }
                }
            }
        });

        var result = Gateway().ParseAndVerifyWebhook(payload, Signature(payload, Now.ToUnixTimeSeconds()), Now);

        Assert.Equal(PaymentWebhookDisposition.Unknown, result.Disposition);
    }

    [Theory]
    [InlineData(BillingProducts.ArtworkCredits5)]
    [InlineData(BillingProducts.CleanCover)]
    [InlineData(BillingProducts.MiniRelease)]
    [InlineData(BillingProducts.ReleasePack)]
    [InlineData(BillingProducts.ActiveArtist)]
    public void Signed_webhook_accepts_each_catalog_product(string productCode)
    {
        var payload = EventPayload("evt_catalog", "checkout.session.completed", productCode);

        var result = Gateway().ParseAndVerifyWebhook(
            payload,
            Signature(payload, Now.ToUnixTimeSeconds()),
            Now);

        Assert.Equal(productCode, result.ProductCode);
    }

    [Fact]
    public void Webhook_outside_timestamp_tolerance_is_rejected_before_parsing()
    {
        var payload = EventPayload("evt_old", "checkout.session.completed", BillingProducts.ReleasePack);
        var timestamp = Now.AddSeconds(-301).ToUnixTimeSeconds();

        var exception = Assert.Throws<CryptographicException>(() =>
            Gateway().ParseAndVerifyWebhook(payload, Signature(payload, timestamp), Now));

        Assert.Contains("timestamp", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Webhook_with_invalid_signature_is_rejected()
    {
        var payload = EventPayload("evt_tampered", "checkout.session.completed", BillingProducts.ReleasePack);
        var signature = $"t={Now.ToUnixTimeSeconds()},v1={new string('0', 64)}";

        var exception = Assert.Throws<CryptographicException>(() =>
            Gateway().ParseAndVerifyWebhook(payload, signature, Now));

        Assert.Contains("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Webhook_accepts_any_matching_v1_during_secret_rotation()
    {
        var payload = EventPayload("evt_rotation", "checkout.session.completed", BillingProducts.ReleasePack);
        var valid = Signature(payload, Now.ToUnixTimeSeconds());
        var signature = $"t={Now.ToUnixTimeSeconds()},v1={new string('0', 64)},{valid[(valid.IndexOf(',') + 1)..]}";

        var result = Gateway().ParseAndVerifyWebhook(payload, signature, Now);

        Assert.Equal(PaymentWebhookDisposition.Paid, result.Disposition);
    }

    [Fact]
    public void Webhook_with_out_of_range_timestamp_is_rejected_as_cryptographic_failure()
    {
        var payload = EventPayload("evt_bad_timestamp", "checkout.session.completed", BillingProducts.ReleasePack);
        var signature = $"t={long.MaxValue},v1={new string('0', 64)}";

        var exception = Assert.Throws<CryptographicException>(() =>
            Gateway().ParseAndVerifyWebhook(payload, signature, Now));

        Assert.Contains("timestamp", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Signed_webhook_with_unknown_product_is_rejected()
    {
        var payload = EventPayload("evt_unknown", "checkout.session.completed", "unknown_product");

        var exception = Assert.Throws<JsonException>(() =>
            Gateway().ParseAndVerifyWebhook(
                payload,
                Signature(payload, Now.ToUnixTimeSeconds()),
                Now));

        Assert.Contains("product", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fixture_gateway_is_rejected_in_production()
    {
        var services = new ServiceCollection();
        services.AddHook2StreamInfrastructure(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:Mode"] = "Fixture",
                ["Stripe:PublicWebBaseUrl"] = "https://app.example.test"
            }).Build(),
            new TestHostEnvironment(Environments.Production));
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<StripeOptions>>().Value);

        Assert.Contains("only allowed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stripe_gateway_fails_fast_without_secrets_and_catalog_prices()
    {
        var services = new ServiceCollection();
        services.AddHook2StreamInfrastructure(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:Mode"] = "Stripe",
                ["Stripe:PublicWebBaseUrl"] = "https://app.example.test"
            }).Build(),
            new TestHostEnvironment(Environments.Production));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<StripeOptions>>().Value);
    }

    private static StripePaymentGateway Gateway() => new(
        new HttpClient(),
        Options.Create(new StripeOptions
        {
            WebhookSecret = WebhookSecret,
            WebhookToleranceSeconds = 300
        }));

    private static StripePaymentGateway CheckoutGateway(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        Options.Create(new StripeOptions
        {
            ApiBaseUrl = "https://api.stripe.test",
            SecretKey = "sk_test_checkout",
            PriceIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BillingProducts.ReleasePack] = "price_release_pack",
                [BillingProducts.ActiveArtist] = "price_active_artist"
            }
        }));

    private static PaymentCheckoutCommand CheckoutCommand(
        string productCode,
        IReadOnlyList<Guid> itemIds,
        Guid? projectId) => new(
        CheckoutId,
        WorkspaceId,
        productCode,
        projectId,
        itemIds,
        "buyer@example.test",
        "https://app.example.test/billing/success",
        "https://app.example.test/billing/cancel",
        $"checkout:{CheckoutId:N}");

    private static void AssertCorrelationMetadata(
        IReadOnlyDictionary<string, string> fields,
        string providerMetadataPrefix,
        string productCode,
        bool includeProject)
    {
        Assert.Equal(CheckoutId.ToString("N"), fields["client_reference_id"]);
        Assert.Equal(CheckoutId.ToString("N"), fields["metadata[checkout_id]"]);
        Assert.Equal(WorkspaceId.ToString("N"), fields["metadata[workspace_id]"]);
        Assert.Equal(productCode, fields["metadata[product_code]"]);
        Assert.Equal(CheckoutId.ToString("N"), fields[$"{providerMetadataPrefix}[checkout_id]"]);
        Assert.Equal(WorkspaceId.ToString("N"), fields[$"{providerMetadataPrefix}[workspace_id]"]);
        Assert.Equal(productCode, fields[$"{providerMetadataPrefix}[product_code]"]);
        if (!includeProject) return;
        Assert.Equal(ProjectId.ToString("N"), fields["metadata[project_id]"]);
        Assert.Equal(ProjectId.ToString("N"), fields[$"{providerMetadataPrefix}[project_id]"]);
    }

    private static IEnumerable<KeyValuePair<string, string>> BillingMetadata(
        IReadOnlyDictionary<string, string> fields) => fields.Where(field =>
        field.Key.StartsWith("metadata[", StringComparison.Ordinal) ||
        field.Key.Contains("[metadata][", StringComparison.Ordinal));

    private static byte[] EventPayload(string eventId, string type, string productCode) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = eventId,
            type,
            created = Now.AddSeconds(-10).ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = "cs_test_acceptance",
                    customer = "cus_acceptance",
                    subscription = "sub_acceptance",
                    payment_status = "paid",
                    amount_total = TestAmount(productCode),
                    amount_paid = TestAmount(productCode),
                    amount = TestAmount(productCode),
                    amount_refunded = TestAmount(productCode),
                    refunded = type == "charge.refunded",
                    currency = "usd",
                    metadata = new Dictionary<string, string>
                    {
                        ["workspace_id"] = WorkspaceId.ToString("N"),
                        ["checkout_id"] = CheckoutId.ToString("N"),
                        ["project_id"] = ProjectId.ToString("N"),
                        ["product_code"] = productCode
                    }
                }
            }
        });

    private static byte[] SubscriptionPayload(string eventId, string type, string status) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = eventId,
            type,
            created = Now.ToUnixTimeSeconds(),
            data = new
            {
                @object = new
                {
                    id = "sub_acceptance",
                    customer = "cus_acceptance",
                    status,
                    metadata = new Dictionary<string, string>
                    {
                        ["workspace_id"] = WorkspaceId.ToString("N"),
                        ["checkout_id"] = CheckoutId.ToString("N"),
                        ["product_code"] = BillingProducts.ActiveArtist
                    }
                }
            }
        });

    private static int TestAmount(string productCode) =>
        BillingProducts.All.Contains(productCode) ? BillingProducts.AmountCents(productCode) : 990;

    private static string Signature(byte[] payload, long timestamp)
    {
        var signed = Encoding.UTF8.GetBytes($"{timestamp}.{Encoding.UTF8.GetString(payload)}");
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), signed);
        return $"t={timestamp},v1={Convert.ToHexStringLower(signature)}";
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Hook2Stream.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CapturingCheckoutHandler : HttpMessageHandler
    {
        public IReadOnlyDictionary<string, string> Fields { get; private set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.stripe.test/v1/checkout/sessions", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("sk_test_checkout", request.Headers.Authorization?.Parameter);
            Assert.Equal($"checkout:{CheckoutId:N}", request.Headers.GetValues("Idempotency-Key").Single());
            Fields = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"cs_test_checkout\",\"url\":\"https://checkout.stripe.test/session\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        private static IReadOnlyDictionary<string, string> ParseForm(string body) => body
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(field => field.Split('=', 2))
            .ToDictionary(
                field => Decode(field[0]),
                field => field.Length == 2 ? Decode(field[1]) : string.Empty,
                StringComparer.Ordinal);

        private static string Decode(string value) =>
            Uri.UnescapeDataString(value.Replace('+', ' '));
    }
}
