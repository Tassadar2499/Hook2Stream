using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Api.Authentication;

/// <summary>
/// Issues and validates the OAuth state parameter using an HMAC over a random nonce
/// and a sanitized return path. The key is derived from the JWT signing key, so a
/// state token issued by one API instance is verifiable by any other instance.
/// </summary>
public sealed class OAuthStateProtector(IOptions<JwtIssuerOptions> jwtOptions)
{
    public const string StateCookieName = "h2s_oauth_state";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private readonly byte[] _key = SHA256.HashData(
        Encoding.UTF8.GetBytes(
            !string.IsNullOrWhiteSpace(jwtOptions.Value.SigningKey) && jwtOptions.Value.SigningKey.Length >= 32
                ? jwtOptions.Value.SigningKey
                : "hook2stream-development-state-key"));

    public (string StateValue, string CookieValue) Issue(string? returnPath)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var sanitizedPath = SanitizeReturnPath(returnPath);
        var payload = $"{Convert.ToHexString(nonce)}|{sanitizedPath}";
        var signature = ComputeSignature(payload);
        var token = $"{payload}|{signature}";
        var cookieValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
        return (token, cookieValue);
    }

    public bool TryValidate(string? stateFromQuery, string? cookieValue, out string returnPath)
    {
        returnPath = "/";
        if (string.IsNullOrWhiteSpace(stateFromQuery) || string.IsNullOrWhiteSpace(cookieValue))
            return false;

        string token;
        try
        {
            token = Encoding.UTF8.GetString(Convert.FromBase64String(cookieValue));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = token.Split('|');
        if (parts.Length != 3) return false;

        var expectedSignature = ComputeSignature($"{parts[0]}|{parts[1]}");
        if (!FixedTimeEquals(expectedSignature, parts[2])) return false;
        if (!string.Equals(stateFromQuery, token, StringComparison.Ordinal)) return false;

        returnPath = SanitizeReturnPath(parts[1]);
        return true;
    }

    public static CookieOptions BuildCookieOptions(bool isHttps) => new()
    {
        HttpOnly = true,
        Secure = isHttps,
        SameSite = SameSiteMode.Lax,
        Expires = DateTimeOffset.UtcNow.Add(StateLifetime),
        Path = "/",
        IsEssential = true
    };

    public static string SanitizeReturnPath(string? returnPath)
    {
        if (string.IsNullOrWhiteSpace(returnPath)) return "/";
        if (!returnPath.StartsWith("/", StringComparison.Ordinal)) return "/";
        if (returnPath.Contains("//", StringComparison.Ordinal)) return "/";
        if (returnPath.Length > 512) return "/";
        return returnPath;
    }

    private string ComputeSignature(string payload)
    {
        var signature = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(signature);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
