using System.Net;
using System.Text;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hook2Stream.UnitTests;

public sealed class OpenRouterRegistrationTests
{
    private static readonly TimeSpan GlobalAttemptTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan GlobalTotalTimeout = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task OpenRouter_client_ignores_the_global_standard_resilience_timeout()
    {
        using var services = BuildServices(
            new DelayedSuccessHandler(TimeSpan.FromMilliseconds(250)));
        var client = services.GetRequiredService<OpenRouterClient>();

        var result = await client.PostJsonAsync(
            "chat/completions",
            new { model = "test/model", messages = Array.Empty<object>() },
            "registration-timeout-test",
            timeoutSeconds: 10,
            outcomeCanBeRetried: false,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task OpenRouter_client_still_honors_caller_cancellation_after_resilience_removal()
    {
        using var services = BuildServices(new WaitForCancellationHandler());
        var client = services.GetRequiredService<OpenRouterClient>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostJsonAsync(
            "chat/completions",
            new { model = "test/model", messages = Array.Empty<object>() },
            "registration-cancellation-test",
            timeoutSeconds: 10,
            outcomeCanBeRetried: false,
            cancellation.Token));
    }

    private static ServiceProvider BuildServices(HttpMessageHandler primaryHandler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenRouter:ApiKey"] = $"sk-or-v1-{new string('a', 64)}"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpClientDefaults(client =>
            client.AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = GlobalAttemptTimeout;
                options.TotalRequestTimeout.Timeout = GlobalTotalTimeout;
            }));
        services.AddHook2StreamPipelineProviders(
            configuration,
            allowFixtureProviders: true,
            [JobRoutingRegistry.Control]);
        services.AddHttpClient<OpenRouterClient>()
            .ConfigurePrimaryHttpMessageHandler(() => primaryHandler);
        return services.BuildServiceProvider();
    }

    private sealed class DelayedSuccessHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class WaitForCancellationHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation-only handler completed unexpectedly.");
        }
    }
}
