using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Media;
using Hook2Stream.Infrastructure.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hook2Stream.Worker;

public static class JobHandlerServiceCollectionExtensions
{
    public static IServiceCollection AddHook2StreamJobHandlers(this IServiceCollection services)
    {
        services.TryAddSingleton<DeterministicVideoRenderer>();
        services.AddScoped<IPipelineArtifactStore, PipelineArtifactStore>();
        services.AddScoped<ICleanCoverComposer, CleanCoverComposer>();
        services.AddScoped<IJobHandler, MediaIngestJobHandler>();
        services.AddScoped<IJobHandler, AudioAnalysisJobHandler>();
        services.AddScoped<IJobHandler, TranscriptionJobHandler>();
        services.AddScoped<IJobHandler, ArtworkGenerationJobHandler>();
        services.AddScoped<IJobHandler, CampaignGenerationJobHandler>();
        services.AddScoped<IJobHandler>(serviceProvider =>
            ActivatorUtilities.CreateInstance<VideoRenderJobHandler>(serviceProvider, JobType.PreviewRender));
        services.AddScoped<IJobHandler>(serviceProvider =>
            ActivatorUtilities.CreateInstance<VideoRenderJobHandler>(serviceProvider, JobType.FinalRender));
        services.AddScoped<IJobHandler, ExportBundleJobHandler>();
        services.AddScoped<IJobHandler, CleanCoverRenderJobHandler>();
        return services;
    }
}
