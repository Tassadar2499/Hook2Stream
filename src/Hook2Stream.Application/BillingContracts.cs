using Hook2Stream.Domain;

namespace Hook2Stream.Application;

public static class BillingProducts
{
    public const string ArtworkCredits5 = "art_credits_5";
    public const string MiniRelease = "mini_release";
    public const string ReleasePack = "release_pack";
    public const string CleanCover = "clean_cover";
    public const string ActiveArtist = "active_artist";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [ArtworkCredits5, MiniRelease, ReleasePack, CleanCover, ActiveArtist],
        StringComparer.Ordinal);

    public static bool IsSubscription(string productCode) =>
        productCode == ActiveArtist;

    public static int IncludedVideoCount(string productCode) => productCode switch
    {
        MiniRelease => 6,
        ReleasePack or ActiveArtist => 18,
        _ => 0
    };

    public static int AmountCents(string productCode) => productCode switch
    {
        ArtworkCredits5 => 100,
        MiniRelease => 500,
        ReleasePack => 990,
        CleanCover => 200,
        ActiveArtist => 2_900,
        _ => throw new ArgumentOutOfRangeException(nameof(productCode), productCode, "Unknown billing product.")
    };
}

public sealed record CreateCheckoutRequest(
    string ProductCode,
    Guid? ProjectId,
    IReadOnlyList<Guid>? ItemIds,
    string ReturnPath);

public sealed record CheckoutResponse(
    Guid CheckoutId,
    string ProductCode,
    string Status,
    string CheckoutUrl);

public sealed record EntitlementResponse(
    Guid Id,
    string ProductCode,
    Guid? ProjectId,
    string State,
    int IncludedItemCount,
    IReadOnlyList<Guid> ItemIds,
    int RemainingContentRerenders,
    DateTimeOffset? ValidUntil);

public sealed record BillingSummaryResponse(
    int WorkspaceArtworkCredits,
    string? ActiveSubscription,
    IReadOnlyList<EntitlementResponse> Entitlements);

public sealed record StartRenderRequest(
    Guid EntitlementId,
    IReadOnlyList<Guid> ItemIds,
    RenderRequestKind Kind = RenderRequestKind.Initial);

public sealed record RenderBatchResponse(
    Guid BatchId,
    string State,
    IReadOnlyList<Guid> JobIds);

public sealed record DownloadGrantResponse(
    Guid AssetId,
    string FileName,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height,
    string Url,
    DateTimeOffset ExpiresAt);

public sealed record RenderItemStatusResponse(
    Guid CampaignItemId,
    string State,
    Guid? JobId,
    string? ErrorCode,
    DownloadGrantResponse? Download);

public sealed record RenderBatchStatusResponse(
    Guid BatchId,
    Guid EntitlementId,
    string State,
    RenderRequestKind Kind,
    IReadOnlyList<RenderItemStatusResponse> Items,
    DownloadGrantResponse? Export,
    DateTimeOffset? CompletedAt);

public sealed record PaymentCheckoutCommand(
    Guid CheckoutId,
    Guid WorkspaceId,
    string ProductCode,
    Guid? ProjectId,
    IReadOnlyList<Guid> ItemIds,
    string CustomerEmail,
    string SuccessUrl,
    string CancelUrl,
    string IdempotencyKey);

public sealed record PaymentCheckoutResult(
    string ExternalSessionId,
    Uri CheckoutUrl,
    bool CompletedSynchronously);

public enum PaymentWebhookDisposition
{
    Unknown = 0,
    Paid = 1,
    Refunded = 2,
    CheckoutFailed = 3,
    PaymentFailed = 4,
    SubscriptionAccessEnded = 5,
    Disputed = 6
}

public sealed record PaymentWebhookEvent(
    string EventId,
    string Type,
    Guid? CheckoutId,
    string? ExternalSessionId,
    string? ProductCode,
    Guid? WorkspaceId,
    Guid? ProjectId,
    string? ExternalCustomerId,
    string? ExternalSubscriptionId,
    string? ExternalPaymentIntentId,
    string? ExternalInvoiceId,
    string? ExternalChargeId,
    PaymentWebhookDisposition Disposition,
    DateTimeOffset OccurredAt,
    DateTimeOffset? PeriodStartsAt,
    DateTimeOffset? PeriodEndsAt,
    string PayloadHash);

public interface IPaymentGateway
{
    Task<PaymentCheckoutResult> CreateCheckoutAsync(
        PaymentCheckoutCommand command,
        CancellationToken cancellationToken);

    PaymentWebhookEvent ParseAndVerifyWebhook(
        ReadOnlySpan<byte> payload,
        string signatureHeader,
        DateTimeOffset now);
}
