using System.Net;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hook2Stream.IntegrationTests;

public sealed class ReverseProxyTests
{
    [Fact]
    public async Task Trusted_proxy_https_header_prevents_redirect_and_enables_hsts()
    {
        await using var factory = new ProductionProxyApiFactory(IPAddress.Loopback);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://internal.example.test")
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-For", "203.0.113.10");
        request.Headers.Add("X-Forwarded-Host", "app.example.test");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Forwarded_headers_from_an_untrusted_peer_are_ignored()
    {
        await using var factory = new ProductionProxyApiFactory(IPAddress.Parse("192.0.2.44"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://internal.example.test")
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-For", "203.0.113.10");
        request.Headers.Add("X-Forwarded-Host", "app.example.test");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(Uri.UriSchemeHttps, response.Headers.Location?.Scheme);
        Assert.Equal("internal.example.test", response.Headers.Location?.Host);
    }

    [Fact]
    public async Task Internal_liveness_probe_is_not_forced_through_public_tls()
    {
        await using var factory = new ProductionProxyApiFactory(IPAddress.Loopback);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost")
        });

        using var response = await client.GetAsync("/health/live", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

internal sealed class ProductionProxyApiFactory(IPAddress remoteAddress)
    : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"hook2stream-proxy-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:hook2stream",
            "Host=postgres;Database=hook2stream;Username=app;Password=secret");
        builder.UseSetting("Auth:Mode", "OAuth");
        builder.UseSetting("Google:ClientId", "google-client");
        builder.UseSetting("Google:ClientSecret", "google-secret");
        builder.UseSetting("Google:PublicApiBaseUrl", "https://app.example.test");
        builder.UseSetting("Google:PublicWebReturnBaseUrl", "https://app.example.test");
        builder.UseSetting("Storage:ServiceUrl", "https://s3.example.test");
        builder.UseSetting("Storage:PublicServiceUrl", "https://s3.example.test");
        builder.UseSetting("Storage:CredentialMode", "Static");
        builder.UseSetting("Storage:AccessKey", "test-access-key");
        builder.UseSetting("Storage:SecretKey", "test-secret-key");
        builder.UseSetting("Stripe:Mode", "Stripe");
        builder.UseSetting("Stripe:PublicWebBaseUrl", "https://app.example.test");
        builder.UseSetting("Stripe:SecretKey", "sk_test_secret");
        builder.UseSetting("Stripe:WebhookSecret", "whsec_test_secret");
        builder.UseSetting("Stripe:PriceIds:art_credits_5", "price_art");
        builder.UseSetting("Stripe:PriceIds:mini_release", "price_mini");
        builder.UseSetting("Stripe:PriceIds:release_pack", "price_release");
        builder.UseSetting("Stripe:PriceIds:clean_cover", "price_cover");
        builder.UseSetting("Stripe:PriceIds:active_artist", "price_artist");
        builder.UseSetting("ReverseProxy:Enabled", "true");
        builder.UseSetting("ReverseProxy:ForwardLimit", "1");
        builder.UseSetting("ReverseProxy:KnownProxies:0", IPAddress.Loopback.ToString());
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<Hook2StreamDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<Hook2StreamDbContext>>();
            services.RemoveAll<Hook2StreamDbContext>();
            services.AddDbContext<Hook2StreamDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<IObjectStorage, FakeObjectStorage>();
            services.AddSingleton<IStartupFilter>(new ConnectionAddressStartupFilter(remoteAddress));
        });
    }
}
