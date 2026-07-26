using System.Globalization;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Providers;

public sealed class OpenRouterTranscriptionProvider(
    OpenRouterClient client,
    IObjectStorage storage,
    IProcessRunner processRunner,
    IOptions<OpenRouterOptions> options,
    IOptions<MediaToolsOptions> mediaOptions,
    TimeProvider timeProvider) : ITranscriptionProvider
{
    private readonly OpenRouterOptions _options = options.Value;
    private readonly MediaToolsOptions _mediaOptions = mediaOptions.Value;

    public async Task<ProviderResult<TranscriptionResult>> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var language = request.Language.Trim().ToLowerInvariant();
        if (language is not ("ru" or "en"))
        {
            return Failed(
                request.Context,
                startedAt,
                new ProviderFailure(
                    ProviderFailureKind.UserInput,
                    "openrouter.language_not_supported",
                    "Automatic transcription currently supports Russian and English."));
        }

        if (!HasValidSource(request.Audio))
        {
            return Failed(
                request.Context,
                startedAt,
                new ProviderFailure(
                    ProviderFailureKind.UserInput,
                    "provider.invalid_source",
                    "The source artifact is missing required immutable metadata."));
        }

        if (request.InstrumentalHint == true)
        {
            return ProviderResult<TranscriptionResult>.Succeeded(
                new TranscriptionResult(language, 1, true, false, [], []),
                Provenance(request.Context, startedAt, [], [], null, null, []));
        }

        var workDirectory = Path.Combine(
            string.IsNullOrWhiteSpace(_mediaOptions.WorkRoot)
                ? Path.GetTempPath()
                : Path.GetFullPath(_mediaOptions.WorkRoot),
            "hook2stream-openrouter-stt",
            request.Context.OperationId.ToString("N"),
            Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(workDirectory, "source-audio");
        var requestIds = new List<string>();
        var generationIds = new List<string>();
        var usages = new List<ProviderUsage>();
        string? resolvedModel = null;
        string? resolvedProvider = null;
        var phrases = new List<RawPhrase>();
        var languageConfidence = 0.5;

        try
        {
            Directory.CreateDirectory(workDirectory);
            await storage.DownloadAsync(request.Audio.ObjectKey, sourcePath, cancellationToken);
            var durationMilliseconds = request.Audio.DurationMilliseconds ??
                await ProbeDurationAsync(sourcePath, workDirectory, cancellationToken);
            if (durationMilliseconds is <= 0 or > 600_000)
            {
                return Failed(
                    request.Context,
                    startedAt,
                    new ProviderFailure(
                        ProviderFailureKind.UserInput,
                        "openrouter.audio_duration_invalid",
                        "Automatic transcription accepts audio up to ten minutes long."));
            }

            var chunkLength = _options.TranscriptionChunkSeconds * 1_000L;
            var overlap = _options.TranscriptionOverlapSeconds * 1_000L;
            var step = chunkLength - overlap;
            var chunkIndex = 0;
            for (var start = 0L; start < durationMilliseconds; start += step, chunkIndex++)
            {
                var length = Math.Min(chunkLength, durationMilliseconds - start);
                var chunkPath = Path.Combine(workDirectory, $"chunk-{chunkIndex:D3}.wav");
                var extraction = await processRunner.RunAsync(
                    _mediaOptions.FfmpegPath,
                    [
                        "-y", "-v", "error",
                        "-ss", Seconds(start),
                        "-i", sourcePath,
                        "-t", Seconds(length),
                        "-vn", "-ac", "1", "-ar", "16000",
                        "-c:a", "pcm_s16le", chunkPath
                    ],
                    TimeSpan.FromSeconds(_mediaOptions.ProcessTimeoutSeconds),
                    workDirectory,
                    cancellationToken);
                if (extraction.ExitCode != 0 || !File.Exists(chunkPath))
                {
                    return Failed(
                        request.Context,
                        startedAt,
                        new ProviderFailure(
                            ProviderFailureKind.Transient,
                            "openrouter.audio_chunk_failed",
                            "The audio could not be prepared for transcription."));
                }

                var audioBytes = await File.ReadAllBytesAsync(chunkPath, cancellationToken);
                var payload = new
                {
                    model = _options.TranscriptionModel,
                    input_audio = new
                    {
                        data = Convert.ToBase64String(audioBytes),
                        format = "wav"
                    },
                    language,
                    temperature = 0,
                    response_format = "verbose_json",
                    timestamp_granularities = new[] { "word" }
                };
                var response = await client.PostJsonAsync(
                    "audio/transcriptions",
                    payload,
                    $"{request.Context.OperationId:N}:stt:{chunkIndex}",
                    _options.TranscriptionTimeoutSeconds,
                    outcomeCanBeRetried: true,
                    cancellationToken);
                if (!response.IsSuccess)
                {
                    return Failed(request.Context, startedAt, response.Failure!, requestIds, generationIds, resolvedModel, resolvedProvider, usages);
                }

                if (response.RequestId is not null) requestIds.Add(response.RequestId);
                if (response.GenerationId is not null) generationIds.Add(response.GenerationId);
                using var json = JsonDocument.Parse(response.Body);
                resolvedModel ??= OpenRouterProviderData.String(json.RootElement, "model");
                resolvedProvider ??= OpenRouterProviderData.String(json.RootElement, "provider");
                usages.Add(OpenRouterProviderData.Usage(json.RootElement));
                languageConfidence = Math.Min(
                    languageConfidence == 0.5 ? 1 : languageConfidence,
                    Confidence(json.RootElement, "language_confidence", 0.5));
                ParseChunk(
                    json.RootElement,
                    chunkIndex,
                    start,
                    length,
                    overlap,
                    phrases);
            }

            var materialized = Materialize(request.Context, phrases, durationMilliseconds);
            var transcriptBytes = JsonSerializer.SerializeToUtf8Bytes(materialized, OpenRouterProviderData.Json);
            var artifact = new ProviderArtifactManifest(
                OpenRouterProviderData.StableId(request.Context.OperationId, "transcript"),
                "transcript",
                $"{request.Context.StagingPrefix.Trim().Trim('/')}/transcript.json",
                OpenRouterProviderData.Sha256(transcriptBytes),
                "application/json",
                transcriptBytes.LongLength,
                Materialized: false);
            var result = new TranscriptionResult(
                language,
                languageConfidence,
                materialized.Count == 0,
                UsedFallbackAudio: false,
                materialized,
                [artifact]);
            return ProviderResult<TranscriptionResult>.Succeeded(
                result,
                Provenance(
                    request.Context,
                    startedAt,
                    requestIds,
                    generationIds,
                    resolvedModel,
                    resolvedProvider,
                    usages));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return Failed(
                request.Context,
                startedAt,
                new ProviderFailure(
                    ProviderFailureKind.Transient,
                    "openrouter.audio_prepare_timeout",
                    "The audio preparation step timed out."),
                requestIds,
                generationIds,
                resolvedModel,
                resolvedProvider,
                usages);
        }
        catch (JsonException)
        {
            return Failed(
                request.Context,
                startedAt,
                new ProviderFailure(
                    ProviderFailureKind.Permanent,
                    "openrouter.transcription_response_invalid",
                    "OpenRouter returned an invalid transcription response."),
                requestIds,
                generationIds,
                resolvedModel,
                resolvedProvider,
                usages);
        }
        catch (IOException)
        {
            return Failed(
                request.Context,
                startedAt,
                new ProviderFailure(
                    ProviderFailureKind.Transient,
                    "openrouter.transcription_io_failure",
                    "The transcription audio could not be staged."),
                requestIds,
                generationIds,
                resolvedModel,
                resolvedProvider,
                usages);
        }
        finally
        {
            OpenRouterProviderData.TryDelete(workDirectory);
        }
    }

    private async Task<long> ProbeDurationAsync(
        string sourcePath,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var probe = await processRunner.RunAsync(
            _mediaOptions.FfprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "json", sourcePath],
            TimeSpan.FromSeconds(_mediaOptions.ProcessTimeoutSeconds),
            workDirectory,
            cancellationToken);
        if (probe.ExitCode != 0) return 0;
        using var json = JsonDocument.Parse(probe.StandardOutput);
        if (!json.RootElement.TryGetProperty("format", out var format) ||
            !format.TryGetProperty("duration", out var duration) ||
            !double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return 0;
        }

        return (long)Math.Round(seconds * 1_000, MidpointRounding.AwayFromZero);
    }

    private static void ParseChunk(
        JsonElement root,
        int chunkIndex,
        long chunkStart,
        long chunkLength,
        long overlap,
        ICollection<RawPhrase> destination)
    {
        var cutoff = chunkIndex == 0 ? 0 : overlap;
        var rootWords = ParseWords(root, chunkStart, cutoff);
        var initialPhraseCount = destination.Count;
        if (root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segments.EnumerateArray())
            {
                var start = Milliseconds(segment, "start", 0);
                var end = Milliseconds(segment, "end", chunkLength);
                if (end <= cutoff) continue;
                start = Math.Max(start, cutoff);
                var absoluteStart = chunkStart + start;
                var absoluteEnd = chunkStart + Math.Max(end, start + 1);
                var text = OpenRouterProviderData.String(segment, "text")?.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                var words = ParseWords(segment, chunkStart, cutoff);
                if (words.Count == 0)
                {
                    words = rootWords
                        .Where(word => word.StartMilliseconds < absoluteEnd && word.EndMilliseconds > absoluteStart)
                        .ToList();
                }

                destination.Add(new RawPhrase(
                    text,
                    absoluteStart,
                    absoluteEnd,
                    Confidence(segment, "confidence", LogProbabilityConfidence(segment)),
                    words));
            }

            if (destination.Count > initialPhraseCount) return;
        }

        var chunkText = OpenRouterProviderData.String(root, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(chunkText)) return;
        var previousText = destination.LastOrDefault()?.Text;
        chunkText = RemoveOverlap(previousText, chunkText);
        if (string.IsNullOrWhiteSpace(chunkText)) return;
        destination.Add(new RawPhrase(
            chunkText,
            chunkStart + cutoff,
            chunkStart + chunkLength,
            0.5,
            rootWords));
    }

    private static List<RawWord> ParseWords(JsonElement root, long chunkStart, long cutoff)
    {
        var result = new List<RawWord>();
        if (!root.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array) return result;
        foreach (var word in words.EnumerateArray())
        {
            var text = (OpenRouterProviderData.String(word, "word") ??
                        OpenRouterProviderData.String(word, "text"))?.Trim();
            var start = Milliseconds(word, "start", 0);
            var end = Milliseconds(word, "end", start + 1);
            if (string.IsNullOrWhiteSpace(text) || end <= cutoff) continue;
            result.Add(new RawWord(
                text,
                chunkStart + Math.Max(start, cutoff),
                chunkStart + Math.Max(end, Math.Max(start, cutoff) + 1),
                Confidence(word, "confidence", Confidence(word, "probability", 0.5))));
        }

        return result;
    }

    private static IReadOnlyList<TranscriptionPhrase> Materialize(
        ProviderExecutionContext context,
        IReadOnlyList<RawPhrase> source,
        long durationMilliseconds)
    {
        var result = new List<TranscriptionPhrase>();
        long lastEnd = 0;
        foreach (var raw in source.OrderBy(value => value.StartMilliseconds).ThenBy(value => value.EndMilliseconds))
        {
            var start = Math.Clamp(raw.StartMilliseconds, lastEnd, durationMilliseconds);
            if (start >= durationMilliseconds) continue;
            var end = Math.Clamp(raw.EndMilliseconds, start + 1, durationMilliseconds);
            var words = raw.Words
                .Where(value => value.EndMilliseconds > start && value.StartMilliseconds < end)
                .Select((word, index) => new TranscriptionWord(
                    OpenRouterProviderData.StableId(
                        context.OperationId,
                        $"phrase:{result.Count}:word:{index}:{word.StartMilliseconds}:{word.Text}"),
                    word.Text,
                    Math.Clamp(word.StartMilliseconds, start, end - 1),
                    Math.Clamp(word.EndMilliseconds, Math.Max(start + 1, word.StartMilliseconds + 1), end),
                    Math.Clamp(word.Confidence, 0, 1)))
                .ToArray();
            var confidence = words.Length == 0
                ? Math.Clamp(raw.Confidence, 0, 1)
                : Math.Min(Math.Clamp(raw.Confidence, 0, 1), words.Min(word => word.Confidence));
            result.Add(new TranscriptionPhrase(
                OpenRouterProviderData.StableId(
                    context.OperationId,
                    $"phrase:{result.Count}:{start}:{raw.Text}"),
                raw.Text,
                start,
                end,
                confidence,
                words));
            lastEnd = end;
        }

        return result;
    }

    private static string RemoveOverlap(string? previous, string current)
    {
        if (string.IsNullOrWhiteSpace(previous)) return current;
        var previousWords = previous.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentWords = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var maximum = Math.Min(12, Math.Min(previousWords.Length, currentWords.Length));
        for (var count = maximum; count > 0; count--)
        {
            if (previousWords[^count..].SequenceEqual(currentWords[..count], StringComparer.OrdinalIgnoreCase))
            {
                return string.Join(' ', currentWords[count..]);
            }
        }

        return current;
    }

    private static long Milliseconds(JsonElement root, string property, long fallback)
    {
        if (!root.TryGetProperty(property, out var value) || !value.TryGetDouble(out var seconds)) return fallback;
        return (long)Math.Round(seconds * 1_000, MidpointRounding.AwayFromZero);
    }

    private static double Confidence(JsonElement root, string property, double fallback) =>
        root.TryGetProperty(property, out var value) && value.TryGetDouble(out var result)
            ? Math.Clamp(result, 0, 1)
            : fallback;

    private static double LogProbabilityConfidence(JsonElement root)
    {
        if (!root.TryGetProperty("avg_logprob", out var value) || !value.TryGetDouble(out var logProbability))
        {
            return 0.5;
        }

        return Math.Clamp(Math.Exp(logProbability), 0, 1);
    }

    private static string Seconds(long milliseconds) =>
        (milliseconds / 1_000d).ToString("0.###", CultureInfo.InvariantCulture);

    private static bool HasValidSource(ProviderObjectReference source) =>
        source.AssetId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(source.ObjectKey) &&
        source.SizeBytes > 0 &&
        source.Sha256.Length == 64 &&
        source.Sha256.All(Uri.IsHexDigit);

    private ProviderResult<TranscriptionResult> Failed(
        ProviderExecutionContext context,
        DateTimeOffset startedAt,
        ProviderFailure failure,
        IReadOnlyCollection<string>? requestIds = null,
        IReadOnlyCollection<string>? generationIds = null,
        string? resolvedModel = null,
        string? resolvedProvider = null,
        IReadOnlyCollection<ProviderUsage>? usages = null) =>
        ProviderResult<TranscriptionResult>.Failed(
            failure,
            Provenance(
                context,
                startedAt,
                requestIds ?? [],
                generationIds ?? [],
                resolvedModel,
                resolvedProvider,
                usages ?? []));

    private ProviderProvenance Provenance(
        ProviderExecutionContext context,
        DateTimeOffset startedAt,
        IReadOnlyCollection<string> requestIds,
        IReadOnlyCollection<string> generationIds,
        string? resolvedModel,
        string? resolvedProvider,
        IReadOnlyCollection<ProviderUsage> usages) =>
        new(
            "openrouter",
            resolvedModel ?? _options.TranscriptionModel,
            "stt-api-v1",
            requestIds.Count == 0 ? null : string.Join(',', requestIds),
            context.InputHash,
            context.ParameterHash,
            startedAt,
            timeProvider.GetUtcNow(),
            _options.TranscriptionModel,
            resolvedProvider,
            generationIds.Count == 0 ? null : string.Join(',', generationIds),
            OpenRouterProviderData.Sum(usages));

    private sealed record RawPhrase(
        string Text,
        long StartMilliseconds,
        long EndMilliseconds,
        double Confidence,
        IReadOnlyList<RawWord> Words);

    private sealed record RawWord(
        string Text,
        long StartMilliseconds,
        long EndMilliseconds,
        double Confidence);
}
