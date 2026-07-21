using Hook2Stream.Application;
using Hook2Stream.Infrastructure.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Providers;

public static class ProviderServiceCollectionExtensions
{
    public static IServiceCollection AddHook2StreamPipelineProviders(
        this IServiceCollection services,
        IConfiguration configuration,
        bool allowFixtureProviders) =>
        services.AddHook2StreamPipelineProviders(
            configuration,
            allowFixtureProviders,
            JobRoutingRegistry.Capabilities);

    public static IServiceCollection AddHook2StreamPipelineProviders(
        this IServiceCollection services,
        IConfiguration configuration,
        bool allowFixtureProviders,
        IReadOnlyCollection<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var selectedCapabilities = capabilities
            .Select(JobRoutingRegistry.NormalizeCapability)
            .ToHashSet(StringComparer.Ordinal);
        if (selectedCapabilities.Count == 0)
        {
            throw new ArgumentException(
                "At least one worker capability is required for provider registration.",
                nameof(capabilities));
        }

        services.AddOptions<PipelineProviderOptions>()
            .Bind(configuration.GetSection(PipelineProviderOptions.SectionName))
            .Validate(
                options => Validate(options, allowFixtureProviders, selectedCapabilities),
                "Pipeline provider configuration is invalid. Fixture providers are allowed only in Development or Testing.")
            .ValidateOnStart();

        services.TryAddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.TryAddSingleton<IProcessRunner, SafeProcessRunner>();

        if (selectedCapabilities.Contains(JobRoutingRegistry.Analysis))
        {
            services.AddSingleton<FixtureAudioAnalysisProvider>();
            services.AddSingleton<ExternalAudioAnalysisProvider>();
            services.AddScoped<DeterministicAudioAnalysisProvider>();
            services.AddScoped<IAudioAnalysisProvider>(serviceProvider =>
                Mode(serviceProvider, options => options.AudioAnalysis) switch
                {
                    ProviderAdapterMode.Deterministic => serviceProvider.GetRequiredService<DeterministicAudioAnalysisProvider>(),
                    ProviderAdapterMode.ExternalProcess => serviceProvider.GetRequiredService<ExternalAudioAnalysisProvider>(),
                    _ => serviceProvider.GetRequiredService<FixtureAudioAnalysisProvider>()
                });
        }

        if (selectedCapabilities.Contains(JobRoutingRegistry.Control))
        {
            services.AddOptions<OpenRouterOptions>()
                .Bind(configuration.GetSection(OpenRouterOptions.SectionName))
                .PostConfigure(options =>
                {
                    if (string.IsNullOrWhiteSpace(options.ApiKey))
                    {
                        options.ApiKey = configuration["OPENROUTER_API_KEY"] ?? "";
                    }
                })
                .Validate(HasValidOpenRouterOptions, "OpenRouter configuration is invalid.")
                .Validate(
                    options => !UsesOpenRouter(configuration) || !string.IsNullOrWhiteSpace(options.ApiKey),
                    "OPENROUTER_API_KEY is required when an OpenRouter provider mode is selected.")
                .ValidateOnStart();
            services.AddHttpClient<OpenRouterClient>(client => client.Timeout = Timeout.InfiniteTimeSpan);
            services.AddSingleton<FixtureTranscriptionProvider>();
            services.AddSingleton<FixtureArtworkProvider>();
            services.AddSingleton<FixtureCampaignPlanner>();
            services.AddSingleton<ExternalTranscriptionProvider>();
            services.AddSingleton<ExternalArtworkProvider>();
            services.AddSingleton<ExternalCampaignPlanner>();
            services.AddScoped<OpenRouterTranscriptionProvider>();
            services.AddScoped<OpenRouterArtworkProvider>();
            services.AddScoped<OpenRouterCampaignPlanner>();
            services.AddScoped<ITranscriptionProvider>(serviceProvider =>
                Mode(serviceProvider, options => options.Transcription) switch
                {
                    ProviderAdapterMode.OpenRouter => serviceProvider.GetRequiredService<OpenRouterTranscriptionProvider>(),
                    ProviderAdapterMode.ExternalProcess => serviceProvider.GetRequiredService<ExternalTranscriptionProvider>(),
                    _ => serviceProvider.GetRequiredService<FixtureTranscriptionProvider>()
                });
            services.AddScoped<IArtworkProvider>(serviceProvider =>
                Mode(serviceProvider, options => options.Artwork) switch
                {
                    ProviderAdapterMode.OpenRouter => serviceProvider.GetRequiredService<OpenRouterArtworkProvider>(),
                    ProviderAdapterMode.ExternalProcess => serviceProvider.GetRequiredService<ExternalArtworkProvider>(),
                    _ => serviceProvider.GetRequiredService<FixtureArtworkProvider>()
                });
            services.AddScoped<ICampaignPlanner>(serviceProvider =>
                Mode(serviceProvider, options => options.CampaignPlanning) switch
                {
                    ProviderAdapterMode.OpenRouter => serviceProvider.GetRequiredService<OpenRouterCampaignPlanner>(),
                    ProviderAdapterMode.ExternalProcess => serviceProvider.GetRequiredService<ExternalCampaignPlanner>(),
                    _ => serviceProvider.GetRequiredService<FixtureCampaignPlanner>()
                });
        }

        if (selectedCapabilities.Contains(JobRoutingRegistry.Render))
        {
            services.AddSingleton<FixtureVideoRenderer>();
            services.AddSingleton<ExternalVideoRenderer>();
            services.AddScoped<DeterministicVideoRenderer>();
            services.AddScoped<IVideoRenderer>(serviceProvider =>
                Mode(serviceProvider, options => options.VideoRendering) switch
                {
                    ProviderAdapterMode.Deterministic => serviceProvider.GetRequiredService<DeterministicVideoRenderer>(),
                    ProviderAdapterMode.ExternalProcess => serviceProvider.GetRequiredService<ExternalVideoRenderer>(),
                    _ => serviceProvider.GetRequiredService<FixtureVideoRenderer>()
                });
        }

        return services;
    }

    private static ProviderAdapterMode Mode(
        IServiceProvider serviceProvider,
        Func<PipelineProviderOptions, ProviderProcessOptions> select) =>
        select(serviceProvider.GetRequiredService<IOptions<PipelineProviderOptions>>().Value).Mode;

    private static bool Validate(
        PipelineProviderOptions options,
        bool allowFixtureProviders,
        IReadOnlySet<string> capabilities)
    {
        if (!allowFixtureProviders)
        {
            return (!capabilities.Contains(JobRoutingRegistry.Analysis) ||
                    options.AudioAnalysis.Mode == ProviderAdapterMode.Deterministic &&
                    HasValidTimeout(options.AudioAnalysis)) &&
                   (!capabilities.Contains(JobRoutingRegistry.Control) ||
                    options.Transcription.Mode == ProviderAdapterMode.OpenRouter &&
                    options.Artwork.Mode == ProviderAdapterMode.OpenRouter &&
                    options.CampaignPlanning.Mode == ProviderAdapterMode.OpenRouter &&
                    HasValidTimeout(options.Transcription) &&
                    HasValidTimeout(options.Artwork) &&
                    HasValidTimeout(options.CampaignPlanning)) &&
                   (!capabilities.Contains(JobRoutingRegistry.Render) ||
                    options.VideoRendering.Mode == ProviderAdapterMode.Deterministic &&
                    HasValidTimeout(options.VideoRendering));
        }

        return (!capabilities.Contains(JobRoutingRegistry.Analysis) ||
                Validate(options.AudioAnalysis, allowFixtureProviders, allowDeterministic: true)) &&
               (!capabilities.Contains(JobRoutingRegistry.Control) ||
                Validate(options.Transcription, allowFixtureProviders, allowOpenRouter: true) &&
                Validate(options.Artwork, allowFixtureProviders, allowOpenRouter: true) &&
                Validate(options.CampaignPlanning, allowFixtureProviders, allowOpenRouter: true)) &&
               (!capabilities.Contains(JobRoutingRegistry.Render) ||
                Validate(options.VideoRendering, allowFixtureProviders, allowDeterministic: true));
    }

    private static bool HasValidTimeout(ProviderProcessOptions options) =>
        options.TimeoutSeconds is >= 10 and <= 7_200;

    private static bool Validate(
        ProviderProcessOptions options,
        bool allowFixtureProviders,
        bool allowOpenRouter = false,
        bool allowDeterministic = false) =>
        options.TimeoutSeconds is >= 10 and <= 7_200 &&
        (allowFixtureProviders && options.Mode == ProviderAdapterMode.Fixture ||
         allowOpenRouter && options.Mode == ProviderAdapterMode.OpenRouter ||
         allowDeterministic && options.Mode == ProviderAdapterMode.Deterministic ||
         options.Mode == ProviderAdapterMode.ExternalProcess &&
         !string.IsNullOrWhiteSpace(options.Executable) &&
         !string.IsNullOrWhiteSpace(options.Provider) &&
         !string.IsNullOrWhiteSpace(options.Model) &&
         !string.IsNullOrWhiteSpace(options.Version));

    private static bool HasValidOpenRouterOptions(OpenRouterOptions options) =>
        Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUrl) &&
        baseUrl.Scheme == Uri.UriSchemeHttps &&
        string.Equals(baseUrl.Host, "openrouter.ai", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(baseUrl.AbsolutePath.TrimEnd('/'), "/api/v1", StringComparison.Ordinal) &&
        string.Equals(options.TranscriptionModel, "openai/whisper-large-v3", StringComparison.Ordinal) &&
        string.Equals(options.ImageModel, "bytedance-seed/seedream-4.5", StringComparison.Ordinal) &&
        string.Equals(options.CampaignModel, "openai/gpt-oss-120b", StringComparison.Ordinal) &&
        options.TranscriptionTimeoutSeconds is >= 10 and <= 900 &&
        options.ImageTimeoutSeconds is >= 10 and <= 900 &&
        options.CampaignTimeoutSeconds is >= 10 and <= 900 &&
        options.MaxRetries is >= 0 and <= 5 &&
        options.TranscriptionChunkSeconds is >= 10 and <= 55 &&
        options.TranscriptionOverlapSeconds is >= 0 and <= 5 &&
        options.TranscriptionOverlapSeconds < options.TranscriptionChunkSeconds &&
        !string.IsNullOrWhiteSpace(options.AppTitle) &&
        options.RequireZeroDataRetention &&
        options.DenyDataCollection &&
        options.RequireParameters;

    private static bool UsesOpenRouter(IConfiguration configuration) =>
        new[] { "Transcription", "Artwork", "CampaignPlanning" }.Any(stage =>
            Enum.TryParse<ProviderAdapterMode>(
                configuration[$"{PipelineProviderOptions.SectionName}:{stage}:Mode"],
                ignoreCase: true,
                out var mode) && mode == ProviderAdapterMode.OpenRouter);
}
