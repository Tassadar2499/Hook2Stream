using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Providers;
using Hook2Stream.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddHook2StreamInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddHook2StreamPipelineProviders(
    builder.Configuration,
    builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"));
builder.Services.AddOptions<WorkerOptions>()
    .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName))
    .Validate(options => options.Capabilities.Length > 0, "At least one worker capability is required.")
    .Validate(options => options.LeaseDurationSeconds is >= 30 and <= 900, "Worker lease duration is out of range.")
    .Validate(
        options => options.HeartbeatIntervalSeconds > 0 &&
                   options.HeartbeatIntervalSeconds * 2 < options.LeaseDurationSeconds,
        "Worker heartbeat interval must be less than half of the lease duration.")
    .Validate(options => options.IdleDelayMilliseconds is >= 100 and <= 30_000, "Worker idle delay is out of range.")
    .Validate(options => options.QueueErrorDelaySeconds is >= 1 and <= 60, "Worker queue error delay is out of range.")
    .Validate(options => options.OutboxPollMilliseconds is >= 100 and <= 30_000, "Outbox poll delay is out of range.")
    .Validate(options => options.OutboxBatchSize is >= 1 and <= 100, "Outbox batch size is out of range.")
    .Validate(options => options.OutboxMaxAttempts is >= 1 and <= 100, "Outbox attempt limit is out of range.")
    .ValidateOnStart();
builder.Services.AddHook2StreamJobHandlers();
builder.Services.AddHostedService<MediaJobWorker>();
builder.Services.AddHostedService<OutboxJobDispatcher>();
builder.Services.AddHostedService<PipelineReconciler>();

var host = builder.Build();
host.Run();
