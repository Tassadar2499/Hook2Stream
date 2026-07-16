using System.Security.Claims;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Api;

public sealed record CurrentWorkspace(AppUser User, Workspace Workspace);

public sealed class CurrentUserService(
    IHttpContextAccessor httpContextAccessor,
    Hook2StreamDbContext dbContext)
{
    public string Subject => Principal.FindFirstValue("sub")
        ?? throw new InvalidOperationException("Authenticated token does not contain a subject.");

    public ClaimsPrincipal Principal =>
        httpContextAccessor.HttpContext?.User
        ?? throw new InvalidOperationException("No HTTP user is available.");

    public async Task<AppUser> EnsureUserAsync(CancellationToken cancellationToken)
    {
        var subject = Subject;
        var user = await dbContext.Users
            .SingleOrDefaultAsync(value => value.ClerkSubject == subject, cancellationToken);

        var email = Principal.FindFirstValue("email")
            ?? Principal.FindFirstValue("primary_email_address");
        var displayName = Principal.FindFirstValue("name")
            ?? Principal.FindFirstValue("first_name");

        if (user is null)
        {
            user = new AppUser
            {
                ClerkSubject = subject,
                Email = email,
                DisplayName = displayName
            };
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else if (user.Email != email || user.DisplayName != displayName)
        {
            user.Email = email;
            user.DisplayName = displayName;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return user;
    }

    public async Task<CurrentWorkspace?> GetWorkspaceAsync(CancellationToken cancellationToken)
    {
        var user = await EnsureUserAsync(cancellationToken);
        var workspace = await dbContext.Workspaces
            .SingleOrDefaultAsync(value => value.OwnerUserId == user.Id, cancellationToken);
        return workspace is null ? null : new CurrentWorkspace(user, workspace);
    }

    public async Task<CurrentWorkspace> RequireWorkspaceAsync(CancellationToken cancellationToken) =>
        await GetWorkspaceAsync(cancellationToken)
        ?? throw new ApiProblemException(
            StatusCodes.Status409Conflict,
            "account.onboarding_required",
            "Complete onboarding before using the workspace.");
}
