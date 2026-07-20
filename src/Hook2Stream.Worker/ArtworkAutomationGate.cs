using Hook2Stream.Domain;

namespace Hook2Stream.Worker;

public sealed record ArtworkAutomationDecision(bool Allowed, string? BlockerCode);
public sealed record ContentRightsDecision(bool Allowed, string? BlockerCode);
public sealed record ExternalAiProcessingDecision(bool Allowed, string? BlockerCode);

public static class ContentRightsGate
{
    public static ContentRightsDecision Evaluate(
        ReleaseProject project,
        MediaAsset audio,
        RightsAttestation? rights)
    {
        var allowed = rights?.OwnsAudioRights == true &&
                      (project.IsInstrumental && project.IsInstrumentalConfirmed || rights.OwnsLyricsRights) &&
                      rights.AudioAssetId == audio.Id &&
                      !string.IsNullOrWhiteSpace(audio.Sha256) &&
                      string.Equals(rights.AudioFingerprint, audio.Sha256, StringComparison.Ordinal);
        return allowed
            ? new ContentRightsDecision(true, null)
            : new ContentRightsDecision(false, "rights.required");
    }
}

public static class ExternalAiProcessingGate
{
    public static ExternalAiProcessingDecision Evaluate(
        ReleaseProject project,
        MediaAsset audio,
        RightsAttestation? rights)
    {
        var contentRights = ContentRightsGate.Evaluate(project, audio, rights);
        if (!contentRights.Allowed)
        {
            return new ExternalAiProcessingDecision(false, contentRights.BlockerCode);
        }

        return rights?.AllowsExternalAiProcessing == true
            ? new ExternalAiProcessingDecision(true, null)
            : new ExternalAiProcessingDecision(false, "rights.external_ai_processing_required");
    }
}

public static class ArtworkAutomationGate
{
    public static ArtworkAutomationDecision Evaluate(
        ReleaseProject project,
        MediaAsset audio,
        RightsAttestation? rights,
        DateOnly today)
    {
        if (project.SetupCompletedAt is null)
        {
            return new ArtworkAutomationDecision(false, "setup.required");
        }

        var releaseTimingConfirmed = project.Mode switch
        {
            ReleaseMode.Upcoming => project.ReleaseDate is { } releaseDate && releaseDate > today,
            ReleaseMode.Released => project.ReleaseDate is { } releaseDate && releaseDate <= today,
            _ => false
        };
        if (!releaseTimingConfirmed)
        {
            return new ArtworkAutomationDecision(false, "release.schedule_required");
        }

        var externalAi = ExternalAiProcessingGate.Evaluate(project, audio, rights);
        return externalAi.Allowed
            ? new ArtworkAutomationDecision(true, null)
            : new ArtworkAutomationDecision(false, externalAi.BlockerCode ?? "rights.external_ai_processing_required");
    }
}
