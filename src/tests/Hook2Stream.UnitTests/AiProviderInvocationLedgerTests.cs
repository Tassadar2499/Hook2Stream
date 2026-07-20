using System.Security.Cryptography;
using System.Text;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.UnitTests;

public sealed class AiProviderInvocationLedgerTests
{
    [Fact]
    public void Record_PersistsOnlySafeProvenanceAndNonNegativeUsage()
    {
        using var db = CreateDbContext();
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var startedAt = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
        var provenance = new ProviderProvenance(
            "openrouter",
            "openai/whisper-large-v3:resolved",
            "stt-api-v1",
            "request-123",
            "raw input accidentally supplied instead of a hash",
            new string('B', 64),
            startedAt,
            startedAt.AddSeconds(-1),
            "openai/whisper-large-v3",
            "groq",
            "generation-123",
            new ProviderUsage(
                InputTokens: 120,
                OutputTokens: -1,
                TotalTokens: 120,
                AudioSeconds: 42.5,
                GeneratedImages: -2,
                CostUsd: 0.00125m));

        var invocation = AiProviderInvocationLedger.Record(
            db,
            workspaceId,
            projectId,
            jobId,
            attemptNumber: 1,
            stage: "transcription",
            operationId: jobId,
            provenance,
            failure: null);

        Assert.Equal(AiProviderInvocationLedger.Succeeded, invocation.Status);
        Assert.Equal("openrouter", invocation.RequestedProvider);
        Assert.Equal("groq", invocation.ResolvedProvider);
        Assert.Equal("openai/whisper-large-v3", invocation.RequestedModel);
        Assert.Equal("openai/whisper-large-v3:resolved", invocation.ResolvedModel);
        Assert.Equal("request-123", invocation.RequestId);
        Assert.Equal("generation-123", invocation.GenerationId);
        Assert.Equal(Sha256("raw input accidentally supplied instead of a hash"), invocation.InputHash);
        Assert.Equal(new string('b', 64), invocation.ParameterHash);
        Assert.Equal(startedAt, invocation.StartedAt);
        Assert.Equal(startedAt, invocation.CompletedAt);
        Assert.Equal(120, invocation.InputTokens);
        Assert.Null(invocation.OutputTokens);
        Assert.Null(invocation.GeneratedImages);
        Assert.Equal(0.00125m, invocation.CostUsd);
        Assert.Same(invocation, db.ChangeTracker.Entries<AiProviderInvocation>().Single().Entity);
    }

    [Fact]
    public void Record_FailureStoresCodeButNotSafeMessage()
    {
        using var db = CreateDbContext();
        const string sensitiveMessage = "Do not persist these lyrics or prompt text";
        var now = DateTimeOffset.UtcNow;
        var provenance = new ProviderProvenance(
            "openrouter",
            "model",
            "v1",
            null,
            new string('a', 64),
            new string('b', 64),
            now,
            now);

        var invocation = AiProviderInvocationLedger.Record(
            db,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            2,
            "artwork",
            Guid.NewGuid(),
            provenance,
            new ProviderFailure(ProviderFailureKind.Permanent, "provider.failed", sensitiveMessage));

        Assert.Equal(AiProviderInvocationLedger.Failed, invocation.Status);
        Assert.Equal("provider.failed", invocation.FailureCode);
        Assert.DoesNotContain(
            typeof(AiProviderInvocation).GetProperties(),
            property => property.Name is "FailureMessage" or "Prompt" or "Lyrics" or "Payload" or "Base64");
        Assert.DoesNotContain(
            db.ChangeTracker.Entries<AiProviderInvocation>()
                .SelectMany(entry => entry.Properties)
                .Select(property => property.CurrentValue?.ToString()),
            value => string.Equals(value, sensitiveMessage, StringComparison.Ordinal));
    }

    private static Hook2StreamDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new Hook2StreamDbContext(options);
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
