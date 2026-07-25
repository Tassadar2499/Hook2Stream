using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Media;
using Hook2Stream.Infrastructure.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hook2Stream.Worker;

public static class JobHandlerServiceCollectionExtensions
{
    public static IServiceCollection AddHook2StreamWorkerRole(
        this IServiceCollection services,
        IReadOnlyCollection<string> capabilities)
    {
        services.AddHook2StreamJobHandlers(capabilities);
        services.AddHostedService<WorkerRoutingStartupValidator>();
        services.AddHostedService<MediaJobWorker>();

        var normalized = capabilities
            .Select(JobRoutingRegistry.NormalizeCapability)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized is [JobRoutingRegistry.Control])
        {
            services.AddHostedService<OutboxJobDispatcher>();
            services.AddHostedService<PipelineReconciler>();
            services.AddHostedService<RetentionSweepService>();
        }

        return services;
    }

    public static IServiceCollection AddHook2StreamJobHandlers(this IServiceCollection services) =>
        services.AddHook2StreamJobHandlers(JobRoutingRegistry.Capabilities);

    public static IServiceCollection AddHook2StreamJobHandlers(
        this IServiceCollection services,
        IReadOnlyCollection<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var selectedCapabilities = capabilities
            .Select(JobRoutingRegistry.NormalizeCapability)
            .ToHashSet(StringComparer.Ordinal);

        services.AddScoped<IPipelineArtifactStore, PipelineArtifactStore>();
        services.AddScoped<ICleanCoverComposer, CleanCoverComposer>();

        if (Includes(JobType.MediaIngest))
        {
            services.AddScoped<IJobHandler, MediaIngestJobHandler>();
        }

        if (Includes(JobType.AudioAnalysis))
        {
            services.AddScoped<IJobHandler, AudioAnalysisJobHandler>();
        }

        if (Includes(JobType.AssetCleanup))
        {
            services.AddScoped<IJobHandler, AssetCleanupJobHandler>();
            services.AddScoped<IJobHandler, TranscriptionJobHandler>();
            services.AddScoped<IJobHandler, ArtworkGenerationJobHandler>();
            services.AddScoped<IJobHandler, CampaignGenerationJobHandler>();
        }

        if (Includes(JobType.PreviewRender))
        {
            services.TryAddScoped<DeterministicVideoRenderer>();
            services.AddScoped<IJobHandler>(serviceProvider =>
                ActivatorUtilities.CreateInstance<VideoRenderJobHandler>(serviceProvider, JobType.PreviewRender));
            services.AddScoped<IJobHandler>(serviceProvider =>
                ActivatorUtilities.CreateInstance<VideoRenderJobHandler>(serviceProvider, JobType.FinalRender));
            services.AddScoped<IJobHandler, CleanCoverRenderJobHandler>();
        }

        if (Includes(JobType.ExportBundle))
        {
            services.AddScoped<IJobHandler, ExportBundleJobHandler>();
        }

        return services;

        bool Includes(JobType type) =>
            selectedCapabilities.Contains(JobRoutingRegistry.GetRequiredCapability(type));
    }
}
