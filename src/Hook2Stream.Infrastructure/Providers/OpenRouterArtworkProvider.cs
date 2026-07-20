using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Providers;

public sealed class OpenRouterArtworkProvider : IArtworkProvider
{
    private const int MaximumEncodedImageCharacters = 80_000_000;
    private readonly OpenRouterClient _client;
    private readonly IObjectStorage _storage;
    private readonly IProcessRunner _processRunner;
    private readonly OpenRouterOptions _options;
    private readonly MediaToolsOptions _mediaOptions;
    private readonly TimeProvider _timeProvider;

    public OpenRouterArtworkProvider(
        OpenRouterClient client,
        IObjectStorage storage,
        IProcessRunner processRunner,
        IOptions<OpenRouterOptions> options,
        IOptions<MediaToolsOptions> mediaOptions,
        TimeProvider timeProvider)
        : this(client, storage, processRunner, options.Value, mediaOptions.Value, timeProvider)
    {
    }

    public OpenRouterArtworkProvider(
        OpenRouterClient client,
        IObjectStorage storage,
        IProcessRunner processRunner,
        OpenRouterOptions options,
        MediaToolsOptions mediaOptions,
        TimeProvider timeProvider)
    {
        _client = client;
        _storage = storage;
        _processRunner = processRunner;
        _options = options;
        _mediaOptions = mediaOptions;
        _timeProvider = timeProvider;
    }

    public async Task<ProviderResult<ArtworkGenerationResult>> GenerateAsync(
        ArtworkGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetUtcNow();
        if (request.CandidateCount is < 1 or > 3 || !HasSupportedDimensions(request.Width, request.Height))
        {
            return Failed(
                request.Context,
                startedAt,
                new ProviderFailure(
                    ProviderFailureKind.UserInput,
                    "openrouter.image_request_invalid",
                    "Artwork dimensions or candidate count are invalid."));
        }

        string? referenceDataUrl;
        try
        {
            referenceDataUrl = request.ReferenceImage is null
                ? null
                : await ReadReferenceAsync(request.ReferenceImage, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return Failed(
                request.Context,
                startedAt,
                new ProviderFailure(
                    ProviderFailureKind.UserInput,
                    "openrouter.reference_image_invalid",
                    "The approved reference image is invalid."));
        }
        catch (IOException)
        {
            return Failed(
                request.Context,
                startedAt,
                new ProviderFailure(
                    ProviderFailureKind.Transient,
                    "openrouter.reference_image_unavailable",
                    "The approved reference image could not be staged."));
        }

        var candidates = new List<ArtworkCandidate>(request.CandidateCount);
        var requestIds = new List<string>();
        var generationIds = new List<string>();
        var usages = new List<ProviderUsage>();
        string? resolvedModel = null;
        string? resolvedProvider = null;
        for (var candidateNumber = 1; candidateNumber <= request.CandidateCount; candidateNumber++)
        {
            var payload = BuildPayload(request, candidateNumber, referenceDataUrl);
            var response = await _client.PostJsonAsync(
                "images",
                payload,
                $"{request.Context.OperationId:N}:image:{candidateNumber}",
                _options.ImageTimeoutSeconds,
                outcomeCanBeRetried: false,
                cancellationToken);
            if (!response.IsSuccess)
            {
                return Failed(
                    request.Context,
                    startedAt,
                    response.Failure!,
                    requestIds,
                    generationIds,
                    resolvedModel,
                    resolvedProvider,
                    usages);
            }

            if (response.RequestId is not null) requestIds.Add(response.RequestId);
            if (response.GenerationId is not null) generationIds.Add(response.GenerationId);
            try
            {
                using var json = JsonDocument.Parse(response.Body);
                resolvedModel ??= OpenRouterProviderData.String(json.RootElement, "model");
                resolvedProvider ??= OpenRouterProviderData.String(json.RootElement, "provider");
                usages.Add(OpenRouterProviderData.Usage(json.RootElement, generatedImages: 1));
                var image = ReadImage(json.RootElement);
                var artifact = await MaterializeAsync(
                    request,
                    candidateNumber,
                    image.Bytes,
                    image.ContentType,
                    cancellationToken);
                candidates.Add(new ArtworkCandidate(
                    OpenRouterProviderData.StableId(request.Context.OperationId, $"candidate:{candidateNumber}"),
                    candidateNumber,
                    artifact));
            }
            catch (JsonException)
            {
                return Failed(
                    request.Context,
                    startedAt,
                    InvalidResponse(),
                    requestIds,
                    generationIds,
                    resolvedModel,
                    resolvedProvider,
                    usages);
            }
            catch (InvalidDataException)
            {
                return Failed(
                    request.Context,
                    startedAt,
                    InvalidResponse(),
                    requestIds,
                    generationIds,
                    resolvedModel,
                    resolvedProvider,
                    usages);
            }
            catch (TimeoutException)
            {
                return Failed(
                    request.Context,
                    startedAt,
                    new ProviderFailure(
                        ProviderFailureKind.Transient,
                        "openrouter.image_normalization_timeout",
                        "The generated image could not be normalized in time."),
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
                        "openrouter.image_artifact_io_failure",
                        "The generated artwork could not be staged."),
                    requestIds,
                    generationIds,
                    resolvedModel,
                    resolvedProvider,
                    usages);
            }
            catch (InvalidOperationException)
            {
                return Failed(
                    request.Context,
                    startedAt,
                    new ProviderFailure(
                        ProviderFailureKind.Permanent,
                        "openrouter.image_normalizer_unavailable",
                        "The generated image normalizer is unavailable."),
                    requestIds,
                    generationIds,
                    resolvedModel,
                    resolvedProvider,
                    usages);
            }
        }

        var result = new ArtworkGenerationResult(
            candidates,
            candidates.Select(value => value.Artwork).ToArray());
        return ProviderResult<ArtworkGenerationResult>.Succeeded(
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

    private object BuildPayload(
        ArtworkGenerationRequest request,
        int candidateNumber,
        string? referenceDataUrl)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _options.ImageModel,
            ["prompt"] = BuildPrompt(request, candidateNumber),
            ["n"] = 1,
            ["resolution"] = "2K",
            ["aspect_ratio"] = AspectRatio(request.Width, request.Height),
            ["output_format"] = "png",
            ["provider"] = new
            {
                zdr = _options.RequireZeroDataRetention,
                data_collection = _options.DenyDataCollection ? "deny" : "allow",
                require_parameters = _options.RequireParameters,
                allow_fallbacks = true
            }
        };
        if (referenceDataUrl is not null)
        {
            payload["input_references"] = new[]
            {
                new
                {
                    type = "image_url",
                    image_url = new { url = referenceDataUrl }
                }
            };
        }

        return payload;
    }

    private static string BuildPrompt(ArtworkGenerationRequest request, int candidateNumber)
    {
        var kind = request.ReferenceImage is null
            ? "Create one original square album-cover artwork"
            : "Create one portrait campaign background derived from the supplied approved cover";
        var excerpts = request.Brief.ShortLyricExcerpts
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(3)
            .Select(value => value.Trim()[..Math.Min(120, value.Trim().Length)]);
        return string.Join(
            '\n',
            kind + ".",
            $"This is visual variation {candidateNumber}; make its composition distinct from other variations.",
            "Do not render text, letters, logos, signatures, labels, UI, borders, or watermarks.",
            $"Mood: {request.Brief.Mood}.",
            $"Palette: {string.Join(", ", request.Brief.Palette.Take(5))}.",
            string.IsNullOrWhiteSpace(request.Brief.UserPrompt)
                ? ""
                : $"Creative direction: {request.Brief.UserPrompt.Trim()}.",
            !excerpts.Any()
                ? ""
                : $"Use only the emotional themes of these short excerpts, without rendering their words: {string.Join(" | ", excerpts)}.");
    }

    private async Task<string> ReadReferenceAsync(
        ProviderObjectReference reference,
        CancellationToken cancellationToken)
    {
        if (reference.SizeBytes is <= 0 or > 20_000_000 ||
            reference.ContentType is not ("image/png" or "image/jpeg" or "image/webp"))
        {
            throw new InvalidDataException();
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), "hook2stream-openrouter-reference", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(workDirectory, "reference");
        try
        {
            Directory.CreateDirectory(workDirectory);
            await _storage.DownloadAsync(reference.ObjectKey, path, cancellationToken);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var detected = DetectImage(bytes);
            if (detected != reference.ContentType) throw new InvalidDataException();
            return $"data:{detected};base64,{Convert.ToBase64String(bytes)}";
        }
        finally
        {
            OpenRouterProviderData.TryDelete(workDirectory);
        }
    }

    private async Task<ProviderArtifactManifest> MaterializeAsync(
        ArtworkGenerationRequest request,
        int candidateNumber,
        byte[] bytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        var detected = DetectImage(bytes);
        if (detected != contentType) throw new InvalidDataException();
        var workDirectory = Path.Combine(Path.GetTempPath(), "hook2stream-openrouter-image", Guid.NewGuid().ToString("N"));
        var inputPath = Path.Combine(workDirectory, detected switch
        {
            "image/jpeg" => "input.jpg",
            "image/webp" => "input.webp",
            _ => "input.png"
        });
        var outputPath = Path.Combine(workDirectory, "normalized.png");
        try
        {
            Directory.CreateDirectory(workDirectory);
            await File.WriteAllBytesAsync(inputPath, bytes, cancellationToken);
            var filter = $"scale=w={request.Width}:h={request.Height}:force_original_aspect_ratio=increase,crop={request.Width}:{request.Height}";
            var normalization = await _processRunner.RunAsync(
                _mediaOptions.FfmpegPath,
                ["-y", "-v", "error", "-i", inputPath, "-vf", filter, "-frames:v", "1", "-c:v", "png", outputPath],
                TimeSpan.FromSeconds(_mediaOptions.ProcessTimeoutSeconds),
                workDirectory,
                cancellationToken);
            if (normalization.ExitCode != 0 || !File.Exists(outputPath)) throw new InvalidDataException();
            var normalized = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            if (!TryReadPngDimensions(normalized, out var width, out var height) ||
                width != request.Width || height != request.Height)
            {
                throw new InvalidDataException();
            }

            var role = request.ReferenceImage is null
                ? $"cover-candidate-{candidateNumber}"
                : $"campaign-background-{candidateNumber}";
            var prefix = request.Context.StagingPrefix.Trim().Trim('/');
            var objectKey = $"{prefix}/{role}.png";
            await _storage.UploadAsync(objectKey, outputPath, "image/png", cancellationToken);
            return new ProviderArtifactManifest(
                OpenRouterProviderData.StableId(request.Context.OperationId, role),
                role,
                objectKey,
                OpenRouterProviderData.Sha256(normalized),
                "image/png",
                normalized.LongLength,
                Materialized: true,
                Width: width,
                Height: height);
        }
        finally
        {
            OpenRouterProviderData.TryDelete(workDirectory);
        }
    }

    private static GeneratedImage ReadImage(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 1 ||
            !data[0].TryGetProperty("b64_json", out var encoded) ||
            encoded.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException();
        }

        var base64 = encoded.GetString() ?? "";
        if (base64.Length == 0 || base64.Length > MaximumEncodedImageCharacters)
        {
            throw new InvalidDataException();
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Invalid image encoding.", exception);
        }

        var detected = DetectImage(bytes);
        var declared = OpenRouterProviderData.String(data[0], "media_type") ?? detected;
        if (declared != detected) throw new InvalidDataException();
        return new GeneratedImage(bytes, detected);
    }

    private static string DetectImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 12 &&
            bytes[..4].SequenceEqual("RIFF"u8) &&
            bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        throw new InvalidDataException();
    }

    private static bool TryReadPngDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return false;
        }

        width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
        return width > 0 && height > 0;
    }

    public static bool HasSupportedDimensions(int width, int height)
    {
        var maximum = Math.Max(width, height);
        var minimum = Math.Min(width, height);
        var pixels = (long)width * height;
        return minimum > 0 &&
               maximum < 3_840 &&
               width % 16 == 0 &&
               height % 16 == 0 &&
               maximum <= minimum * 3L &&
               pixels is >= 655_360 and <= 8_294_400;
    }

    private static string AspectRatio(int width, int height) =>
        width == height ? "1:1" : width < height ? "9:16" : "16:9";

    private static ProviderFailure InvalidResponse() =>
        new(
            ProviderFailureKind.Permanent,
            "openrouter.image_response_invalid",
            "OpenRouter returned an invalid image response.");

    private ProviderResult<ArtworkGenerationResult> Failed(
        ProviderExecutionContext context,
        DateTimeOffset startedAt,
        ProviderFailure failure,
        IReadOnlyCollection<string>? requestIds = null,
        IReadOnlyCollection<string>? generationIds = null,
        string? resolvedModel = null,
        string? resolvedProvider = null,
        IReadOnlyCollection<ProviderUsage>? usages = null) =>
        ProviderResult<ArtworkGenerationResult>.Failed(
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
            resolvedModel ?? _options.ImageModel,
            "image-api-v1",
            requestIds.Count == 0 ? null : string.Join(',', requestIds),
            context.InputHash,
            context.ParameterHash,
            startedAt,
            _timeProvider.GetUtcNow(),
            _options.ImageModel,
            resolvedProvider,
            generationIds.Count == 0 ? null : string.Join(',', generationIds),
            OpenRouterProviderData.Sum(usages));

    private sealed record GeneratedImage(byte[] Bytes, string ContentType);
}
