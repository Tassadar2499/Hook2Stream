using System.Globalization;
using System.Net;

namespace Hook2Stream.IntegrationTests;

public sealed class ApiRateLimitingTests
{
    private const int AuthenticatedReadPermitLimit = 600;
    private const int AuthenticatedMutationPermitLimit = 120;
    private const int AnonymousPermitLimit = 120;

    [Fact]
    public async Task Authenticated_workflow_burst_has_headroom_but_remains_bounded_per_subject()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var primary = CreateClient(factory, "rate-limit-primary");

        for (var requestNumber = 1; requestNumber <= AuthenticatedReadPermitLimit; requestNumber++)
        {
            using var response = await primary.GetAsync("/");
            Assert.True(
                response.IsSuccessStatusCode,
                $"Authenticated request {requestNumber} was rejected with {(int)response.StatusCode}.");
        }

        using var rejected = await primary.GetAsync("/");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        AssertRetryAfter(rejected);

        using var sameSubjectMutation = await primary.PostAsync("/", content: null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, sameSubjectMutation.StatusCode);

        using var independent = CreateClient(factory, "rate-limit-independent");
        using var independentResponse = await independent.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, independentResponse.StatusCode);
    }

    [Fact]
    public async Task Authenticated_mutations_keep_the_stricter_boundary_and_receive_retry_after()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = CreateClient(factory, "rate-limit-mutation");

        for (var requestNumber = 1; requestNumber <= AuthenticatedMutationPermitLimit; requestNumber++)
        {
            using var response = await client.PostAsync("/", content: null);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        using var rejected = await client.PostAsync("/", content: null);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        AssertRetryAfter(rejected);

        using var sameSubjectRead = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, sameSubjectRead.StatusCode);
    }

    [Fact]
    public async Task Anonymous_ip_keeps_the_stricter_boundary_and_receives_retry_after()
    {
        await using var factory = new LocalAuthenticationApiFactory();
        using var client = factory.CreateClient();

        for (var requestNumber = 1; requestNumber <= AnonymousPermitLimit; requestNumber++)
        {
            using var response = await client.GetAsync("/");
            Assert.True(
                response.IsSuccessStatusCode,
                $"Anonymous request {requestNumber} was rejected with {(int)response.StatusCode}.");
        }

        using var rejected = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        AssertRetryAfter(rejected);
    }

    private static HttpClient CreateClient(Hook2StreamApiFactory factory, string subject)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        return client;
    }

    private static void AssertRetryAfter(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfterValues));
        var retryAfter = Assert.Single(retryAfterValues);
        Assert.True(int.TryParse(retryAfter, NumberStyles.None, CultureInfo.InvariantCulture, out var retryAfterSeconds));
        Assert.InRange(retryAfterSeconds, 1, 60);
    }
}
