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
    /// the API has issued a session JWT. Defaults to the same origin when empty.
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

    public string BuildRedirectUri() =>
        $"{PublicApiBaseUrl.TrimEnd('/')}{CallbackPath}";
}
