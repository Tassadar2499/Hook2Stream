using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Infrastructure.Jobs;

public static class JobLeaseFence
{
    public static Task CommitAsync(
        Hook2StreamDbContext db,
        LeasedJob job,
        CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(
            async token =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var fencedRows = await db.Jobs
                    .Where(value => value.Id == job.Id &&
                                    value.State == JobState.Running &&
                                    value.LeaseOwner == job.LeaseOwner &&
                                    value.LeaseToken == job.LeaseToken)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(value => value.UpdatedAt, value => value.UpdatedAt),
                        token);
                if (fencedRows != 1)
                {
                    throw new JobLeaseFenceException(job.Id);
                }

                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            },
            cancellationToken);
}

public sealed class JobLeaseFenceException(Guid jobId)
    : Exception($"The lease fence rejected a stale write for job {jobId}.");
