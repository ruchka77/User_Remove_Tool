using System.Text.Json;
using System.Text.Json.Serialization;

namespace UserRemoveTool.Core.Configuration;

public sealed class AppSettings
{
    public ActiveDirectorySettings ActiveDirectory { get; set; } = new();
    public ServiceDeskSettings ServiceDesk { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
            throw new UserFacingException($"קובץ ההגדרות לא נמצא: {path}");
        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJsonContext.Default.AppSettings)
                       ?? throw new JsonException("empty");
        }
        catch (JsonException ex)
        {
            throw new UserFacingException($"קובץ ההגדרות אינו תקין: {ex.Message}", ex);
        }
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        // Explicit nulls in the JSON must produce a clear message, not a NullReferenceException.
        if (ActiveDirectory is null || ServiceDesk is null || Logging is null)
            throw new UserFacingException("שגיאה בקובץ ההגדרות: חסר אחד מהמקטעים ActiveDirectory / ServiceDesk / Logging.");

        var errors = new List<string>();
        void Require(string? value, string key)
        {
            if (string.IsNullOrWhiteSpace(value)) errors.Add($"חסר ערך: {key}");
        }

        Require(ActiveDirectory.DomainController, "ActiveDirectory:DomainController");
        Require(ActiveDirectory.TargetOu, "ActiveDirectory:TargetOu");
        if (ActiveDirectory.TargetOu is { Length: > 0 } ou &&
            !(ou.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) && ou.Contains("DC=", StringComparison.OrdinalIgnoreCase)))
            errors.Add("ActiveDirectory:TargetOu אינו Distinguished Name תקין של OU");
        if (ActiveDirectory.MaxResults is < 1 or > 1000) errors.Add("ActiveDirectory:MaxResults חייב להיות בין 1 ל-1000");
        if (ActiveDirectory.TimeoutSeconds is < 1 or > 600) errors.Add("ActiveDirectory:TimeoutSeconds חייב להיות בין 1 ל-600");
        if (ActiveDirectory.MinSearchLength < 1) errors.Add("ActiveDirectory:MinSearchLength חייב להיות 1 לפחות");
        if (ActiveDirectory.SearchDelayMs < 0) errors.Add("ActiveDirectory:SearchDelayMs אינו תקין");

        foreach (var (key, url) in new[] { ("ServiceDesk:Url", ServiceDesk.Url), ("ServiceDesk:NewTicketUrl", ServiceDesk.NewTicketUrl) })
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
                errors.Add($"{key} אינו כתובת HTTP תקינה");
        }
        if (errors.Count == 0 && new Uri(ServiceDesk.Url).GetLeftPart(UriPartial.Authority) != new Uri(ServiceDesk.NewTicketUrl).GetLeftPart(UriPartial.Authority))
            errors.Add("ServiceDesk:Url ו-ServiceDesk:NewTicketUrl חייבים להיות באותו שרת");

        Require(ServiceDesk.AssigneeGroup, "ServiceDesk:AssigneeGroup");
        Require(ServiceDesk.Assignee, "ServiceDesk:Assignee");
        Require(ServiceDesk.Source, "ServiceDesk:Source");
        Require(ServiceDesk.Status, "ServiceDesk:Status");
        if (ServiceDesk.CategoryPath is not { Count: > 0 }) errors.Add("חסר ערך: ServiceDesk:CategoryPath");
        if (ServiceDesk.CategorySeparator is null) errors.Add("חסר ערך: ServiceDesk:CategorySeparator");
        if (ServiceDesk.SingleLineSeparator is null) errors.Add("חסר ערך: ServiceDesk:SingleLineSeparator");
        if (ServiceDesk.OpenStrategies is not { Count: > 0 }) errors.Add("חסר ערך: ServiceDesk:OpenStrategies");
        if (ServiceDesk.MenuTexts is null) errors.Add("חסר ערך: ServiceDesk:MenuTexts");
        if (ServiceDesk.Fields is null || ServiceDesk.Fields.Values.Any(f => f?.Selectors is null || f.Labels is null))
            errors.Add("ServiceDesk:Fields אינו תקין");
        if (ServiceDesk.LoginTimeoutSeconds < 1 || ServiceDesk.FormTimeoutSeconds < 1)
            errors.Add("ServiceDesk: ערכי Timeout חייבים להיות חיוביים");

        if (errors.Count > 0)
            throw new UserFacingException("שגיאה בקובץ ההגדרות:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }
}

public sealed class ActiveDirectorySettings
{
    public string DomainController { get; set; } = "moia.gov.il";
    public string TargetOu { get; set; } = "OU=Disabled Users,OU=Users,OU=MOIA,DC=moia,DC=gov,DC=il";
    public int MaxResults { get; set; } = 50;
    public int MinSearchLength { get; set; } = 2;
    public int SearchDelayMs { get; set; } = 300;
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>false = prefix match (indexed, fast); true = match anywhere (slower on large domains).</summary>
    public bool SearchAnywhere { get; set; }
}

public sealed class ServiceDeskSettings
{
    public string Url { get; set; } = "http://kl-sd-app/CAisd/pdmweb.exe";
    public string NewTicketUrl { get; set; } = "http://kl-sd-app/CAisd/html/popup_frames.html?POPUP_URLIX=0+popuptype=1";
    public List<string> OpenStrategies { get; set; } = new();
    public List<string> MenuTexts { get; set; } = new();
    public string TicketFactory { get; set; } = "cr";
    public List<string> CategoryPath { get; set; } = new();
    public string CategorySeparator { get; set; } = ".";
    public string SingleLineSeparator { get; set; } = " ";
    public string Source { get; set; } = "";
    public string Status { get; set; } = "";
    public string AssigneeGroup { get; set; } = "";
    public string Assignee { get; set; } = "";
    public string EdgePath { get; set; } = "";
    public int LoginTimeoutSeconds { get; set; } = 180;
    public int FormTimeoutSeconds { get; set; } = 15;
    public Dictionary<string, FieldLocator> Fields { get; set; } = new();
}

public sealed class FieldLocator
{
    public List<string> Selectors { get; set; } = new();
    public List<string> Labels { get; set; } = new();
}

public sealed class LoggingSettings
{
    public string Directory { get; set; } = @"%LOCALAPPDATA%\UserRemoveTool\Logs";
    /// <summary>Optional separate location (e.g. a central share) for the audit log.</summary>
    public string AuditDirectory { get; set; } = "";
}

[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
