using System.DirectoryServices.ActiveDirectory;
using System.DirectoryServices.Protocols;
using System.Net.Sockets;
using System.Security.Principal;
using System.Collections.Concurrent;
using UserRemoveTool.Core.Configuration;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.Models;

namespace UserRemoveTool.Core.ActiveDirectory;

/// <summary>
/// Active Directory access over LDAP with the logged-on user's Windows credentials (Kerberos via Negotiate),
/// signed and sealed. Search runs against the forest Global Catalog ("Entire Directory"); everything that reads
/// in detail or changes an object talks to a domain controller of the user's own domain, pinned per domain so that
/// consecutive writes do not hit replication lag.
/// </summary>
public sealed class LdapDirectoryService(ActiveDirectorySettings settings, IAppLogger log) : IDirectoryService, IDisposable
{
    private const int AccountDisable = 0x2; // ADS_UF_ACCOUNTDISABLE
    private const int DomainUsersRid = 513;
    private const int ServerDown = 81;
    private const int GlobalCatalogPort = 3268;
    private const int PageSize = 1000;

    private static readonly string[] UserAttributes =
    [
        "objectGUID", "objectSid", "objectClass", "displayName", "name", "sAMAccountName", "mail", "homeDirectory", "extensionAttribute1",
        "userAccountControl", "primaryGroupID", "isCriticalSystemObject", "adminCount"
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, string> _domainOf = new();
    private string? _homeDomain;
    private LdapConnection? _globalCatalog;

    private sealed record Session(LdapConnection Connection, string Domain, string BaseDn, SecurityIdentifier DomainSid);

    /// <summary>
    /// "Entire Directory": the Global Catalog, subtree from the root, enabled and disabled users (and contacts when
    /// configured), *word* on every name attribute. All pages are read up to the cap. If the Global Catalog cannot be
    /// reached, each domain of the forest is searched on its own.
    /// </summary>
    public Task<SearchResult> SearchAsync(string term, CancellationToken ct) => Run(() =>
    {
        var filter = LdapText.BuildUserFilter(term, settings.IncludeContacts);
        string source;
        List<DirectoryUser> users;
        if (settings.SearchEntireDirectory)
        {
            try
            {
                users = SearchGlobalCatalog(filter, ct);
                source = "כל מדריך הכתובות";
            }
            catch (Exception ex) when (IsUnreachable(ex))
            {
                log.Error("Global Catalog search failed; searching the domains one by one", ex);
                users = SearchDomains(filter, ct);
                source = "ה-Global Catalog לא זמין, החיפוש בוצע לפי דומיינים";
            }
        }
        else
        {
            users = SearchDomains(filter, ct, homeOnly: true);
            source = "דומיין אחד";
        }

        foreach (var user in users) _domainOf[user.ObjectGuid] = user.Domain;
        var sorted = users.Take(settings.MaxResults).OrderBy(u => u.DisplayName, StringComparer.CurrentCulture).ToList();
        return new SearchResult(sorted, source);
    }, ct);

    private static bool IsUnreachable(Exception ex) =>
        ex is LdapException { ErrorCode: ServerDown or 91 } or SocketException or ActiveDirectoryOperationException
            or ActiveDirectoryObjectNotFoundException;

    private List<DirectoryUser> SearchGlobalCatalog(string filter, CancellationToken ct)
    {
        try { return Paged(GetGlobalCatalog(), "", filter, ct); }
        catch (LdapException ex) when (ex.ErrorCode == ServerDown)
        {
            lock (_gate) { _globalCatalog = null; } // one retry on a fresh connection
            return Paged(GetGlobalCatalog(), "", filter, ct);
        }
    }

    private List<DirectoryUser> SearchDomains(string filter, CancellationToken ct, bool homeOnly = false)
    {
        var domains = new List<string?> { null };
        if (!homeOnly)
        {
            try { domains = Forest.GetCurrentForest().Domains.Cast<Domain>().Select(d => (string?)d.Name).ToList(); }
            catch (Exception ex) when (IsUnreachable(ex)) { log.Error("Cannot enumerate the forest domains", ex); }
        }

        var users = new List<DirectoryUser>();
        Exception? last = null;
        var succeeded = 0;
        foreach (var domain in domains)
        {
            if (users.Count >= settings.MaxResults) break;
            try
            {
                var found = Read(domain, s => Paged(s.Connection, s.BaseDn, filter, ct));
                users.AddRange(found);
                succeeded++;
            }
            catch (Exception ex) when (IsUnreachable(ex) || ex is DirectoryException)
            {
                last = ex;
                log.Error($"Search in domain {domain ?? settings.DomainController} failed", ex);
            }
        }
        if (succeeded == 0 && last is not null) throw last;
        return users;
    }

    /// <summary>Subtree search with paging (PageSize 1000, SizeLimit 0) until the pages end or the cap is reached.</summary>
    private List<DirectoryUser> Paged(LdapConnection connection, string baseDn, string filter, CancellationToken ct)
    {
        var users = new List<DirectoryUser>();
        var cookie = Array.Empty<byte>();
        do
        {
            ct.ThrowIfCancellationRequested();
            var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, UserAttributes) { SizeLimit = 0 };
            request.Controls.Add(new PageResultRequestControl(PageSize) { Cookie = cookie });
            var response = (SearchResponse)connection.SendRequest(request);
            users.AddRange(response.Entries.Cast<SearchResultEntry>().Select(ToUser));
            cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie ?? [];
        } while (cookie.Length > 0 && users.Count < settings.MaxResults);
        return users;
    }

    /// <summary>Full details come from a DC of the user's own domain; the Global Catalog holds only some attributes.</summary>
    public Task<DirectoryUser> GetUserAsync(Guid objectGuid) => Run(() => Read(DomainOf(objectGuid), s => FindUser(s, objectGuid)));

    /// <summary>The target OU must exist, and the user must be in the same domain: AD cannot move an object between domains with a plain move.</summary>
    public Task EnsureTargetOuAsync(Guid objectGuid) => Run(() =>
    {
        var targetDomain = LdapText.GetDnsDomain(settings.TargetOu);
        var userDomain = DomainOf(objectGuid) ?? Read(null, s => s.Domain);
        if (!string.Equals(userDomain, targetDomain, StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException($"המשתמש נמצא בדומיין {userDomain}, וה-OU היעד בדומיין {targetDomain}. לא ניתן להעביר משתמש בין דומיינים בכלי זה, ולכן לא בוצע שינוי.");
        return Read(targetDomain, s =>
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
        });
    });

    private string? DomainOf(Guid objectGuid) => _domainOf.TryGetValue(objectGuid, out var domain) ? domain : null;
    public Task<IReadOnlyList<GroupMembership>> GetGroupsAsync(Guid objectGuid) => Run(() => Read(DomainOf(objectGuid), s => LoadGroups(s, objectGuid)));

    public Task RemoveFromGroupAsync(Guid objectGuid, GroupMembership group) => Run(() => Write(DomainOf(objectGuid), s =>
    {
        var user = FindUser(s, objectGuid);
        try
        {
            // Universal groups may live in another domain of the forest and must be changed there.
            var groupDomain = LdapText.GetDnsDomain(group.DistinguishedName);
            var connection = groupDomain.Equals(s.Domain, StringComparison.OrdinalIgnoreCase) ? s.Connection : Connect(groupDomain).Connection;

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
        return true;
    }));

    public Task MoveToTargetOuAsync(Guid objectGuid) => Run(() => Write(DomainOf(objectGuid), s =>
    {
        var user = FindUser(s, objectGuid);
        if (IsInTargetOu(user)) return true;
        s.Connection.SendRequest(new ModifyDNRequest(user.DistinguishedName, settings.TargetOu, LdapText.GetRdn(user.DistinguishedName)) { DeleteOldRdn = true });
        if (!IsInTargetOu(FindUser(s, objectGuid)))
            throw new UserFacingException("ההעברה לא אומתה: המשתמש אינו נמצא ב-OU היעד.");
        return true;
    }));

    public Task DisableAsync(Guid objectGuid) => Run(() => Write(DomainOf(objectGuid), s =>
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

        var memberOf = Strings(entry, "memberOf").ToList();
        try
        {
            // A domain controller lists only its own domain's groups; the Global Catalog adds universal groups of other domains.
            var fromGc = GetEntry(GetGlobalCatalog(), $"<GUID={objectGuid}>", "memberOf");
            memberOf.AddRange(Strings(fromGc, "memberOf").Where(g => !memberOf.Contains(g, StringComparer.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (IsUnreachable(ex) || ex is DirectoryException or UserFacingException)
        {
            log.Error("Cannot read memberOf from the Global Catalog; only the domain groups are listed", ex);
        }

        foreach (var dn in memberOf)
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
            IsPrivileged = Text(e, "adminCount") == "1",
            IsContact = Strings(e, "objectClass").Contains("contact", StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string[] Strings(SearchResultEntry e, string name) =>
        e.Attributes[name] is { } a ? (string[])a.GetValues(typeof(string)) : [];
    private static string Text(SearchResultEntry e, string name) => Strings(e, name).FirstOrDefault() ?? "";
    private static byte[]? Bytes(SearchResultEntry e, string name) =>
        e.Attributes[name] is { Count: > 0 } a ? (byte[])a.GetValues(typeof(byte[]))[0] : null;

    private LdapConnection Open(string host, int port = 389)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Negotiate, // current Windows logon, Kerberos first
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds)
        };
        try
        {
            connection.SessionOptions.ProtocolVersion = 3;
            // Searches use the Global Catalog or one domain at a time, and writes must never follow a referral.
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

    /// <summary>The configured server, otherwise a Global Catalog server found for the current forest, otherwise the configured domain.</summary>
    private LdapConnection GetGlobalCatalog()
    {
        lock (_gate)
        {
            if (_globalCatalog is not null) return _globalCatalog;
            var host = settings.GlobalCatalogServer;
            if (string.IsNullOrWhiteSpace(host))
            {
                try { using var gc = Forest.GetCurrentForest().FindGlobalCatalog(); host = gc.Name; }
                catch (Exception ex) when (IsUnreachable(ex))
                {
                    log.Error("FindGlobalCatalog failed; trying the configured domain on the Global Catalog port", ex);
                    host = settings.DomainController;
                }
            }
            _globalCatalog = Open(host, GlobalCatalogPort);
            log.Info($"Connected to Global Catalog {host}:{GlobalCatalogPort}");
            return _globalCatalog;
        }
    }

    /// <summary>A pinned connection to a domain controller of the domain (null = the configured home domain).</summary>
    private Session Connect(string? domain = null)
    {
        lock (_gate)
        {
            domain ??= _homeDomain;
            if (domain is not null && _sessions.TryGetValue(domain, out var existing)) return existing;
            string host, baseDn;
            using (var locator = Open(domain ?? settings.DomainController))
            {
                var root = GetEntry(locator, "", "dnsHostName", "defaultNamingContext");
                host = Text(root, "dnsHostName");
                baseDn = Text(root, "defaultNamingContext");
            }
            var dnsDomain = LdapText.GetDnsDomain(baseDn);
            if (_sessions.TryGetValue(dnsDomain, out existing)) return existing;
            var connection = Open(host);
            try
            {
                var sid = GetSid(GetEntry(connection, baseDn, "objectSid"))
                          ?? throw new UserFacingException("לא ניתן לקרוא את מזהה הדומיין.");
                log.Info($"Connected to {host}, base {baseDn}");
                var session = new Session(connection, dnsDomain, baseDn, sid);
                _sessions[dnsDomain] = session;
                if (domain is null) _homeDomain = dnsDomain;
                return session;
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
            if (_sessions.TryGetValue(session.Domain, out var current) && ReferenceEquals(current, session))
                _sessions.Remove(session.Domain);
    }

    private T Read<T>(string? domain, Func<Session, T> action)
    {
        var session = Connect(domain);
        try { return action(session); }
        catch (LdapException ex) when (ex.ErrorCode == ServerDown)
        {
            Drop(session); // reads are safe to repeat on a fresh connection
            return action(Connect(session.Domain));
        }
    }

    private T Write<T>(string? domain, Func<Session, T> action)
    {
        var session = Connect(domain);
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
        catch (Exception ex) when (ex is SocketException or ActiveDirectoryOperationException or ActiveDirectoryObjectNotFoundException)
        {
            log.Error("Directory connection failed", ex);
            throw new UserFacingException("לא ניתן להתחבר ל-Active Directory (Domain Controller או Global Catalog).", ex);
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
            foreach (var s in _sessions.Values) s.Connection.Dispose();
            _sessions.Clear();
            _globalCatalog?.Dispose();
            _globalCatalog = null;
        }
    }
}