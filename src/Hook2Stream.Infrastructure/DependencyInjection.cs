using Amazon.S3;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure.Billing;
using Hook2Stream.Infrastructure.Jobs;
using Hook2Stream.Infrastructure.Media;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure;

public static class DependencyInjection
{
    private const string DevelopmentConnectionString =
        "Host=localhost;Port=5432;Database=hook2stream;Username=postgres;Password=postgres";

    public static IServiceCollection AddHook2StreamInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        bool includeBilling = true)
    {
        var configuredConnectionString = FirstNonEmpty(
            configuration.GetConnectionString("hook2stream"),
            configuration.GetConnectionString("Database"));
        services.AddOptions<DatabaseConnectionOptions>()
            .Configure(options => options.ConnectionString = configuredConnectionString ??
                (environment.IsProduction() ? "" : DevelopmentConnectionString))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                "ConnectionStrings:hook2stream (or ConnectionStrings:Database) is required in Production.")
            .ValidateOnStart();

        services.AddDbContext<Hook2StreamDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                    serviceProvider.GetRequiredService<IOptions<DatabaseConnectionOptions>>()
                        .Value.ConnectionString,
                    npgsql => npgsql.EnableRetryOnFailure())
                .UseSnakeCaseNamingConvention());

        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .Validate(
                options => IsValidOrigin(options.ServiceUrl, requireHttps: false),
                "Storage ServiceUrl must be an absolute HTTP(S) origin without credentials, paths, queries, or fragments.")
            .Validate(
                options => IsValidOrigin(options.PublicServiceUrl, environment.IsProduction()),
                "Storage PublicServiceUrl must be an absolute HTTPS origin without credentials, paths, queries, or fragments in Production.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Bucket), "Storage Bucket is required.")
            .Validate(
                HasCompleteOrEmptyStaticCredentials,
                "Storage AccessKey and SecretKey must either both be configured or both be empty.")
            .Validate(
                options => options.CredentialMode != StorageCredentialMode.Static || HasStaticCredentials(options),
                "Storage static credential mode requires both AccessKey and SecretKey.")
            .Validate(
                options => options.CredentialMode != StorageCredentialMode.DefaultChain ||
                           !HasAnyStaticCredential(options),
                "Storage DefaultChain credential mode cannot be combined with AccessKey or SecretKey.")
            .Validate(
                options => !options.RequireCredentials ||
                           options.CredentialMode != StorageCredentialMode.DefaultChain &&
                           HasStaticCredentials(options),
                "Storage RequireCredentials requires static AccessKey and SecretKey credentials.")
            .Validate(
                options => !options.ConfigureBucketCors ||
                           options.BrowserUploadOrigins is { Length: > 0 } &&
                           options.BrowserUploadOrigins.All(origin =>
                               IsValidBrowserOrigin(origin, environment.IsProduction())),
                "Storage BrowserUploadOrigins must contain absolute HTTPS origins without paths in Production.")
            .ValidateOnStart();

        services.AddOptions<MediaToolsOptions>()
            .Bind(configuration.GetSection(MediaToolsOptions.SectionName))
            .Validate(options => options.ProcessTimeoutSeconds is >= 10 and <= 900, "Media process timeout is out of range.")
            .ValidateOnStart();

        services.AddOptions<OperationalPolicyOptions>()
            .Bind(configuration.GetSection(OperationalPolicyOptions.SectionName))
            .Validate(options => options.UploadUrlMinutes is >= 1 and <= 1_440,
                "Upload URL lifetime must be between one minute and one day.")
            .Validate(options => options.UploadSessionHours is >= 1 and <= 168,
                "Upload session lifetime must be between one hour and seven days.")
            .Validate(options => options.StagingHours is >= 1 and <= 168,
                "Staging retention must be between one hour and seven days.")
            .Validate(options => options.SupersededArtworkDays is >= 1 and <= 365,
                "Superseded artwork retention must be between one day and one year.")
            .Validate(options => options.UnpaidProjectDays is >= 1 and <= 365,
                "Unpaid project retention must be between one day and one year.")
            .Validate(options => options.PaidSourceDays is >= 1 and <= 730,
                "Paid source retention must be between one day and two years.")
            .Validate(options => options.PaidOutputDays is >= 1 and <= 3_650,
                "Paid output retention must be between one day and ten years.")
            .Validate(options => options.ExplicitDeletionDays is >= 0 and <= 7,
                "Explicit project deletion must purge content within seven days.")
            .Validate(options => options.DeletionFenceMinutes is >= 1 and <= 60,
                "The deletion fence must be between one minute and one hour.")
            .Validate(options => options.DeletionFenceMinutes > options.UploadUrlMinutes,
                "The deletion fence must outlive every previously issued upload URL.")
            .Validate(options => options.IdempotencyDays is >= 1 and <= 90,
                "Idempotency retention must be between one and ninety days.")
            .Validate(options => options.RetentionSweepMinutes is >= 1 and <= 1_440,
                "Retention sweep interval must be between one minute and one day.")
            .ValidateOnStart();

        if (includeBilling)
        {
            services.AddOptions<StripeOptions>()
                .Bind(configuration.GetSection(StripeOptions.SectionName))
                .Validate(options => options.Mode != PaymentGatewayMode.Fixture ||
                                     environment.IsDevelopment() || environment.IsEnvironment("Testing"),
                    "Stripe Fixture mode is only allowed in Development or Testing.")
                .Validate(options => Uri.TryCreate(options.PublicWebBaseUrl, UriKind.Absolute, out var uri) &&
                                     uri.Scheme is "http" or "https", "Stripe PublicWebBaseUrl is invalid.")
                .Validate(options => options.Mode == PaymentGatewayMode.Fixture ||
                                     options.SecretKey.StartsWith("sk_", StringComparison.Ordinal) &&
                                     options.WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal),
                    "Stripe secrets are required in Stripe mode.")
                .Validate(options => options.Mode == PaymentGatewayMode.Fixture ||
                                     BillingProducts.All.All(product =>
                                         options.PriceIds.TryGetValue(product, out var price) && !string.IsNullOrWhiteSpace(price)),
                    "Every billing product requires a Stripe Price in Stripe mode.")
                .ValidateOnStart();
            services.AddSingleton<FixturePaymentGateway>();
            services.AddHttpClient<StripePaymentGateway>();
            services.AddTransient<IPaymentGateway>(serviceProvider =>
                serviceProvider.GetRequiredService<IOptions<StripeOptions>>().Value.Mode == PaymentGatewayMode.Stripe
                    ? serviceProvider.GetRequiredService<StripePaymentGateway>()
                    : serviceProvider.GetRequiredService<FixturePaymentGateway>());
        }

        services.AddSingleton<IAmazonS3>(serviceProvider =>
            S3ClientFactory.Create(
                serviceProvider.GetRequiredService<IOptions<StorageOptions>>().Value,
                usePublicServiceUrl: false));
        services.AddKeyedSingleton<IAmazonS3>(
            S3ClientFactory.PublicPresignerKey,
            (serviceProvider, _) => S3ClientFactory.Create(
                serviceProvider.GetRequiredService<IOptions<StorageOptions>>().Value,
                usePublicServiceUrl: true));

        services.AddScoped<IObjectStorage, S3ObjectStorage>();
        services.AddSingleton<IAiProviderInvocationWriter, AiProviderInvocationWriter>();
        services.AddScoped<IJobQueue, PostgresJobQueue>();
        services.AddSingleton<IProcessRunner, SafeProcessRunner>();
        services.AddScoped<IMediaIngestProcessor, MediaIngestProcessor>();
        services.TryAddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("postgres", failureStatus: HealthStatus.Unhealthy, tags: ["ready"])
            .AddCheck<ObjectStorageHealthCheck>("object-storage", failureStatus: HealthStatus.Unhealthy, tags: ["ready"]);

        return services;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool HasAnyStaticCredential(StorageOptions options) =>
        !string.IsNullOrWhiteSpace(options.AccessKey) ||
        !string.IsNullOrWhiteSpace(options.SecretKey);

    private static bool HasStaticCredentials(StorageOptions options) =>
        !string.IsNullOrWhiteSpace(options.AccessKey) &&
        !string.IsNullOrWhiteSpace(options.SecretKey);

    private static bool HasCompleteOrEmptyStaticCredentials(StorageOptions options) =>
        !HasAnyStaticCredential(options) || HasStaticCredentials(options);

    private static bool IsValidBrowserOrigin(string value, bool requireHttps) =>
        IsValidOrigin(value, requireHttps);

    private static bool IsValidOrigin(string value, bool requireHttps)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") ||
            requireHttps && origin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(origin.Host);
    }
}
