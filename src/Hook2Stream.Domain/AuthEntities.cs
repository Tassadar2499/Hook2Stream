namespace Hook2Stream.Domain;

/// <summary>
/// A browser session. Only hashes are persisted; the bearer values exist solely
/// in short-lived HTTP cookies.
/// </summary>
public sealed class AuthSession : Entity
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public required string TokenHash { get; set; }
    public required string CsrfTokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// Single-use server-side OAuth state. The value sent to the browser is never
/// persisted in plaintext.
/// </summary>
public sealed class OAuthLoginState : Entity
{
    public required string StateHash { get; set; }
    public required string ReturnPath { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
