namespace Hook2Stream.Domain;

/// <summary>
/// Privacy-safe evidence that a project deletion was requested and whether its
/// content purge has completed. The record intentionally has no FK to the
/// project so it survives content removal.
/// </summary>
public sealed class ProjectDeletionTombstone : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public string? ActorSubject { get; set; }
    public string PolicyVersion { get; set; } = "retention-v1";
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset PurgeDueAt { get; set; }
    public DateTimeOffset? ContentPurgedAt { get; set; }
    public string State { get; set; } = "queued";
    public string? LastError { get; set; }
}
