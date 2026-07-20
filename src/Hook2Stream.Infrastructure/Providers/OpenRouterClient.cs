using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Providers;

public sealed record OpenRouterHttpResult(
    byte[] Body,
    HttpStatusCode? StatusCode,
    ProviderFailure? Failure,
    string? RequestId,
    string? GenerationId)
{
    public bool IsSuccess => Failure is null && StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices;
}

public sealed class OpenRouterClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenRouterOptions _options;
    private readonly TimeProvider _timeProvider;

    public OpenRouterClient(
        HttpClient httpClient,
        IOptions<OpenRouterOptions> options,
        TimeProvider timeProvider)
        : this(httpClient, options.Value, timeProvider)
    {
    }

    public OpenRouterClient(
        HttpClient httpClient,
        OpenRouterOptions options,
        TimeProvider timeProvider)
    {
        _httpClient = httpClient;
        _options = options;
        _timeProvider = timeProvider;
    }

    public Task<OpenRouterHttpResult> PostJsonAsync(
        string relativePath,
        object payload,
        string idempotencyKey,
        int timeoutSeconds,
        bool outcomeCanBeRetried,
        CancellationToken cancellationToken) =>
        SendAsync(
            relativePath,
            JsonSerializer.SerializeToUtf8Bytes(payload, OpenRouterProviderData.Json),
            idempotencyKey,
            timeoutSeconds,
            outcomeCanBeRetried,
            cancellationToken);

    private async Task<OpenRouterHttpResult> SendAsync(
        string relativePath,
        byte[] body,
        string idempotencyKey,
        int timeoutSeconds,
        bool outcomeCanBeRetried,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return Failed(new ProviderFailure(
                ProviderFailureKind.Authentication,
                "openrouter.api_key_missing",
                "OpenRouter is not configured."));
        }

        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                using var request = CreateRequest(relativePath, body, idempotencyKey);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                var responseBody = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                var requestId = Header(response, "x-request-id");
                var generationId = Header(response, "x-generation-id");
                if (response.IsSuccessStatusCode)
                {
                    return new OpenRouterHttpResult(
                        responseBody,
                        response.StatusCode,
                        null,
                        requestId,
                        generationId);
                }

                var failure = Classify(
                    response.StatusCode,
                    responseBody,
                    RetryAfter(response, _timeProvider.GetUtcNow()));
                if (attempt < _options.MaxRetries && ShouldRetryResponse(response.StatusCode))
                {
                    await DelayAsync(failure.RetryAfter, attempt, cancellationToken);
                    continue;
                }

                return new OpenRouterHttpResult(
                    responseBody,
                    response.StatusCode,
                    failure,
                    requestId,
                    generationId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                if (outcomeCanBeRetried && attempt < _options.MaxRetries)
                {
                    await DelayAsync(null, attempt, cancellationToken);
                    continue;
                }

                return Failed(outcomeCanBeRetried
                    ? new ProviderFailure(
                        ProviderFailureKind.Transient,
                        "openrouter.timeout",
                        "OpenRouter timed out before returning a result.")
                    : new ProviderFailure(
                        ProviderFailureKind.Unknown,
                        "openrouter.image_outcome_unknown",
                        "The image request ended without a confirmed result."));
            }
            catch (HttpRequestException)
            {
                if (outcomeCanBeRetried && attempt < _options.MaxRetries)
                {
                    await DelayAsync(null, attempt, cancellationToken);
                    continue;
                }

                return Failed(outcomeCanBeRetried
                    ? new ProviderFailure(
                        ProviderFailureKind.Transient,
                        "openrouter.network_failure",
                        "OpenRouter is temporarily unavailable.")
                    : new ProviderFailure(
                        ProviderFailureKind.Unknown,
                        "openrouter.image_outcome_unknown",
                        "The image request ended without a confirmed result."));
            }
        }
    }

    private HttpRequestMessage CreateRequest(
        string relativePath,
        byte[] body,
        string idempotencyKey)
    {
        var baseUrl = new Uri(_options.BaseUrl.EndsWith('/') ? _options.BaseUrl : _options.BaseUrl + "/");
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, relativePath.TrimStart('/')))
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("X-Title", _options.AppTitle);
        if (!string.IsNullOrWhiteSpace(_options.HttpReferer))
        {
            request.Headers.TryAddWithoutValidation("HTTP-Referer", _options.HttpReferer);
        }

        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return request;
    }

    private async Task DelayAsync(
        TimeSpan? retryAfter,
        int attempt,
        CancellationToken cancellationToken)
    {
        var suggested = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(suggested.TotalMilliseconds, 100, 20_000));
        await Task.Delay(delay, _timeProvider, cancellationToken);
    }

    private static bool ShouldRetryResponse(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date <= now ? TimeSpan.Zero : date - now;
        return null;
    }

    private static ProviderFailure Classify(
        HttpStatusCode statusCode,
        ReadOnlySpan<byte> body,
        TimeSpan? retryAfter)
    {
        var errorCode = ErrorCode(body);
        if (statusCode == HttpStatusCode.PaymentRequired ||
            statusCode == HttpStatusCode.TooManyRequests &&
            errorCode.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderFailure(
                ProviderFailureKind.Quota,
                "openrouter.quota_exhausted",
                "The OpenRouter spending limit or credit balance has been exhausted.");
        }

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ProviderFailure(
                ProviderFailureKind.Authentication,
                "openrouter.authentication_failed",
                "OpenRouter authentication failed.");
        }

        if (statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ||
            (int)statusCode >= 500)
        {
            return new ProviderFailure(
                ProviderFailureKind.Transient,
                "openrouter.temporarily_unavailable",
                "OpenRouter is temporarily unavailable.",
                retryAfter);
        }

        if (errorCode.Contains("moderation", StringComparison.OrdinalIgnoreCase) ||
            errorCode.Contains("safety", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderFailure(
                ProviderFailureKind.Moderation,
                "openrouter.moderation_blocked",
                "The request was blocked by the provider safety policy.");
        }

        if (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            return new ProviderFailure(
                ProviderFailureKind.UserInput,
                "openrouter.request_rejected",
                "OpenRouter rejected the request parameters or content.");
        }

        if (statusCode == HttpStatusCode.NotFound)
        {
            return new ProviderFailure(
                ProviderFailureKind.Permanent,
                "openrouter.model_unavailable",
                "The configured OpenRouter model is unavailable.");
        }

        return new ProviderFailure(
            ProviderFailureKind.Permanent,
            "openrouter.request_failed",
            "OpenRouter could not complete the request.");
    }

    private static string ErrorCode(ReadOnlySpan<byte> body)
    {
        try
        {
            using var json = JsonDocument.Parse(body.ToArray());
            if (!json.RootElement.TryGetProperty("error", out var error) ||
                !error.TryGetProperty("code", out var code))
            {
                return "";
            }

            return code.ValueKind == JsonValueKind.String
                ? code.GetString() ?? ""
                : code.GetRawText();
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static OpenRouterHttpResult Failed(ProviderFailure failure) =>
        new([], null, failure, null, null);
}

internal static class OpenRouterProviderData
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Guid StableId(Guid operationId, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{operationId:N}:{purpose}"))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    public static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    public static string? String(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static ProviderUsage Usage(JsonElement root, int? generatedImages = null)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return new ProviderUsage(GeneratedImages: generatedImages);
        }

        return new ProviderUsage(
            Long(usage, "input_tokens") ?? Long(usage, "prompt_tokens"),
            Long(usage, "output_tokens") ?? Long(usage, "completion_tokens"),
            Long(usage, "total_tokens"),
            Double(usage, "seconds"),
            generatedImages,
            Decimal(usage, "cost"));
    }

    public static ProviderUsage Sum(IEnumerable<ProviderUsage> values)
    {
        var materialized = values.ToArray();
        return new ProviderUsage(
            SumNullable(materialized.Select(value => value.InputTokens)),
            SumNullable(materialized.Select(value => value.OutputTokens)),
            SumNullable(materialized.Select(value => value.TotalTokens)),
            SumNullable(materialized.Select(value => value.AudioSeconds)),
            SumNullable(materialized.Select(value => value.GeneratedImages)),
            SumNullable(materialized.Select(value => value.CostUsd)));
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort and must not alter a completed provider result.
        }
        catch (UnauthorizedAccessException)
        {
            // Temp cleanup is best effort and must not alter a completed provider result.
        }
    }

    private static long? Long(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt64(out var result) ? result : null;

    private static double? Double(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetDouble(out var result) ? result : null;

    private static decimal? Decimal(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetDecimal(out var result) ? result : null;

    private static long? SumNullable(IEnumerable<long?> values)
    {
        var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Sum();
    }

    private static int? SumNullable(IEnumerable<int?> values)
    {
        var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Sum();
    }

    private static double? SumNullable(IEnumerable<double?> values)
    {
        var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Sum();
    }

    private static decimal? SumNullable(IEnumerable<decimal?> values)
    {
        var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Sum();
    }
}
