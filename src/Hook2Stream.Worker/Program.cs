using Hook2Stream.Application;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Providers;
using Hook2Stream.Worker;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddHook2StreamInfrastructure(
    builder.Configuration,
    builder.Environment,
    includeBilling: false);
var configuredCapabilities = builder.Configuration
    .GetSection($"{WorkerOptions.SectionName}:Capabilities")
    .Get<string[]>() ?? [JobRoutingRegistry.Media];
configuredCapabilities = configuredCapabilities
    .Where(value => !string.IsNullOrWhiteSpace(value))
    .Select(JobRoutingRegistry.NormalizeCapability)
    .Distinct(StringComparer.Ordinal)
    .ToArray();
if (configuredCapabilities.Length != 1)
{
    throw new InvalidOperationException(
        "A worker process must configure exactly one isolated capability pool.");
}
builder.Services.AddHook2StreamPipelineProviders(
    builder.Configuration,
    builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"),
    configuredCapabilities);
builder.Services.AddOptions<WorkerOptions>()
    .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName))
    .PostConfigure(options => options.Capabilities = configuredCapabilities)
    .Validate(
        options => options.Capabilities is { Length: 1 },
        "A worker process must host exactly one isolated capability pool.")
    .Validate(
        options => options.Capabilities is { Length: 1 } &&
                   JobRoutingRegistry.IsKnownCapability(options.Capabilities[0]),
        $"Worker capability must be one of: {string.Join(", ", JobRoutingRegistry.Capabilities)}.")
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
builder.Services.AddHook2StreamWorkerRole(configuredCapabilities);

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();

public partial class Program;
