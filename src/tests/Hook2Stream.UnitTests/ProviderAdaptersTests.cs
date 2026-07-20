using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class ProviderAdaptersTests
{
    private static readonly ProviderExecutionContext Context = new(
        Guid.Parse("01900000-0000-7000-8000-000000000010"),
        new string('a', 64),
        new string('b', 64),
        "staging/provider-test");

    private static readonly ProviderObjectReference Audio = new(
        Guid.Parse("01900000-0000-7000-8000-000000000011"),
        "w/workspace/audio/source",
        new string('c', 64),
        "audio/mpeg",
        4_000_000,
        180_000);

    [Theory]
    [InlineData(ProviderFailureKind.UserInput, false)]
    [InlineData(ProviderFailureKind.Moderation, false)]
    [InlineData(ProviderFailureKind.Transient, true)]
    [InlineData(ProviderFailureKind.Authentication, false)]
    [InlineData(ProviderFailureKind.Quota, false)]
    [InlineData(ProviderFailureKind.Permanent, false)]
    [InlineData(ProviderFailureKind.Unknown, false)]
    public void Only_confirmed_transient_failures_are_automatically_retryable(
        ProviderFailureKind kind,
        bool expected)
    {
        var failure = new ProviderFailure(kind, "provider.failure", "The provider failed.");

        Assert.Equal(expected, failure.Retryable);
    }

    [Fact]
    public async Task Fixture_artwork_outputs_are_stable_text_free_manifests()
    {
        var provider = new FixtureArtworkProvider(TimeProvider.System);
        var request = new ArtworkGenerationRequest(
            Context,
            "Secret Artist",
            "Secret Track",
            new ArtworkCreativeBrief(
                "dreamy",
                ["#112233", "#445566"],
                ["private lyric excerpt"],
                "no typography"),
            3,
            2_048,
            2_048);

        var first = await provider.GenerateAsync(request, CancellationToken.None);
        var second = await provider.GenerateAsync(request, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(3, first.Value!.Candidates.Count);
        Assert.Equal(
            first.Value.Candidates.Select(candidate => candidate.CandidateId),
            second.Value!.Candidates.Select(candidate => candidate.CandidateId));
        Assert.Equal(
            first.Value.Artifacts.Select(artifact => artifact.Sha256),
            second.Value.Artifacts.Select(artifact => artifact.Sha256));
        Assert.All(first.Value.Artifacts, artifact =>
        {
            Assert.False(artifact.Materialized);
            Assert.Equal("image/png", artifact.ContentType);
            Assert.Equal(2_048, artifact.Width);
            Assert.Equal(2_048, artifact.Height);
            Assert.DoesNotContain("secret", artifact.ObjectKey, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Fixture_campaign_has_the_locked_eighteen_item_recipe()
    {
        var hooks = Enumerable.Range(1, 3)
            .Select(index => new CampaignHookInput(
                Guid.CreateVersion7(),
                $"Hook {index}",
                index * 10_000,
                index * 10_000 + 15_000,
                $"Excerpt {index}"))
            .ToArray();
        var request = new CampaignPlanningRequest(
            Context,
            "Artist",
            "Track",
            new DateOnly(2026, 8, 1),
            false,
            "direct",
            "Listen now",
            hooks,
            []);

        var result = await new FixtureCampaignPlanner(TimeProvider.System)
            .PlanAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(18, result.Value!.Items.Count);
        Assert.Equal(18, result.Value.Items.Select(item => item.ItemId).Distinct().Count());
        Assert.Equal(3, result.Value.Items.Count(item => item.TemplateKey == "kinetic-lyrics"));
        Assert.Equal(3, result.Value.Items.Count(item => item.TemplateKey == "animated-cover"));
        Assert.Equal(3, result.Value.Items.Count(item => item.TemplateKey == "visual-loop-a"));
        Assert.Equal(3, result.Value.Items.Count(item => item.TemplateKey == "visual-loop-b"));
        Assert.Equal(2, result.Value.Items.Count(item => item.TemplateKey == "teaser"));
        Assert.Equal(2, result.Value.Items.Count(item => item.TemplateKey == "countdown"));
        Assert.Equal(2, result.Value.Items.Count(item => item.TemplateKey == "out-now"));
        Assert.Equal(
            [-10, -9, -8, -6, -5, -3, -2, -1, 0, 0, 1, 2, 3, 5, 6, 7, 9, 10],
            result.Value.Items.Select(item => item.RelativeDay));
        Assert.True(CampaignPlanContractValidator.Validate(request, result.Value.Items).IsValid);
        Assert.All(result.Value.Items, item =>
        {
            Assert.InRange(item.DurationMilliseconds, 10_000, 30_000);
            using var composition = JsonDocument.Parse(item.CompositionJson);
            Assert.Equal(item.DurationMilliseconds, composition.RootElement.GetProperty("durationMilliseconds").GetInt64());
            Assert.NotEmpty(composition.RootElement.GetProperty("hashtags").EnumerateArray());
            var destinations = composition.RootElement.GetProperty("copyVariants").GetProperty("destinations");
            Assert.False(string.IsNullOrWhiteSpace(destinations.GetProperty("tiktok").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(destinations.GetProperty("youtubeShorts").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(destinations.GetProperty("instagramReels").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(destinations.GetProperty("vkClips").GetString()));
        });
    }

    [Fact]
    public async Task Fixture_transcription_rejects_languages_without_a_quality_baseline()
    {
        var request = new TranscriptionRequest(Context, Audio, null, "es");

        var result = await new FixtureTranscriptionProvider(TimeProvider.System)
            .TranscribeAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderFailureKind.UserInput, result.Failure!.Kind);
        Assert.False(result.Failure.Retryable);
    }

    [Fact]
    public void Provider_registration_uses_fixtures_by_default()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection()
            .AddHook2StreamPipelineProviders(configuration, allowFixtureProviders: true)
            .BuildServiceProvider();

        Assert.IsType<FixtureAudioAnalysisProvider>(services.GetRequiredService<IAudioAnalysisProvider>());
        Assert.IsType<FixtureTranscriptionProvider>(services.GetRequiredService<ITranscriptionProvider>());
        Assert.IsType<FixtureArtworkProvider>(services.GetRequiredService<IArtworkProvider>());
        Assert.IsType<FixtureCampaignPlanner>(services.GetRequiredService<ICampaignPlanner>());
        Assert.IsType<FixtureVideoRenderer>(services.GetRequiredService<IVideoRenderer>());
    }

    [Fact]
    public void Production_registration_rejects_fixture_providers()
    {
        var configuration = new ConfigurationBuilder().Build();
        using var services = new ServiceCollection()
            .AddHook2StreamPipelineProviders(configuration, allowFixtureProviders: false)
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<PipelineProviderOptions>>().Value);
    }

    [Fact]
    public void OpenRouter_mode_requires_an_api_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PipelineProviders:Artwork:Mode"] = "OpenRouter"
            })
            .Build();
        using var services = new ServiceCollection()
            .AddHook2StreamPipelineProviders(configuration, allowFixtureProviders: true)
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<OpenRouterOptions>>().Value);
    }

    [Fact]
    public async Task External_process_failure_does_not_expose_prompt_or_standard_error()
    {
        var processRunner = new CapturingFailedProcessRunner();
        var providerOptions = new PipelineProviderOptions
        {
            WorkRoot = Path.Combine(Path.GetTempPath(), "hook2stream-provider-tests"),
            Artwork = new ProviderProcessOptions
            {
                Mode = ProviderAdapterMode.ExternalProcess,
                Executable = "artwork-sidecar",
                Provider = "test-provider",
                Model = "test-image-model",
                Version = "test-image-model-v1"
            }
        };
        var provider = new ExternalArtworkProvider(
            processRunner,
            Options.Create(providerOptions),
            TimeProvider.System);
        const string privatePrompt = "do-not-leak-this-prompt";
        var request = new ArtworkGenerationRequest(
            Context,
            "Artist",
            "Track",
            new ArtworkCreativeBrief("dark", [], [], privatePrompt),
            3,
            2_048,
            2_048);

        var result = await provider.GenerateAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderFailureKind.Transient, result.Failure!.Kind);
        Assert.DoesNotContain("api-key", result.Failure.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(privatePrompt, processRunner.Arguments);
    }

    private sealed class CapturingFailedProcessRunner : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Arguments = arguments;
            return Task.FromResult(new ProcessExecutionResult(
                1,
                "",
                "api-key=super-secret",
                TimeSpan.FromMilliseconds(1)));
        }
    }
}
