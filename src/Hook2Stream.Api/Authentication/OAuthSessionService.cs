using System.Security.Cryptography;
using System.Text;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Api.Authentication;

public sealed record IssuedOAuthSession(
    Guid Id,
    string SessionToken,
    string CsrfToken,
    DateTimeOffset ExpiresAt);

public sealed record ActiveOAuthSession(AuthSession Session, AppUser User);

public sealed class OAuthSessionService(
    Hook2StreamDbContext dbContext,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan LoginStateLifetime = TimeSpan.FromMinutes(10);

    public async Task<string> IssueLoginStateAsync(
        string? returnPath,
        CancellationToken cancellationToken)
    {
        var state = CreateSecret();
        dbContext.Set<OAuthLoginState>().Add(new OAuthLoginState
        {
            StateHash = HashSecret(state),
            ReturnPath = SanitizeReturnPath(returnPath),
            ExpiresAt = timeProvider.GetUtcNow().Add(LoginStateLifetime)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return state;
    }

    public async Task<string?> ConsumeLoginStateAsync(
        string? stateFromQuery,
        string? stateFromCookie,
        CancellationToken cancellationToken)
    {
        if (!FixedTimeEquals(stateFromQuery, stateFromCookie)) return null;

        var stateHash = HashSecret(stateFromQuery!);
        var state = await dbContext.Set<OAuthLoginState>()
            .SingleOrDefaultAsync(value => value.StateHash == stateHash, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (state is null || state.ConsumedAt is not null || state.ExpiresAt <= now)
            return null;

        state.ConsumedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return SanitizeReturnPath(state.ReturnPath);
    }

    public async Task<IssuedOAuthSession> IssueSessionAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var sessionToken = CreateSecret();
        var csrfToken = CreateSecret();
        var expiresAt = timeProvider.GetUtcNow().Add(SessionLifetime);
        var session = new AuthSession
        {
            UserId = userId,
            TokenHash = HashSecret(sessionToken),
            CsrfTokenHash = HashSecret(csrfToken),
            ExpiresAt = expiresAt
        };
        dbContext.Set<AuthSession>().Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new IssuedOAuthSession(session.Id, sessionToken, csrfToken, expiresAt);
    }

    public async Task<ActiveOAuthSession?> GetActiveSessionAsync(
        string? sessionToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return null;

        var tokenHash = HashSecret(sessionToken);
        var now = timeProvider.GetUtcNow();
        var session = await dbContext.Set<AuthSession>()
            .AsNoTracking()
            .Include(value => value.User)
            .SingleOrDefaultAsync(
                value => value.TokenHash == tokenHash &&
                         value.RevokedAt == null &&
                         value.ExpiresAt > now,
                cancellationToken);

        return session?.User is null ? null : new ActiveOAuthSession(session, session.User);
    }

    public async Task<(string Token, DateTimeOffset ExpiresAt)?> RotateCsrfAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var session = await dbContext.Set<AuthSession>().SingleOrDefaultAsync(
            value => value.Id == sessionId &&
                     value.RevokedAt == null &&
                     value.ExpiresAt > now,
            cancellationToken);
        if (session is null) return null;

        var csrfToken = CreateSecret();
        session.CsrfTokenHash = HashSecret(csrfToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (csrfToken, session.ExpiresAt);
    }

    public async Task RevokeAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await dbContext.Set<AuthSession>()
            .SingleOrDefaultAsync(value => value.Id == sessionId, cancellationToken);
        if (session is null || session.RevokedAt is not null) return;

        session.RevokedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public static string HashSecret(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool FixedTimeEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    public static string SanitizeReturnPath(string? returnPath)
    {
        if (string.IsNullOrWhiteSpace(returnPath)) return "/";
        if (!returnPath.StartsWith("/", StringComparison.Ordinal)) return "/";
        if (returnPath.StartsWith("//", StringComparison.Ordinal)) return "/";
        if (returnPath.Contains("\\", StringComparison.Ordinal)) return "/";
        if (returnPath.Any(char.IsControl)) return "/";
        return returnPath.Length <= 512 ? returnPath : "/";
    }

    private static string CreateSecret() =>
        WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
}
