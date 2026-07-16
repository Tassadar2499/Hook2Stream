using Amazon.Runtime;
using Amazon.S3;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure.Jobs;
using Hook2Stream.Infrastructure.Media;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddHook2StreamInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("hook2stream")
            ?? configuration.GetConnectionString("Database")
            ?? "Host=localhost;Port=5432;Database=hook2stream;Username=postgres;Password=postgres";

        services.AddDbContext<Hook2StreamDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure())
                .UseSnakeCaseNamingConvention());

        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .Validate(options => Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out _), "Storage ServiceUrl is invalid.")
            .Validate(options => Uri.TryCreate(options.PublicServiceUrl, UriKind.Absolute, out _), "Storage PublicServiceUrl is invalid.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Bucket), "Storage Bucket is required.")
            .Validate(
                options => !options.RequireCredentials ||
                           (!string.IsNullOrWhiteSpace(options.AccessKey) &&
                            !string.IsNullOrWhiteSpace(options.SecretKey)),
                "Storage credentials are required for this environment.")
            .ValidateOnStart();

        services.AddOptions<MediaToolsOptions>()
            .Bind(configuration.GetSection(MediaToolsOptions.SectionName))
            .Validate(options => options.ProcessTimeoutSeconds is >= 10 and <= 900, "Media process timeout is out of range.")
            .ValidateOnStart();

        services.AddSingleton<IAmazonS3>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<StorageOptions>>().Value;
            var credentials = new BasicAWSCredentials(options.AccessKey, options.SecretKey);
            var config = new AmazonS3Config
            {
                ServiceURL = options.ServiceUrl,
                ForcePathStyle = options.ForcePathStyle,
                AuthenticationRegion = options.Region,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
            };
            return new AmazonS3Client(credentials, config);
        });

        services.AddScoped<IObjectStorage, S3ObjectStorage>();
        services.AddScoped<IJobQueue, PostgresJobQueue>();
        services.AddSingleton<IProcessRunner, SafeProcessRunner>();
        services.AddScoped<IMediaIngestProcessor, MediaIngestProcessor>();
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("postgres", failureStatus: HealthStatus.Unhealthy, tags: ["ready"])
            .AddCheck<ObjectStorageHealthCheck>("object-storage", failureStatus: HealthStatus.Unhealthy, tags: ["ready"]);

        return services;
    }
}
