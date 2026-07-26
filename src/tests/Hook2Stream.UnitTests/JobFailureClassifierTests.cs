using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Worker;

namespace Hook2Stream.UnitTests;

public sealed class JobFailureClassifierTests
{
    [Fact]
    public void Undefined_postgres_operator_is_terminal_on_first_attempt()
    {
        var failure = JobFailureClassifier.ClassifySqlState(
            "42883",
            providerReportsTransient: false,
            Job(attempt: 1, maxAttempts: 3));

        Assert.False(failure.Retryable);
        Assert.Equal("job.database_contract_invalid", failure.Code);
        Assert.DoesNotContain("retried", failure.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transient_database_failure_schedules_retry_when_budget_remains()
    {
        var failure = JobFailureClassifier.ClassifySqlState(
            "08006",
            providerReportsTransient: true,
            Job(attempt: 1, maxAttempts: 3));

        Assert.True(failure.Retryable);
        Assert.Equal("job.database_unavailable", failure.Code);
        Assert.Contains("retried", failure.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exhausted_transient_failure_does_not_promise_another_retry()
    {
        var failure = JobFailureClassifier.ClassifySqlState(
            "08006",
            providerReportsTransient: true,
            Job(attempt: 3, maxAttempts: 3));

        Assert.True(failure.Retryable);
        Assert.DoesNotContain("retried", failure.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attention", failure.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static LeasedJob Job(int attempt, int maxAttempts) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        null,
        JobType.PreviewRender,
        "{}",
        attempt,
        maxAttempts,
        JobRoutingRegistry.Render,
        "deterministic-render-v1",
        null,
        1,
        "worker",
        DateTimeOffset.UtcNow.AddMinutes(1),
        Guid.CreateVersion7());
}
