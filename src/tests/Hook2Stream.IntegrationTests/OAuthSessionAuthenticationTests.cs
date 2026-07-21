using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Hook2Stream.Api.Authentication;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Hook2Stream.IntegrationTests;

public sealed class OAuthSessionAuthenticationTests
{
    [Fact]
    public async Task OAuth_callback_issues_opaque_cookie_session_and_state_is_single_use()
    {
        await using var factory = new OAuthSessionApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("http://localhost")
        });

        var login = await client.GetAsync(
            "/api/v1/auth/login?returnPath=%2Fdashboard",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var authorizationQuery = HttpUtility.ParseQueryString(login.Headers.Location!.Query);
        var state = Assert.IsType<string>(authorizationQuery["state"]);
        Assert.NotEmpty(state);

        var callback = await client.GetAsync(
            $"/api/v1/auth/callback?code=valid-code&state={Uri.EscapeDataString(state)}",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("http://web.example.test/dashboard", callback.Headers.Location!.ToString());
        Assert.DoesNotContain('#', callback.Headers.Location.ToString());
        var setCookies = callback.Headers.GetValues("Set-Cookie").ToArray();
        var sessionCookie = Assert.Single(
            setCookies,
            value => value.StartsWith("h2s_session=", StringComparison.Ordinal));
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", sessionCookie, StringComparison.OrdinalIgnoreCase);
        var sessionToken = ReadCookieValue(sessionCookie);

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            var persistedState = await dbContext.Set<OAuthLoginState>().SingleAsync();
            var persistedSession = await dbContext.Set<AuthSession>().SingleAsync();
            Assert.Equal(OAuthSessionService.HashSecret(state), persistedState.StateHash);
            Assert.NotNull(persistedState.ConsumedAt);
            Assert.Equal(OAuthSessionService.HashSecret(sessionToken), persistedSession.TokenHash);
            Assert.DoesNotContain(state, persistedState.StateHash, StringComparison.Ordinal);
            Assert.DoesNotContain(sessionToken, persistedSession.TokenHash, StringComparison.Ordinal);
        }

        using var replayRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/auth/callback?code=valid-code&state={Uri.EscapeDataString(state)}");
        replayRequest.Headers.Add("Cookie", $"h2s_oauth_state={state}");
        var replay = await client.SendAsync(replayRequest, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        Assert.Equal(
            "http://web.example.test/sign-in?auth=state_invalid",
            replay.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Cookie_session_requires_csrf_for_mutations_and_logout_revokes_it()
    {
        await using var factory = new OAuthSessionApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("http://localhost")
        });
        await AuthenticateAsync(client);

        var sessionResponse = await client.GetAsync(
            "/api/v1/auth/session",
            CancellationToken.None);
        sessionResponse.EnsureSuccessStatusCode();
        var session = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>(
            CancellationToken.None);
        Assert.True(session.GetProperty("authenticated").GetBoolean());
        Assert.Equal("google:google-user", session.GetProperty("subject").GetString());
        var csrfToken = session.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrfToken));

        var account = await client.GetAsync("/api/v1/account/me", CancellationToken.None);
        account.EnsureSuccessStatusCode();

        var unprotectedLogout = await client.PostAsync(
            "/api/v1/auth/logout",
            content: null,
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, unprotectedLogout.StatusCode);

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logoutRequest.Headers.Add(OAuthCsrfMiddleware.HeaderName, csrfToken);
        var logout = await client.SendAsync(logoutRequest, CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var afterLogout = await client.GetAsync("/api/v1/account/me", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.NotNull((await dbContext.Set<AuthSession>().SingleAsync()).RevokedAt);
    }

    [Theory]
    [InlineData("google-user", "google@example.test", false, "email_unverified")]
    [InlineData("google-user", "google@example.test", null, "email_unverified")]
    [InlineData("", "google@example.test", true, "identity_invalid")]
    [InlineData("   ", "google@example.test", true, "identity_invalid")]
    [InlineData("google-user", "", true, "identity_invalid")]
    [InlineData("google-user", "   ", true, "identity_invalid")]
    public async Task OAuth_callback_rejects_unverified_or_incomplete_identity(
        string subject,
        string email,
        bool? emailVerified,
        string expectedError)
    {
        await using var factory = new OAuthSessionApiFactory(
            new GoogleUserInfo(subject, email, "Google User", emailVerified));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("http://localhost")
        });

        var callback = await StartCallbackAsync(client);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(
            $"http://web.example.test/sign-in?auth={expectedError}",
            callback.Headers.Location!.ToString());
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        Assert.Empty(await dbContext.Users.ToListAsync());
        Assert.Empty(await dbContext.Set<AuthSession>().ToListAsync());
    }

    [Theory]
    [InlineData("network")]
    [InlineData("json")]
    [InlineData("provider")]
    [InlineData("timeout")]
    public async Task OAuth_callback_converts_provider_failures_to_safe_redirect(string failureKind)
    {
        Exception failure = failureKind switch
        {
            "network" => new HttpRequestException("network unavailable"),
            "json" => new JsonException("malformed provider payload"),
            "provider" => new InvalidOperationException("provider rejected response"),
            "timeout" => new TaskCanceledException("provider timeout"),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        await using var factory = new OAuthSessionApiFactory(exchangeFailure: failure);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("http://localhost")
        });

        var callback = await StartCallbackAsync(client);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(
            "http://web.example.test/sign-in?auth=exchange_failed",
            callback.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("https://app.example.test", "https://app.example.test", true)]
    [InlineData("https://app.example.test:8443", "https://app.example.test", true)]
    [InlineData("https://app.example.test", "", true)]
    [InlineData("https://api.example.test", "https://app.example.test", false)]
    [InlineData("http://app.example.test", "https://app.example.test", false)]
    [InlineData("https://app.example.test/api", "https://app.example.test", false)]
    public void Production_origins_must_be_https_and_share_the_exact_host(
        string apiBaseUrl,
        string webBaseUrl,
        bool expected)
    {
        var options = new GoogleOAuthOptions
        {
            ClientId = "google-client",
            ClientSecret = "google-secret",
            PublicApiBaseUrl = apiBaseUrl,
            PublicWebReturnBaseUrl = webBaseUrl
        };

        Assert.Equal(expected, options.HasValidProductionOrigins);
    }

    [Fact]
    public void Production_session_cookies_are_host_scoped_secure_and_http_only()
    {
        var environment = new StubHostEnvironment(Environments.Production);
        var manager = new OAuthCookieManager(environment, TimeProvider.System);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";

        manager.AppendSession(
            context.Request,
            context.Response,
            "opaque-session",
            "csrf-token",
            DateTimeOffset.UtcNow.AddHours(1));

        var cookies = context.Response.Headers.SetCookie.ToArray();
        var sessionCookie = Assert.Single(
            cookies,
            value => value is not null &&
                     value.StartsWith("__Host-h2s_session=", StringComparison.Ordinal));
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", sessionCookie, StringComparison.OrdinalIgnoreCase);

        var csrfCookie = Assert.Single(
            cookies,
            value => value is not null &&
                     value.StartsWith("__Host-h2s_csrf=", StringComparison.Ordinal));
        Assert.Contains("secure", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("httponly", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", csrfCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://evil.example/path")]
    [InlineData("//evil.example/path")]
    [InlineData("/safe\\evil")]
    [InlineData("")]
    public void Return_paths_are_restricted_to_safe_local_paths(string value)
    {
        Assert.Equal("/", OAuthSessionService.SanitizeReturnPath(value));
    }

    private static async Task AuthenticateAsync(HttpClient client)
    {
        var callback = await StartCallbackAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
    }

    private static async Task<HttpResponseMessage> StartCallbackAsync(HttpClient client)
    {
        var login = await client.GetAsync(
            "/api/v1/auth/login?returnPath=%2Fdashboard",
            CancellationToken.None);
        var query = HttpUtility.ParseQueryString(login.Headers.Location!.Query);
        var state = Assert.IsType<string>(query["state"]);
        return await client.GetAsync(
            $"/api/v1/auth/callback?code=valid-code&state={Uri.EscapeDataString(state)}",
            CancellationToken.None);
    }

    private static string ReadCookieValue(string setCookie)
    {
        var pair = setCookie.Split(';', 2)[0];
        return pair[(pair.IndexOf('=') + 1)..];
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Hook2Stream.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

internal sealed class OAuthSessionApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"hook2stream-oauth-tests-{Guid.NewGuid():N}";
    private readonly GoogleUserInfo _userInfo;
    private readonly Exception? _exchangeFailure;

    public OAuthSessionApiFactory(
        GoogleUserInfo? userInfo = null,
        Exception? exchangeFailure = null)
    {
        _userInfo = userInfo ?? new GoogleUserInfo(
            "google-user",
            "google@example.test",
            "Google User",
            true);
        _exchangeFailure = exchangeFailure;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Auth:Mode", "OAuth");
        builder.UseSetting("Google:ClientId", "google-client");
        builder.UseSetting("Google:ClientSecret", "google-secret");
        builder.UseSetting("Google:PublicApiBaseUrl", "http://localhost");
        builder.UseSetting("Google:PublicWebReturnBaseUrl", "http://web.example.test");
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
            services.RemoveAll<IGoogleOAuthClient>();
            services.AddSingleton<IGoogleOAuthClient>(
                new FakeGoogleOAuthClient(_userInfo, _exchangeFailure));
        });
    }
}

internal sealed class FakeGoogleOAuthClient(
    GoogleUserInfo userInfo,
    Exception? exchangeFailure) : IGoogleOAuthClient
{
    public Task<GoogleUserInfo> ExchangeCodeForUserInfoAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        Assert.Equal("valid-code", code);
        Assert.Equal("http://localhost/api/v1/auth/callback", redirectUri);
        return exchangeFailure is null
            ? Task.FromResult(userInfo)
            : Task.FromException<GoogleUserInfo>(exchangeFailure);
    }
}
