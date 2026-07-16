using System.Globalization;
using System.Text.RegularExpressions;
using Hook2Stream.Domain;

namespace Hook2Stream.Application;

public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool IsValid => _errors.Count == 0;

    public IReadOnlyDictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            messages = [];
            _errors[field] = messages;
        }

        messages.Add(message);
    }
}

public static partial class BrandKitRules
{
    public static readonly IReadOnlySet<string> AllowedFonts =
        new HashSet<string>(["Inter", "Manrope", "Montserrat", "Oswald"], StringComparer.Ordinal);

    public static ValidationErrors Validate(UpdateBrandKitRequest request)
    {
        var errors = new ValidationErrors();

        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 120)
        {
            errors.Add("displayName", "Display name is required and must not exceed 120 characters.");
        }

        ValidateColor(request.PrimaryColor, "primaryColor", errors);
        ValidateColor(request.SecondaryColor, "secondaryColor", errors);
        ValidateColor(request.AccentColor, "accentColor", errors);

        if (!AllowedFonts.Contains(request.HeadingFont))
        {
            errors.Add("headingFont", "Choose a font from the supported catalog.");
        }

        if (!AllowedFonts.Contains(request.BodyFont))
        {
            errors.Add("bodyFont", "Choose a font from the supported catalog.");
        }

        if (string.IsNullOrWhiteSpace(request.DefaultCta) || request.DefaultCta.Trim().Length > 160)
        {
            errors.Add("defaultCta", "CTA is required and must not exceed 160 characters.");
        }

        if (!string.IsNullOrWhiteSpace(request.SmartLink) &&
            (!Uri.TryCreate(request.SmartLink, UriKind.Absolute, out var uri) ||
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("smartLink", "Smart link must be an absolute HTTPS URL.");
        }

        if (request.ToneRestrictions?.Length > 1_000)
        {
            errors.Add("toneRestrictions", "Tone restrictions must not exceed 1000 characters.");
        }

        if (TryParseColor(request.PrimaryColor, out var primary) &&
            TryParseColor(request.SecondaryColor, out var secondary) &&
            ContrastRatio(primary, secondary) < 4.5)
        {
            errors.Add("primaryColor", "Primary and secondary colors must have a contrast ratio of at least 4.5:1.");
        }

        return errors;
    }

    private static void ValidateColor(string value, string field, ValidationErrors errors)
    {
        if (!HexColorRegex().IsMatch(value))
        {
            errors.Add(field, "Use a six-digit HEX color such as #F97316.");
        }
    }

    private static bool TryParseColor(string value, out (double R, double G, double B) color)
    {
        color = default;
        if (!HexColorRegex().IsMatch(value))
        {
            return false;
        }

        color = (
            int.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d,
            int.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d,
            int.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d);
        return true;
    }

    private static double ContrastRatio(
        (double R, double G, double B) first,
        (double R, double G, double B) second)
    {
        static double Channel(double value) =>
            value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

        static double Luminance((double R, double G, double B) value) =>
            0.2126 * Channel(value.R) + 0.7152 * Channel(value.G) + 0.0722 * Channel(value.B);

        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColorRegex();
}

public static class ReleaseRules
{
    public static ValidationErrors Validate(CreateReleaseRequest request, DateOnly today) =>
        ValidateCore(
            request.ProjectLabel,
            request.ArtistName,
            request.TrackTitle,
            request.Language,
            request.LyricsText,
            request.IsInstrumental,
            request.Mode,
            request.ReleaseDate,
            request.CampaignStartDate,
            today);

    public static ValidationErrors Validate(UpdateReleaseRequest request, DateOnly today) =>
        ValidateCore(
            request.ProjectLabel,
            request.ArtistName,
            request.TrackTitle,
            request.Language,
            request.LyricsText,
            request.IsInstrumental,
            request.Mode,
            request.ReleaseDate,
            request.CampaignStartDate,
            today);

    private static ValidationErrors ValidateCore(
        string projectLabel,
        string artistName,
        string trackTitle,
        string language,
        string? lyricsText,
        bool isInstrumental,
        ReleaseMode mode,
        DateOnly? releaseDate,
        DateOnly? campaignStartDate,
        DateOnly today)
    {
        var errors = new ValidationErrors();

        RequireText(projectLabel, "projectLabel", 160, errors);
        RequireText(artistName, "artistName", 160, errors);
        RequireText(trackTitle, "trackTitle", 160, errors);
        RequireText(language, "language", 16, errors);

        if (!isInstrumental && string.IsNullOrWhiteSpace(lyricsText))
        {
            errors.Add("lyricsText", "Provide lyrics or mark the release as instrumental.");
        }

        if (isInstrumental && !string.IsNullOrWhiteSpace(lyricsText))
        {
            errors.Add("lyricsText", "Instrumental releases must not include lyrics.");
        }

        if (mode == ReleaseMode.Upcoming)
        {
            if (releaseDate is null || releaseDate <= today)
            {
                errors.Add("releaseDate", "Upcoming releases require a future release date.");
            }

            if (campaignStartDate is not null)
            {
                errors.Add("campaignStartDate", "Campaign start is derived from the upcoming release date.");
            }
        }
        else
        {
            if (releaseDate is null || releaseDate > today)
            {
                errors.Add("releaseDate", "Released tracks require an actual release date that is not in the future.");
            }

            if (campaignStartDate is null || campaignStartDate < today)
            {
                errors.Add("campaignStartDate", "Choose today or a future campaign start date.");
            }
        }

        return errors;
    }

    private static void RequireText(string value, string field, int maxLength, ValidationErrors errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
        {
            errors.Add(field, $"{field} is required and must not exceed {maxLength} characters.");
        }
    }
}
