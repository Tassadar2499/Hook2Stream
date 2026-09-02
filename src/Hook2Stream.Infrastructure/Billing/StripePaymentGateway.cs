using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Billing;

public enum PaymentGatewayMode
{
    Fixture = 1,
    Stripe = 2
}

public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    public PaymentGatewayMode Mode { get; set; } = PaymentGatewayMode.Fixture;
    public string ApiBaseUrl { get; set; } = "https://api.stripe.com";
    public string SecretKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string PublicWebBaseUrl { get; set; } = "http://localhost:3000";
    public int WebhookToleranceSeconds { get; set; } = 300;
    public Dictionary<string, string> PriceIds { get; set; } = new(StringComparer.Ordinal);
}

public sealed class FixturePaymentGateway : IPaymentGateway
{
    public Task<PaymentCheckoutResult> CreateCheckoutAsync(
        PaymentCheckoutCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var separator = command.SuccessUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = new Uri($"{command.SuccessUrl}{separator}checkout=fixture-complete&checkoutId={command.CheckoutId:N}");
        return Task.FromResult(new PaymentCheckoutResult(
            $"fixture_{command.CheckoutId:N}",
            url,
            CompletedSynchronously: true));
    }

    public PaymentWebhookEvent ParseAndVerifyWebhook(
        ReadOnlySpan<byte> payload,
        string signatureHeader,
        DateTimeOffset now) =>
        throw new InvalidOperationException("Fixture checkout is fulfilled synchronously and has no webhook endpoint.");
}

public sealed class StripePaymentGateway(
    HttpClient httpClient,
    IOptions<StripeOptions> options) : IPaymentGateway
{
    private readonly StripeOptions _options = options.Value;

    public async Task<PaymentCheckoutResult> CreateCheckoutAsync(
        PaymentCheckoutCommand command,
        CancellationToken cancellationToken)
    {
        if (!_options.PriceIds.TryGetValue(command.ProductCode, out var priceId) ||
            string.IsNullOrWhiteSpace(priceId))
        {
            throw new InvalidOperationException($"No Stripe Price is configured for '{command.ProductCode}'.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/"), "v1/checkout/sessions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
        request.Headers.Add("Idempotency-Key", command.IdempotencyKey);
        // Item IDs remain in the immutable BillingCheckout snapshot. A release
        // pack's 18 UUIDs exceed Stripe's 500-character metadata value limit.
        var fields = new Dictionary<string, string>
        {
            ["mode"] = BillingProducts.IsSubscription(command.ProductCode) ? "subscription" : "payment",
            ["success_url"] = command.SuccessUrl,
            ["cancel_url"] = command.CancelUrl,
            ["client_reference_id"] = command.CheckoutId.ToString("N"),
            ["customer_email"] = command.CustomerEmail,
            ["line_items[0][price]"] = priceId,
            ["line_items[0][quantity]"] = "1",
            ["metadata[checkout_id]"] = command.CheckoutId.ToString("N"),
            ["metadata[workspace_id]"] = command.WorkspaceId.ToString("N"),
            ["metadata[product_code]"] = command.ProductCode
        };
        if (command.ProjectId is { } projectId)
        {
            fields["metadata[project_id]"] = projectId.ToString("N");
        }
        if (BillingProducts.IsSubscription(command.ProductCode))
        {
            fields["subscription_data[metadata][checkout_id]"] = command.CheckoutId.ToString("N");
            fields["subscription_data[metadata][workspace_id]"] = command.WorkspaceId.ToString("N");
            fields["subscription_data[metadata][product_code]"] = command.ProductCode;
            if (command.ProjectId is { } subscriptionProjectId)
                fields["subscription_data[metadata][project_id]"] = subscriptionProjectId.ToString("N");
        }
        else
        {
            fields["payment_intent_data[metadata][checkout_id]"] = command.CheckoutId.ToString("N");
            fields["payment_intent_data[metadata][workspace_id]"] = command.WorkspaceId.ToString("N");
            fields["payment_intent_data[metadata][product_code]"] = command.ProductCode;
            if (command.ProjectId is { } paymentProjectId)
                fields["payment_intent_data[metadata][project_id]"] = paymentProjectId.ToString("N");
        }
        request.Content = new FormUrlEncodedContent(fields);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Stripe Checkout returned {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var sessionId = root.GetProperty("id").GetString();
        var checkoutUrl = root.GetProperty("url").GetString();
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !Uri.TryCreate(checkoutUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Stripe returned an invalid Checkout Session.");
        }
        return new PaymentCheckoutResult(sessionId, uri, CompletedSynchronously: false);
    }

    public PaymentWebhookEvent ParseAndVerifyWebhook(
        ReadOnlySpan<byte> payload,
        string signatureHeader,
        DateTimeOffset now)
    {
        var signature = ParseSignature(signatureHeader);
        DateTimeOffset signedAt;
        try
        {
            signedAt = DateTimeOffset.FromUnixTimeSeconds(signature.Timestamp);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new CryptographicException("Stripe webhook timestamp is invalid.", exception);
        }
        if (Math.Abs((now - signedAt).TotalSeconds) > _options.WebhookToleranceSeconds)
        {
            throw new CryptographicException("Stripe webhook timestamp is outside the accepted tolerance.");
        }

        var signedPayload = Encoding.UTF8.GetBytes(
            $"{signature.Timestamp}.{Encoding.UTF8.GetString(payload)}");
        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_options.WebhookSecret),
            signedPayload);
        var matches = false;
        foreach (var candidate in signature.V1)
        {
            var received = Convert.FromHexString(candidate);
            matches |= expected.Length == received.Length &&
                       CryptographicOperations.FixedTimeEquals(expected, received);
        }
        if (!matches)
        {
            throw new CryptographicException("Stripe webhook signature is invalid.");
        }

        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        var eventId = RequiredString(root, "id");
        var eventType = RequiredString(root, "type");
        var created = root.TryGetProperty("created", out var createdNode)
            ? DateTimeOffset.FromUnixTimeSeconds(createdNode.GetInt64())
            : now;
        var value = root.GetProperty("data").GetProperty("object");
        var metadata = FindBillingMetadata(value);
        var workspaceId = TryMetadata(metadata, "workspace_id") is { Length: > 0 } workspace
            ? ParseGuid(workspace, "workspace_id")
            : (Guid?)null;
        var checkoutId = TryMetadata(metadata, "checkout_id") is { Length: > 0 } checkout
            ? ParseGuid(checkout, "checkout_id")
            : (Guid?)null;
        var productCode = TryMetadata(metadata, "product_code");
        if (productCode is not null && !BillingProducts.All.Contains(productCode))
        {
            throw new JsonException("Stripe event contains an unknown product code.");
        }
        var projectId = TryMetadata(metadata, "project_id") is { Length: > 0 } project
            ? ParseGuid(project, "project_id")
            : (Guid?)null;
        var checkoutPaid = (eventType is "checkout.session.completed" or "checkout.session.async_payment_succeeded") &&
                           OptionalString(value, "payment_status") == "paid";
        // Stripe defines Charge.refunded as true only after the captured charge
        // has been fully refunded. Comparing amount_refunded with the original
        // authorization amount breaks valid partial-capture refunds.
        var fullyRefunded = eventType == "charge.refunded" &&
                            value.TryGetProperty("refunded", out var refundedNode) &&
                            refundedNode.ValueKind == JsonValueKind.True;
        var subscriptionStatus = eventType == "customer.subscription.updated"
            ? OptionalString(value, "status")
            : null;
        var disposition = eventType switch
        {
            "invoice.paid" => PaymentWebhookDisposition.Paid,
            "checkout.session.completed" or "checkout.session.async_payment_succeeded" when checkoutPaid =>
                PaymentWebhookDisposition.Paid,
            "charge.refunded" when fullyRefunded => PaymentWebhookDisposition.Refunded,
            "checkout.session.expired" or "checkout.session.async_payment_failed" =>
                PaymentWebhookDisposition.CheckoutFailed,
            "invoice.payment_failed" or "invoice.finalization_failed" or "invoice.voided" or
                "invoice.marked_uncollectible" => PaymentWebhookDisposition.PaymentFailed,
            "customer.subscription.deleted" or "customer.subscription.paused" =>
                PaymentWebhookDisposition.SubscriptionAccessEnded,
            "customer.subscription.updated" when subscriptionStatus is
                "canceled" or "unpaid" or "paused" or "incomplete_expired" =>
                PaymentWebhookDisposition.SubscriptionAccessEnded,
            "charge.dispute.created" => PaymentWebhookDisposition.Disputed,
            _ => PaymentWebhookDisposition.Unknown
        };
        if (disposition == PaymentWebhookDisposition.Paid && productCode is not null)
        {
            var amount = eventType == "invoice.paid"
                ? OptionalInt64(value, "amount_paid")
                : OptionalInt64(value, "amount_total");
            var currency = OptionalString(value, "currency");
            if (amount != BillingProducts.AmountCents(productCode) ||
                !string.Equals(currency, "usd", StringComparison.OrdinalIgnoreCase))
                throw new JsonException("Stripe payment amount or currency does not match the purchased product.");
        }
        var objectId = OptionalString(value, "id");
        var sessionId = eventType.StartsWith("checkout.session.", StringComparison.Ordinal)
            ? objectId
            : null;
        var customerId = OptionalString(value, "customer");
        var subscriptionId = eventType.StartsWith("customer.subscription.", StringComparison.Ordinal)
            ? objectId
            : OptionalString(value, "subscription")
              ?? OptionalStringAtPath(value, "parent", "subscription_details", "subscription");
        var paymentIntentId = PaymentIntentId(value, eventType);
        var invoiceId = eventType.StartsWith("invoice.", StringComparison.Ordinal)
            ? objectId
            : OptionalString(value, "invoice");
        var chargeId = eventType == "charge.refunded"
            ? objectId
            : OptionalString(value, "charge");
        var (periodStartsAt, periodEndsAt) = InvoicePeriod(value);
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        return new PaymentWebhookEvent(
            eventId,
            eventType,
            checkoutId,
            sessionId,
            productCode,
            workspaceId,
            projectId,
            customerId,
            subscriptionId,
            paymentIntentId,
            invoiceId,
            chargeId,
            disposition,
            created,
            periodStartsAt,
            periodEndsAt,
            payloadHash);
    }

    private static (long Timestamp, IReadOnlyList<string> V1) ParseSignature(string header)
    {
        long? timestamp = null;
        var v1 = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) continue;
            if (pair[0] == "t" && long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                timestamp = parsed;
            if (pair[0] == "v1" && pair[1].Length == 64 && pair[1].All(Uri.IsHexDigit))
                v1.Add(pair[1]);
        }
        if (timestamp is null || v1.Count == 0)
            throw new CryptographicException("Stripe-Signature is malformed.");
        return (timestamp.Value, v1);
    }

    private static string RequiredString(JsonElement element, string property) =>
        OptionalString(element, property) ?? throw new JsonException($"Stripe event is missing '{property}'.");

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? OptionalInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static string? TryMetadata(JsonElement metadata, string property) =>
        metadata.ValueKind == JsonValueKind.Object &&
        metadata.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement FindBillingMetadata(JsonElement value)
    {
        var candidates = new List<JsonElement>();
        if (value.TryGetProperty("metadata", out var direct)) candidates.Add(direct);
        if (TryElementAtPath(value, out var subscriptionMetadata, "parent", "subscription_details", "metadata"))
            candidates.Add(subscriptionMetadata);
        if (TryElementAtPath(value, out var legacySubscriptionMetadata, "subscription_details", "metadata"))
            candidates.Add(legacySubscriptionMetadata);
        if (TryElementAtPath(value, out var lines, "lines", "data") && lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lines.EnumerateArray())
            {
                if (line.TryGetProperty("metadata", out var lineMetadata)) candidates.Add(lineMetadata);
            }
        }

        return candidates.FirstOrDefault(candidate =>
            TryMetadata(candidate, "checkout_id") is not null ||
            TryMetadata(candidate, "workspace_id") is not null ||
            TryMetadata(candidate, "product_code") is not null);
    }

    private static string? OptionalStringAtPath(JsonElement value, params string[] path) =>
        TryElementAtPath(value, out var node, path) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static string? PaymentIntentId(JsonElement value, string eventType)
    {
        var legacy = OptionalString(value, "payment_intent");
        if (string.IsNullOrWhiteSpace(legacy))
            legacy = OptionalStringAtPath(value, "payment", "payment_intent");
        if (string.IsNullOrWhiteSpace(legacy)) legacy = null;
        if (eventType != "invoice.paid" ||
            !TryElementAtPath(value, out var payments, "payments", "data") ||
            payments.ValueKind != JsonValueKind.Array)
            return legacy;

        var paidPaymentIntents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payment in payments.EnumerateArray())
        {
            if (!string.Equals(OptionalString(payment, "status"), "paid", StringComparison.Ordinal))
                continue;
            var paymentIntent = OptionalStringAtPath(payment, "payment", "payment_intent");
            if (!string.IsNullOrWhiteSpace(paymentIntent)) paidPaymentIntents.Add(paymentIntent);
        }

        if (paidPaymentIntents.Count > 1)
            throw new JsonException("Stripe invoice contains conflicting paid payment intents.");
        var paidPaymentIntent = paidPaymentIntents.SingleOrDefault();
        if (!string.IsNullOrWhiteSpace(legacy) &&
            !string.IsNullOrWhiteSpace(paidPaymentIntent) &&
            !string.Equals(legacy, paidPaymentIntent, StringComparison.Ordinal))
            throw new JsonException("Stripe invoice payment intent fields conflict.");
        return paidPaymentIntent ?? legacy;
    }

    private static bool TryElementAtPath(JsonElement value, out JsonElement result, params string[] path)
    {
        result = value;
        foreach (var segment in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(segment, out result))
            {
                result = default;
                return false;
            }
        }
        return true;
    }

    private static (DateTimeOffset? StartsAt, DateTimeOffset? EndsAt) InvoicePeriod(JsonElement value)
    {
        if (!TryElementAtPath(value, out var lines, "lines", "data") || lines.ValueKind != JsonValueKind.Array)
            return (null, null);
        long? start = null;
        long? end = null;
        foreach (var line in lines.EnumerateArray())
        {
            if (!line.TryGetProperty("period", out var period) || period.ValueKind != JsonValueKind.Object) continue;
            if (period.TryGetProperty("start", out var startNode) && startNode.TryGetInt64(out var currentStart))
                start = start is null ? currentStart : Math.Min(start.Value, currentStart);
            if (period.TryGetProperty("end", out var endNode) && endNode.TryGetInt64(out var currentEnd))
                end = end is null ? currentEnd : Math.Max(end.Value, currentEnd);
        }
        return (
            start is null ? null : DateTimeOffset.FromUnixTimeSeconds(start.Value),
            end is null ? null : DateTimeOffset.FromUnixTimeSeconds(end.Value));
    }

    private static Guid ParseGuid(string value, string name) =>
        Guid.TryParseExact(value, "N", out var parsed)
            ? parsed
            : throw new JsonException($"Stripe metadata '{name}' is invalid.");
}
