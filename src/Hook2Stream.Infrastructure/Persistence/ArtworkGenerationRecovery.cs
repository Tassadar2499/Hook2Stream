using System.Text.Json;
using Hook2Stream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Infrastructure.Persistence;

public static class ArtworkGenerationRecovery
{
    public static async Task<TerminalArtworkGeneration?> TryFailProcessingCoverAsync(
        Hook2StreamDbContext db,
        ArtworkPackRevision pack,
        CancellationToken cancellationToken)
    {
        if (pack.State != RevisionState.Processing ||
            string.IsNullOrWhiteSpace(pack.SourceFingerprint))
        {
            return null;
        }

        var latestCoverJob = await db.Jobs
            .AsNoTracking()
            .Where(value => value.WorkspaceId == pack.WorkspaceId &&
                            value.ProjectId == pack.ProjectId &&
                            value.Type == JobType.ArtworkGeneration &&
                            value.AssetId == null &&
                            value.InputFingerprint == pack.SourceFingerprint)
            .OrderByDescending(value => value.CreatedAt)
            .Select(value => new
            {
                value.Id,
                value.State,
                value.PayloadJson,
                value.ErrorCode
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (latestCoverJob is not { State: JobState.Failed } ||
            !TargetsCoverRevision(latestCoverJob.PayloadJson, pack.Id))
        {
            return null;
        }

        pack.State = RevisionState.Failed;
        await ArtworkCreditLedger.ReleaseReservationAsync(
            db,
            pack.WorkspaceId,
            pack.Id,
            cancellationToken);
        return new TerminalArtworkGeneration(latestCoverJob.Id, latestCoverJob.ErrorCode);
    }

    private static bool TargetsCoverRevision(string payloadJson, Guid revisionId)
    {
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            var root = payload.RootElement;
            if (!root.TryGetProperty("artworkPackRevisionId", out var revision) ||
                !revision.TryGetGuid(out var parsedRevisionId) ||
                parsedRevisionId != revisionId)
            {
                return false;
            }

            return !root.TryGetProperty("mode", out var mode) ||
                   !string.Equals(mode.GetString(), "backgrounds", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record TerminalArtworkGeneration(Guid JobId, string? ErrorCode);
