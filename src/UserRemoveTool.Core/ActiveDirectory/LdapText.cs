using System.Text;

namespace UserRemoveTool.Core.ActiveDirectory;

/// <summary>Escaping and DN parsing helpers (RFC 4514 / RFC 4515).</summary>
public static class LdapText
{
    public static string EscapeFilter(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(c switch { '\\' => @"\5c", '*' => @"\2a", '(' => @"\28", ')' => @"\29", '\0' => @"\00", _ => c.ToString() });
        return sb.ToString();
    }

    public static string EscapeFilter(byte[] value) => string.Concat(value.Select(b => $"\\{b:x2}"));

    private static int FirstSeparator(string dn)
    {
        var quoted = false;
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\') i++;
            else if (dn[i] == '"') quoted = !quoted;
            else if (dn[i] == ',' && !quoted) return i;
        }
        return -1;
    }

    public static string GetRdn(string dn) => FirstSeparator(dn) is var i and >= 0 ? dn[..i] : dn;

    public static string GetParent(string dn) => FirstSeparator(dn) is var i and >= 0 ? dn[(i + 1)..] : "";

    /// <summary>"CN=x,OU=y,DC=a,DC=b" => "a.b".</summary>
    public static string GetDnsDomain(string dn)
    {
        var parts = new List<string>();
        for (var rest = dn; rest.Length > 0; rest = GetParent(rest))
        {
            var rdn = GetRdn(rest).Trim();
            if (rdn.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)) parts.Add(rdn[3..]);
        }
        return string.Join('.', parts);
    }

    public static bool DnEquals(string a, string b)
    {
        static string Normalize(string dn) => string.Join(',', dn.Split(',').Select(p => p.Trim()));
        return Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The attributes searched, as in ADUC "Find Users, Contacts, and Groups" plus mail, description and the national id.</summary>
    public static readonly string[] SearchAttributes =
    [
        "displayName", "cn", "name", "givenName", "sn", "sAMAccountName", "userPrincipalName", "mail", "description", "extensionAttribute1"
    ];

    /// <summary>
    /// Users only (enabled and disabled). Every whitespace-separated word must appear (*word*) in at least one attribute,
    /// so "משה כהן" finds givenName + sn as well as a display name.
    /// </summary>
    public static string BuildUserFilter(string term)
    {
        string Any(string word) => "(|" + string.Concat(SearchAttributes.Select(a => $"({a}=*{EscapeFilter(word)}*)")) + ")";
        var words = term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return $"(&(objectCategory=person)(objectClass=user){string.Concat(words.Select(Any))})";
    }}
