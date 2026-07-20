using Hook2Stream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Infrastructure.Persistence;

/// <summary>
/// Maintains the artwork-credit ledger. The caller owns the surrounding unit of
/// work, so reserving a credit and enqueueing the matching job are committed by
/// the same database transaction.
/// </summary>
public static class ArtworkCreditLedger
{
    public const int IncludedGenerationCount = 3;

    public static async Task<bool> HasIncludedGenerationAsync(
        Hook2StreamDbContext db,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var reservedOrCompleted = await db.ArtworkPackRevisions.CountAsync(
            value => value.ProjectId == projectId &&
                     (value.State == RevisionState.Processing || value.CandidateAssetIdsJson != "[]"),
            cancellationToken);
        return reservedOrCompleted < IncludedGenerationCount;
    }

    public static async Task<bool> TryReserveAsync(
        Hook2StreamDbContext db,
        Guid workspaceId,
        Guid artworkRevisionId,
        CancellationToken cancellationToken)
    {
        var reference = ReserveReference(artworkRevisionId);
        if (await db.ArtworkCreditTransactions.AnyAsync(
                value => value.WorkspaceId == workspaceId && value.Reference == reference,
                cancellationToken))
        {
            return true;
        }

        var wallet = await db.WorkspaceArtworkCredits.SingleOrDefaultAsync(
            value => value.WorkspaceId == workspaceId,
            cancellationToken);
        var grant = await db.ArtworkCreditGrants
            .Where(value => value.WorkspaceId == workspaceId && value.Remaining > 0 && value.RevokedAt == null)
            .OrderBy(value => value.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (wallet is null || wallet.Balance <= 0 || grant is null)
        {
            return false;
        }

        wallet.Balance--;
        grant.Remaining--;
        db.ArtworkCreditTransactions.Add(new ArtworkCreditTransaction
        {
            WorkspaceId = workspaceId,
            GrantId = grant.Id,
            Delta = -1,
            BalanceAfter = wallet.Balance,
            Reason = "artwork_generation_reserved",
            Reference = reference
        });
        return true;
    }

    public static async Task<bool> CommitReservationAsync(
        Hook2StreamDbContext db,
        Guid workspaceId,
        Guid artworkRevisionId,
        CancellationToken cancellationToken)
    {
        var reservation = await ReservationAsync(db, workspaceId, artworkRevisionId, cancellationToken);
        if (reservation is null || await IsFinalizedAsync(db, workspaceId, artworkRevisionId, cancellationToken))
        {
            return false;
        }

        var balance = await db.WorkspaceArtworkCredits
            .Where(value => value.WorkspaceId == workspaceId)
            .Select(value => value.Balance)
            .SingleAsync(cancellationToken);
        db.ArtworkCreditTransactions.Add(new ArtworkCreditTransaction
        {
            WorkspaceId = workspaceId,
            GrantId = reservation.GrantId,
            Delta = 0,
            BalanceAfter = balance,
            Reason = "artwork_generation_committed",
            Reference = FinalizationReference(artworkRevisionId)
        });
        return true;
    }

    public static async Task<bool> ReleaseReservationAsync(
        Hook2StreamDbContext db,
        Guid workspaceId,
        Guid artworkRevisionId,
        CancellationToken cancellationToken)
    {
        var reservation = await ReservationAsync(db, workspaceId, artworkRevisionId, cancellationToken);
        if (reservation is null || await IsFinalizedAsync(db, workspaceId, artworkRevisionId, cancellationToken))
        {
            return false;
        }

        var wallet = await db.WorkspaceArtworkCredits.SingleAsync(
            value => value.WorkspaceId == workspaceId,
            cancellationToken);
        var grant = reservation.GrantId is { } grantId
            ? await db.ArtworkCreditGrants.SingleOrDefaultAsync(value => value.Id == grantId, cancellationToken)
            : null;
        var canRestore = grant is { RevokedAt: null };
        if (canRestore)
        {
            wallet.Balance++;
            grant!.Remaining++;
        }

        db.ArtworkCreditTransactions.Add(new ArtworkCreditTransaction
        {
            WorkspaceId = workspaceId,
            GrantId = reservation.GrantId,
            Delta = canRestore ? 1 : 0,
            BalanceAfter = wallet.Balance,
            Reason = canRestore
                ? "artwork_generation_released"
                : "artwork_generation_release_revoked",
            Reference = FinalizationReference(artworkRevisionId)
        });
        return true;
    }

    private static Task<ArtworkCreditTransaction?> ReservationAsync(
        Hook2StreamDbContext db,
        Guid workspaceId,
        Guid artworkRevisionId,
        CancellationToken cancellationToken) =>
        db.ArtworkCreditTransactions.SingleOrDefaultAsync(
            value => value.WorkspaceId == workspaceId &&
                     value.Reference == ReserveReference(artworkRevisionId),
            cancellationToken);

    private static Task<bool> IsFinalizedAsync(
        Hook2StreamDbContext db,
        Guid workspaceId,
        Guid artworkRevisionId,
        CancellationToken cancellationToken)
    {
        var finalizationReference = FinalizationReference(artworkRevisionId);
        return db.ArtworkCreditTransactions.AnyAsync(
            value => value.WorkspaceId == workspaceId &&
                     value.Reference == finalizationReference,
            cancellationToken);
    }

    private static string ReserveReference(Guid artworkRevisionId) =>
        $"artwork:{artworkRevisionId:N}:reserve";

    private static string FinalizationReference(Guid artworkRevisionId) =>
        $"artwork:{artworkRevisionId:N}:finalize";
}
