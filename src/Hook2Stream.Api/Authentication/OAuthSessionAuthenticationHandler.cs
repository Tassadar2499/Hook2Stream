using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Api.Authentication;

public sealed class OAuthSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    OAuthCookieManager cookieManager,
    OAuthSessionService sessionService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Hook2Stream.OAuthSession";
    public const string SessionIdClaim = "h2s:session_id";
    public const string CsrfHashClaim = "h2s:csrf_hash";
    public const string SessionExpiresClaim = "h2s:session_expires";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = cookieManager.ReadSessionToken(Request);
        if (string.IsNullOrWhiteSpace(token)) return AuthenticateResult.NoResult();

        var active = await sessionService.GetActiveSessionAsync(token, Context.RequestAborted);
        if (active is null) return AuthenticateResult.Fail("The browser session is invalid or expired.");

        var claims = new List<Claim>
        {
            new("sub", active.User.ExternalSubject),
            new(SessionIdClaim, active.Session.Id.ToString()),
            new(CsrfHashClaim, active.Session.CsrfTokenHash),
            new(SessionExpiresClaim, active.Session.ExpiresAt.ToString("O"))
        };
        if (!string.IsNullOrWhiteSpace(active.User.Email))
            claims.Add(new Claim("email", active.User.Email));
        if (!string.IsNullOrWhiteSpace(active.User.DisplayName))
            claims.Add(new Claim("name", active.User.DisplayName));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
