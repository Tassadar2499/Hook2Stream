using System.Security.Claims;

namespace Hook2Stream.Api.Authentication;

public sealed class OAuthCsrfMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-CSRF-Token";

    public async Task InvokeAsync(HttpContext context, OAuthCookieManager cookieManager)
    {
        if (!RequiresCsrf(context))
        {
            await next(context);
            return;
        }

        var headerValues = context.Request.Headers[HeaderName];
        var headerToken = headerValues.Count == 1 ? headerValues[0] : null;
        var cookieToken = cookieManager.ReadCsrfToken(context.Request);
        var expectedHash = context.User.FindFirstValue(
            OAuthSessionAuthenticationHandler.CsrfHashClaim);

        if (!OAuthSessionService.FixedTimeEquals(headerToken, cookieToken) ||
            string.IsNullOrWhiteSpace(expectedHash) ||
            !OAuthSessionService.FixedTimeEquals(
                OAuthSessionService.HashSecret(headerToken!),
                expectedHash))
        {
            throw new ApiProblemException(
                StatusCodes.Status403Forbidden,
                "auth.csrf_invalid",
                "The CSRF token is missing or invalid.");
        }

        await next(context);
    }

    private static bool RequiresCsrf(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) ||
            HttpMethods.IsHead(context.Request.Method) ||
            HttpMethods.IsOptions(context.Request.Method) ||
            HttpMethods.IsTrace(context.Request.Method))
        {
            return false;
        }

        return context.User.Identities.Any(identity =>
            identity.IsAuthenticated &&
            string.Equals(
                identity.AuthenticationType,
                OAuthSessionAuthenticationHandler.SchemeName,
                StringComparison.Ordinal));
    }
}
