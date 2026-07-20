using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Worker;

namespace Hook2Stream.UnitTests;

public sealed class ArtworkCreditCompensationTests
{
    [Fact]
    public void Retryable_failure_releases_only_on_the_last_attempt()
    {
        var pack = new ArtworkPackRevision { State = RevisionState.Processing };
        var failure = new JobHandlerException("artwork.proxy_failed", "Proxy failed.", retryable: true);

        Assert.False(ArtworkGenerationJobHandler.ShouldReleaseReservation(
            failure,
            isBackgrounds: false,
            pack,
            Job(attempt: 2, maxAttempts: 3)));
        Assert.True(ArtworkGenerationJobHandler.ShouldReleaseReservation(
            failure,
            isBackgrounds: false,
            pack,
            Job(attempt: 3, maxAttempts: 3)));
        Assert.True(ArtworkGenerationJobHandler.ShouldReleaseReservation(
            new IOException("Storage failed."),
            isBackgrounds: false,
            pack,
            Job(attempt: 3, maxAttempts: 3)));
    }

    [Fact]
    public void Nonretryable_failure_releases_immediately_but_control_flow_failures_never_do()
    {
        var pack = new ArtworkPackRevision { State = RevisionState.Processing };
        var firstAttempt = Job(attempt: 1, maxAttempts: 3);

        Assert.True(ArtworkGenerationJobHandler.ShouldReleaseReservation(
            new JobHandlerException("artwork.invalid", "Invalid artwork.", retryable: false),
            isBackgrounds: false,
            pack,
            firstAttempt));
        Assert.False(ArtworkGenerationJobHandler.ShouldReleaseReservation(
            new JobBlockedException("rights.required", "Rights required."),
            isBackgrounds: false,
            pack,
            Job(attempt: 3, maxAttempts: 3)));
        Assert.False(ArtworkGenerationJobHandler.ShouldReleaseReservation(
            new JobHandlerException("job.lease_lost", "Lease lost.", retryable: true),
            isBackgrounds: false,
            pack,
            Job(attempt: 3, maxAttempts: 3)));
        Assert.False(ArtworkGenerationJobHandler.ShouldReleaseReservation(
            new JobHandlerException("artwork.invalid", "Invalid artwork.", retryable: false),
            isBackgrounds: true,
            pack,
            firstAttempt));
    }

    private static LeasedJob Job(int attempt, int maxAttempts) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            null,
            JobType.ArtworkGeneration,
            "{}",
            attempt,
            maxAttempts,
            "artwork",
            "v1",
            null,
            1,
            "test-worker",
            DateTimeOffset.UtcNow.AddMinutes(1),
            Guid.CreateVersion7());
}
