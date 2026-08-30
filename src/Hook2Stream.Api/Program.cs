using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Hook2Stream.Api;
using Hook2Stream.Api.Authentication;
using Hook2Stream.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
var reverseProxy = builder.Services.AddTrustedReverseProxy(builder.Configuration);
builder.Services.AddHttpsRedirection(options => options.HttpsPort = 443);
builder.Services.AddHook2StreamInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<OpenApiSecurityTransformers>();
    options.AddOperationTransformer<OpenApiSecurityTransformers>();
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUserService>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

builder.Services.Configure<GoogleOAuthOptions>(
    builder.Configuration.GetSection(GoogleOAuthOptions.SectionName));
builder.Services.Configure<ApplicationAuthenticationOptions>(
    builder.Configuration.GetSection(ApplicationAuthenticationOptions.SectionName));
builder.Services.AddSingleton<OAuthCookieManager>();
builder.Services.AddScoped<OAuthSessionService>();
builder.Services.AddHttpClient<IGoogleOAuthClient, GoogleOAuthClient>();

var authentication = builder.Configuration
    .GetSection(ApplicationAuthenticationOptions.SectionName)
    .Get<ApplicationAuthenticationOptions>() ?? new ApplicationAuthenticationOptions();

if (string.Equals(authentication.Mode, ApplicationAuthenticationOptions.LocalMode, StringComparison.OrdinalIgnoreCase))
{
    if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
    {
        throw new InvalidOperationException(
            "Local authentication is only available in the Development or Testing environment.");
    }

    if (string.IsNullOrWhiteSpace(authentication.LocalToken))
    {
        throw new InvalidOperationException(
            "Auth:LocalToken is required when Auth:Mode is Local.");
    }

    builder.Services
        .AddAuthentication(LocalDevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<LocalDevelopmentAuthenticationOptions, LocalDevelopmentAuthenticationHandler>(
            LocalDevelopmentAuthenticationHandler.SchemeName,
            options =>
            {
                options.Token = authentication.LocalToken;
                options.Subject = authentication.LocalSubject;
                options.Email = authentication.LocalEmail;
                options.DisplayName = authentication.LocalDisplayName;
            });
}
else if (string.Equals(authentication.Mode, ApplicationAuthenticationOptions.OAuthMode, StringComparison.OrdinalIgnoreCase))
{
    var google = builder.Configuration.GetSection(GoogleOAuthOptions.SectionName)
        .Get<GoogleOAuthOptions>() ?? new GoogleOAuthOptions();
    if (!builder.Environment.IsDevelopment() &&
        !builder.Environment.IsEnvironment("Testing") &&
        (!google.IsConfigured || !google.HasValidProductionOrigins))
    {
        throw new InvalidOperationException(
            "Google OAuth requires an HTTPS PublicApiBaseUrl and, when configured, an HTTPS PublicWebReturnBaseUrl on the same public host outside Development.");
    }
    if (!builder.Environment.IsDevelopment() &&
        !builder.Environment.IsEnvironment("Testing") &&
        !authentication.InviteOnly)
    {
        throw new InvalidOperationException(
            "Auth:InviteOnly must be true outside Development and Testing.");
    }
    if (!builder.Environment.IsDevelopment() &&
        !builder.Environment.IsEnvironment("Testing") &&
        !string.IsNullOrWhiteSpace(authentication.InvitedEmailsFile))
    {
        try
        {
            using var inviteFile = File.Open(
                authentication.InvitedEmailsFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Auth:InvitedEmailsFile is configured but cannot be read.",
                exception);
        }
    }

    builder.Services
        .AddAuthentication(OAuthSessionAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, OAuthSessionAuthenticationHandler>(
            OAuthSessionAuthenticationHandler.SchemeName,
            _ => { });
}
else
{
    throw new InvalidOperationException(
        $"Unsupported Auth:Mode '{authentication.Mode}'. Use '{ApplicationAuthenticationOptions.OAuthMode}' or '{ApplicationAuthenticationOptions.LocalMode}'.");
}
builder.Services.AddAuthorization();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
            ?? ["http://localhost:3000"];
        policy.WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .WithExposedHeaders("ETag", "Location");
    });
});

builder.Services.AddRateLimiter(options =>
{
    const int authenticatedReadPermitLimit = 600;
    const int authenticatedMutationPermitLimit = 120;
    const int anonymousPermitLimit = 120;
    const int windowSeconds = 60;

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = static (context, _) =>
    {
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : windowSeconds;
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        return ValueTask.CompletedTask;
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var subject = context.User.FindFirst("sub")?.Value;
        var authenticated = context.User.Identity?.IsAuthenticated == true &&
            !string.IsNullOrWhiteSpace(subject);
        var authenticatedRead = authenticated &&
            (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method));
        var partition = authenticated
            ? $"subject:{subject}:{(authenticatedRead ? "read" : "mutation")}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "anonymous"}";
        return RateLimitPartition.GetSlidingWindowLimiter(
            partition,
            _ => new SlidingWindowRateLimiterOptions
            {
                // The release workspace deliberately loads several independent views in parallel.
                // Give authenticated reads headroom without relaxing mutation or anonymous limits.
                PermitLimit = authenticatedRead
                    ? authenticatedReadPermitLimit
                    : authenticated
                        ? authenticatedMutationPermitLimit
                        : anonymousPermitLimit,
                SegmentsPerWindow = 6,
                Window = TimeSpan.FromSeconds(windowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

var app = builder.Build();

if (reverseProxy.Enabled)
{
    app.UseForwardedHeaders();
}

if (app.Environment.IsProduction())
{
    app.UseHsts();
    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/health"),
        branch => branch.UseHttpsRedirection());
}

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<OAuthCsrfMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () => Results.Ok(new
{
    product = "Hook2Stream",
    offer = "One song. Three weeks of ready-to-post lyric shorts.",
    apiVersion = "v1"
})).AllowAnonymous();

app.MapHook2StreamApi();
app.MapDefaultEndpoints();

app.Run();

public partial class Program;
