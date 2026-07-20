namespace Hook2Stream.Worker;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public string[] Capabilities { get; set; } = ["media"];
    public int LeaseDurationSeconds { get; set; } = 120;
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int IdleDelayMilliseconds { get; set; } = 1_000;
    public int QueueErrorDelaySeconds { get; set; } = 5;
    public int OutboxPollMilliseconds { get; set; } = 500;
    public int OutboxBatchSize { get; set; } = 20;
    public int OutboxMaxAttempts { get; set; } = 10;
}
