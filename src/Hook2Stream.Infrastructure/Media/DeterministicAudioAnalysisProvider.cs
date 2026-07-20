using System.Buffers.Binary;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure.Providers;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Media;

public sealed class DeterministicAudioAnalysisProvider(
    IObjectStorage storage,
    IProcessRunner processRunner,
    IOptions<MediaToolsOptions> mediaOptions,
    IOptions<PipelineProviderOptions> providerOptions,
    TimeProvider timeProvider) : IAudioAnalysisProvider
{
    private const int SampleRate = 8_000;

    public async Task<ProviderResult<AudioAnalysisResult>> AnalyzeAsync(
        AudioAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        if (!Valid(request.Audio))
        {
            return Failed(
                request,
                startedAt,
                ProviderFailureKind.UserInput,
                "analysis.source_invalid",
                "The audio selected for analysis is not available.");
        }

        var workDirectory = Path.Combine(
            Path.GetTempPath(),
            "hook2stream-analysis",
            request.Context.OperationId.ToString("N"),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var sourcePath = Path.Combine(workDirectory, "source-audio");
        var pcmPath = Path.Combine(workDirectory, "analysis.pcm");

        try
        {
            await storage.DownloadAsync(request.Audio.ObjectKey, sourcePath, cancellationToken);
            var execution = await processRunner.RunAsync(
                mediaOptions.Value.FfmpegPath,
                [
                    "-y", "-v", "error", "-i", sourcePath,
                    "-map", "0:a:0", "-vn", "-ac", "1", "-ar", SampleRate.ToString(),
                    "-c:a", "pcm_s16le", "-f", "s16le", pcmPath
                ],
                TimeSpan.FromSeconds(providerOptions.Value.AudioAnalysis.TimeoutSeconds),
                workDirectory,
                cancellationToken);
            if (execution.ExitCode != 0 || !File.Exists(pcmPath))
            {
                return Failed(
                    request,
                    startedAt,
                    ProviderFailureKind.UserInput,
                    "analysis.audio_decode_failed",
                    "The uploaded audio could not be decoded for analysis.");
            }

            var bytes = await File.ReadAllBytesAsync(pcmPath, cancellationToken);
            if (bytes.Length < SampleRate * sizeof(short))
            {
                return Failed(
                    request,
                    startedAt,
                    ProviderFailureKind.UserInput,
                    "analysis.audio_too_short",
                    "The uploaded audio is too short to analyze.");
            }

            var sampleCount = bytes.Length / sizeof(short);
            var samples = new short[sampleCount];
            for (var index = 0; index < sampleCount; index++)
            {
                samples[index] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(index * sizeof(short), sizeof(short)));
            }

            var features = DeterministicPcmAnalyzer.Analyze(samples, SampleRate);
            var result = new AudioAnalysisResult(
                features.DurationMilliseconds,
                features.BeatsPerMinute,
                features.BeatMilliseconds,
                features.Sections,
                features.EnergyCurve,
                InstrumentalConfidence: 0.5,
                Artifacts: []);
            return ProviderResult<AudioAnalysisResult>.Succeeded(
                result,
                Provenance(request, startedAt, timeProvider.GetUtcNow()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Failed(
                request,
                startedAt,
                ProviderFailureKind.Transient,
                "analysis.processing_failed",
                "Audio analysis could not be completed. Try again.");
        }
        finally
        {
            TryDelete(workDirectory);
        }
    }

    private ProviderResult<AudioAnalysisResult> Failed(
        AudioAnalysisRequest request,
        DateTimeOffset startedAt,
        ProviderFailureKind kind,
        string code,
        string message) =>
        ProviderResult<AudioAnalysisResult>.Failed(
            new ProviderFailure(kind, code, message),
            Provenance(request, startedAt, timeProvider.GetUtcNow()));

    private static ProviderProvenance Provenance(
        AudioAnalysisRequest request,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt) =>
        new(
            "hook2stream.deterministic",
            "pcm-onset-energy",
            "deterministic-audio-v1",
            request.Context.OperationId.ToString("N"),
            request.Context.InputHash,
            request.Context.ParameterHash,
            startedAt,
            completedAt);

    private static bool Valid(ProviderObjectReference source) =>
        source.AssetId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(source.ObjectKey) &&
        source.SizeBytes > 0 &&
        !string.IsNullOrWhiteSpace(source.Sha256) &&
        source.Sha256.Length == 64 &&
        source.Sha256.All(Uri.IsHexDigit);

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Temporary analysis data is removed by the host lifecycle.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary analysis data is removed by the host lifecycle.
        }
    }
}

public sealed record DeterministicAudioFeatures(
    long DurationMilliseconds,
    double BeatsPerMinute,
    IReadOnlyList<long> BeatMilliseconds,
    IReadOnlyList<AudioSection> Sections,
    IReadOnlyList<EnergyPoint> EnergyCurve);

public static class DeterministicPcmAnalyzer
{
    private const int FramesPerSecond = 20;
    private const double SilenceFloor = 0.0005;

    public static DeterministicAudioFeatures Analyze(IReadOnlyList<short> samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (samples.Count < sampleRate) throw new ArgumentException("At least one second of PCM audio is required.", nameof(samples));

        var frameSize = Math.Max(1, sampleRate / FramesPerSecond);
        var frameCount = (samples.Count + frameSize - 1) / frameSize;
        var rms = new double[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var start = frame * frameSize;
            var end = Math.Min(samples.Count, start + frameSize);
            var sumSquares = 0d;
            for (var index = start; index < end; index++)
            {
                var normalized = samples[index] / 32768d;
                sumSquares += normalized * normalized;
            }

            rms[frame] = Math.Sqrt(sumSquares / Math.Max(1, end - start));
        }

        var duration = (long)Math.Round(samples.Count * 1000d / sampleRate, MidpointRounding.AwayFromZero);
        var energy = BuildEnergyCurve(rms, duration);
        if (rms.Max() < SilenceFloor)
        {
            return new DeterministicAudioFeatures(
                duration,
                0,
                [],
                BuildSections(duration, energy),
                energy);
        }

        var onset = BuildOnsetEnvelope(rms);
        var tempo = EstimateTempo(onset);
        var beats = tempo <= 0 ? [] : BuildBeatGrid(onset, tempo, duration);
        return new DeterministicAudioFeatures(
            duration,
            Math.Round(tempo, 2),
            beats,
            BuildSections(duration, energy),
            energy);
    }

    private static IReadOnlyList<EnergyPoint> BuildEnergyCurve(IReadOnlyList<double> rms, long duration)
    {
        var bucketFrames = FramesPerSecond;
        var points = new List<EnergyPoint>((rms.Count + bucketFrames - 1) / bucketFrames + 1);
        for (var start = 0; start < rms.Count; start += bucketFrames)
        {
            var count = Math.Min(bucketFrames, rms.Count - start);
            var value = 0d;
            for (var index = start; index < start + count; index++) value += rms[index] * rms[index];
            points.Add(new EnergyPoint(
                start * 1000L / FramesPerSecond,
                Math.Round(Math.Sqrt(value / count), 6)));
        }

        if (points.Count == 0 || points[^1].AtMilliseconds != duration)
        {
            points.Add(new EnergyPoint(duration, points.Count == 0 ? 0 : points[^1].Energy));
        }

        return points;
    }

    private static double[] BuildOnsetEnvelope(IReadOnlyList<double> rms)
    {
        var onset = new double[rms.Count];
        var prior = 0d;
        for (var index = 0; index < rms.Count; index++)
        {
            var start = Math.Max(0, index - 8);
            var count = Math.Max(1, index - start);
            var local = 0d;
            for (var priorIndex = start; priorIndex < index; priorIndex++) local += rms[priorIndex];
            prior = index == 0 ? rms[index] : local / count;
            onset[index] = Math.Max(0, rms[index] - prior);
        }

        var peak = onset.Max();
        if (peak > 0)
        {
            for (var index = 0; index < onset.Length; index++) onset[index] /= peak;
        }

        return onset;
    }

    private static double EstimateTempo(IReadOnlyList<double> onset)
    {
        var bestScore = 0d;
        var bestLag = 0;
        var minimumLag = (int)Math.Floor(60d * FramesPerSecond / 200d);
        var maximumLag = (int)Math.Ceiling(60d * FramesPerSecond / 60d);
        for (var lag = minimumLag; lag <= maximumLag && lag < onset.Count; lag++)
        {
            var score = 0d;
            for (var index = lag; index < onset.Count; index++) score += onset[index] * onset[index - lag];
            score /= Math.Max(1, onset.Count - lag);
            var bpm = 60d * FramesPerSecond / lag;
            var tempoPrior = 0.92 + 0.08 * Math.Exp(-Math.Pow((bpm - 120) / 45d, 2));
            score *= tempoPrior;
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        return bestScore < 0.002 || bestLag == 0
            ? 0
            : 60d * FramesPerSecond / bestLag;
    }

    private static IReadOnlyList<long> BuildBeatGrid(
        IReadOnlyList<double> onset,
        double tempo,
        long duration)
    {
        var intervalFrames = Math.Max(1, (int)Math.Round(60d * FramesPerSecond / tempo));
        var phase = 0;
        var bestScore = double.MinValue;
        for (var candidate = 0; candidate < intervalFrames; candidate++)
        {
            var score = 0d;
            for (var frame = candidate; frame < onset.Count; frame += intervalFrames) score += onset[frame];
            if (score > bestScore)
            {
                bestScore = score;
                phase = candidate;
            }
        }

        var intervalMilliseconds = 60_000d / tempo;
        var first = phase * 1000d / FramesPerSecond;
        var beats = new List<long>();
        for (var at = first; at < duration; at += intervalMilliseconds)
        {
            beats.Add((long)Math.Round(at, MidpointRounding.AwayFromZero));
        }

        return beats;
    }

    private static IReadOnlyList<AudioSection> BuildSections(
        long duration,
        IReadOnlyList<EnergyPoint> energy)
    {
        string[] kinds = ["intro", "verse", "chorus", "drop", "outro"];
        var sectionEnergy = new double[kinds.Length];
        var average = energy.Count == 0 ? 0 : energy.Average(point => point.Energy);
        for (var index = 0; index < kinds.Length; index++)
        {
            var start = duration * index / kinds.Length;
            var end = index == kinds.Length - 1 ? duration : duration * (index + 1) / kinds.Length;
            sectionEnergy[index] = energy
                .Where(point => point.AtMilliseconds >= start && point.AtMilliseconds < end)
                .Select(point => point.Energy)
                .DefaultIfEmpty(average)
                .Average();
        }

        var rankedMiddle = Enumerable.Range(1, 3).OrderBy(index => sectionEnergy[index]).ToArray();
        kinds[rankedMiddle[0]] = "verse";
        kinds[rankedMiddle[1]] = "chorus";
        kinds[rankedMiddle[2]] = "drop";

        var sections = new AudioSection[kinds.Length];
        for (var index = 0; index < kinds.Length; index++)
        {
            var start = duration * index / kinds.Length;
            var end = index == kinds.Length - 1 ? duration : duration * (index + 1) / kinds.Length;
            var confidence = average <= 0
                ? 0.5
                : Math.Clamp(0.6 + Math.Abs(sectionEnergy[index] - average) / average * 0.2, 0.6, 0.9);
            sections[index] = new AudioSection(kinds[index], start, end, Math.Round(confidence, 3));
        }

        return sections;
    }
}
