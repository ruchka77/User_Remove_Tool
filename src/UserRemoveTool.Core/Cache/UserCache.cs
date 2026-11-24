using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.Models;

namespace UserRemoveTool.Core.Cache;

/// <summary>
/// Local cache of users seen in earlier searches, for instant results while AD is queried in the background.
/// Stored next to the application (falling back to %LOCALAPPDATA% if that folder is read-only) and encrypted with
/// Windows DPAPI for the current user, because it contains names, e-mail addresses and national ids.
/// Entries older than the TTL are ignored. Every cached user is re-read from AD before anything is changed.
/// </summary>
public sealed class UserCache
{
    private const int MaxEntries = 50_000;
    private static readonly byte[] Entropy = "UserRemoveTool.cache.v1"u8.ToArray();

    private sealed class Entry
    {
        public Guid Guid { get; set; }
        public string Dn { get; set; } = "";
        public string Sam { get; set; } = "";
        public string Name { get; set; } = "";
        public string Mail { get; set; } = "";
        public string NationalId { get; set; } = "";
        public bool Contact { get; set; }
        public int? Uac { get; set; }
        public DateTimeOffset At { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly string[] _paths;
    private readonly TimeSpan _ttl;
    private readonly IAppLogger _log;
    private int _saving;

    public UserCache(string directory, TimeSpan ttl, IAppLogger log)
    {
        _ttl = ttl;
        _log = log;
        _paths = [Path.Combine(directory, "ad-cache.bin"),
                  Path.Combine(Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\UserRemoveTool\cache"), "ad-cache.bin")];
        Ready = Task.Run(Load);
    }

    /// <summary>Completes when the file has been read (never faults).</summary>
    public Task Ready { get; }

    public int Count { get { lock (_gate) return _entries.Count; } }

    public IReadOnlyList<DirectoryUser> Search(string term, int max)
    {
        var words = term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var oldest = DateTimeOffset.Now - _ttl;
        lock (_gate)
            return _entries.Values.Where(e => e.At >= oldest && words.All(w => Matches(e, w)))
                .Select(ToUser).OrderBy(u => u.DisplayName, StringComparer.CurrentCulture).Take(max).ToList();
    }

    private static bool Matches(Entry e, string w) =>
        new[] { e.Name, e.Sam, e.Mail, e.NationalId, Cn(e.Dn) }.Any(v => v.Contains(w, StringComparison.CurrentCultureIgnoreCase));

    private static string Cn(string dn) => dn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? dn[3..] : dn;

    public void Merge(IEnumerable<DirectoryUser> users)
    {
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            foreach (var u in users)
                _entries[u.ObjectGuid] = new Entry
                {
                    Guid = u.ObjectGuid, Dn = u.DistinguishedName, Sam = u.SamAccountName, Name = u.DisplayName, Mail = u.Email,
                    NationalId = u.NationalId, Contact = u.IsContact, Uac = u.UserAccountControl, At = now
                };
            if (_entries.Count > MaxEntries)
                foreach (var old in _entries.Values.OrderBy(e => e.At).Take(_entries.Count - MaxEntries).ToList())
                    _entries.Remove(old.Guid);
        }
        SaveInBackground();
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        SaveInBackground();
    }

    private static DirectoryUser ToUser(Entry e) => new()
    {
        ObjectGuid = e.Guid, DistinguishedName = e.Dn, SamAccountName = e.Sam, DisplayName = e.Name, Email = e.Mail,
        NationalId = e.NationalId, IsContact = e.Contact, UserAccountControl = e.Uac,
        CurrentOu = ActiveDirectory.LdapText.GetParent(e.Dn)
    };

    private void Load()
    {
        foreach (var path in _paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
                var oldest = DateTimeOffset.Now - _ttl;
                lock (_gate)
                    foreach (var e in JsonSerializer.Deserialize<List<Entry>>(json) ?? [])
                        if (e.At >= oldest) _entries[e.Guid] = e;
                _log.Info($"Cache loaded: {_entries.Count} users from {path}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
            {
                _log.Error($"Cache file {path} ignored", ex); // unreadable or from another user: start empty
            }
        }
    }

    private void SaveInBackground()
    {
        if (Interlocked.Exchange(ref _saving, 1) != 0) return; // a save is already queued; it will write the latest state
        Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false); // collapse bursts of keystrokes into one write
            Volatile.Write(ref _saving, 0);
            byte[] data;
            lock (_gate)
                data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_entries.Values.ToList()), Entropy, DataProtectionScope.CurrentUser);
            foreach (var path in _paths)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temp = path + ".tmp";
                    File.WriteAllBytes(temp, data);
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Error($"Cannot write cache to {path}", ex);
                }
            }
        });
    }
}