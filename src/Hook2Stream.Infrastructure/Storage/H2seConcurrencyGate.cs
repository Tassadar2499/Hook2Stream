using Microsoft.Extensions.Options;
using Npgsql;

namespace Hook2Stream.Infrastructure.Storage;

internal interface IH2seConcurrencyGate
{
    ValueTask<IAsyncDisposable> AcquireEncryptionAsync(CancellationToken cancellationToken);
    ValueTask<IAsyncDisposable> AcquireDownloadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Uses PostgreSQL transaction advisory locks as an environment-wide bounded
/// semaphore. Every API/worker container shares the same database, so the
/// configured 8 encryption and 4 download slots are real host/contour limits
/// instead of per-request or per-process limits. Holding a transaction also
/// keeps PgBouncer transaction-pool connections pinned for the lease.
/// </summary>
internal sealed class PostgresH2seConcurrencyGate(
    IOptions<DatabaseConnectionOptions> databaseOptions,
    IOptions<StorageEncryptionOptions> encryptionOptions) : IH2seConcurrencyGate
{
    private const int EncryptionNamespace = 0x48324531; // H2E1
    private const int DownloadNamespace = 0x48324431; // H2D1
    private readonly string _connectionString = databaseOptions.Value.ConnectionString;
    private readonly int _encryptionSlots = encryptionOptions.Value.MaxConcurrentEncryptions;
    private readonly int _downloadSlots = encryptionOptions.Value.MaxConcurrentDownloads;

    public ValueTask<IAsyncDisposable> AcquireEncryptionAsync(CancellationToken cancellationToken) =>
        AcquireAsync(EncryptionNamespace, _encryptionSlots, cancellationToken);

    public ValueTask<IAsyncDisposable> AcquireDownloadAsync(CancellationToken cancellationToken) =>
        AcquireAsync(DownloadNamespace, _downloadSlots, cancellationToken);

    private async ValueTask<IAsyncDisposable> AcquireAsync(
        int lockNamespace,
        int slots,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connection = new NpgsqlConnection(_connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                var transaction = await connection.BeginTransactionAsync(cancellationToken);
                for (var slot = 0; slot < slots; slot++)
                {
                    await using var command = new NpgsqlCommand(
                        "SELECT pg_try_advisory_xact_lock(@namespace, @slot)",
                        connection,
                        transaction);
                    command.Parameters.AddWithValue("namespace", lockNamespace);
                    command.Parameters.AddWithValue("slot", slot);
                    if (await command.ExecuteScalarAsync(cancellationToken) is true)
                        return new AdvisoryLease(connection, transaction);
                }
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }

            await connection.DisposeAsync();
            await Task.Delay(50, cancellationToken);
        }
    }

    private sealed class AdvisoryLease(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Closing the PostgreSQL session releases the transaction lock
                // even when an explicit rollback cannot be sent.
            }
            finally
            {
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }
}

internal sealed class ProcessH2seConcurrencyGate(int encryptionSlots, int downloadSlots) : IH2seConcurrencyGate
{
    private readonly SemaphoreSlim _encryptions = new(encryptionSlots);
    private readonly SemaphoreSlim _downloads = new(downloadSlots);

    public async ValueTask<IAsyncDisposable> AcquireEncryptionAsync(CancellationToken cancellationToken)
    {
        await _encryptions.WaitAsync(cancellationToken);
        return new SemaphoreLease(_encryptions);
    }

    public async ValueTask<IAsyncDisposable> AcquireDownloadAsync(CancellationToken cancellationToken)
    {
        await _downloads.WaitAsync(cancellationToken);
        return new SemaphoreLease(_downloads);
    }

    private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
