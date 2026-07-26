using Hook2Stream.Domain;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Media;
using Hook2Stream.Infrastructure.Providers;
using Microsoft.Extensions.Configuration;
using Hook2Stream.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class WorkerRegistrationTests
{
    [Fact]
    public void Worker_registers_distinct_preview_final_export_and_clean_cover_handlers()
    {
        var services = new ServiceCollection();

        services.AddHook2StreamJobHandlers();

        var handlers = services.Where(value => value.ServiceType == typeof(IJobHandler)).ToArray();
        Assert.Equal(10, handlers.Length);
        Assert.Equal(2, handlers.Count(value => value.ImplementationFactory is not null));
        Assert.Contains(handlers, value => value.ImplementationType == typeof(ExportBundleJobHandler));
        Assert.Contains(handlers, value => value.ImplementationType == typeof(CleanCoverRenderJobHandler));

        var preview = new VideoRenderJobHandler(
            JobType.PreviewRender, null!, null!, null!, null!);
        var final = new VideoRenderJobHandler(
            JobType.FinalRender, null!, null!, null!, null!);
        Assert.Equal(JobType.PreviewRender, preview.Type);
        Assert.Equal(JobType.FinalRender, final.Type);
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoRenderJobHandler(
            JobType.ExportBundle, null!, null!, null!, null!));
    }

    [Theory]
    [InlineData(JobType.MediaIngest, JobRoutingRegistry.Media)]
    [InlineData(JobType.AudioAnalysis, JobRoutingRegistry.Analysis)]
    [InlineData(JobType.Transcription, JobRoutingRegistry.Control)]
    [InlineData(JobType.ArtworkGeneration, JobRoutingRegistry.Control)]
    [InlineData(JobType.CampaignGeneration, JobRoutingRegistry.Control)]
    [InlineData(JobType.AssetCleanup, JobRoutingRegistry.Control)]
    [InlineData(JobType.PreviewRender, JobRoutingRegistry.Render)]
    [InlineData(JobType.FinalRender, JobRoutingRegistry.Render)]
    [InlineData(JobType.CleanCoverRender, JobRoutingRegistry.Render)]
    [InlineData(JobType.ExportBundle, JobRoutingRegistry.Export)]
    public void Job_routing_registry_is_authoritative(JobType type, string capability)
    {
        Assert.Equal(capability, JobRoutingRegistry.GetRequiredCapability(type));
        JobRoutingRegistry.EnsureMatches(type, capability.ToUpperInvariant());
    }

    [Theory]
    [InlineData(JobRoutingRegistry.Media, 1)]
    [InlineData(JobRoutingRegistry.Analysis, 1)]
    [InlineData(JobRoutingRegistry.Control, 4)]
    [InlineData(JobRoutingRegistry.Render, 3)]
    [InlineData(JobRoutingRegistry.Export, 1)]
    public void Worker_pool_registers_only_its_routed_handlers(
        string capability,
        int expectedHandlerCount)
    {
        var services = new ServiceCollection();

        services.AddHook2StreamJobHandlers([capability]);

        var handlers = services.Count(value => value.ServiceType == typeof(IJobHandler));
        Assert.Equal(expectedHandlerCount, handlers);
    }

    [Theory]
    [InlineData(JobRoutingRegistry.Media)]
    [InlineData(JobRoutingRegistry.Analysis)]
    [InlineData(JobRoutingRegistry.Control)]
    [InlineData(JobRoutingRegistry.Export)]
    public void Non_render_pool_does_not_register_deterministic_video_renderer(string capability)
    {
        var services = new ServiceCollection();

        services.AddHook2StreamJobHandlers([capability]);

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(DeterministicVideoRenderer));
    }

    [Fact]
    public void Render_pool_registers_deterministic_video_renderer_as_scoped()
    {
        var services = new ServiceCollection();

        services.AddHook2StreamJobHandlers([JobRoutingRegistry.Render]);

        var renderer = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(DeterministicVideoRenderer));
        Assert.Equal(ServiceLifetime.Scoped, renderer.Lifetime);
    }

    [Fact]
    public void Worker_routing_validation_rejects_a_handler_capability_mismatch()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkerRoutingValidation.Validate(
                [JobRoutingRegistry.Media],
                [new MismatchedHandler()]));

        Assert.Contains("authoritative job route", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(JobRoutingRegistry.Media)]
    [InlineData(JobRoutingRegistry.Analysis)]
    [InlineData(JobRoutingRegistry.Render)]
    [InlineData(JobRoutingRegistry.Export)]
    public void Non_control_pool_does_not_register_or_require_OpenRouter(string capability)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PipelineProviders:AudioAnalysis:Mode"] = "Deterministic",
                ["PipelineProviders:VideoRendering:Mode"] = "Deterministic"
            })
            .Build();
        var services = new ServiceCollection()
            .AddHook2StreamPipelineProviders(
                configuration,
                allowFixtureProviders: false,
                [capability]);

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(OpenRouterClient));
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(ITranscriptionProvider));
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IArtworkProvider));
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(ICampaignPlanner));

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<PipelineProviderOptions>>().Value;
    }

    [Fact]
    public void Control_pool_owns_OpenRouter_and_AI_provider_registrations()
    {
        var services = new ServiceCollection()
            .AddHook2StreamPipelineProviders(
                new ConfigurationBuilder().Build(),
                allowFixtureProviders: true,
                [JobRoutingRegistry.Control]);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(OpenRouterClient));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ITranscriptionProvider));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IArtworkProvider));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ICampaignPlanner));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IAudioAnalysisProvider));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IVideoRenderer));
    }

    [Fact]
    public void Control_pool_resolves_the_typed_OpenRouter_client_when_enabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PipelineProviders:Transcription:Mode"] = "OpenRouter",
                ["OpenRouter:ApiKey"] = $"sk-or-v1-{new string('a', 64)}",
                ["OpenRouter:AccountOrGuardrailZdrEnforced"] = "true"
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddHook2StreamPipelineProviders(
                configuration,
                allowFixtureProviders: true,
                [JobRoutingRegistry.Control])
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<OpenRouterClient>());
    }

    [Theory]
    [InlineData(JobRoutingRegistry.Media, false)]
    [InlineData(JobRoutingRegistry.Analysis, false)]
    [InlineData(JobRoutingRegistry.Control, true)]
    [InlineData(JobRoutingRegistry.Render, false)]
    [InlineData(JobRoutingRegistry.Export, false)]
    public void Only_control_pool_registers_control_loop_services(
        string capability,
        bool expectedControlLoops)
    {
        var services = new ServiceCollection()
            .AddHook2StreamWorkerRole([capability]);
        var hostedServiceTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .ToHashSet();

        Assert.Equal(expectedControlLoops, hostedServiceTypes.Contains(typeof(OutboxJobDispatcher)));
        Assert.Equal(expectedControlLoops, hostedServiceTypes.Contains(typeof(PipelineReconciler)));
        Assert.Equal(expectedControlLoops, hostedServiceTypes.Contains(typeof(RetentionSweepService)));
        Assert.Contains(typeof(WorkerRoutingStartupValidator), hostedServiceTypes);
        Assert.Contains(typeof(MediaJobWorker), hostedServiceTypes);
    }

    [Fact]
    public void Manual_campaign_controls_change_the_canonical_render_fingerprint()
    {
        var itemId = Guid.NewGuid();
        var backgroundId = Guid.NewGuid();
        var baseline = new CampaignItemRequest(
            itemId,
            1,
            "kinetic-lyrics",
            "chorus",
            backgroundId,
            "Approved text",
            """{"durationMilliseconds":15000,"cta":"Listen","caption":"One","fit":"fill","focalX":0.5,"focalY":0.5,"opening":"fade","textLayout":"center"}""");
        var edited = baseline with
        {
            CompositionJson =
                """{"durationMilliseconds":22000,"cta":"Save now","caption":"Two","fit":"fit","focalX":0.2,"focalY":0.8,"opening":"punch","textLayout":"lowerThird"}"""
        };
        var hook = new HookRequest("chorus", "chorus", 1_000, 25_000, "Chorus");
        var audio = Asset(AssetKind.Audio, "a");
        var cover = Asset(AssetKind.Cover, "b");
        var background = Asset(AssetKind.Visual, "c");

        var first = VideoCompositionControls.Parse(baseline, hook, 60_000)
            .BindSources(audio, cover, background, 1);
        var second = VideoCompositionControls.Parse(edited, hook, 60_000)
            .BindSources(audio, cover, background, 1);

        Assert.NotEqual(first.CompositionHash, second.CompositionHash);
        Assert.Equal("fill", first.Fit);
        Assert.Equal("fit", second.Fit);
        Assert.Equal(22_000, second.DurationMilliseconds);
        Assert.Equal("Save now", second.CallToAction);
        Assert.Equal("lowerThird", second.TextLayout);
    }

    [Fact]
    public void Legacy_campaign_call_to_action_is_preserved_during_render()
    {
        var item = new CampaignItemRequest(
            Guid.NewGuid(),
            1,
            "kinetic-lyrics",
            "chorus",
            Guid.NewGuid(),
            "Approved text",
            """{"callToAction":"Pre-save now"}""");
        var hook = new HookRequest("chorus", "chorus", 1_000, 20_000, "Chorus");

        var controls = VideoCompositionControls.Parse(item, hook, 60_000);

        Assert.Equal("Pre-save now", controls.CallToAction);
    }

    private static MediaAsset Asset(AssetKind kind, string hashSeed) => new()
    {
        WorkspaceId = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        Kind = kind,
        OriginalFileName = "asset",
        DeclaredContentType = kind == AssetKind.Audio ? "audio/mpeg" : "image/png",
        DeclaredBytes = 1,
        ObjectKey = "asset/object",
        Sha256 = new string(hashSeed[0], 64)
    };

    private sealed class MismatchedHandler : IJobHandler
    {
        public JobType Type => JobType.MediaIngest;
        public string Capability => JobRoutingRegistry.Analysis;

        public Task ProcessAsync(LeasedJob job, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
