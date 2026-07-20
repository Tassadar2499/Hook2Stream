using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.IntegrationTests;

public sealed class ArtworkCreditLedgerTests
{
    [Fact]
    public async Task Failed_revisions_do_not_consume_an_included_generation()
    {
        await using var db = Database();
        var projectId = Guid.CreateVersion7();
        var abandoned = Pack(projectId, 4, RevisionState.Superseded);
        abandoned.CandidateAssetIdsJson = "[]";
        db.ArtworkPackRevisions.AddRange(
            Pack(projectId, 1, RevisionState.Approved),
            Pack(projectId, 2, RevisionState.Superseded),
            Pack(projectId, 3, RevisionState.Failed),
            abandoned);
        await db.SaveChangesAsync();

        Assert.True(await ArtworkCreditLedger.HasIncludedGenerationAsync(db, projectId, default));

        db.ArtworkPackRevisions.Add(Pack(projectId, 5, RevisionState.Processing));
        await db.SaveChangesAsync();

        Assert.False(await ArtworkCreditLedger.HasIncludedGenerationAsync(db, projectId, default));
    }

    [Fact]
    public async Task Reserved_credit_is_released_exactly_once_after_terminal_failure()
    {
        await using var db = Database();
        var (workspaceId, grant) = SeedCredit(db);
        var revisionId = Guid.CreateVersion7();
        await db.SaveChangesAsync();

        Assert.True(await ArtworkCreditLedger.TryReserveAsync(db, workspaceId, revisionId, default));
        await db.SaveChangesAsync();
        Assert.Equal(0, (await db.WorkspaceArtworkCredits.SingleAsync()).Balance);
        Assert.Equal(0, (await db.ArtworkCreditGrants.SingleAsync()).Remaining);
        Assert.True(await ArtworkCreditLedger.TryReserveAsync(db, workspaceId, revisionId, default));
        await db.SaveChangesAsync();
        Assert.Single(await db.ArtworkCreditTransactions.ToListAsync());

        Assert.True(await ArtworkCreditLedger.ReleaseReservationAsync(db, workspaceId, revisionId, default));
        await db.SaveChangesAsync();
        Assert.Equal(1, (await db.WorkspaceArtworkCredits.SingleAsync()).Balance);
        Assert.Equal(1, (await db.ArtworkCreditGrants.SingleAsync()).Remaining);

        Assert.False(await ArtworkCreditLedger.ReleaseReservationAsync(db, workspaceId, revisionId, default));
        await db.SaveChangesAsync();

        var transactions = await db.ArtworkCreditTransactions.OrderBy(value => value.CreatedAt).ToListAsync();
        Assert.Collection(
            transactions,
            reserve =>
            {
                Assert.Equal(grant.Id, reserve.GrantId);
                Assert.Equal(-1, reserve.Delta);
                Assert.Equal("artwork_generation_reserved", reserve.Reason);
                Assert.EndsWith(":reserve", reserve.Reference, StringComparison.Ordinal);
            },
            release =>
            {
                Assert.Equal(grant.Id, release.GrantId);
                Assert.Equal(1, release.Delta);
                Assert.Equal("artwork_generation_released", release.Reason);
                Assert.EndsWith(":finalize", release.Reference, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task Committed_reservation_cannot_be_released()
    {
        await using var db = Database();
        var (workspaceId, _) = SeedCredit(db);
        var revisionId = Guid.CreateVersion7();
        await db.SaveChangesAsync();

        Assert.True(await ArtworkCreditLedger.TryReserveAsync(db, workspaceId, revisionId, default));
        await db.SaveChangesAsync();
        Assert.True(await ArtworkCreditLedger.CommitReservationAsync(db, workspaceId, revisionId, default));
        await db.SaveChangesAsync();

        Assert.False(await ArtworkCreditLedger.ReleaseReservationAsync(db, workspaceId, revisionId, default));
        Assert.Equal(0, (await db.WorkspaceArtworkCredits.SingleAsync()).Balance);
        Assert.Equal(0, (await db.ArtworkCreditGrants.SingleAsync()).Remaining);
        Assert.Equal(2, await db.ArtworkCreditTransactions.CountAsync());
        Assert.Contains(
            await db.ArtworkCreditTransactions.ToListAsync(),
            value => value.Reason == "artwork_generation_committed" && value.Delta == 0);
    }

    [Fact]
    public async Task Release_after_grant_revocation_does_not_restore_spendable_credit()
    {
        await using var db = Database();
        var (workspaceId, grant) = SeedCredit(db);
        var revisionId = Guid.CreateVersion7();
        await db.SaveChangesAsync();

        Assert.True(await ArtworkCreditLedger.TryReserveAsync(db, workspaceId, revisionId, default));
        await db.SaveChangesAsync();
        grant.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        Assert.True(await ArtworkCreditLedger.ReleaseReservationAsync(db, workspaceId, revisionId, default));
        await db.SaveChangesAsync();

        Assert.Equal(0, (await db.WorkspaceArtworkCredits.SingleAsync()).Balance);
        Assert.Equal(0, grant.Remaining);
        var release = await db.ArtworkCreditTransactions.SingleAsync(
            value => value.Reason == "artwork_generation_release_revoked");
        Assert.Equal(0, release.Delta);
        Assert.Equal(0, release.BalanceAfter);
    }

    private static Hook2StreamDbContext Database()
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"artwork-credit-ledger-{Guid.NewGuid():N}")
            .Options;
        return new Hook2StreamDbContext(options);
    }

    private static (Guid WorkspaceId, ArtworkCreditGrant Grant) SeedCredit(Hook2StreamDbContext db)
    {
        var workspaceId = Guid.CreateVersion7();
        var grant = new ArtworkCreditGrant
        {
            WorkspaceId = workspaceId,
            CheckoutId = Guid.CreateVersion7(),
            Granted = 1,
            Remaining = 1
        };
        db.WorkspaceArtworkCredits.Add(new WorkspaceArtworkCredit
        {
            WorkspaceId = workspaceId,
            Balance = 1
        });
        db.ArtworkCreditGrants.Add(grant);
        return (workspaceId, grant);
    }

    private static ArtworkPackRevision Pack(Guid projectId, int number, RevisionState state) =>
        new()
        {
            WorkspaceId = Guid.CreateVersion7(),
            ProjectId = projectId,
            Number = number,
            OperationNumber = number,
            State = state,
            CandidateAssetIdsJson = state is RevisionState.ReadyForReview or RevisionState.Approved or RevisionState.Superseded
                ? $"[\"{Guid.CreateVersion7()}\"]"
                : "[]"
        };
}
