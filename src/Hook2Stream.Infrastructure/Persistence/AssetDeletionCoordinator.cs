using System.Text.Json;
using Hook2Stream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Infrastructure.Persistence;

/// <summary>
/// Revokes active consumers before a retained asset can be removed from
/// storage. The caller must schedule physical cleanup after its lease fence.
/// </summary>
public static class AssetDeletionCoordinator
{
    public static async Task FenceAsync(
        Hook2StreamDbContext db,
        MediaAsset asset,
        DateTimeOffset now,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        asset.State = AssetState.Deleted;
        asset.IsActive = false;
        asset.DeletedAt ??= now;

        var uploadSessions = await db.UploadSessions
            .Where(value => value.AssetId == asset.Id)
            .ToListAsync(cancellationToken);
        foreach (var upload in uploadSessions.Where(value =>
                     value.State is UploadState.Initiated or UploadState.Uploading))
        {
            upload.State = UploadState.Expired;
            upload.AbortedAt ??= now;
        }

        var activeJobs = await db.Jobs
            .Where(value => value.ProjectId == asset.ProjectId &&
                            value.Type != JobType.AssetCleanup &&
                            (value.State == JobState.Queued || value.State == JobState.Running))
            .ToListAsync(cancellationToken);
        var referenceIds = new HashSet<Guid> { asset.Id };
        if (asset.ArtworkPackRevisionId is { } artworkPackRevisionId)
        {
            referenceIds.Add(artworkPackRevisionId);
        }
        if (asset.RenderBatchId is { } renderBatchId)
        {
            referenceIds.Add(renderBatchId);
        }
        if (asset.CampaignItemId is { } campaignItemId)
        {
            referenceIds.Add(campaignItemId);
        }

        var artworkPacks = await db.ArtworkPackRevisions
            .Where(value => value.ProjectId == asset.ProjectId)
            .Select(value => new
            {
                value.Id,
                value.SelectedAssetId,
                value.CandidateAssetIdsJson,
                value.BackgroundAssetIdsJson
            })
            .ToListAsync(cancellationToken);
        foreach (var pack in artworkPacks.Where(value =>
                     value.SelectedAssetId == asset.Id ||
                     ReferencesIdentifier(value.CandidateAssetIdsJson, asset.Id) ||
                     ReferencesIdentifier(value.BackgroundAssetIdsJson, asset.Id)))
        {
            referenceIds.Add(pack.Id);
        }

        var campaigns = await db.CampaignPlanRevisions
            .Where(value => value.ProjectId == asset.ProjectId)
            .Select(value => new { value.Id, value.ArtworkPackRevisionId, value.ItemsJson })
            .ToListAsync(cancellationToken);
        foreach (var campaign in campaigns.Where(value =>
                     referenceIds.Contains(value.ArtworkPackRevisionId) ||
                     referenceIds.Any(referenceId => ReferencesIdentifier(value.ItemsJson, referenceId))))
        {
            referenceIds.Add(campaign.Id);
        }

        var consumers = activeJobs
            .Where(value => value.AssetId == asset.Id ||
                            referenceIds.Any(referenceId => ReferencesIdentifier(value.PayloadJson, referenceId)))
            .ToList();
        var consumerIds = consumers.Select(value => value.Id).ToArray();
        var attempts = await db.JobAttempts
            .Where(value => consumerIds.Contains(value.JobId) && value.State == JobState.Running)
            .ToListAsync(cancellationToken);

        foreach (var job in consumers)
        {
            job.State = JobState.Cancelled;
            job.CompletedAt = now;
            job.ProgressStage = "cancelled";
            job.ErrorCode = reasonCode;
            job.ErrorMessage = "A required asset was deleted.";
            job.LeaseOwner = null;
            job.LeaseToken = null;
            job.LeaseExpiresAt = null;
            db.JobEvents.Add(new JobEvent
            {
                JobId = job.Id,
                EventType = "cancelled",
                DataJson = JsonSerializer.Serialize(new { code = reasonCode, assetId = asset.Id })
            });
        }

        foreach (var attempt in attempts)
        {
            attempt.State = JobState.Cancelled;
            attempt.CompletedAt = now;
            attempt.ErrorCode = reasonCode;
        }
    }

    private static bool ReferencesIdentifier(string payloadJson, Guid identifier) =>
        payloadJson.Contains(identifier.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
        payloadJson.Contains(identifier.ToString("N"), StringComparison.OrdinalIgnoreCase);
}
