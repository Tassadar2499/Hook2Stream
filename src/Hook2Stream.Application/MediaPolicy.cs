using Hook2Stream.Domain;

namespace Hook2Stream.Application;

public static class MediaPolicy
{
    public const long AudioMaxBytes = 250L * 1024 * 1024;
    public const long ImageMaxBytes = 25L * 1024 * 1024;
    public const long VideoMaxBytes = 250L * 1024 * 1024;
    public const long VisualsMaxTotalBytes = 500L * 1024 * 1024;
    public const long MultipartThresholdBytes = 25L * 1024 * 1024;
    public const long MultipartPartSizeBytes = 10L * 1024 * 1024;
    public const int MaxVisualCount = 10;
    public const int MinVisualCount = 3;
    public const int MaxAudioDurationSeconds = 10 * 60;
    public const int MaxVideoDurationSeconds = 60;
    public const int MaxDimension = 4096;

    private static readonly IReadOnlySet<string> AudioContentTypes =
        new HashSet<string>(["audio/mpeg", "audio/mp3", "audio/wav", "audio/x-wav"], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> ImageContentTypes =
        new HashSet<string>(["image/jpeg", "image/png", "image/webp"], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> VideoContentTypes =
        new HashSet<string>(["video/mp4", "video/quicktime", "video/webm"], StringComparer.OrdinalIgnoreCase);

    public static ValidationErrors ValidateReservation(
        CreateUploadRequest request,
        int activeVisualCount,
        long activeVisualBytes)
    {
        var errors = new ValidationErrors();

        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 255)
        {
            errors.Add("fileName", "File name is required and must not exceed 255 characters.");
        }

        if (request.SizeBytes <= 0)
        {
            errors.Add("sizeBytes", "File size must be greater than zero.");
            return errors;
        }

        switch (request.Kind)
        {
            case AssetKind.Audio:
                ValidateTypeAndSize(request, AudioContentTypes, AudioMaxBytes, errors);
                break;
            case AssetKind.Cover:
                ValidateTypeAndSize(request, ImageContentTypes, ImageMaxBytes, errors);
                break;
            case AssetKind.Visual:
                if (activeVisualCount >= MaxVisualCount && request.ReplacesAssetId is null)
                {
                    errors.Add("kind", $"A release can contain at most {MaxVisualCount} visual assets.");
                }

                if (activeVisualBytes + request.SizeBytes > VisualsMaxTotalBytes && request.ReplacesAssetId is null)
                {
                    errors.Add("sizeBytes", "The visual asset total must not exceed 500 MB.");
                }

                if (ImageContentTypes.Contains(request.ContentType))
                {
                    ValidateTypeAndSize(request, ImageContentTypes, ImageMaxBytes, errors);
                }
                else
                {
                    ValidateTypeAndSize(request, VideoContentTypes, VideoMaxBytes, errors);
                }

                break;
            case AssetKind.BrandCharacter:
                ValidateTypeAndSize(request, ImageContentTypes, ImageMaxBytes, errors);
                break;
            default:
                errors.Add("kind", "Unsupported asset kind.");
                break;
        }

        return errors;
    }

    public static bool IsImageContentType(string contentType) => ImageContentTypes.Contains(contentType);

    public static bool IsVideoContentType(string contentType) => VideoContentTypes.Contains(contentType);

    public static bool IsAudioContentType(string contentType) => AudioContentTypes.Contains(contentType);

    private static void ValidateTypeAndSize(
        CreateUploadRequest request,
        IReadOnlySet<string> contentTypes,
        long maxBytes,
        ValidationErrors errors)
    {
        if (!contentTypes.Contains(request.ContentType))
        {
            errors.Add("contentType", "The declared media type is not supported for this asset role.");
        }

        if (request.SizeBytes > maxBytes)
        {
            errors.Add("sizeBytes", $"File exceeds the {maxBytes / (1024 * 1024)} MB limit.");
        }
    }
}

public static class ObjectKeyFactory
{
    public static string Original(Guid workspaceId, Guid projectId, Guid assetId, int revision) =>
        $"w/{workspaceId:N}/p/{projectId:N}/assets/{assetId:N}/r/{revision}/original";

    public static string Derivative(
        Guid workspaceId,
        Guid projectId,
        Guid assetId,
        int revision,
        string processorVersion,
        DerivativeKind kind)
    {
        var safeVersion = string.Concat(
            processorVersion.Select(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '-'));

        return $"w/{workspaceId:N}/p/{projectId:N}/assets/{assetId:N}/r/{revision}/derived/{safeVersion}/{kind.ToString().ToLowerInvariant()}";
    }
}

public static class JobRetrySchedule
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5)
    ];

    public static TimeSpan ForAttempt(int attemptNumber) =>
        Delays[Math.Clamp(attemptNumber - 1, 0, Delays.Length - 1)];
}
