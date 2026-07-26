using System.Security.Cryptography;
using System.Text;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    [Fact]
    public async Task Writer_retries_with_a_fresh_scope_and_is_idempotent()
    {
        var interceptor = new FailingSaveChangesInterceptor(failures: 1);
        using var services = CreateWriterServices(interceptor);
        var logger = new RecordingLogger();
        var writer = new AiProviderInvocationWriter(
            services.GetRequiredService<IServiceScopeFactory>(),
            logger);
        var (job, context, provenance) = WriterInput();

        await writer.RecordAsync(
            job,
            " transcription ",
            context,
            provenance,
            failure: null,
            AiProviderInvocationLedger.DiscardedStaleInput,
            CancellationToken.None);
        await writer.RecordAsync(
            job,
            "transcription",
            context,
            provenance,
            failure: null,
            AiProviderInvocationLedger.DiscardedStaleInput,
            CancellationToken.None);

        await using var verificationScope = services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var invocation = await db.AiProviderInvocations.SingleAsync();
        Assert.Equal(2, interceptor.SaveAttempts);
        Assert.Equal(2, interceptor.SaveContextIds.Count);
        Assert.Equal(AiProviderInvocationLedger.DiscardedStaleInput, invocation.Status);
        Assert.Equal("transcription", invocation.Stage);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Writer_logs_and_returns_after_exhausting_retry_budget()
    {
        const string sensitiveFailure = "database failure containing private provider metadata";
        var interceptor = new FailingSaveChangesInterceptor(
            failures: int.MaxValue,
            sensitiveFailure);
        using var services = CreateWriterServices(interceptor);
        var logger = new RecordingLogger();
        var writer = new AiProviderInvocationWriter(
            services.GetRequiredService<IServiceScopeFactory>(),
            logger);
        var (job, context, provenance) = WriterInput();

        var exception = await Record.ExceptionAsync(() => writer.RecordAsync(
            job,
            "artwork.covers",
            context,
            provenance with { RequestId = "provider-request-must-not-enter-the-log" },
            failure: null,
            status: null,
            CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(3, interceptor.SaveAttempts);
        Assert.Equal(3, interceptor.SaveContextIds.Count);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("AiProviderInvocationPersistenceFailed", entry.EventId.Name);
        Assert.Null(entry.Exception);
        Assert.Contains(job.Id.ToString(), entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(context.OperationId.ToString(), entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider-request-must-not-enter-the-log", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveFailure, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), entry.Message, StringComparison.Ordinal);

        await using var verificationScope = services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Empty(await db.AiProviderInvocations.ToListAsync());
    }

    [Fact]
    public async Task Writer_preserves_caller_cancellation()
    {
        var interceptor = new FailingSaveChangesInterceptor(failures: 0);
        using var services = CreateWriterServices(interceptor);
        var logger = new RecordingLogger();
        var writer = new AiProviderInvocationWriter(
            services.GetRequiredService<IServiceScopeFactory>(),
            logger);
        var (job, context, provenance) = WriterInput();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.RecordAsync(
            job,
            "campaign",
            context,
            provenance,
            failure: null,
            status: null,
            cancellation.Token));

        Assert.Equal(0, interceptor.SaveAttempts);
        Assert.Empty(logger.Entries);
    }

    private static Hook2StreamDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new Hook2StreamDbContext(options);
    }

    private static ServiceProvider CreateWriterServices(FailingSaveChangesInterceptor interceptor)
    {
        var databaseName = $"ai-invocation-writer-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<Hook2StreamDbContext>(options =>
            options.UseInMemoryDatabase(databaseName).AddInterceptors(interceptor));
        return services.BuildServiceProvider();
    }

    private static (LeasedJob Job, ProviderExecutionContext Context, ProviderProvenance Provenance) WriterInput()
    {
        var jobId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return (
            new LeasedJob(
                jobId,
                workspaceId,
                projectId,
                null,
                JobType.Transcription,
                "{}",
                2,
                3,
                JobRoutingRegistry.Control,
                "openrouter-v1",
                new string('a', 64),
                1,
                "worker",
                now.AddMinutes(1),
                Guid.NewGuid()),
            new ProviderExecutionContext(
                operationId,
                new string('a', 64),
                new string('b', 64),
                $"staging/{workspaceId:N}/{projectId:N}/{jobId:N}/attempt-2"),
            new ProviderProvenance(
                "openrouter",
                "openai/test-model",
                "test-v1",
                "provider-request",
                new string('a', 64),
                new string('b', 64),
                now,
                now.AddSeconds(1),
                "openai/test-model"));
    }

    private sealed class FailingSaveChangesInterceptor(
        int failures,
        string failureMessage = "Injected invocation persistence failure.") : SaveChangesInterceptor
    {
        private int _remainingFailures = failures;

        public int SaveAttempts { get; private set; }
        public HashSet<Guid> SaveContextIds { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            SaveContextIds.Add(eventData.Context!.ContextId.InstanceId);
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                throw new InvalidOperationException(failureMessage);
            }

            return new ValueTask<InterceptionResult<int>>(result);
        }
    }

    private sealed class RecordingLogger : ILogger<AiProviderInvocationWriter>
    {
        public List<(LogLevel Level, EventId EventId, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, exception, formatter(state, exception)));
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
