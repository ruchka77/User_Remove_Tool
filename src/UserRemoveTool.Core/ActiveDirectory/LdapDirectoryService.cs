using System.DirectoryServices.Protocols;
using System.Security.Principal;
using UserRemoveTool.Core.Configuration;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.Models;

namespace UserRemoveTool.Core.ActiveDirectory;

/// <summary>
/// Active Directory access over LDAP with the logged-on user's Windows credentials (Kerberos via Negotiate),
/// signed and sealed. One DC is pinned for the session so that consecutive writes do not hit replication lag.
/// </summary>
public sealed class LdapDirectoryService(ActiveDirectorySettings settings, IAppLogger log) : IDirectoryService, IDisposable
{
    private const int AccountDisable = 0x2; // ADS_UF_ACCOUNTDISABLE
    private const int DomainUsersRid = 513;
    private const int ServerDown = 81;

    private static readonly string[] UserAttributes =
    [
        "objectGUID", "objectSid", "displayName", "name", "sAMAccountName", "mail", "homeDirectory", "extensionAttribute1",
        "userAccountControl", "primaryGroupID", "isCriticalSystemObject", "adminCount"
    ];

    private readonly object _gate = new();
    private Session? _session;

    private sealed record Session(LdapConnection Connection, string BaseDn, SecurityIdentifier DomainSid);

    private const int PageSize = 1000;

    /// <summary>
    /// Like "Find Users, Contacts, and Groups": whole domain, subtree, enabled and disabled users, *term* on all name
    /// attributes. Every page is read until the cap, so AD never truncates silently at the first page.
    /// </summary>
    public Task<IReadOnlyList<DirectoryUser>> SearchAsync(string term, CancellationToken ct) => Run(() => Read(s =>
    {
        var filter = LdapText.BuildUserFilter(term);
        var users = new List<DirectoryUser>();
        var cookie = Array.Empty<byte>();
        do
        {
            ct.ThrowIfCancellationRequested();
            var request = new SearchRequest(s.BaseDn, filter, SearchScope.Subtree, UserAttributes) { SizeLimit = 0 };
            request.Controls.Add(new PageResultRequestControl(PageSize) { Cookie = cookie });
            var response = (SearchResponse)s.Connection.SendRequest(request);
            users.AddRange(response.Entries.Cast<SearchResultEntry>().Select(ToUser));
            cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie ?? [];
        } while (cookie.Length > 0 && users.Count < settings.MaxResults);

        return (IReadOnlyList<DirectoryUser>)users.Take(settings.MaxResults)
            .OrderBy(u => u.DisplayName, StringComparer.CurrentCulture).ToList();
    }), ct);
    public Task<DirectoryUser> GetUserAsync(Guid objectGuid) => Run(() => Read(s => FindUser(s, objectGuid)));

    public Task EnsureTargetOuAsync() => Run(() => Read(s =>
    {
        try
        {
            GetEntry(s.Connection, settings.TargetOu, "distinguishedName");
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode is ResultCode.NoSuchObject or ResultCode.InvalidDNSyntax or ResultCode.Referral)
        {
            throw new UserFacingException($"ה-OU היעד לא נמצא: {settings.TargetOu}", ex);
        }
        return true;
    }));

    public Task<IReadOnlyList<GroupMembership>> GetGroupsAsync(Guid objectGuid) => Run(() => Read(s => LoadGroups(s, objectGuid)));

    public Task RemoveFromGroupAsync(Guid objectGuid, GroupMembership group) => Run(() => Write(s =>
    {
        var user = FindUser(s, objectGuid);
        LdapConnection? foreign = null;
        try
        {
            // Universal groups may live in another domain of the forest and must be changed there.
            var groupDomain = LdapText.GetDnsDomain(group.DistinguishedName);
            var connection = s.Connection;
            if (!groupDomain.Equals(LdapText.GetDnsDomain(s.BaseDn), StringComparison.OrdinalIgnoreCase))
                connection = foreign = Open(groupDomain);

            // Last line of defence: decide by the group's SID as it is right now, never by its name.
            var groupSid = GetSid(GetEntry(connection, group.DistinguishedName, "objectSid"))
                           ?? throw new UserFacingException("לא ניתן לזהות את הקבוצה, ולכן היא לא הוסרה.");
            if (IsDomainUsers(groupSid))
                throw new UserFacingException("הסרה מ-Domain Users חסומה.");

            if (groupSid.Value == $"{s.DomainSid.Value}-{user.PrimaryGroupId}")
            {
                // AD refuses to remove a primary group. Switching requires existing membership in Domain Users.
                try
                {
                    s.Connection.SendRequest(new ModifyRequest(user.DistinguishedName, DirectoryAttributeOperation.Replace,
                        "primaryGroupID", DomainUsersRid.ToString()));
                }
                catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.UnwillingToPerform)
                {
                    throw new UserFacingException("זו הקבוצה הראשית, והמשתמש אינו חבר ב-Domain Users. יש לשנות קבוצה ראשית ידנית ולנסות שוב.", ex);
                }
                log.Info($"Primary group of {user.SamAccountName} changed from {group.DistinguishedName} to Domain Users");
            }

            // Deletes one value only; never loads or rewrites the (possibly huge) member list.
            connection.SendRequest(new ModifyRequest(group.DistinguishedName, DirectoryAttributeOperation.Delete, "member", user.DistinguishedName));
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode is ResultCode.UnwillingToPerform or ResultCode.NoSuchAttribute)
        {
            if (LoadGroups(s, objectGuid).Any(g => g.DistinguishedName.Equals(group.DistinguishedName, StringComparison.OrdinalIgnoreCase)))
                throw;
            log.Info($"{user.SamAccountName} was already not a member of {group.DistinguishedName}");
        }
        finally
        {
            foreign?.Dispose();
        }
        return true;
    }));

    public Task MoveToTargetOuAsync(Guid objectGuid) => Run(() => Write(s =>
    {
        var user = FindUser(s, objectGuid);
        if (IsInTargetOu(user)) return true;
        s.Connection.SendRequest(new ModifyDNRequest(user.DistinguishedName, settings.TargetOu, LdapText.GetRdn(user.DistinguishedName)) { DeleteOldRdn = true });
        if (!IsInTargetOu(FindUser(s, objectGuid)))
            throw new UserFacingException("ההעברה לא אומתה: המשתמש אינו נמצא ב-OU היעד.");
        return true;
    }));

    public Task DisableAsync(Guid objectGuid) => Run(() => Write(s =>
    {
        var user = FindUser(s, objectGuid);
        if (user.IsDisabled) return true;
        var current = user.UserAccountControl
                      ?? throw new UserFacingException("לא ניתן לקרוא את userAccountControl (ייתכן שחסרה הרשאה).");
        s.Connection.SendRequest(new ModifyRequest(user.DistinguishedName, DirectoryAttributeOperation.Replace,
            "userAccountControl", (current | AccountDisable).ToString()));
        if (!FindUser(s, objectGuid).IsDisabled)
            throw new UserFacingException("ההשבתה לא אומתה: החשבון עדיין פעיל.");
        return true;
    }));

    private bool IsInTargetOu(DirectoryUser user) => LdapText.DnEquals(LdapText.GetParent(user.DistinguishedName), settings.TargetOu);

    private IReadOnlyList<GroupMembership> LoadGroups(Session s, Guid objectGuid)
    {
        var entry = GetEntry(s.Connection, $"<GUID={objectGuid}>", "memberOf", "primaryGroupID");
        var result = new List<GroupMembership>();

        foreach (var dn in Strings(entry, "memberOf"))
        {
            var sid = TryGetGroupSid(s, dn);
            result.Add(new GroupMembership(dn, NameOf(dn), sid is not null && IsDomainUsers(sid), false));
        }

        // The primary group is never listed in memberOf.
        if (!int.TryParse(Text(entry, "primaryGroupID"), out var rid))
            throw new UserFacingException("לא ניתן לקרוא את הקבוצה הראשית של המשתמש.");
        var primary = new SecurityIdentifier($"{s.DomainSid.Value}-{rid}");
        var bytes = new byte[primary.BinaryLength];
        primary.GetBinaryForm(bytes, 0);
        var found = (SearchResponse)s.Connection.SendRequest(new SearchRequest(s.BaseDn,
            $"(objectSid={LdapText.EscapeFilter(bytes)})", SearchScope.Subtree, "objectSid"));
        if (found.Entries.Count == 1)
        {
            var dn = found.Entries[0].DistinguishedName;
            result.Add(new GroupMembership(dn, NameOf(dn), rid == DomainUsersRid, true));
        }
        else if (rid != DomainUsersRid)
        {
            throw new UserFacingException($"לא ניתן לאתר את הקבוצה הראשית של המשתמש (RID {rid}).");
        }
        return result.OrderBy(g => g.IsDomainUsers).ThenBy(g => g.Name, StringComparer.CurrentCulture).ToList();
    }

    private SecurityIdentifier? TryGetGroupSid(Session s, string dn)
    {
        try { return GetSid(GetEntry(s.Connection, dn, "objectSid")); }
        catch (Exception ex) when (ex is DirectoryOperationException or UserFacingException)
        {
            // Used for display only. RemoveFromGroupAsync refuses to act on a group whose SID it cannot read.
            log.Error($"Cannot read SID of {dn}", ex);
            return null;
        }
    }

    private static string NameOf(string dn)
    {
        var rdn = LdapText.GetRdn(dn);
        return rdn[(rdn.IndexOf('=') + 1)..].Replace("\\", "");
    }

    private static bool IsDomainUsers(SecurityIdentifier sid) => sid.IsWellKnown(WellKnownSidType.AccountDomainUsersSid);

    private static SecurityIdentifier? GetSid(SearchResultEntry entry) =>
        Bytes(entry, "objectSid") is { } bytes ? new SecurityIdentifier(bytes, 0) : null;

    private static DirectoryUser FindUser(Session s, Guid objectGuid) =>
        ToUser(GetEntry(s.Connection, $"<GUID={objectGuid}>", UserAttributes));

    private static SearchResultEntry GetEntry(LdapConnection connection, string dn, params string[] attributes)
    {
        var response = (SearchResponse)connection.SendRequest(new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, attributes));
        return response.Entries.Count == 1 ? response.Entries[0]
            : throw new UserFacingException("האובייקט לא נמצא ב-Active Directory או שאין הרשאה לקרוא אותו.");
    }

    private static DirectoryUser ToUser(SearchResultEntry e)
    {
        var dn = e.DistinguishedName;
        var display = Text(e, "displayName");
        return new DirectoryUser
        {
            ObjectGuid = new Guid(Bytes(e, "objectGUID") ?? throw new UserFacingException("לא ניתן לקרוא את מזהה המשתמש (objectGUID).")),
            DistinguishedName = dn,
            SamAccountName = Text(e, "sAMAccountName"),
            DisplayName = display.Length > 0 ? display : Text(e, "name"),
            Email = Text(e, "mail"),
            HomeDirectory = Text(e, "homeDirectory"),
            NationalId = Text(e, "extensionAttribute1"),
            CurrentOu = LdapText.GetParent(dn),
            Sid = GetSid(e)?.Value ?? "",
            UserAccountControl = int.TryParse(Text(e, "userAccountControl"), out var uac) ? uac : null,
            PrimaryGroupId = int.TryParse(Text(e, "primaryGroupID"), out var pg) ? pg : 0,
            IsCriticalSystemObject = Text(e, "isCriticalSystemObject").Equals("TRUE", StringComparison.OrdinalIgnoreCase),
            IsPrivileged = Text(e, "adminCount") == "1"
        };
    }

    private static string[] Strings(SearchResultEntry e, string name) =>
        e.Attributes[name] is { } a ? (string[])a.GetValues(typeof(string)) : [];
    private static string Text(SearchResultEntry e, string name) => Strings(e, name).FirstOrDefault() ?? "";
    private static byte[]? Bytes(SearchResultEntry e, string name) =>
        e.Attributes[name] is { Count: > 0 } a ? (byte[])a.GetValues(typeof(byte[]))[0] : null;

    private LdapConnection Open(string host)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(host))
        {
            AuthType = AuthType.Negotiate, // current Windows logon, Kerberos first
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds)
        };
        try
        {
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            connection.SessionOptions.Signing = true;
            connection.SessionOptions.Sealing = true;
            connection.Bind();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private Session Connect()
    {
        lock (_gate)
        {
            if (_session is not null) return _session;
            string host, baseDn;
            using (var locator = Open(settings.DomainController))
            {
                var root = GetEntry(locator, "", "dnsHostName", "defaultNamingContext");
                host = Text(root, "dnsHostName");
                baseDn = Text(root, "defaultNamingContext");
            }
            var connection = Open(host);
            try
            {
                var sid = GetSid(GetEntry(connection, baseDn, "objectSid"))
                          ?? throw new UserFacingException("לא ניתן לקרוא את מזהה הדומיין.");
                log.Info($"Connected to {host}, base {baseDn}");
                return _session = new Session(connection, baseDn, sid);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }

    private void Drop(Session session)
    {
        // Not disposed here: another read may still be using it; the finalizer releases the handle.
        lock (_gate)
            if (ReferenceEquals(_session, session)) _session = null;
    }

    private T Read<T>(Func<Session, T> action)
    {
        var session = Connect();
        try { return action(session); }
        catch (LdapException ex) when (ex.ErrorCode == ServerDown)
        {
            Drop(session); // reads are safe to repeat on a fresh connection
            return action(Connect());
        }
    }

    private T Write<T>(Func<Session, T> action)
    {
        var session = Connect();
        try { return action(session); }
        catch (LdapException ex) when (ex.ErrorCode == ServerDown)
        {
            Drop(session); // a write is never repeated automatically; the operator decides
            throw;
        }
    }

    private Task<T> Run<T>(Func<T> action, CancellationToken ct = default) => Task.Run(() =>
    {
        try { return action(); }
        catch (DirectoryException ex)
        {
            log.Error("LDAP operation failed", ex);
            throw new UserFacingException(Translate(ex), ex);
        }
    }, ct);

    private static string Translate(Exception ex) => ex switch
    {
        DirectoryOperationException { Response: { } r } => r.ResultCode switch
        {
            ResultCode.InsufficientAccessRights => "אין הרשאה לבצע את הפעולה ב-Active Directory.",
            ResultCode.EntryAlreadyExists => "כבר קיים אובייקט באותו שם ב-OU היעד.",
            ResultCode.NoSuchObject => "האובייקט לא נמצא ב-Active Directory.",
            ResultCode.UnwillingToPerform => $"השרת סירב לבצע את הפעולה: {r.ErrorMessage}",
            ResultCode.ConstraintViolation => $"הפעולה נדחתה בשל מגבלת מדיניות: {r.ErrorMessage}",
            _ => $"שגיאת Active Directory ({r.ResultCode}): {r.ErrorMessage}"
        },
        LdapException { ErrorCode: ServerDown } => "לא ניתן להתחבר ל-Domain Controller.",
        LdapException { ErrorCode: 49 or 82 } => "האימות מול Active Directory נכשל עבור המשתמש המחובר.",
        LdapException { ErrorCode: 85 } => "תם הזמן שהוקצב לתשובה מ-Active Directory. ייתכן שהפעולה בוצעה; יש לנסות שוב כדי לבדוק.",
        LdapException l => $"שגיאת LDAP ({l.ErrorCode}): {l.Message}",
        _ => ex.Message
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _session?.Connection.Dispose();
            _session = null;
        }
    }
}
