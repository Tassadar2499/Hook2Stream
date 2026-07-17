using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Api.Authentication;

public sealed class LocalDevelopmentAuthenticationOptions : AuthenticationSchemeOptions
{
    public string Token { get; set; } = "";
    public string Subject { get; set; } = "local-development-user";
    public string Email { get; set; } = "local@hook2stream.test";
    public string DisplayName { get; set; } = "Local Developer";
}

public sealed class LocalDevelopmentAuthenticationHandler(
    IOptionsMonitor<LocalDevelopmentAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<LocalDevelopmentAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "Hook2Stream.LocalDevelopment";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var remoteAddress = Context.Connection.RemoteIpAddress;
        if (remoteAddress?.IsIPv4MappedToIPv6 == true)
        {
            remoteAddress = remoteAddress.MapToIPv4();
        }

        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "Local development authentication only accepts loopback requests."));
        }

        var authorization = Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var suppliedToken = authorization[bearerPrefix.Length..].Trim();
        if (!TokensMatch(suppliedToken, Options.Token))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "The local development authentication token is invalid."));
        }

        var claims = new[]
        {
            new Claim("sub", Options.Subject),
            new Claim("email", Options.Email),
            new Claim("name", Options.DisplayName)
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool TokensMatch(string suppliedToken, string expectedToken)
    {
        if (string.IsNullOrEmpty(suppliedToken) || string.IsNullOrEmpty(expectedToken))
        {
            return false;
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        return suppliedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
