namespace Hook2Stream.Api.Authentication;

public sealed class ApplicationAuthenticationOptions
{
    public const string SectionName = "Auth";
    public const string ClerkMode = "Clerk";
    public const string LocalMode = "Local";

    public string Mode { get; set; } = ClerkMode;
    public string LocalToken { get; set; } = "";
    public string LocalSubject { get; set; } = "local-development-user";
    public string LocalEmail { get; set; } = "local@hook2stream.test";
    public string LocalDisplayName { get; set; } = "Local Developer";
}
