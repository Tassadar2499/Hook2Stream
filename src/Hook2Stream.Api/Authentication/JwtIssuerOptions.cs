namespace Hook2Stream.Api.Authentication;

public sealed class JwtIssuerOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "hook2stream";
    public string Audience { get; set; } = "hook2stream-api";

    /// <summary>
    /// HMAC-SHA256 signing key. Must contain at least 32 characters of high entropy.
    /// Generate with, for example: <c>openssl rand -base64 48</c>.
    /// </summary>
    public string SigningKey { get; set; } = "";

    /// <summary>Session lifetime in minutes. Defaults to seven days.</summary>
    public int ExpirationMinutes { get; set; } = 60 * 24 * 7;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(SigningKey) && SigningKey.Length >= 32;
}
