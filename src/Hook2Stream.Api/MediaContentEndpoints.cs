using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Hook2Stream.Api;

internal static class MediaContentEndpoints
{
    public static RouteGroupBuilder MapMediaContentApi(this RouteGroupBuilder api)
    {
        api.MapMethods("/releases/{projectId:guid}/assets/{assetId:guid}/content", ["GET", "HEAD"],
            (Guid projectId, Guid assetId, CurrentUserService user, Hook2StreamDbContext db, IObjectStorage storage, TimeProvider clock, CancellationToken token) =>
                Serve(projectId, assetId, false, user, db, storage, clock, token));
        api.MapMethods("/releases/{projectId:guid}/downloads/{assetId:guid}", ["GET", "HEAD"],
            (Guid projectId, Guid assetId, CurrentUserService user, Hook2StreamDbContext db, IObjectStorage storage, TimeProvider clock, CancellationToken token) =>
                Serve(projectId, assetId, true, user, db, storage, clock, token));
        return api;
    }

    private static async Task<IResult> Serve(
        Guid projectId,
        Guid assetId,
        bool download,
        CurrentUserService currentUser,
        Hook2StreamDbContext db,
        IObjectStorage storage,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var context = await currentUser.RequireWorkspaceAsync(cancellationToken);
        var asset = await db.MediaAssets.AsNoTracking().Include(value => value.Derivatives).SingleOrDefaultAsync(value =>
            value.Id == assetId && value.ProjectId == projectId && value.WorkspaceId == context.Workspace.Id,
            cancellationToken) ?? throw new ApiProblemException(404, "asset.not_found", "The media asset was not found.");
        if (asset.State is not (AssetState.Ready or AssetState.Uploaded))
            throw new ApiProblemException(409, "asset.not_available", "The media asset is not available.");

        if (download && asset.Origin == AssetOrigin.Generated)
        {
            var now = clock.GetUtcNow();
            var entitled = asset.RenderBatchId is { } renderBatchId
                ? await db.RenderBatches.AsNoTracking().AnyAsync(batch =>
                    batch.Id == renderBatchId && db.Entitlements.Any(value =>
                        value.Id == batch.EntitlementId && value.WorkspaceId == context.Workspace.Id &&
                        value.ProjectId == projectId && value.State == EntitlementState.Active && value.RevokedAt == null &&
                        (value.ValidUntil == null || value.ValidUntil > now)),
                    cancellationToken)
                : asset.Purpose == AssetPurpose.CleanCover && await db.Entitlements.AsNoTracking().AnyAsync(value =>
                    value.WorkspaceId == context.Workspace.Id && value.ProjectId == projectId &&
                    value.ArtworkPackRevisionId == asset.ArtworkPackRevisionId &&
                    value.State == EntitlementState.Active && value.RevokedAt == null &&
                    (value.ValidUntil == null || value.ValidUntil > now),
                    cancellationToken);
            if (!entitled)
                throw new ApiProblemException(402, "download.entitlement_required", "The entitlement for this download is missing or revoked.");
        }

        var objectKey = asset.ObjectKey;
        var contentType = asset.DetectedContentType ?? asset.DeclaredContentType;
        if (!download && asset.Origin == AssetOrigin.Generated && asset.Purpose != AssetPurpose.PreviewVideo)
        {
            var preview = asset.Derivatives
                .Where(value => value.DeletedAt == null && value.Kind is DerivativeKind.ImageProxy or DerivativeKind.Thumbnail)
                .OrderBy(value => value.Kind == DerivativeKind.ImageProxy ? 0 : 1)
                .Select(value => new { value.ObjectKey, value.ContentType })
                .FirstOrDefault();
            if (preview is null)
                throw new ApiProblemException(409, "asset.preview_unavailable", "A protected preview is not available for this generated asset.");
            objectKey = preview.ObjectKey;
            contentType = preview.ContentType;
        }
        var info = await storage.HeadAsync(objectKey, cancellationToken)
            ?? throw new ApiProblemException(404, "asset.content_missing", "The media content was not found.");
        return new EncryptedMediaResult(
            storage,
            objectKey,
            info.SizeBytes,
            contentType ?? info.ContentType ?? "application/octet-stream",
            asset.OriginalFileName,
            download,
            info.ETag);
    }
}

internal sealed class EncryptedMediaResult(
    IObjectStorage storage,
    string objectKey,
    long plaintextLength,
    string contentType,
    string fileName,
    bool attachment,
    string? entityTag) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        var response = context.Response;
        response.Headers.AcceptRanges = "bytes";
        response.Headers.CacheControl = "private, no-store";
        if (!string.IsNullOrWhiteSpace(entityTag))
        {
            var quoted = $"\"{entityTag.Trim('"')}\"";
            response.Headers.ETag = quoted;
            if (context.Request.Headers.IfNoneMatch.Any(value => string.Equals(value, quoted, StringComparison.Ordinal)))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }
        }
        response.Headers.ContentType = contentType;
        response.Headers.ContentDisposition = new ContentDispositionHeaderValue(attachment ? "attachment" : "inline")
        {
            FileNameStar = Path.GetFileName(fileName)
        }.ToString();

        long offset = 0;
        long length = plaintextLength;
        if (context.Request.Headers.TryGetValue(HeaderNames.Range, out var rangeHeader))
        {
            if (!TryParseSingleRange(rangeHeader.ToString(), plaintextLength, out offset, out length))
            {
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.Headers.ContentRange = $"bytes */{plaintextLength}";
                return;
            }
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers.ContentRange = $"bytes {offset}-{offset + length - 1}/{plaintextLength}";
        }
        response.ContentLength = length;
        if (HttpMethods.IsHead(context.Request.Method) || length == 0) return;
        await storage.CopyToAsync(objectKey, response.Body, offset, length, context.RequestAborted);
    }

    internal static bool TryParseSingleRange(string value, long total, out long offset, out long length)
    {
        offset = 0; length = 0;
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || value.Contains(',')) return false;
        var parts = value[6..].Split('-', 2);
        if (parts.Length != 2) return false;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], out var suffix) || suffix <= 0 || total == 0) return false;
            length = Math.Min(suffix, total); offset = total - length; return true;
        }
        if (!long.TryParse(parts[0], out var start) || start < 0 || start >= total) return false;
        var end = total - 1;
        if (parts[1].Length > 0 && (!long.TryParse(parts[1], out end) || end < start)) return false;
        end = Math.Min(end, total - 1);
        offset = start; length = end - start + 1; return true;
    }
}
