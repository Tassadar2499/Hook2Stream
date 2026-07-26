using Hook2Stream.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hook2Stream.Worker;

internal sealed record UnexpectedJobFailure(
    string Code,
    string SafeMessage,
    bool Retryable);

internal static class JobFailureClassifier
{
    public static UnexpectedJobFailure Classify(Exception exception, LeasedJob job)
    {
        var databaseException = UnwrapDatabaseException(exception);
        if (databaseException is PostgresException postgresException)
        {
            return ClassifySqlState(postgresException.SqlState, postgresException.IsTransient, job);
        }

        if (databaseException is NpgsqlException npgsqlException)
        {
            return RetryableOrTerminal(
                "job.database_unavailable",
                npgsqlException.IsTransient,
                job);
        }

        return RetryableOrTerminal("job.processing_failed", retryable: true, job);
    }

    internal static UnexpectedJobFailure ClassifySqlState(
        string? sqlState,
        bool providerReportsTransient,
        LeasedJob job)
    {
        // PostgreSQL class 42 represents syntax/access-rule violations,
        // including undefined operators such as the former jsonb LIKE lookup.
        // Retrying an unchanged query can never make that contract valid.
        if (!string.IsNullOrWhiteSpace(sqlState) &&
            sqlState.StartsWith("42", StringComparison.Ordinal))
        {
            return new UnexpectedJobFailure(
                "job.database_contract_invalid",
                "Processing cannot continue because an internal data operation is invalid.",
                Retryable: false);
        }

        return RetryableOrTerminal(
            "job.database_unavailable",
            providerReportsTransient,
            job);
    }

    private static UnexpectedJobFailure RetryableOrTerminal(
        string code,
        bool retryable,
        LeasedJob job)
    {
        var willRetry = retryable && job.AttemptNumber < job.MaxAttempts;
        return new UnexpectedJobFailure(
            code,
            willRetry
                ? "Processing failed. The operation will be retried when possible."
                : "Processing failed and requires attention.",
            retryable);
    }

    private static Exception UnwrapDatabaseException(Exception exception)
    {
        while (exception is DbUpdateException updateException &&
               updateException.InnerException is { } innerException)
        {
            exception = innerException;
        }

        return exception;
    }
}
