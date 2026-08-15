namespace Hook2Stream.Api.Authentication;

public sealed class ApplicationAuthenticationOptions
{
    public const string SectionName = "Auth";
    public const string OAuthMode = "OAuth";
    public const string LocalMode = "Local";

    public string Mode { get; set; } = OAuthMode;
    public string LocalToken { get; set; } = "";
    public string LocalSubject { get; set; } = "local-development-user";
    public string LocalEmail { get; set; } = "local@hook2stream.test";
    public string LocalDisplayName { get; set; } = "Local Developer";

    /// <summary>
    /// Prevents an otherwise valid OAuth identity from creating a new account
    /// unless its email address is explicitly listed in <see cref="InvitedEmails"/>.
    /// Existing users remain able to sign in after an invite is removed.
    /// </summary>
    public bool InviteOnly { get; set; } = true;

    public string[] InvitedEmails { get; set; } = [];

    /// <summary>
    /// Optional newline-delimited allowlist mounted as a host-managed file secret.
    /// Blank lines and lines beginning with '#' are ignored.
    /// </summary>
    public string InvitedEmailsFile { get; set; } = "";

    public bool IsInvited(string email)
    {
        if (InvitedEmails.Any(value =>
            !string.IsNullOrWhiteSpace(value) &&
            string.Equals(value.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(InvitedEmailsFile)) return false;

        try
        {
            return File.ReadLines(InvitedEmailsFile).Any(value =>
                !string.IsNullOrWhiteSpace(value) &&
                !value.TrimStart().StartsWith('#') &&
                string.Equals(value.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
