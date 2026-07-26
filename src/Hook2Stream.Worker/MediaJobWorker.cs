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

            try
            {
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A queue transition may still fail after its bounded retry
                // budget. Keep the worker alive and let the lease-recovery
                // path make the job available again.
                logger.LogError(
                    exception,
                    "Worker {WorkerId} could not finish processing job {JobId}; the lease will be recovered.",
                    _workerId,
                    job.Id);
            }
        }
    }

    private async Task<string[]> ResolveCapabilitiesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var requested = WorkerRoutingValidation.Validate(
            _options.Capabilities,
            scope.ServiceProvider.GetServices<IJobHandler>());
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
        var handlerSucceeded = false;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                JobRoutingRegistry.EnsureMatches(job.Type, job.RequiredCapability);
            }
            catch (InvalidOperationException exception)
            {
                throw new JobHandlerException(
                    "job.capability_mismatch",
                    "The queued operation was assigned to the wrong worker pool.",
                    retryable: false,
                    exception);
            }

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
            handlerSucceeded = true;

            if (!await StopHeartbeatBeforeTransitionAsync(
                    job,
                    processingCancellation,
                    heartbeatTask))
            {
                return;
            }

            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            await queue.CompleteAsync(job.Id, job.LeaseOwner, job.LeaseToken, stoppingToken);
            logger.LogInformation("Job {JobId} succeeded.", job.Id);
        }
        catch (JobDeferredException exception) when (!handlerSucceeded)
        {
            if (!await StopHeartbeatBeforeTransitionAsync(
                    job,
                    processingCancellation,
                    heartbeatTask))
            {
                return;
            }

            await DeferAsync(job, exception.Delay, exception.ReasonCode, stoppingToken);
            logger.LogInformation(
                "Job {JobId} was deferred for {Delay} because {ReasonCode}.",
                job.Id,
                exception.Delay,
                exception.ReasonCode);
        }
        catch (JobBlockedException exception) when (!handlerSucceeded)
        {
            if (!await StopHeartbeatBeforeTransitionAsync(
                    job,
                    processingCancellation,
                    heartbeatTask))
            {
                return;
            }

            await BlockAsync(job, exception.ReasonCode, exception.SafeMessage, stoppingToken);
            logger.LogInformation(
                "Job {JobId} is waiting for a user action because {ReasonCode}.",
                job.Id,
                exception.ReasonCode);
        }
        catch (JobHandlerException exception) when (!handlerSucceeded)
        {
            if (!await StopHeartbeatBeforeTransitionAsync(
                    job,
                    processingCancellation,
                    heartbeatTask))
            {
                return;
            }

            await FailAsync(job, exception.Code, exception.SafeMessage, exception.Retryable, stoppingToken);
            logger.LogWarning("Job {JobId} failed: {ErrorCode}.", job.Id, exception.Code);
        }
        catch (OperationCanceledException) when (
            !handlerSucceeded &&
            !stoppingToken.IsCancellationRequested &&
            processingCancellation.IsCancellationRequested)
        {
            var heartbeatFailure = await StopAndObserveHeartbeatAsync(
                processingCancellation,
                heartbeatTask);
            LogHeartbeatFailure(job, heartbeatFailure);
        }
        catch (OperationCanceledException) when (!handlerSucceeded && stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Worker shutdown interrupted job {JobId}; the lease will be recovered.", job.Id);
        }
        catch (Exception exception) when (!handlerSucceeded)
        {
            logger.LogError(exception, "Job {JobId} failed and may be retried.", job.Id);
            if (!await StopHeartbeatBeforeTransitionAsync(
                    job,
                    processingCancellation,
                    heartbeatTask))
            {
                return;
            }

            var failure = JobFailureClassifier.Classify(exception, job);
            await FailAsync(
                job,
                failure.Code,
                failure.SafeMessage,
                failure.Retryable,
                stoppingToken);
        }
        finally
        {
            // Awaiting a completed task is idempotent. This final observation
            // protects future branches from ever leaking a heartbeat fault.
            await StopAndObserveHeartbeatAsync(processingCancellation, heartbeatTask);
        }
    }

    private async Task<Exception?> KeepLeaseAliveAsync(
        LeasedJob job,
        CancellationTokenSource processingCancellation,
        CancellationToken stoppingToken)
    {
        try
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
                    return stoppingToken.IsCancellationRequested
                        ? null
                        : new LeaseLostException(job.Id);
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (processingCancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            // Heartbeat and provider processing are a single lease-scoped
            // operation. A failed heartbeat invalidates that operation and
            // must cancel the provider immediately. Return the fault so the
            // processing path can observe it without faulting the
            // BackgroundService.
            processingCancellation.Cancel();
            return exception;
        }
    }

    private async Task<bool> StopHeartbeatBeforeTransitionAsync(
        LeasedJob job,
        CancellationTokenSource processingCancellation,
        Task<Exception?> heartbeatTask)
    {
        var heartbeatFailure = await StopAndObserveHeartbeatAsync(
            processingCancellation,
            heartbeatTask);
        if (heartbeatFailure is null)
        {
            return true;
        }

        LogHeartbeatFailure(job, heartbeatFailure);
        return false;
    }

    private static async Task<Exception?> StopAndObserveHeartbeatAsync(
        CancellationTokenSource processingCancellation,
        Task<Exception?> heartbeatTask)
    {
        processingCancellation.Cancel();
        try
        {
            return await heartbeatTask;
        }
        catch (OperationCanceledException) when (processingCancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            // Keep this defensive boundary even though KeepLeaseAliveAsync
            // normally represents heartbeat faults as its result.
            return exception;
        }
    }

    private void LogHeartbeatFailure(LeasedJob job, Exception? heartbeatFailure)
    {
        if (heartbeatFailure is LeaseLostException)
        {
            // The fencing token makes any late result harmless. Do not mutate
            // a job that another worker may already have leased.
            logger.LogWarning("Processing stopped after lease loss for job {JobId}.", job.Id);
            return;
        }

        if (heartbeatFailure is not null)
        {
            logger.LogError(
                heartbeatFailure,
                "Heartbeat failed for job {JobId}; processing was cancelled and the lease will be recovered.",
                job.Id);
            return;
        }

        logger.LogWarning("Processing stopped after lease cancellation for job {JobId}.", job.Id);
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
