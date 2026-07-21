using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hook2Stream.IntegrationTests;

public sealed class LocalDevelopmentAuthenticationTests
{
    [Fact]
    public async Task Correct_loopback_token_authenticates_the_fixed_local_user()
    {
        await using var factory = new LocalAuthenticationApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            LocalAuthenticationApiFactory.Token);

        var response = await client.GetAsync("/api/v1/account/me", CancellationToken.None);

        response.EnsureSuccessStatusCode();
        var account = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Equal("local-development-user", account.GetProperty("subject").GetString());
        Assert.Equal("local@hook2stream.test", account.GetProperty("email").GetString());
        Assert.True(account.GetProperty("onboardingRequired").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task Missing_or_invalid_local_token_is_unauthorized(string? token)
    {
        await using var factory = new LocalAuthenticationApiFactory();
        using var client = factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await client.GetAsync("/api/v1/account/me", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Correct_token_from_a_non_loopback_address_is_unauthorized()
    {
        await using var factory = new LocalAuthenticationApiFactory(
            remoteAddress: IPAddress.Parse("192.0.2.1"));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            LocalAuthenticationApiFactory.Token);

        var response = await client.GetAsync("/api/v1/account/me", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Local_authentication_cannot_start_outside_development()
    {
        using var factory = new LocalAuthenticationApiFactory("Production");

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains(
            "Local authentication is only available in the Development or Testing environment.",
            exception.ToString(),
            StringComparison.Ordinal);
    }
}

internal sealed class LocalAuthenticationApiFactory(
    string environment = "Development",
    IPAddress? remoteAddress = null) : WebApplicationFactory<Program>
{
    public const string Token = "local-authentication-integration-test-token";
    private readonly string _databaseName = $"hook2stream-local-auth-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("Auth:Mode", "Local");
        builder.UseSetting("Auth:LocalToken", Token);
        builder.UseSetting("Storage:AccessKey", "test-access-key");
        builder.UseSetting("Storage:SecretKey", "test-secret-key");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<Hook2StreamDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<Hook2StreamDbContext>>();
            services.RemoveAll<Hook2StreamDbContext>();
            services.AddDbContext<Hook2StreamDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<IObjectStorage, FakeObjectStorage>();
            services.AddSingleton<IStartupFilter>(
                new ConnectionAddressStartupFilter(remoteAddress ?? IPAddress.Loopback));
        });
    }
}

internal sealed class ConnectionAddressStartupFilter(IPAddress remoteAddress) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(async (context, pipeline) =>
            {
                context.Connection.RemoteIpAddress = remoteAddress;
                await pipeline();
            });
            next(app);
        };
}
