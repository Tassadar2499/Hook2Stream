using System.Collections.ObjectModel;
using Hook2Stream.Domain;

namespace Hook2Stream.Application;

/// <summary>
/// The authoritative mapping between durable job types and isolated worker pools.
/// Producers, the queue, and workers must all use this registry instead of
/// maintaining their own capability strings.
/// </summary>
public static class JobRoutingRegistry
{
    public const string Media = "media";
    public const string Analysis = "analysis";
    public const string Control = "control";
    public const string Render = "render";
    public const string Export = "export";

    private static readonly IReadOnlyDictionary<JobType, string> Routes =
        CreateRoutes();

    private static readonly IReadOnlyList<string> CapabilityNames =
        Array.AsReadOnly(
        new[]
        {
            Media,
            Analysis,
            Control,
            Render,
            Export
        });

    public static IReadOnlyDictionary<JobType, string> All => Routes;

    public static IReadOnlyList<string> Capabilities => CapabilityNames;

    public static string GetRequiredCapability(JobType type)
    {
        if (Routes.TryGetValue(type, out var capability))
        {
            return capability;
        }

        throw new ArgumentOutOfRangeException(
            nameof(type),
            type,
            "The job type has no worker route.");
    }

    public static IReadOnlyList<JobType> GetJobTypes(string capability)
    {
        var normalized = NormalizeCapability(capability);
        return Routes
            .Where(route => string.Equals(route.Value, normalized, StringComparison.Ordinal))
            .Select(route => route.Key)
            .OrderBy(type => type)
            .ToArray();
    }

    public static bool IsKnownCapability(string? capability) =>
        capability is not null &&
        CapabilityNames.Contains(capability.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string NormalizeCapability(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        var normalized = capability.Trim().ToLowerInvariant();
        if (!IsKnownCapability(normalized))
        {
            throw new ArgumentOutOfRangeException(
                nameof(capability),
                capability,
                $"Unknown worker capability. Expected one of: {string.Join(", ", CapabilityNames)}.");
        }

        return normalized;
    }

    public static void EnsureMatches(JobType type, string capability)
    {
        var expected = GetRequiredCapability(type);
        if (!string.Equals(expected, capability?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Job type '{type}' must be routed to capability '{expected}', not '{capability}'.");
        }
    }

    private static IReadOnlyDictionary<JobType, string> CreateRoutes()
    {
        var routes = new Dictionary<JobType, string>
        {
            [JobType.MediaIngest] = Media,
            [JobType.AudioAnalysis] = Analysis,
            [JobType.Transcription] = Control,
            [JobType.ArtworkGeneration] = Control,
            [JobType.CampaignGeneration] = Control,
            [JobType.AssetCleanup] = Control,
            [JobType.PreviewRender] = Render,
            [JobType.FinalRender] = Render,
            [JobType.CleanCoverRender] = Render,
            [JobType.ExportBundle] = Export
        };

        var missing = Enum.GetValues<JobType>().Except(routes.Keys).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Worker routes are missing for job types: {string.Join(", ", missing)}.");
        }

        return new ReadOnlyDictionary<JobType, string>(routes);
    }
}
