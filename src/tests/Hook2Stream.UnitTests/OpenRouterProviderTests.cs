using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class OpenRouterProviderTests
{
    private static readonly ProviderExecutionContext Context = new(
        Guid.Parse("01900000-0000-7000-8000-000000000701"),
        new string('a', 64),
        new string('b', 64),
        "staging/openrouter-test");

    [Fact]
    public async Task Transcription_chunks_audio_and_merges_verbose_timestamps()
    {
        var handler = new CapturingHandler(call => Json(new
        {
            model = "openai/whisper-large-v3",
            provider = "groq",
            text = $"chunk {call}",
            language_confidence = 0.9,
            segments = new[]
            {
                new
                {
                    start = call == 1 ? 0 : 2,
                    end = call == 1 ? 49 : call == 2 ? 49 : 4,
                    text = $"phrase {call}",
                    confidence = 0.8,
                    words = new[]
                    {
                        new { word = $"word{call}", start = call == 1 ? 0 : 2, end = call == 1 ? 1 : 3, probability = 0.9 }
                    }
                }
            },
            usage = new { seconds = 50, input_tokens = 2, output_tokens = 3, total_tokens = 5, cost = 0.001m }
        }, $"generation-{call}"));
        var options = Options();
        var provider = new OpenRouterTranscriptionProvider(
            new OpenRouterClient(new HttpClient(handler), options, TimeProvider.System),
            new FakeStorage(),
            new MaterializingProcessRunner(),
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(new MediaToolsOptions()),
            TimeProvider.System);
        var audio = new ProviderObjectReference(
            Guid.NewGuid(),
            "audio/source.mp3",
            new string('c', 64),
            "audio/mpeg",
            4_000_000,
            100_000);

        var result = await provider.TranscribeAsync(
            new TranscriptionRequest(Context, audio, audio, "en"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([0, 50_000, 98_000], result.Value!.Phrases.Select(value => value.StartMilliseconds));
        Assert.Equal(15, result.Provenance.Usage!.TotalTokens);
        Assert.Equal(0.003m, result.Provenance.Usage.CostUsd);
        Assert.Equal("groq", result.Provenance.ResolvedProvider);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("openai/whisper-large-v3", request.GetProperty("model").GetString());
            Assert.Equal("verbose_json", request.GetProperty("response_format").GetString());
            Assert.True(request.GetProperty("provider").GetProperty("zdr").GetBoolean());
            Assert.Equal("deny", request.GetProperty("provider").GetProperty("data_collection").GetString());
        });
    }

    [Fact]
    public async Task Transcription_without_upstream_timestamps_remains_flagged_for_manual_review()
    {
        var handler = new CapturingHandler(call => Json(new
        {
            text = "The line needs review",
            usage = new { seconds = 40 }
        }, $"plain-{call}"));
        var options = Options();
        var provider = new OpenRouterTranscriptionProvider(
            new OpenRouterClient(new HttpClient(handler), options, TimeProvider.System),
            new FakeStorage(),
            new MaterializingProcessRunner(),
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(new MediaToolsOptions()),
            TimeProvider.System);
        var audio = new ProviderObjectReference(
            Guid.NewGuid(),
            "audio/source.mp3",
            new string('c', 64),
            "audio/mpeg",
            2_000_000,
            40_000);

        var result = await provider.TranscribeAsync(
            new TranscriptionRequest(Context, audio, null, "en"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var phrase = Assert.Single(result.Value!.Phrases);
        Assert.Equal(0.5, phrase.Confidence);
        Assert.Empty(phrase.Words);
        Assert.Equal(0, phrase.StartMilliseconds);
        Assert.Equal(40_000, phrase.EndMilliseconds);
    }

    [Fact]
    public async Task Artwork_uses_separate_reference_requests_and_materializes_exact_png_dimensions()
    {
        var handler = new CapturingHandler(call => Json(new
        {
            model = "bytedance-seed/seedream-4.5",
            provider = "bytedance",
            data = new[]
            {
                new { b64_json = Convert.ToBase64String(Png(128, 128)), media_type = "image/png" }
            },
            usage = new { prompt_tokens = 1, completion_tokens = 2, total_tokens = 3, cost = 0.05m }
        }, $"image-{call}"));
        var storage = new FakeStorage();
        var options = Options();
        var provider = new OpenRouterArtworkProvider(
            new OpenRouterClient(new HttpClient(handler), options, TimeProvider.System),
            storage,
            new MaterializingProcessRunner(),
            options,
            new MediaToolsOptions(),
            TimeProvider.System);
        var reference = new ProviderObjectReference(
            Guid.NewGuid(),
            "artwork/approved.png",
            new string('d', 64),
            "image/png",
            Png(2_048, 2_048).Length,
            Width: 2_048,
            Height: 2_048);

        var result = await provider.GenerateAsync(
            new ArtworkGenerationRequest(
                Context,
                "Artist",
                "Track",
                new ArtworkCreativeBrief("dark", ["#112233"], ["short lyric"], "abstract"),
                3,
                1_088,
                1_920,
                reference),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, storage.Uploaded.Count);
        Assert.All(result.Value!.Candidates, candidate =>
        {
            Assert.True(candidate.Artwork.Materialized);
            Assert.Equal(1_088, candidate.Artwork.Width);
            Assert.Equal(1_920, candidate.Artwork.Height);
        });
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("bytedance-seed/seedream-4.5", request.GetProperty("model").GetString());
            Assert.Equal("2K", request.GetProperty("resolution").GetString());
            Assert.Equal("9:16", request.GetProperty("aspect_ratio").GetString());
            Assert.True(request.GetProperty("provider").GetProperty("zdr").GetBoolean());
            Assert.Equal("deny", request.GetProperty("provider").GetProperty("data_collection").GetString());
            Assert.True(request.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
            var dataUrl = request.GetProperty("input_references")[0]
                .GetProperty("image_url").GetProperty("url").GetString();
            Assert.StartsWith("data:image/png;base64,", dataUrl, StringComparison.Ordinal);
        });
        Assert.Equal(0.15m, result.Provenance.Usage!.CostUsd);
    }

    [Fact]
    public async Task Campaign_model_only_supplies_copy_for_server_owned_canonical_slots()
    {
        var handler = new CapturingHandler(call => Json(new
        {
            id = $"chat-{call}",
            model = "openai/gpt-oss-120b",
            provider = "fireworks",
            choices = new[]
            {
                new { message = new { content = CampaignCopy() } }
            },
            usage = new { input_tokens = 100, output_tokens = 200, total_tokens = 300, cost = 0.02m }
        }, $"campaign-{call}"));
        var options = Options();
        var planner = new OpenRouterCampaignPlanner(
            new OpenRouterClient(new HttpClient(handler), options, TimeProvider.System),
            Microsoft.Extensions.Options.Options.Create(options),
            TimeProvider.System);
        var hooks = Enumerable.Range(0, 3)
            .Select(index => new CampaignHookInput(
                Guid.NewGuid(),
                $"Hook {index + 1}",
                index * 20_000,
                index * 20_000 + 15_000,
                $"Excerpt {index + 1}"))
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

        var result = await planner.PlanAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(handler.Requests);
        Assert.True(CampaignPlanContractValidator.Validate(request, result.Value!.Items).IsValid);
        Assert.Equal(
            CampaignPlanContractValidator.CanonicalSlots(false).Select(value => value.RelativeDay),
            result.Value.Items.Select(value => value.RelativeDay));
        var body = handler.Requests[0];
        Assert.Equal("openai/gpt-oss-120b", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.True(body.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
        Assert.Equal("deny", body.GetProperty("provider").GetProperty("data_collection").GetString());
        Assert.Equal("fireworks", result.Provenance.ResolvedProvider);
    }

    [Fact]
    public async Task Campaign_performs_only_one_schema_repair_request()
    {
        var handler = new CapturingHandler(call => Json(new
        {
            id = $"repair-{call}",
            choices = new[]
            {
                new { message = new { content = call == 1 ? "{}" : CampaignCopy() } }
            },
            usage = new { total_tokens = 10, cost = 0.001m }
        }, $"repair-{call}"));
        var options = Options();
        var planner = new OpenRouterCampaignPlanner(
            new OpenRouterClient(new HttpClient(handler), options, TimeProvider.System),
            Microsoft.Extensions.Options.Options.Create(options),
            TimeProvider.System);
        var hooks = Enumerable.Range(0, 3)
            .Select(index => new CampaignHookInput(
                Guid.NewGuid(),
                $"Hook {index + 1}",
                index * 20_000,
                index * 20_000 + 15_000,
                $"Excerpt {index + 1}"))
            .ToArray();

        var result = await planner.PlanAsync(
            new CampaignPlanningRequest(
                Context,
                "Artist",
                "Track",
                null,
                true,
                "direct",
                "Listen now",
                hooks,
                []),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(20, result.Provenance.Usage!.TotalTokens);
    }

    [Fact]
    public async Task Client_retries_confirmed_gateway_timeout_with_same_bounded_operation()
    {
        var handler = new CapturingHandler(call =>
        {
            if (call > 1) return Json(new { ok = true }, "retry-success");
            var response = new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        });
        var options = Options();
        options.MaxRetries = 1;
        var client = new OpenRouterClient(new HttpClient(handler), options, TimeProvider.System);

        var result = await client.PostJsonAsync(
            "chat/completions",
            new { model = options.CampaignModel, messages = Array.Empty<object>() },
            "one-logical-operation",
            30,
            outcomeCanBeRetried: true,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("OpenRouter:BaseUrl", "https://api.openai.com/v1/")]
    [InlineData("OpenRouter:TranscriptionModel", "openai/whisper-large-v3-turbo")]
    [InlineData("OpenRouter:ImageModel", "openai/gpt-image-1")]
    [InlineData("OpenRouter:CampaignModel", "openrouter/auto")]
    [InlineData("OpenRouter:RequireZeroDataRetention", "false")]
    [InlineData("OpenRouter:DenyDataCollection", "false")]
    [InlineData("OpenRouter:RequireParameters", "false")]
    public void Registration_rejects_policy_escape_hatches(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();
        using var services = new ServiceCollection()
            .AddHook2StreamPipelineProviders(configuration, allowFixtureProviders: true)
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<OpenRouterOptions>>().Value);
    }

    [Fact]
    public void Production_registration_rejects_external_process_for_ai_stage()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PipelineProviders:AudioAnalysis:Mode"] = "Deterministic",
                ["PipelineProviders:Transcription:Mode"] = "ExternalProcess",
                ["PipelineProviders:Transcription:Executable"] = "legacy-whisper-sidecar",
                ["PipelineProviders:Transcription:Provider"] = "legacy",
                ["PipelineProviders:Transcription:Model"] = "whisper",
                ["PipelineProviders:Transcription:Version"] = "v1",
                ["PipelineProviders:Artwork:Mode"] = "OpenRouter",
                ["PipelineProviders:CampaignPlanning:Mode"] = "OpenRouter",
                ["PipelineProviders:VideoRendering:Mode"] = "Deterministic"
            })
            .Build();
        using var services = new ServiceCollection()
            .AddHook2StreamPipelineProviders(configuration, allowFixtureProviders: false)
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<PipelineProviderOptions>>().Value);
    }

    private static OpenRouterOptions Options() => new()
    {
        ApiKey = "test-openrouter-key",
        MaxRetries = 0
    };

    private static string CampaignCopy() => JsonSerializer.Serialize(new
    {
        items = Enumerable.Range(1, 18).Select(sequence => new
        {
            sequence,
            headline = $"Headline {sequence}",
            caption = $"Caption {sequence}",
            hashtags = new[] { "#NewMusic", "#Track" },
            neutral = $"Neutral {sequence}",
            emotional = $"Emotional {sequence}",
            destinations = new
            {
                tiktok = $"TikTok {sequence}",
                youtubeShorts = $"YouTube {sequence}",
                instagramReels = $"Instagram {sequence}",
                vkClips = $"VK {sequence}"
            }
        })
    });

    private static HttpResponseMessage Json(object body, string generationId)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("x-generation-id", generationId);
        response.Headers.TryAddWithoutValidation("x-request-id", $"request-{generationId}");
        return response;
    }

    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[24];
        new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private sealed class CapturingHandler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            Requests.Add(json.RootElement.Clone());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-openrouter-key", request.Headers.Authorization?.Parameter);
            return response(Requests.Count);
        }
    }

    private sealed class MaterializingProcessRunner : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var output = arguments[^1];
            if (output.EndsWith(".png", StringComparison.Ordinal))
            {
                var filter = arguments.First(value => value.StartsWith("scale=", StringComparison.Ordinal));
                var crop = filter[(filter.IndexOf("crop=", StringComparison.Ordinal) + 5)..].Split(':');
                File.WriteAllBytes(output, Png(int.Parse(crop[0]), int.Parse(crop[1])));
            }
            else
            {
                File.WriteAllBytes(output, "RIFF-test-wave"u8.ToArray());
            }

            return Task.FromResult(new ProcessExecutionResult(0, "", "", TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class FakeStorage : IObjectStorage
    {
        public Dictionary<string, byte[]> Uploaded { get; } = [];

        public Task EnsureBucketAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StorageObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult<StorageObjectInfo?>(null);
        public Task UploadAsync(string objectKey, string sourcePath, string contentType, CancellationToken cancellationToken)
        {
            Uploaded[objectKey] = File.ReadAllBytes(sourcePath);
            return Task.CompletedTask;
        }
        public Task DownloadAsync(string objectKey, string destinationPath, CancellationToken cancellationToken)
        {
            File.WriteAllBytes(
                destinationPath,
                objectKey.EndsWith(".png", StringComparison.Ordinal) ? Png(2_048, 2_048) : "source-audio"u8.ToArray());
            return Task.CompletedTask;
        }
        public Task<Uri> CreateUploadUrlAsync(string objectKey, string contentType, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Uri> CreateReadUrlAsync(string objectKey, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MultipartUpload> CreateMultipartUploadAsync(string objectKey, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Uri> CreateMultipartPartUploadUrlAsync(string objectKey, string uploadId, int partNumber, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteMultipartUploadAsync(string objectKey, string uploadId, IReadOnlyList<MultipartPart> parts, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortMultipartUploadAsync(string objectKey, string uploadId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
