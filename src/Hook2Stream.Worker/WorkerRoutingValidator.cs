using Hook2Stream.Application;
using Hook2Stream.Domain;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Worker;

internal static class WorkerRoutingValidation
{
    public static string[] Validate(
        IEnumerable<string> configuredCapabilities,
        IEnumerable<IJobHandler> handlers)
    {
        var capabilities = configuredCapabilities
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(JobRoutingRegistry.NormalizeCapability)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (capabilities.Length != 1)
        {
            throw new InvalidOperationException(
                "A worker process must host exactly one isolated capability pool.");
        }

        var capability = capabilities[0];
        var registeredHandlers = handlers.ToArray();
        var duplicateTypes = registeredHandlers
            .GroupBy(handler => handler.Type)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateTypes.Length > 0)
        {
            throw new InvalidOperationException(
                $"Multiple handlers are registered for job types: {string.Join(", ", duplicateTypes)}.");
        }

        foreach (var handler in registeredHandlers)
        {
            try
            {
                JobRoutingRegistry.EnsureMatches(handler.Type, handler.Capability);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"Handler '{handler.GetType().Name}' does not match the authoritative job route.",
                    exception);
            }
        }

        var expectedTypes = JobRoutingRegistry.GetJobTypes(capability).ToHashSet();
        var registeredTypes = registeredHandlers.Select(handler => handler.Type).ToHashSet();
        var missing = expectedTypes.Except(registeredTypes).OrderBy(type => type).ToArray();
        var unexpected = registeredTypes.Except(expectedTypes).OrderBy(type => type).ToArray();
        if (missing.Length > 0 || unexpected.Length > 0)
        {
            throw new InvalidOperationException(
                $"Worker pool '{capability}' has an invalid handler set. " +
                $"Missing: {Format(missing)}. Unexpected: {Format(unexpected)}.");
        }

        return capabilities;
    }

    private static string Format(IEnumerable<JobType> types)
    {
        var values = types.Select(type => type.ToString()).ToArray();
        return values.Length == 0 ? "none" : string.Join(", ", values);
    }
}

internal sealed class WorkerRoutingStartupValidator(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var handlers = scope.ServiceProvider.GetServices<IJobHandler>();
        _ = WorkerRoutingValidation.Validate(options.Value.Capabilities, handlers);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
