using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Hook2Stream.Api.Authentication;

public sealed class ClerkOptions
{
    public const string SectionName = "Clerk";

    public string Issuer { get; set; } = "";
    public string[] AuthorizedParties { get; set; } = [];
}

public static class ClerkJwtEvents
{
    public static JwtBearerEvents Create(ClerkOptions options) =>
        new()
        {
            OnTokenValidated = context =>
            {
                if (options.AuthorizedParties.Length == 0)
                {
                    return Task.CompletedTask;
                }

                var authorizedParty = context.Principal?.FindFirst("azp")?.Value;
                if (string.IsNullOrWhiteSpace(authorizedParty) ||
                    !options.AuthorizedParties.Contains(authorizedParty, StringComparer.OrdinalIgnoreCase))
                {
                    context.Fail("The token authorized party is not allowed.");
                }

                return Task.CompletedTask;
            }
        };
}
