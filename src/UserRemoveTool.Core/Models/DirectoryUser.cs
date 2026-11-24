namespace UserRemoveTool.Core.Models;

public sealed record DirectoryUser
{
    public required Guid ObjectGuid { get; init; }
    public required string DistinguishedName { get; init; }
    public required string SamAccountName { get; init; }
    public string DisplayName { get; init; } = "";
    public string Email { get; init; } = "";
    public string HomeDirectory { get; init; } = "";
    /// <summary>extensionAttribute1.</summary>
    public string NationalId { get; init; } = "";
    public string CurrentOu { get; init; } = "";
    public string Sid { get; init; } = "";
    /// <summary>Null when the attribute cannot be read; it must never be written back in that case.</summary>
    public int? UserAccountControl { get; init; }
    public int PrimaryGroupId { get; init; }
    public bool IsCriticalSystemObject { get; init; }
    public bool IsPrivileged { get; init; }
    public bool IsContact { get; init; }
    public string Kind => IsContact ? "איש קשר" : "משתמש";
    /// <summary>DNS name of the domain the object lives in, taken from its distinguished name.</summary>
    public string Domain => UserRemoveTool.Core.ActiveDirectory.LdapText.GetDnsDomain(DistinguishedName);
    public bool IsDisabled => UserAccountControl is { } uac && (uac & 0x2) != 0;
}

/// <summary>Search outcome; Source says where it was searched (entire directory, or a fallback).</summary>
public sealed record SearchResult(IReadOnlyList<DirectoryUser> Users, string Source);

public sealed record GroupMembership(string DistinguishedName, string Name, bool IsDomainUsers, bool IsPrimary);

public enum OperationStatus { Pending, Running, Success, Failed, Kept }

public sealed record GroupRemovalResult(GroupMembership Group, OperationStatus Status, string Message);
