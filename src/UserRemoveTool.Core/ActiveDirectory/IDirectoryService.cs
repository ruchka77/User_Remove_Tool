using UserRemoveTool.Core.Models;

namespace UserRemoveTool.Core.ActiveDirectory;

public interface IDirectoryService
{
    Task<IReadOnlyList<DirectoryUser>> SearchAsync(string term, CancellationToken ct);
    /// <summary>Re-reads the user by objectGUID, so a moved or renamed object is still found.</summary>
    Task<DirectoryUser> GetUserAsync(Guid objectGuid);
    Task EnsureTargetOuAsync();
    Task<IReadOnlyList<GroupMembership>> GetGroupsAsync(Guid objectGuid);
    Task RemoveFromGroupAsync(Guid objectGuid, GroupMembership group);
    Task MoveToTargetOuAsync(Guid objectGuid);
    Task DisableAsync(Guid objectGuid);
}
