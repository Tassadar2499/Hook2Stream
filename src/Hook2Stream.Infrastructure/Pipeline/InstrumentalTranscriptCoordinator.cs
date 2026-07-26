using System.Text.Json;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Infrastructure.Pipeline;

/// <summary>
/// Keeps a confirmed instrumental release bound to an immutable transcript
/// revision for the current audio fingerprint. The revision is intentionally
/// created without invoking an external transcription provider.
/// </summary>
public static class InstrumentalTranscriptCoordinator
{
    public static async Task<TranscriptRevision> EnsureAsync(
        Hook2StreamDbContext db,
        ReleaseProject project,
        MediaAsset? audio,
        string fallbackActorSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!project.IsInstrumental || !project.IsInstrumentalConfirmed)
        {
            throw new InvalidOperationException(
                "An instrumental transcript can only be ensured for a confirmed instrumental release.");
        }

        var fingerprint = audio?.Sha256 ?? string.Empty;
        var current = project.CurrentTranscriptRevisionId is { } currentId
            ? await db.TranscriptRevisions.SingleOrDefaultAsync(
                value => value.Id == currentId && value.ProjectId == project.Id,
                cancellationToken)
            : null;

        if (current is
            {
                Source: TranscriptSource.Instrumental,
                State: RevisionState.Approved
            } &&
            string.Equals(current.SourceFingerprint, fingerprint, StringComparison.Ordinal) &&
            string.Equals(current.Language, project.Language, StringComparison.OrdinalIgnoreCase))
        {
            current.PhrasesJson = "[]";
            return current;
        }

        var matching = await db.TranscriptRevisions
            .Where(value =>
                value.ProjectId == project.Id &&
                value.Source == TranscriptSource.Instrumental &&
                value.State == RevisionState.Approved &&
                value.SourceFingerprint == fingerprint &&
                value.Language == project.Language)
            .OrderByDescending(value => value.Number)
            .FirstOrDefaultAsync(cancellationToken);
        if (matching is not null)
        {
            if (current is not null && current.Id != matching.Id &&
                current.State != RevisionState.Superseded)
            {
                current.State = RevisionState.Superseded;
            }

            matching.PhrasesJson = "[]";
            project.CurrentTranscriptRevisionId = matching.Id;
            project.LyricsText = null;
            return matching;
        }

        var attribution = current?.Source == TranscriptSource.Instrumental
            ? current
            : await db.TranscriptRevisions
                .Where(value =>
                    value.ProjectId == project.Id &&
                    value.Source == TranscriptSource.Instrumental &&
                    value.ApprovedAt != null)
                .OrderByDescending(value => value.Number)
                .FirstOrDefaultAsync(cancellationToken);
        var superseded = current ?? attribution;
        if (current is not null && current.State != RevisionState.Superseded)
        {
            current.State = RevisionState.Superseded;
        }

        var number = await db.TranscriptRevisions
            .Where(value => value.ProjectId == project.Id)
            .Select(value => value.Number)
            .DefaultIfEmpty()
            .MaxAsync(cancellationToken) + 1;
        var revision = new TranscriptRevision
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            Number = number,
            Source = TranscriptSource.Instrumental,
            State = RevisionState.Approved,
            Language = project.Language,
            PhrasesJson = "[]",
            SourceFingerprint = fingerprint,
            SupersedesRevisionId = superseded?.Id,
            ApprovedBySubject = attribution?.ApprovedBySubject ?? fallbackActorSubject,
            ApprovedAt = attribution?.ApprovedAt ?? now
        };
        db.TranscriptRevisions.Add(revision);
        project.CurrentTranscriptRevisionId = revision.Id;
        project.LyricsText = null;
        db.ProjectEvents.Add(new ProjectEvent
        {
            WorkspaceId = project.WorkspaceId,
            ProjectId = project.Id,
            EventType = "transcript.instrumental_rebound",
            DataJson = JsonSerializer.Serialize(new
            {
                transcriptRevisionId = revision.Id,
                audioAssetId = audio?.Id,
                sourceFingerprint = fingerprint
            })
        });
        return revision;
    }
}
