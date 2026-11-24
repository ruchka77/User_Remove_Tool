namespace UserRemoveTool.Core.Models;

public sealed record DirectoryUser
{
    public required Guid ObjectGuid { get; init; }
    public required string DistinguishedName { get; init; }
    public required string SamAccountName { get; init; }
    public string DisplayName { get; init; } = "";
    public string Email { get; init; } = "";
    public string HomeDirectory { get; init; } = "";
    public string CurrentOu { get; init; } = "";
    public string Sid { get; init; } = "";
    /// <summary>Null when the attribute cannot be read; it must never be written back in that case.</summary>
    public int? UserAccountControl { get; init; }
    public int PrimaryGroupId { get; init; }
    public bool IsCriticalSystemObject { get; init; }
    public bool IsPrivileged { get; init; }
    public bool IsDisabled => UserAccountControl is { } uac && (uac & 0x2) != 0;
}

public sealed record GroupMembership(string DistinguishedName, string Name, bool IsDomainUsers, bool IsPrimary);

public enum OperationStatus { Pending, Running, Success, Failed, Kept }

public sealed record GroupRemovalResult(GroupMembership Group, OperationStatus Status, string Message);
