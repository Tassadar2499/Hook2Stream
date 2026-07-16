using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Media;

namespace Hook2Stream.Worker;

public sealed class MediaJobWorker(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime applicationLifetime,
    ILogger<MediaJobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan QueueErrorDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Media worker {WorkerId} started.", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            LeasedJob? job = null;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
                job = await queue.TryLeaseAsync(_workerId, LeaseDuration, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "The media worker could not lease a job. Retrying after {Delay}.",
                    QueueErrorDelay);
                await Task.Delay(QueueErrorDelay, stoppingToken);
                continue;
            }

            if (job is null)
            {
                await Task.Delay(IdleDelay, stoppingToken);
                continue;
            }

            await ProcessJobAsync(job, stoppingToken);
        }
    }

    private async Task ProcessJobAsync(LeasedJob job, CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Processing {JobType} job {JobId}, attempt {AttemptNumber}.",
            job.Type,
            job.Id,
            job.AttemptNumber);

        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeatTask = KeepLeaseAliveAsync(job, heartbeatCancellation.Token);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            switch (job.Type)
            {
                case JobType.MediaIngest:
                    await scope.ServiceProvider
                        .GetRequiredService<IMediaIngestProcessor>()
                        .ProcessAsync(job, stoppingToken);
                    break;
                default:
                    throw new MediaRejectedException(
                        "job.type_unsupported",
                        "This worker does not support the queued operation.");
            }

            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            await queue.CompleteAsync(job.Id, job.LeaseOwner, stoppingToken);
            logger.LogInformation("Job {JobId} succeeded.", job.Id);
        }
        catch (MediaRejectedException exception)
        {
            await FailAsync(job, exception.Code, exception.SafeMessage, retryable: false, stoppingToken);
            logger.LogWarning("Job {JobId} rejected: {ErrorCode}.", job.Id, exception.Code);
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
                "Media processing failed. The operation will be retried when possible.",
                retryable: true,
                stoppingToken);
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when processing completes or the host stops.
            }
        }
    }

    private async Task KeepLeaseAliveAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(HeartbeatInterval, cancellationToken);
            await using var scope = scopeFactory.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var renewed = await queue.HeartbeatAsync(
                job.Id,
                job.LeaseOwner,
                LeaseDuration,
                progressPercent: -1,
                stage: "",
                cancellationToken);
            if (!renewed)
            {
                applicationLifetime.StopApplication();
                throw new InvalidOperationException($"Lease for job {job.Id} was lost.");
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
}
