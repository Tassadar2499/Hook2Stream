using System.Security.Claims;
using System.Text.Json;
using System.Web;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Api.Authentication;

public sealed record AuthSessionResponse(
    bool Authenticated,
    string? Subject,
    string? Email,
    string? DisplayName,
    DateTimeOffset? ExpiresAt,
    string? CsrfToken);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth");

        group.MapGet("/login", StartGoogleLogin).AllowAnonymous();
        group.MapGet("/callback", HandleGoogleCallback).AllowAnonymous();
        group.MapGet("/session", GetSession).AllowAnonymous()
            .Produces<AuthSessionResponse>();
        group.MapPost("/logout", Logout).AllowAnonymous()
            .Produces(StatusCodes.Status204NoContent);

        return endpoints;
    }

    private static async Task<IResult> StartGoogleLogin(
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        IOptions<GoogleOAuthOptions> googleOptions,
        OAuthCookieManager cookieManager,
        OAuthSessionService sessionService,
        CancellationToken cancellationToken)
    {
        var options = googleOptions.Value;
        if (!options.IsConfigured)
        {
            throw new ApiProblemException(
                StatusCodes.Status503ServiceUnavailable,
                "auth.oauth_not_configured",
                "Google OAuth is not configured. Set Google:ClientId, Google:ClientSecret and Google:PublicApiBaseUrl.");
        }

        var state = await sessionService.IssueLoginStateAsync(
            httpRequest.Query["returnPath"].ToString(),
            cancellationToken);
        cookieManager.AppendState(httpRequest, httpResponse, state);

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = options.ClientId;
        query["redirect_uri"] = options.BuildRedirectUri();
        query["response_type"] = "code";
        query["scope"] = string.Join(' ', options.Scopes);
        query["state"] = state;
        query["access_type"] = "online";
        query["include_granted_scopes"] = "true";
        query["prompt"] = "consent";

        return Results.Redirect(
            $"{options.AuthorizationEndpoint}?{query}",
            permanent: false);
    }

    private static async Task<IResult> HandleGoogleCallback(
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        IOptions<GoogleOAuthOptions> googleOptions,
        IOptions<ApplicationAuthenticationOptions> authenticationOptions,
        IGoogleOAuthClient googleClient,
        OAuthCookieManager cookieManager,
        OAuthSessionService sessionService,
        Hook2StreamDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var options = googleOptions.Value;
        var redirectBase = BuildWebReturnBase(options);
        var returnPath = await sessionService.ConsumeLoginStateAsync(
            httpRequest.Query["state"].ToString(),
            cookieManager.ReadState(httpRequest),
            cancellationToken);
        cookieManager.DeleteState(httpRequest, httpResponse);

        if (returnPath is null)
            return RedirectToSignIn(redirectBase, "state_invalid");

        var error = httpRequest.Query["error"].ToString();
        if (!string.IsNullOrWhiteSpace(error))
            return RedirectToSignIn(redirectBase, "denied");

        var code = httpRequest.Query["code"].ToString();
        if (string.IsNullOrWhiteSpace(code))
            return RedirectToSignIn(redirectBase, "missing_code");

        GoogleUserInfo userInfo;
        try
        {
            userInfo = await googleClient.ExchangeCodeForUserInfoAsync(
                code,
                options.BuildRedirectUri(),
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RedirectToSignIn(redirectBase, "exchange_failed");
        }
        catch (HttpRequestException)
        {
            return RedirectToSignIn(redirectBase, "exchange_failed");
        }
        catch (JsonException)
        {
            return RedirectToSignIn(redirectBase, "exchange_failed");
        }
        catch (KeyNotFoundException)
        {
            return RedirectToSignIn(redirectBase, "exchange_failed");
        }
        catch (InvalidOperationException)
        {
            return RedirectToSignIn(redirectBase, "exchange_failed");
        }

        if (userInfo.EmailVerified != true)
            return RedirectToSignIn(redirectBase, "email_unverified");

        if (string.IsNullOrWhiteSpace(userInfo.Subject) ||
            string.IsNullOrWhiteSpace(userInfo.Email))
        {
            return RedirectToSignIn(redirectBase, "identity_invalid");
        }

        var subject = userInfo.Subject.Trim();
        var email = userInfo.Email.Trim();
        var displayName = string.IsNullOrWhiteSpace(userInfo.Name)
            ? null
            : userInfo.Name.Trim();
        var externalSubject = $"google:{subject}";
        var user = await dbContext.Users.SingleOrDefaultAsync(
            value => value.ExternalSubject == externalSubject,
            cancellationToken);
        if (user is null)
        {
            var access = authenticationOptions.Value;
            if (access.InviteOnly && !access.IsInvited(email))
                return RedirectToSignIn(redirectBase, "invite_required");

            user = new AppUser
            {
                ExternalSubject = externalSubject,
                Email = email,
                DisplayName = displayName
            };
            dbContext.Users.Add(user);
        }
        else
        {
            user.Email = email;
            if (displayName is not null) user.DisplayName = displayName;
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        var session = await sessionService.IssueSessionAsync(user.Id, cancellationToken);
        cookieManager.AppendSession(
            httpRequest,
            httpResponse,
            session.SessionToken,
            session.CsrfToken,
            session.ExpiresAt);

        return Results.Redirect(
            $"{redirectBase}{OAuthSessionService.SanitizeReturnPath(returnPath)}",
            permanent: false);
    }

    private static async Task<IResult> GetSession(
        HttpContext httpContext,
        OAuthCookieManager cookieManager,
        OAuthSessionService sessionService,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        var identity = httpContext.User.Identities.SingleOrDefault(value =>
            value.IsAuthenticated &&
            string.Equals(
                value.AuthenticationType,
                OAuthSessionAuthenticationHandler.SchemeName,
                StringComparison.Ordinal));
        if (identity is null ||
            !Guid.TryParse(
                identity.FindFirst(OAuthSessionAuthenticationHandler.SessionIdClaim)?.Value,
                out var sessionId) ||
            !DateTimeOffset.TryParse(
                identity.FindFirst(OAuthSessionAuthenticationHandler.SessionExpiresClaim)?.Value,
                out var expiresAt))
        {
            return Results.Ok(AnonymousSession());
        }

        var csrfToken = cookieManager.ReadCsrfToken(httpContext.Request);
        var expectedCsrfHash = identity.FindFirst(
            OAuthSessionAuthenticationHandler.CsrfHashClaim)?.Value;
        if (string.IsNullOrWhiteSpace(csrfToken) ||
            string.IsNullOrWhiteSpace(expectedCsrfHash) ||
            !OAuthSessionService.FixedTimeEquals(
                OAuthSessionService.HashSecret(csrfToken),
                expectedCsrfHash))
        {
            var rotated = await sessionService.RotateCsrfAsync(sessionId, cancellationToken);
            if (rotated is null)
            {
                cookieManager.DeleteSession(httpContext.Request, httpContext.Response);
                return Results.Ok(AnonymousSession());
            }

            csrfToken = rotated.Value.Token;
            expiresAt = rotated.Value.ExpiresAt;
            cookieManager.AppendCsrf(
                httpContext.Request,
                httpContext.Response,
                csrfToken,
                expiresAt);
        }

        return Results.Ok(new AuthSessionResponse(
            true,
            identity.FindFirst("sub")?.Value,
            identity.FindFirst("email")?.Value,
            identity.FindFirst("name")?.Value,
            expiresAt,
            csrfToken));
    }

    private static async Task<IResult> Logout(
        HttpContext httpContext,
        OAuthCookieManager cookieManager,
        OAuthSessionService sessionService,
        CancellationToken cancellationToken)
    {
        var sessionIdValue = httpContext.User.FindFirstValue(
            OAuthSessionAuthenticationHandler.SessionIdClaim);
        if (Guid.TryParse(sessionIdValue, out var sessionId))
            await sessionService.RevokeAsync(sessionId, cancellationToken);

        cookieManager.DeleteState(httpContext.Request, httpContext.Response);
        cookieManager.DeleteSession(httpContext.Request, httpContext.Response);
        return Results.NoContent();
    }

    private static AuthSessionResponse AnonymousSession() =>
        new(false, null, null, null, null, null);

    private static IResult RedirectToSignIn(string redirectBase, string error) =>
        Results.Redirect($"{redirectBase}/sign-in?auth={error}", permanent: false);

    private static string BuildWebReturnBase(GoogleOAuthOptions options) =>
        string.IsNullOrWhiteSpace(options.PublicWebReturnBaseUrl)
            ? ""
            : options.PublicWebReturnBaseUrl.TrimEnd('/');
}
