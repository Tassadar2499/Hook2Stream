using System.Text;
using System.Web;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Api.Authentication;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth").AllowAnonymous();

        group.MapGet("/login", StartGoogleLogin);
        group.MapGet("/callback", HandleGoogleCallback);
        group.MapGet("/logout", Logout);

        return endpoints;
    }

    private static IResult StartGoogleLogin(
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        IOptions<GoogleOAuthOptions> googleOptions,
        OAuthStateProtector stateProtector)
    {
        var options = googleOptions.Value;
        if (!options.IsConfigured)
        {
            throw new ApiProblemException(
                StatusCodes.Status503ServiceUnavailable,
                "auth.oauth_not_configured",
                "Google OAuth is not configured. Set Google:ClientId, Google:ClientSecret and Google:PublicApiBaseUrl.");
        }

        var returnPath = httpRequest.Query["returnPath"].ToString();
        var (state, cookieValue) = stateProtector.Issue(returnPath);
        httpResponse.Cookies.Append(
            OAuthStateProtector.StateCookieName,
            cookieValue,
            OAuthStateProtector.BuildCookieOptions(httpRequest.IsHttps));

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = options.ClientId;
        query["redirect_uri"] = options.BuildRedirectUri();
        query["response_type"] = "code";
        query["scope"] = string.Join(' ', options.Scopes);
        query["state"] = state;
        query["access_type"] = "online";
        query["include_granted_scopes"] = "true";
        query["prompt"] = "consent";
        var authorizationUrl = $"{options.AuthorizationEndpoint}?{query}";

        return Results.Redirect(authorizationUrl, permanent: false);
    }

    private static async Task<IResult> HandleGoogleCallback(
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        IOptions<GoogleOAuthOptions> googleOptions,
        IGoogleOAuthClient googleClient,
        IApplicationJwtIssuer jwtIssuer,
        OAuthStateProtector stateProtector,
        Hook2StreamDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var options = googleOptions.Value;
        var redirectBase = BuildWebReturnBase(options);
        var code = httpRequest.Query["code"].ToString();
        var stateFromQuery = httpRequest.Query["state"].ToString();
        var error = httpRequest.Query["error"].ToString();
        var cookieValue = httpRequest.Cookies[OAuthStateProtector.StateCookieName];

        if (!string.IsNullOrWhiteSpace(error))
        {
            return Results.Redirect($"{redirectBase}/sign-in?auth=denied", permanent: false);
        }

        if (!stateProtector.TryValidate(stateFromQuery, cookieValue, out var returnPath))
        {
            return Results.Redirect($"{redirectBase}/sign-in?auth=state_invalid", permanent: false);
        }

        httpResponse.Cookies.Delete(OAuthStateProtector.StateCookieName);

        if (string.IsNullOrWhiteSpace(code))
        {
            return Results.Redirect($"{redirectBase}/sign-in?auth=missing_code", permanent: false);
        }

        GoogleUserInfo userInfo;
        try
        {
            userInfo = await googleClient.ExchangeCodeForUserInfoAsync(
                code,
                options.BuildRedirectUri(),
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return Results.Redirect($"{redirectBase}/sign-in?auth=exchange_failed", permanent: false);
        }

        if (userInfo.EmailVerified == false)
        {
            return Results.Redirect($"{redirectBase}/sign-in?auth=email_unverified", permanent: false);
        }

        var externalSubject = $"google:{userInfo.Subject}";
        var user = await dbContext.Users.SingleOrDefaultAsync(
            value => value.ExternalSubject == externalSubject,
            cancellationToken);
        if (user is null)
        {
            user = new AppUser
            {
                ExternalSubject = externalSubject,
                Email = userInfo.Email,
                DisplayName = userInfo.Name
            };
            dbContext.Users.Add(user);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(userInfo.Email)) user.Email = userInfo.Email;
            if (!string.IsNullOrWhiteSpace(userInfo.Name)) user.DisplayName = userInfo.Name;
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        var jwt = jwtIssuer.Issue(externalSubject, userInfo.Email, userInfo.Name);
        var fragmentBuilder = new StringBuilder("#token=");
        fragmentBuilder.Append(Uri.EscapeDataString(jwt.Token));
        fragmentBuilder.Append("&expires_at=");
        fragmentBuilder.Append(Uri.EscapeDataString(jwt.ExpiresAt.ToString("O")));

        var safeReturnPath = OAuthStateProtector.SanitizeReturnPath(returnPath);
        var isSameOrigin = string.IsNullOrEmpty(options.PublicWebReturnBaseUrl);
        var target = isSameOrigin
            ? $"{safeReturnPath}{fragmentBuilder}"
            : $"{redirectBase}/auth/callback{fragmentBuilder}&next={Uri.EscapeDataString(safeReturnPath)}";
        return Results.Redirect(target, permanent: false);
    }

    private static IResult Logout(HttpResponse httpResponse, IOptions<GoogleOAuthOptions> googleOptions)
    {
        var redirectBase = BuildWebReturnBase(googleOptions.Value);
        httpResponse.Cookies.Delete(OAuthStateProtector.StateCookieName);
        return Results.Redirect($"{redirectBase}/?auth=signed_out", permanent: false);
    }

    private static string BuildWebReturnBase(GoogleOAuthOptions options) =>
        string.IsNullOrWhiteSpace(options.PublicWebReturnBaseUrl)
            ? ""
            : options.PublicWebReturnBaseUrl.TrimEnd('/');
}
