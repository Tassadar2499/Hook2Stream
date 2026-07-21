namespace Hook2Stream.Api.Authentication;

public sealed class GoogleOAuthOptions
{
    public const string SectionName = "Google";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// Public base URL of the API as reachable from the browser. Used to build the
    /// OAuth redirect URI. Must match a URI registered in the Google Cloud Console.
    /// </summary>
    public string PublicApiBaseUrl { get; set; } = "";

    /// <summary>
    /// Public base URL of the Next.js web app to which the browser returns after
    /// the API has issued the session cookies. Defaults to the same origin when empty.
    /// </summary>
    public string PublicWebReturnBaseUrl { get; set; } = "";

    public string CallbackPath { get; set; } = "/api/v1/auth/callback";

    public string[] Scopes { get; set; } = ["openid", "email", "profile"];

    public string AuthorizationEndpoint { get; set; } =
        "https://accounts.google.com/o/oauth2/v2/auth";

    public string TokenEndpoint { get; set; } =
        "https://oauth2.googleapis.com/token";

    public string UserInfoEndpoint { get; set; } =
        "https://openidconnect.googleapis.com/v1/userinfo";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(PublicApiBaseUrl);

    /// <summary>
    /// OAuth session cookies use SameSite=Lax. Production therefore keeps the
    /// browser app and API on the same public host (ports may differ). Requiring
    /// the exact host is intentionally conservative: ad-hoc suffix comparisons
    /// cannot safely determine registrable domains without the public suffix list.
    /// </summary>
    public bool HasValidProductionOrigins
    {
        get
        {
            var webReturnBaseUrl = string.IsNullOrWhiteSpace(PublicWebReturnBaseUrl)
                ? PublicApiBaseUrl
                : PublicWebReturnBaseUrl;
            if (!TryGetHttpsOrigin(PublicApiBaseUrl, out var apiOrigin) ||
                !TryGetHttpsOrigin(webReturnBaseUrl, out var webOrigin))
            {
                return false;
            }

            return string.Equals(
                apiOrigin.Host,
                webOrigin.Host,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public string BuildRedirectUri() =>
        $"{PublicApiBaseUrl.TrimEnd('/')}{CallbackPath}";

    private static bool TryGetHttpsOrigin(string value, out Uri origin)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.Equals(
                uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
                value.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
        {
            origin = null!;
            return false;
        }

        origin = uri;
        return true;
    }
}
