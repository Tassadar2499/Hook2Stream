using Hook2Stream.Application;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Worker;

/// <summary>
/// Leases only capabilities for which this process has registered handlers.
/// The historical type name is retained to avoid breaking deployment tooling.
/// </summary>
public sealed class MediaJobWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    ILogger<MediaJobWorker> logger) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var capabilities = await ResolveCapabilitiesAsync(stoppingToken);
        logger.LogInformation(
            "Worker {WorkerId} started with capabilities {Capabilities}.",
            _workerId,
            capabilities);

        while (!stoppingToken.IsCancellationRequested)
        {
            LeasedJob? job = null;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
                job = await queue.TryLeaseAsync(
                    _workerId,
                    TimeSpan.FromSeconds(_options.LeaseDurationSeconds),
                    capabilities,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Worker {WorkerId} could not lease a job. Retrying after {DelaySeconds} seconds.",
                    _workerId,
                    _options.QueueErrorDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(_options.QueueErrorDelaySeconds), stoppingToken);
                continue;
            }

            if (job is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_options.IdleDelayMilliseconds), stoppingToken);
                continue;
            }

            await ProcessJobAsync(job, stoppingToken);
        }
    }

    private async Task<string[]> ResolveCapabilitiesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registered = scope.ServiceProvider
            .GetServices<IJobHandler>()
            .Select(value => value.Capability)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requested = _options.Capabilities
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unsupported = requested.Where(value => !registered.Contains(value)).ToArray();
        if (unsupported.Length > 0)
        {
            throw new InvalidOperationException(
                $"No job handler is registered for worker capabilities: {string.Join(", ", unsupported)}.");
        }

        if (requested.Length == 0)
        {
            throw new InvalidOperationException("At least one Worker:Capabilities value is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return requested;
    }

    private async Task ProcessJobAsync(LeasedJob job, CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Processing {JobType} job {JobId}, capability {Capability}, attempt {AttemptNumber}.",
            job.Type,
            job.Id,
            job.RequiredCapability,
            job.AttemptNumber);

        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeatTask = KeepLeaseAliveAsync(job, processingCancellation, stoppingToken);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider
                .GetServices<IJobHandler>()
                .SingleOrDefault(value =>
                    value.Type == job.Type &&
                    string.Equals(value.Capability, job.RequiredCapability, StringComparison.OrdinalIgnoreCase));
            if (handler is null)
            {
                throw new JobHandlerException(
                    "job.type_unsupported",
                    "This worker does not support the queued operation.",
                    retryable: false);
            }

            await handler.ProcessAsync(job, processingCancellation.Token);

            // Stop and join the heartbeat before completing the lease. This
            // prevents a late heartbeat write from racing the terminal update.
            processingCancellation.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when the successful operation stops its heartbeat.
            }

            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            await queue.CompleteAsync(job.Id, job.LeaseOwner, job.LeaseToken, stoppingToken);
            logger.LogInformation("Job {JobId} succeeded.", job.Id);
        }
        catch (JobDeferredException exception)
        {
            await DeferAsync(job, exception.Delay, exception.ReasonCode, stoppingToken);
            logger.LogInformation(
                "Job {JobId} was deferred for {Delay} because {ReasonCode}.",
                job.Id,
                exception.Delay,
                exception.ReasonCode);
        }
        catch (JobBlockedException exception)
        {
            await BlockAsync(job, exception.ReasonCode, exception.SafeMessage, stoppingToken);
            logger.LogInformation(
                "Job {JobId} is waiting for a user action because {ReasonCode}.",
                job.Id,
                exception.ReasonCode);
        }
        catch (JobHandlerException exception)
        {
            await FailAsync(job, exception.Code, exception.SafeMessage, exception.Retryable, stoppingToken);
            logger.LogWarning("Job {JobId} failed: {ErrorCode}.", job.Id, exception.Code);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && processingCancellation.IsCancellationRequested)
        {
            // The fencing token makes any late result harmless. Do not mutate a
            // job that another worker may already have leased.
            logger.LogWarning("Processing stopped after lease loss for job {JobId}.", job.Id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Worker shutdown interrupted job {JobId}; the lease will be recovered.", job.Id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Job {JobId} failed and may be retried.", job.Id);
            await FailAsync(
                job,
                "job.processing_failed",
                "Processing failed. The operation will be retried when possible.",
                retryable: true,
                stoppingToken);
        }
        finally
        {
            processingCancellation.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when processing completes, the lease is lost, or the host stops.
            }
            catch (LeaseLostException)
            {
                // Already reported above; stale completion is blocked by the fencing token.
            }
        }
    }

    private async Task KeepLeaseAliveAsync(
        LeasedJob job,
        CancellationTokenSource processingCancellation,
        CancellationToken stoppingToken)
    {
        while (!processingCancellation.IsCancellationRequested)
        {
            await Task.Delay(
                TimeSpan.FromSeconds(_options.HeartbeatIntervalSeconds),
                processingCancellation.Token);
            await using var scope = scopeFactory.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var renewed = await queue.HeartbeatAsync(
                job.Id,
                job.LeaseOwner,
                job.LeaseToken,
                TimeSpan.FromSeconds(_options.LeaseDurationSeconds),
                progressPercent: -1,
                stage: "",
                processingCancellation.Token);
            if (!renewed)
            {
                processingCancellation.Cancel();
                if (!stoppingToken.IsCancellationRequested)
                {
                    throw new LeaseLostException(job.Id);
                }
            }
        }
    }

    private async Task FailAsync(
        LeasedJob job,
        string code,
        string message,
        bool retryable,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        await queue.FailAsync(job, code, message, retryable, cancellationToken);
    }

    private async Task DeferAsync(
        LeasedJob job,
        TimeSpan delay,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        await queue.DeferAsync(job, delay, reasonCode, cancellationToken);
    }

    private async Task BlockAsync(
        LeasedJob job,
        string reasonCode,
        string safeMessage,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        await queue.BlockAsync(job, reasonCode, safeMessage, cancellationToken);
    }

    private sealed class LeaseLostException(Guid jobId)
        : Exception($"Lease for job {jobId} was lost.");
}
