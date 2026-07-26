using System.Text.Json;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.UnitTests;

public sealed class ArtworkGenerationRecoveryTests
{
    [Fact]
    public async Task Failed_background_job_cannot_unlock_a_processing_cover_pack()
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"artwork-generation-recovery-{Guid.CreateVersion7():N}")
            .Options;
        await using var db = new Hook2StreamDbContext(options);
        var workspaceId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var pack = new ArtworkPackRevision
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Number = 1,
            OperationNumber = 1,
            State = RevisionState.Processing,
            Prompt = "Processing cover",
            SourceFingerprint = "request:cover-pack"
        };
        db.ArtworkPackRevisions.Add(pack);
        db.Jobs.Add(new Job
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            AssetId = Guid.CreateVersion7(),
            Type = JobType.ArtworkGeneration,
            RequiredCapability = "control",
            HandlerVersion = "openrouter-image-v1",
            InputFingerprint = pack.SourceFingerprint,
            PayloadJson = JsonSerializer.Serialize(new
            {
                projectId,
                artworkPackRevisionId = pack.Id,
                mode = "backgrounds"
            }),
            State = JobState.Failed,
            ErrorCode = "job.lease_expired"
        });
        var grant = new ArtworkCreditGrant
        {
            WorkspaceId = workspaceId,
            CheckoutId = Guid.CreateVersion7(),
            Granted = 1,
            Remaining = 1
        };
        db.ArtworkCreditGrants.Add(grant);
        db.WorkspaceArtworkCredits.Add(new WorkspaceArtworkCredit
        {
            WorkspaceId = workspaceId,
            Balance = 1
        });
        await db.SaveChangesAsync();
        Assert.True(await ArtworkCreditLedger.TryReserveAsync(db, workspaceId, pack.Id, default));
        await db.SaveChangesAsync();

        var recovered = await ArtworkGenerationRecovery.TryFailProcessingCoverAsync(
            db,
            pack,
            CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Null(recovered);
        Assert.Equal(RevisionState.Processing, pack.State);
        Assert.Equal(0, (await db.WorkspaceArtworkCredits.SingleAsync()).Balance);
        Assert.Equal(0, grant.Remaining);
        Assert.Single(await db.ArtworkCreditTransactions.ToListAsync());
    }
}
