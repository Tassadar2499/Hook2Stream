using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Worker;

public sealed class OutboxJobDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    ILogger<OutboxJobDispatcher> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = await DispatchBatchAsync(stoppingToken);
                if (dispatched == 0)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(_options.OutboxPollMilliseconds),
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Outbox dispatch failed. Retrying after {DelaySeconds} seconds.",
                    _options.QueueErrorDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(_options.QueueErrorDelaySeconds), stoppingToken);
            }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < _options.OutboxBatchSize &&
               await DispatchOneAsync(cancellationToken))
        {
            count++;
        }

        return count;
    }

    private async Task<bool> DispatchOneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async token =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
                var message = await dbContext.OutboxMessages
                    .FromSqlRaw(
                        """
                        SELECT *
                        FROM outbox_messages
                        WHERE deleted_at IS NULL
                          AND processed_at IS NULL
                          AND destination = 'job'
                        ORDER BY created_at
                        FOR UPDATE SKIP LOCKED
                        LIMIT 1
                        """)
                    .SingleOrDefaultAsync(token);
                if (message is null)
                {
                    await transaction.RollbackAsync(token);
                    return false;
                }

                await DispatchAsync(scope.ServiceProvider, dbContext, message, token);
                await transaction.CommitAsync(token);
                return true;
            },
            cancellationToken);
    }

    private async Task DispatchAsync(
        IServiceProvider serviceProvider,
        Hook2StreamDbContext dbContext,
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = JsonSerializer.Deserialize<JobEnqueueRequest>(
                message.PayloadJson,
                SerializerOptions)
                ?? throw new JsonException("The job envelope was empty.");
            if (request.WorkspaceId != message.WorkspaceId)
            {
                throw new JsonException("The outbox and job workspaces do not match.");
            }

            // The outbox dedupe key is authoritative. This also makes delivery
            // safe when the process crashes after enqueue but before acknowledgement.
            request = request with { IdempotencyKey = $"outbox:{message.DedupeKey}" };
            var queue = serviceProvider.GetRequiredService<IJobQueue>();
            await queue.EnqueueAsync(request, cancellationToken);

            if (dbContext.Entry(message).State == EntityState.Detached)
            {
                message = await dbContext.OutboxMessages.SingleAsync(
                    value => value.Id == message.Id,
                    cancellationToken);
            }

            message.AttemptCount++;
            message.ProcessedAt = DateTimeOffset.UtcNow;
            message.LastError = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (JsonException exception)
        {
            await DeadLetterAsync(
                dbContext,
                message,
                "outbox.invalid_job_envelope",
                exception.Message,
                incrementAttempt: true,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            message.AttemptCount++;
            message.LastError = "outbox.delivery_failed";
            if (message.AttemptCount >= _options.OutboxMaxAttempts)
            {
                await DeadLetterAsync(
                    dbContext,
                    message,
                    "outbox.delivery_exhausted",
                    "The message exceeded its delivery attempt limit.",
                    incrementAttempt: false,
                    cancellationToken);
                return;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogWarning(
                exception,
                "Outbox message {MessageId} delivery failed on attempt {AttemptCount}.",
                message.Id,
                message.AttemptCount);
        }
    }

    private static async Task DeadLetterAsync(
        Hook2StreamDbContext dbContext,
        OutboxMessage message,
        string errorCode,
        string safeMessage,
        bool incrementAttempt,
        CancellationToken cancellationToken)
    {
        if (incrementAttempt)
        {
            message.AttemptCount++;
        }
        message.ProcessedAt = DateTimeOffset.UtcNow;
        message.LastError = errorCode;
        dbContext.AuditEvents.Add(new AuditEvent
        {
            WorkspaceId = message.WorkspaceId,
            Action = "outbox.dead_lettered",
            ResourceType = "outbox_message",
            ResourceId = message.Id,
            DataJson = JsonSerializer.Serialize(new { errorCode, message = safeMessage })
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
