using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class MediaJobWorkerResilienceTests
{
    [Fact]
    public async Task Heartbeat_fault_cancels_processing_and_worker_keeps_polling()
    {
        var queue = new HeartbeatFailingQueue();
        var handler = new CancellationObservingHandler();
        var logger = new RecordingLogger();
        using var serviceProvider = CreateServiceProvider(queue, handler);
        using var worker = CreateWorker(
            serviceProvider,
            heartbeatIntervalSeconds: 0,
            logger: logger);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForSignalAsync(handler.CancellationObserved.Task, worker);
            await WaitForSignalAsync(queue.PolledAfterJob.Task, worker);

            Assert.NotNull(worker.ExecuteTask);
            Assert.False(worker.ExecuteTask.IsCompleted);
            Assert.Equal(1, queue.HeartbeatCalls);
            Assert.Equal(0, queue.CompleteCalls);
            Assert.Equal(0, queue.FailCalls);
            Assert.Contains(
                logger.Entries,
                entry => entry is
                {
                    Level: LogLevel.Error,
                    Exception: InvalidOperationException
                    {
                        Message: "Injected heartbeat failure."
                    }
                });
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Queue_transition_fault_does_not_stop_worker_loop()
    {
        var queue = new FinalizationFailingQueue();
        var handler = new FailingHandler(queue.HeartbeatStarted.Task);
        using var serviceProvider = CreateServiceProvider(queue, handler);
        using var worker = CreateWorker(
            serviceProvider,
            heartbeatIntervalSeconds: 0);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForSignalAsync(queue.PolledAfterJob.Task, worker);

            Assert.NotNull(worker.ExecuteTask);
            Assert.False(worker.ExecuteTask.IsCompleted);
            Assert.Equal(1, queue.FailCalls);
            Assert.True(queue.TransitionSawStoppedHeartbeat);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Complete_transition_fault_does_not_reclassify_successful_handler_as_failed()
    {
        var queue = new CompletionFailingQueue();
        var handler = new SuccessfulHandler(queue.HeartbeatStarted.Task);
        using var serviceProvider = CreateServiceProvider(queue, handler);
        using var worker = CreateWorker(
            serviceProvider,
            heartbeatIntervalSeconds: 0);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForSignalAsync(queue.PolledAfterJob.Task, worker);

            Assert.NotNull(worker.ExecuteTask);
            Assert.False(worker.ExecuteTask.IsCompleted);
            Assert.Equal(1, queue.CompleteCalls);
            Assert.Equal(0, queue.FailCalls);
            Assert.True(queue.TransitionSawStoppedHeartbeat);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static ServiceProvider CreateServiceProvider(
        IJobQueue queue,
        IJobHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IJobQueue>(queue);
        services.AddSingleton<IJobHandler>(handler);
        return services.BuildServiceProvider();
    }

    private static async Task WaitForSignalAsync(
        Task signal,
        MediaJobWorker worker)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(5));
        var completed = await Task.WhenAny(signal, worker.ExecuteTask!, timeout);
        if (completed == worker.ExecuteTask)
        {
            await worker.ExecuteTask;
            throw new InvalidOperationException("The worker exited before the expected signal.");
        }

        await signal.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static MediaJobWorker CreateWorker(
        ServiceProvider serviceProvider,
        int heartbeatIntervalSeconds,
        ILogger<MediaJobWorker>? logger = null) =>
        new(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkerOptions
            {
                Capabilities = [JobRoutingRegistry.Media],
                LeaseDurationSeconds = 120,
                HeartbeatIntervalSeconds = heartbeatIntervalSeconds,
                IdleDelayMilliseconds = 1,
                QueueErrorDelaySeconds = 1
            }),
            logger ?? NullLogger<MediaJobWorker>.Instance);

    private sealed class CancellationObservingHandler : IJobHandler
    {
        public JobType Type => JobType.MediaIngest;
        public string Capability => JobRoutingRegistry.Media;
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class FailingHandler(Task heartbeatStarted) : IJobHandler
    {
        public JobType Type => JobType.MediaIngest;
        public string Capability => JobRoutingRegistry.Media;

        public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
        {
            await heartbeatStarted.WaitAsync(cancellationToken);
            throw new JobHandlerException(
                "provider.failure",
                "The provider failed.",
                retryable: true);
        }
    }

    private sealed class SuccessfulHandler(Task heartbeatStarted) : IJobHandler
    {
        public JobType Type => JobType.MediaIngest;
        public string Capability => JobRoutingRegistry.Media;

        public async Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken)
        {
            await heartbeatStarted.WaitAsync(cancellationToken);
        }
    }

    private abstract class TestQueue : IJobQueue
    {
        private int _leaseCalls;

        public TaskCompletionSource PolledAfterJob { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CompleteCalls { get; protected set; }
        public int FailCalls { get; protected set; }

        public Task<Guid> EnqueueAsync(
            JobEnqueueRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<LeasedJob?> TryLeaseAsync(
            string workerId,
            TimeSpan leaseDuration,
            IReadOnlyCollection<string> capabilities,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _leaseCalls) == 1)
            {
                return CreateLeasedJob(workerId, leaseDuration);
            }

            PolledAfterJob.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }

        public abstract Task<bool> HeartbeatAsync(
            Guid jobId,
            string workerId,
            Guid leaseToken,
            TimeSpan leaseDuration,
            int progressPercent,
            string stage,
            CancellationToken cancellationToken);

        public virtual Task CompleteAsync(
            Guid jobId,
            string workerId,
            Guid leaseToken,
            CancellationToken cancellationToken)
        {
            CompleteCalls++;
            return Task.CompletedTask;
        }

        public Task DeferAsync(
            LeasedJob job,
            TimeSpan delay,
            string reasonCode,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task BlockAsync(
            LeasedJob job,
            string reasonCode,
            string safeMessage,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task FailAsync(
            LeasedJob job,
            string errorCode,
            string safeMessage,
            bool retryable,
            CancellationToken cancellationToken)
        {
            FailCalls++;
            return Task.CompletedTask;
        }

        public Task AppendEventAsync(
            Guid jobId,
            string eventType,
            object data,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static LeasedJob CreateLeasedJob(
            string workerId,
            TimeSpan leaseDuration) =>
            new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                null,
                JobType.MediaIngest,
                "{}",
                1,
                3,
                JobRoutingRegistry.Media,
                "test-v1",
                null,
                1,
                workerId,
                DateTimeOffset.UtcNow.Add(leaseDuration),
                Guid.NewGuid());
    }

    private sealed class HeartbeatFailingQueue : TestQueue
    {
        public int HeartbeatCalls { get; private set; }

        public override Task<bool> HeartbeatAsync(
            Guid jobId,
            string workerId,
            Guid leaseToken,
            TimeSpan leaseDuration,
            int progressPercent,
            string stage,
            CancellationToken cancellationToken)
        {
            HeartbeatCalls++;
            return Task.FromException<bool>(
                new InvalidOperationException("Injected heartbeat failure."));
        }
    }

    private sealed class FinalizationFailingQueue : TestQueue
    {
        public TaskCompletionSource HeartbeatStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeartbeatStopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TransitionSawStoppedHeartbeat { get; private set; }

        public override async Task<bool> HeartbeatAsync(
            Guid jobId,
            string workerId,
            Guid leaseToken,
            TimeSpan leaseDuration,
            int progressPercent,
            string stage,
            CancellationToken cancellationToken)
        {
            HeartbeatStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            }
            finally
            {
                HeartbeatStopped.TrySetResult();
            }
        }

        public override Task FailAsync(
            LeasedJob job,
            string errorCode,
            string safeMessage,
            bool retryable,
            CancellationToken cancellationToken)
        {
            FailCalls++;
            TransitionSawStoppedHeartbeat = HeartbeatStopped.Task.IsCompleted;
            return Task.FromException(
                new InvalidOperationException("Injected queue transition failure."));
        }
    }

    private sealed class CompletionFailingQueue : TestQueue
    {
        public TaskCompletionSource HeartbeatStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeartbeatStopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TransitionSawStoppedHeartbeat { get; private set; }

        public override async Task<bool> HeartbeatAsync(
            Guid jobId,
            string workerId,
            Guid leaseToken,
            TimeSpan leaseDuration,
            int progressPercent,
            string stage,
            CancellationToken cancellationToken)
        {
            HeartbeatStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            }
            finally
            {
                HeartbeatStopped.TrySetResult();
            }
        }

        public override Task CompleteAsync(
            Guid jobId,
            string workerId,
            Guid leaseToken,
            CancellationToken cancellationToken)
        {
            CompleteCalls++;
            TransitionSawStoppedHeartbeat = HeartbeatStopped.Task.IsCompleted;
            return Task.FromException(
                new InvalidOperationException("Injected complete transition failure."));
        }
    }

    private sealed class RecordingLogger : ILogger<MediaJobWorker>
    {
        private readonly object _gate = new();

        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                Entries.Add((logLevel, exception));
            }
        }
    }
}
