using System.Security.Principal;
using System.Text.Json;
using UserRemoveTool.Core.ActiveDirectory;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.Models;

namespace UserRemoveTool.Core.Workflow;

/// <summary>Enforces the order of the offboarding steps independently of the UI. Every step is safe to retry.</summary>
public sealed class OffboardingWorkflow(IDirectoryService directory, IAuditLogger audit, IAppLogger log, RecordStore store, string? operatorSid = null)
{
    private readonly string? _operatorSid = operatorSid ?? WindowsIdentity.GetCurrent().User?.Value;
    private int _busy;

    public OffboardingRecord? Record { get; private set; }
    public DirectoryUser? User { get; private set; }
    public string? RecordPath { get; private set; }
    public bool GroupsRemoved { get; private set; }
    public bool Moved { get; private set; }
    public bool Disabled { get; private set; }
    public bool CanOpenTicket => GroupsRemoved && Moved && Disabled;

    /// <summary>Steps 1-2: re-reads the selected user, checks it is safe to continue and captures its data.</summary>
    public Task StartAsync(Guid objectGuid) => Exclusive(async () =>
    {
        ResetCore();
        var user = await directory.GetUserAsync(objectGuid).ConfigureAwait(false);
        if (_operatorSid is not null && user.Sid.Equals(_operatorSid, StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("לא ניתן להסיר את המשתמש שמפעיל את התוכנה.");
        if (user.IsContact)
            throw new UserFacingException("זהו איש קשר (Contact) ולא חשבון משתמש, ולכן אין מה להסיר ממנו.");
        if (user.IsCriticalSystemObject)
            throw new UserFacingException("זהו חשבון מערכת מובנה ולא ניתן להסירו.");
        // Checked before anything is changed, so the process does not get stuck half way.
        await directory.EnsureTargetOuAsync(user.ObjectGuid).ConfigureAwait(false);

        var record = new OffboardingRecord
        {
            CorrelationId = Guid.NewGuid().ToString("N"),
            CapturedAt = DateTimeOffset.Now,
            ObjectGuid = user.ObjectGuid,
            DisplayName = user.DisplayName,
            DistinguishedName = user.DistinguishedName,
            CurrentOu = user.CurrentOu,
            Mail = user.Email,
            HomeDirectory = user.HomeDirectory,
            NationalId = user.NationalId,
            SamAccountName = user.SamAccountName
        };
        RecordPath = store.Save(record);
        User = user;
        Record = record;
        Audit("UserSelected", AuditOutcome.Success, $"OU={user.CurrentOu}; mail={user.Email}; homeDirectory={user.HomeDirectory}");
        return true;
    });

    public Task<IReadOnlyList<GroupMembership>> GetGroupsAsync() => directory.GetGroupsAsync(RequireRecord().ObjectGuid);

    /// <summary>Step 3. Continues past individual failures and reports each group. Returns true when nothing is left.</summary>
    public Task<bool> RemoveGroupsAsync(Action<GroupRemovalResult> report) => Exclusive(async () =>
    {
        var record = RequireRecord();
        GroupsRemoved = false;
        var groups = await directory.GetGroupsAsync(record.ObjectGuid).ConfigureAwait(false);
        Audit("RemoveGroups", AuditOutcome.Attempt, $"{groups.Count(g => !g.IsDomainUsers)} groups");

        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            if (group.IsDomainUsers)
            {
                report(new(group, OperationStatus.Kept, "נשמרת תמיד"));
                Audit("RemoveGroup", AuditOutcome.Skipped, group.DistinguishedName);
                continue;
            }
            report(new(group, OperationStatus.Running, ""));
            Audit("RemoveGroup", AuditOutcome.Attempt, group.DistinguishedName);
            try
            {
                await directory.RemoveFromGroupAsync(record.ObjectGuid, group).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var message = failures[group.DistinguishedName] = Describe(ex);
                log.Error($"Removing {record.SamAccountName} from {group.DistinguishedName} failed", ex);
                report(new(group, OperationStatus.Failed, message));
                Audit("RemoveGroup", AuditOutcome.Failure, $"{group.DistinguishedName}: {message}");
                continue;
            }
            log.Info($"Removed {record.SamAccountName} from {group.DistinguishedName}");
            report(new(group, OperationStatus.Success, "הוסרה"));
            Audit("RemoveGroup", AuditOutcome.Success, group.DistinguishedName);
        }

        // The directory, not our bookkeeping, decides whether the step is complete.
        var left = (await directory.GetGroupsAsync(record.ObjectGuid).ConfigureAwait(false)).Where(g => !g.IsDomainUsers).ToList();
        foreach (var group in left)
            report(new(group, OperationStatus.Failed, failures.GetValueOrDefault(group.DistinguishedName, "המשתמש עדיין חבר בקבוצה. ניתן לנסות שוב.")));
        Audit("RemoveGroups", left.Count == 0 ? AuditOutcome.Success : AuditOutcome.Failure, $"{left.Count} groups left");
        GroupsRemoved = left.Count == 0;
        return GroupsRemoved;
    });

    /// <summary>Step 4. Moves, then disables only after the move succeeded.</summary>
    public Task MoveAndDisableAsync(Action<string> report) => Exclusive(async () =>
    {
        var record = RequireRecord();
        if (!GroupsRemoved) throw new UserFacingException("יש להשלים את הסרת הקבוצות לפני ההעברה.");

        if (!Moved)
        {
            report("מעביר את המשתמש ל-Disabled Users...");
            await AuditedAsync("MoveUser", () => directory.MoveToTargetOuAsync(record.ObjectGuid)).ConfigureAwait(false);
            Moved = true;
        }
        if (!Disabled)
        {
            report("מבצע Disable לחשבון...");
            try
            {
                await AuditedAsync("DisableUser", () => directory.DisableAsync(record.ObjectGuid)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new UserFacingException($"שים לב: המשתמש הועבר, אך לא אושר שהחשבון הושבת. {Describe(ex)}", ex);
            }
            Disabled = true;
        }
        report("המשתמש הועבר והחשבון הושבת.");
        return true;
    });

    /// <summary>Steps 5-6 read the persisted file, as captured before any change.</summary>
    public OffboardingRecord LoadRecordForTicket()
    {
        if (!CanOpenTicket) throw new UserFacingException("לא ניתן לפתוח קריאה לפני השלמת כל השלבים.");
        try { return store.Load(RecordPath!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Error("Temporary JSON is unavailable; using the in-memory copy", ex);
            return RequireRecord();
        }
    }

    public void Audit(string action, AuditOutcome outcome, string detail = "")
    {
        var record = RequireRecord();
        audit.Write(record.CorrelationId, action, outcome, record.SamAccountName, record.DistinguishedName, detail);
    }

    public void Reset()
    {
        if (Volatile.Read(ref _busy) == 0) ResetCore();
    }

    private void ResetCore()
    {
        if (RecordPath is not null) store.Delete(RecordPath);
        RecordPath = null;
        Record = null;
        User = null;
        GroupsRemoved = Moved = Disabled = false;
    }

    private async Task AuditedAsync(string action, Func<Task> work)
    {
        Audit(action, AuditOutcome.Attempt);
        try { await work().ConfigureAwait(false); }
        catch (Exception ex)
        {
            log.Error($"{action} failed", ex);
            Audit(action, AuditOutcome.Failure, Describe(ex));
            throw;
        }
        log.Info($"{action} succeeded for {RequireRecord().SamAccountName}");
        Audit(action, AuditOutcome.Success);
    }

    private OffboardingRecord RequireRecord() => Record ?? throw new UserFacingException("לא נבחר משתמש.");

    private static string Describe(Exception ex) => ex is UserFacingException ? ex.Message : $"שגיאה לא צפויה: {ex.Message}";

    private async Task<T> Exclusive<T>(Func<Task<T>> work)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) throw new UserFacingException("פעולה אחרת כבר מתבצעת.");
        try { return await work().ConfigureAwait(false); }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
