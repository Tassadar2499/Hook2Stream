using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Hook2Stream.Api.Authentication;

public sealed record ApplicationJwt(string Token, DateTimeOffset ExpiresAt);

public interface IApplicationJwtIssuer
{
    ApplicationJwt Issue(string subject, string? email, string? displayName);
}

public sealed class ApplicationJwtIssuer(IOptions<JwtIssuerOptions> options) : IApplicationJwtIssuer
{
    private readonly JwtIssuerOptions _options = options.Value;

    public ApplicationJwt Issue(string subject, string? email, string? displayName)
    {
        if (!_options.IsValid)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey must be configured with at least 32 characters before issuing tokens.");
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(_options.ExpirationMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString("N"))
        };
        if (!string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new Claim("email", email));
        }
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            claims.Add(new Claim("name", displayName));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return new ApplicationJwt(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public static string GenerateSigningKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
}
