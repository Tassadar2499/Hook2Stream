using System.Security.Cryptography;
using System.Text;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Microsoft.Extensions.DependencyInjection;

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

public sealed class AiProviderInvocationWriter(IServiceScopeFactory scopeFactory)
    : IAiProviderInvocationWriter
{
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

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var invocation = AiProviderInvocationLedger.Record(
            db,
            job.WorkspaceId,
            job.ProjectId,
            job.Id,
            job.AttemptNumber,
            stage,
            context.OperationId,
            provenance,
            failure);
        if (!string.IsNullOrWhiteSpace(status))
        {
            invocation.Status = status;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
