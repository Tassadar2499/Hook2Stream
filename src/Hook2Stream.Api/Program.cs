using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Hook2Stream.Api;
using Hook2Stream.Api.Authentication;
using Hook2Stream.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHook2StreamInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUserService>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

builder.Services.Configure<GoogleOAuthOptions>(
    builder.Configuration.GetSection(GoogleOAuthOptions.SectionName));
builder.Services.Configure<JwtIssuerOptions>(
    builder.Configuration.GetSection(JwtIssuerOptions.SectionName));
builder.Services.AddSingleton<IApplicationJwtIssuer, ApplicationJwtIssuer>();
builder.Services.AddSingleton<OAuthStateProtector>();
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
    var jwt = builder.Configuration.GetSection(JwtIssuerOptions.SectionName)
        .Get<JwtIssuerOptions>() ?? new JwtIssuerOptions();
    if (!jwt.IsValid)
    {
        throw new InvalidOperationException(
            "Jwt:SigningKey must be configured with at least 32 characters when Auth:Mode is OAuth.");
    }

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromMinutes(1)
            };
        });
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
            .WithExposedHeaders("ETag", "Location");
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var partition = context.User.FindFirst("sub")?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";
        return RateLimitPartition.GetSlidingWindowLimiter(
            partition,
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 120,
                SegmentsPerWindow = 6,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
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
