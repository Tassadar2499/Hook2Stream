using System.Security.Cryptography;
using System.Text;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hook2Stream.Infrastructure.Persistence;

/// <summary>
/// Captures provider provenance without accepting provider inputs or outputs.
/// This deliberately narrow API prevents prompts, lyrics, audio, images, and
/// base64 payloads from entering the invocation ledger.
/// </summary>
public static class AiProviderInvocationLedger
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    public const string DiscardedConsentRevoked = "discarded_consent_revoked";
    public const string DiscardedStaleInput = "discarded_stale_input";

    public static AiProviderInvocation Record(
        Hook2StreamDbContext db,
        Guid workspaceId,
        Guid? projectId,
        Guid jobId,
        int attemptNumber,
        string stage,
        Guid operationId,
        ProviderProvenance provenance,
        ProviderFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentOutOfRangeException.ThrowIfEqual(workspaceId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(jobId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(operationId, Guid.Empty);
        if (projectId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(projectId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNumber);

        var startedAt = provenance.StartedAt;
        var completedAt = provenance.CompletedAt < startedAt
            ? startedAt
            : provenance.CompletedAt;
        var usage = provenance.Usage;

        var invocation = new AiProviderInvocation
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            JobId = jobId,
            OperationId = operationId,
            AttemptNumber = attemptNumber,
            Stage = RequiredMetadata(stage, 64, nameof(stage)),
            Status = failure is null ? Succeeded : Failed,
            FailureCode = OptionalMetadata(failure?.Code, 128),
            RequestedProvider = RequiredMetadata(provenance.Provider, 64, nameof(provenance.Provider)),
            ResolvedProvider = OptionalMetadata(provenance.ResolvedProvider, 128),
            RequestedModel = RequiredMetadata(
                provenance.RequestedModel ?? provenance.Model,
                255,
                nameof(provenance.RequestedModel)),
            ResolvedModel = OptionalMetadata(provenance.Model, 255),
            RequestId = OptionalMetadata(provenance.RequestId, 2_048),
            GenerationId = OptionalMetadata(provenance.GenerationId, 2_048),
            InputHash = Sha256(provenance.InputHash),
            ParameterHash = Sha256(provenance.ParameterHash),
            StartedAt = startedAt,
            CompletedAt = completedAt,
            InputTokens = NonNegative(usage?.InputTokens),
            OutputTokens = NonNegative(usage?.OutputTokens),
            TotalTokens = NonNegative(usage?.TotalTokens),
            AudioSeconds = NonNegative(usage?.AudioSeconds),
            GeneratedImages = NonNegative(usage?.GeneratedImages),
            CostUsd = NonNegative(usage?.CostUsd)
        };

        db.AiProviderInvocations.Add(invocation);
        return invocation;
    }

    private static string RequiredMetadata(string? value, int maxLength, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            throw new ArgumentException("Provider invocation metadata must not be empty.", parameterName);
        }

        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string? OptionalMetadata(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string Sha256(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 64 && normalized.All(Uri.IsHexDigit))
        {
            return normalized.ToLowerInvariant();
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static long? NonNegative(long? value) => value is >= 0 ? value : null;

    private static int? NonNegative(int? value) => value is >= 0 ? value : null;

    private static double? NonNegative(double? value) =>
        value is >= 0 && double.IsFinite(value.Value) ? value : null;

    private static decimal? NonNegative(decimal? value) => value is >= 0 ? value : null;
}

/// <summary>
/// Persists usage in an independent scope so a provider failure can be audited
/// without committing partially-mutated workflow state from the job handler.
/// Persistence is best effort: implementations preserve caller cancellation,
/// but audit-storage failures must not change the paid provider workflow.
/// </summary>
public interface IAiProviderInvocationWriter
{
    Task RecordAsync(
        LeasedJob job,
        string stage,
        ProviderExecutionContext context,
        ProviderProvenance provenance,
        ProviderFailure? failure,
        string? status,
        CancellationToken cancellationToken);
}

public sealed class AiProviderInvocationWriter(
    IServiceScopeFactory scopeFactory,
    ILogger<AiProviderInvocationWriter> logger)
    : IAiProviderInvocationWriter
{
    private const int MaxWriteAttempts = 3;
    private static readonly EventId PersistenceFailedEvent =
        new(1002, "AiProviderInvocationPersistenceFailed");

    public async Task RecordAsync(
        LeasedJob job,
        string stage,
        ProviderExecutionContext context,
        ProviderProvenance provenance,
        ProviderFailure? failure,
        string? status,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(provenance.Provider, "openrouter", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // A failed SaveChanges leaves its DbContext unsuitable for a
                // reliable retry. Recreate the entire unit of work each time.
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
                var normalizedStage = NormalizeStage(stage);
                var alreadyRecorded = await db.AiProviderInvocations
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .AnyAsync(
                        value => value.JobId == job.Id &&
                                 value.AttemptNumber == job.AttemptNumber &&
                                 value.Stage == normalizedStage,
                        cancellationToken);
                if (alreadyRecorded)
                {
                    return;
                }

                var invocation = AiProviderInvocationLedger.Record(
                    db,
                    job.WorkspaceId,
                    job.ProjectId,
                    job.Id,
                    job.AttemptNumber,
                    normalizedStage,
                    context.OperationId,
                    provenance,
                    failure);
                if (!string.IsNullOrWhiteSpace(status))
                {
                    invocation.Status = status;
                }

                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            logger.LogError(
                PersistenceFailedEvent,
                "Could not persist OpenRouter invocation metadata for job {JobId}, attempt {AttemptNumber}, stage {Stage}, operation {OperationId} after {WriteAttempts} attempts.",
                job.Id,
                job.AttemptNumber,
                SafeStage(stage),
                context.OperationId,
                MaxWriteAttempts);
        }
        catch
        {
            // Audit telemetry must never control the paid provider workflow,
            // including when a custom logging sink itself is unavailable.
        }
    }

    private static string NormalizeStage(string stage)
    {
        var normalized = stage?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            throw new ArgumentException("Provider invocation stage must not be empty.", nameof(stage));
        }

        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static string SafeStage(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return "missing";
        var safe = new string(stage.Trim().Where(value => !char.IsControl(value)).Take(64).ToArray());
        return safe.Length == 0 ? "missing" : safe;
    }
}
