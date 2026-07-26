using Hook2Stream.Domain;

namespace Hook2Stream.Application;

/// <summary>
/// Records an explicit user mutation for retention purposes. Background work
/// and read-only requests must not call this helper.
/// </summary>
public static class ProjectActivity
{
    public static void Touch(ReleaseProject project, TimeProvider timeProvider)
    {
        Touch(project, timeProvider.GetUtcNow());
    }

    public static void Touch(ReleaseProject project, DateTimeOffset occurredAt)
    {
        if (occurredAt > project.LastActivityAt)
        {
            project.LastActivityAt = occurredAt;
        }
    }
}
