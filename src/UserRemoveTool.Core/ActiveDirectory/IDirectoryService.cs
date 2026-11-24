using UserRemoveTool.Core.Models;

namespace UserRemoveTool.Core.ActiveDirectory;

public interface IDirectoryService
{
    Task<SearchResult> SearchAsync(string term, CancellationToken ct);
    /// <summary>Re-reads the user by objectGUID, so a moved or renamed object is still found.</summary>
    Task<DirectoryUser> GetUserAsync(Guid objectGuid);
    /// <summary>Checks the target OU exists and is in the user's domain, before anything is changed.</summary>
    Task EnsureTargetOuAsync(Guid objectGuid);
    Task<IReadOnlyList<GroupMembership>> GetGroupsAsync(Guid objectGuid);
    Task RemoveFromGroupAsync(Guid objectGuid, GroupMembership group);
    Task MoveToTargetOuAsync(Guid objectGuid);
    Task DisableAsync(Guid objectGuid);
}
