using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Api.Authentication;

public sealed record GoogleUserInfo(
    string Subject,
    string? Email,
    string? Name,
    bool? EmailVerified);

public interface IGoogleOAuthClient
{
    Task<GoogleUserInfo> ExchangeCodeForUserInfoAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken);
}

public sealed class GoogleOAuthClient(
    HttpClient httpClient,
    IOptions<GoogleOAuthOptions> options,
    ILogger<GoogleOAuthClient> logger) : IGoogleOAuthClient
{
    private readonly GoogleOAuthOptions _options = options.Value;

    public async Task<GoogleUserInfo> ExchangeCodeForUserInfoAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var tokenRequestParams = new Dictionary<string, string?>
        {
            ["code"] = code,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        };

        var tokenResponse = await httpClient.PostAsync(
            _options.TokenEndpoint,
            new FormUrlEncodedContent(tokenRequestParams),
            cancellationToken);
        var tokenJson = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Google token endpoint returned {Status}: {Body}",
                tokenResponse.StatusCode,
                tokenJson);
            throw new InvalidOperationException("The Google authorization code could not be exchanged.");
        }

        using var tokenDocument = JsonDocument.Parse(tokenJson);
        var root = tokenDocument.RootElement;
        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("The Google token response did not include access_token.");

        var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, _options.UserInfoEndpoint);
        userInfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var userInfoResponse = await httpClient.SendAsync(userInfoRequest, cancellationToken);
        var userInfoJson = await userInfoResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!userInfoResponse.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Google userinfo endpoint returned {Status}: {Body}",
                userInfoResponse.StatusCode,
                userInfoJson);
            throw new InvalidOperationException("Google user information could not be retrieved.");
        }

        using var userInfoDocument = JsonDocument.Parse(userInfoJson);
        var userInfo = userInfoDocument.RootElement;
        bool? emailVerified = null;
        if (userInfo.TryGetProperty("email_verified", out var verified))
        {
            emailVerified = verified.ValueKind == JsonValueKind.True
                || (verified.ValueKind == JsonValueKind.String &&
                    bool.TryParse(verified.GetString(), out var parsed) && parsed);
        }

        return new GoogleUserInfo(
            Subject: userInfo.GetProperty("sub").GetString()
                ?? throw new InvalidOperationException("Google userinfo did not include sub."),
            Email: userInfo.TryGetProperty("email", out var email) ? email.GetString() : null,
            Name: userInfo.TryGetProperty("name", out var name) ? name.GetString() : null,
            EmailVerified: emailVerified);
    }
}
