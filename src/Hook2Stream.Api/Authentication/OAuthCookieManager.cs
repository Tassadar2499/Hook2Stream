namespace Hook2Stream.Api.Authentication;

public sealed class OAuthCookieManager(IHostEnvironment environment, TimeProvider timeProvider)
{
    private const string DevelopmentSessionCookie = "h2s_session";
    private const string ProductionSessionCookie = "__Host-h2s_session";
    private const string DevelopmentCsrfCookie = "h2s_csrf";
    private const string ProductionCsrfCookie = "__Host-h2s_csrf";
    private const string DevelopmentStateCookie = "h2s_oauth_state";
    private const string ProductionStateCookie = "__Host-h2s_oauth_state";

    public string SessionCookieName => UseProductionCookies
        ? ProductionSessionCookie
        : DevelopmentSessionCookie;

    public string CsrfCookieName => UseProductionCookies
        ? ProductionCsrfCookie
        : DevelopmentCsrfCookie;

    public string StateCookieName => UseProductionCookies
        ? ProductionStateCookie
        : DevelopmentStateCookie;

    private bool UseProductionCookies =>
        !environment.IsDevelopment() && !environment.IsEnvironment("Testing");

    public string? ReadSessionToken(HttpRequest request) =>
        request.Cookies[SessionCookieName];

    public string? ReadCsrfToken(HttpRequest request) =>
        request.Cookies[CsrfCookieName];

    public string? ReadState(HttpRequest request) =>
        request.Cookies[StateCookieName];

    public void AppendState(HttpRequest request, HttpResponse response, string state)
    {
        response.Cookies.Append(
            StateCookieName,
            state,
            BuildOptions(request, timeProvider.GetUtcNow().AddMinutes(10), httpOnly: true));
    }

    public void AppendSession(
        HttpRequest request,
        HttpResponse response,
        string sessionToken,
        string csrfToken,
        DateTimeOffset expiresAt)
    {
        response.Cookies.Append(
            SessionCookieName,
            sessionToken,
            BuildOptions(request, expiresAt, httpOnly: true));
        response.Cookies.Append(
            CsrfCookieName,
            csrfToken,
            BuildOptions(request, expiresAt, httpOnly: false));
    }

    public void AppendCsrf(
        HttpRequest request,
        HttpResponse response,
        string csrfToken,
        DateTimeOffset expiresAt) =>
        response.Cookies.Append(
            CsrfCookieName,
            csrfToken,
            BuildOptions(request, expiresAt, httpOnly: false));

    public void DeleteState(HttpRequest request, HttpResponse response) =>
        DeleteAll(response, request, DevelopmentStateCookie, ProductionStateCookie);

    public void DeleteSession(HttpRequest request, HttpResponse response)
    {
        DeleteAll(response, request, DevelopmentSessionCookie, ProductionSessionCookie);
        DeleteAll(response, request, DevelopmentCsrfCookie, ProductionCsrfCookie);
    }

    private CookieOptions BuildOptions(
        HttpRequest request,
        DateTimeOffset expiresAt,
        bool httpOnly) => new()
    {
        HttpOnly = httpOnly,
        Secure = UseProductionCookies || request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Expires = expiresAt,
        Path = "/",
        IsEssential = true
    };

    private static void DeleteAll(
        HttpResponse response,
        HttpRequest request,
        params string[] names)
    {
        foreach (var name in names)
        {
            response.Cookies.Delete(name, new CookieOptions
            {
                Secure = name.StartsWith("__Host-", StringComparison.Ordinal) || request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/"
            });
        }
    }
}
