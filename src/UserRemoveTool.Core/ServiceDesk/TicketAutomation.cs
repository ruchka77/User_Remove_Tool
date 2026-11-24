using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using UserRemoveTool.Core.Configuration;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.Models;

namespace UserRemoveTool.Core.ServiceDesk;

public sealed record TicketField(string Key, string Caption, string Value);

public sealed record TicketFieldResult(TicketField Field, bool Filled, string Message);

public static class TicketContent
{
    public static string BuildSummary(OffboardingRecord r) =>
        $"Email:\n{r.Mail}\n\nHome Folder:\n{r.HomeDirectory}\n\nUsername:\n{r.SamAccountName}";

    /// <summary>Fill order: CA may derive group, assignee and status from the category, so they come after it.</summary>
    public static IReadOnlyList<TicketField> Build(OffboardingRecord r, ServiceDeskSettings s) =>
    [
        new("Customer", "לקוח", r.DisplayName),
        new("Category", "קטגוריה", string.Join(s.CategorySeparator, s.CategoryPath)),
        new("Summary", "תיאור מקוצר", BuildSummary(r)),
        new("Source", "מקור הקריאה", s.Source),
        new("AssigneeGroup", "צוות מטפל", s.AssigneeGroup),
        new("Assignee", "גורם מטפל", s.Assignee),
        new("Status", "סטטוס", s.Status)
    ];
}

public interface ITicketAutomation
{
    Task<IReadOnlyList<TicketFieldResult>> RunAsync(IReadOnlyList<TicketField> fields, Action<string> report, CancellationToken ct);
    void OpenManually();
}

/// <summary>
/// Opens CA Service Desk in Edge, opens a new ticket and fills it in. It then disconnects:
/// there is deliberately no code here that saves or submits a ticket.
/// </summary>
public sealed class TicketAutomation(ServiceDeskSettings settings, string profileDirectory, IAppLogger log) : ITicketAutomation
{
    private static readonly string Script = LoadScript();
    private readonly EdgeBrowser _edge = new(settings.EdgePath, profileDirectory, log);
    private readonly Uri _url = new(settings.Url);

    private string Origin => _url.GetLeftPart(UriPartial.Authority);

    private sealed class Browser(CdpClient cdp)
    {
        public CdpClient Cdp { get; } = cdp;
        public Dictionary<string, string> Sessions { get; } = new();
    }

    public void OpenManually() => _edge.OpenManually(settings.Url);

    public async Task<IReadOnlyList<TicketFieldResult>> RunAsync(IReadOnlyList<TicketField> fields, Action<string> report, CancellationToken ct)
    {
        report("פותח את Microsoft Edge...");
        using var cdp = await _edge.ConnectAsync(_url, ct).ConfigureAwait(false);
        var browser = new Browser(cdp);
        try
        {
            var created = await cdp.SendAsync("Target.createTarget", new JsonObject { ["url"] = settings.Url }, ct: ct).ConfigureAwait(false);
            var main = (string)created!["targetId"]!;
            await CloseBlankTabsAsync(browser, main, ct).ConfigureAwait(false);

            report("ממתין ל-CA Service Desk. אם מופיע מסך כניסה, יש להתחבר בחלון Edge.");
            await WaitForMainPageAsync(browser, main, ct).ConfigureAwait(false);

            report("פותח קריאה חדשה...");
            var form = await OpenFormAsync(browser, main, ct).ConfigureAwait(false);

            var results = await FillAsync(browser, form, fields, report, ct).ConfigureAwait(false);
            await cdp.SendAsync("Target.activateTarget", new JsonObject { ["targetId"] = form }, ct: ct).ConfigureAwait(false);
            return results;
        }
        catch (CdpException ex)
        {
            log.Error("Edge automation failed", ex);
            throw new UserFacingException("הקשר עם Edge נותק או שהדף אינו מגיב. ייתכן שחלון Edge נסגר או שמוצגת בו הודעה.", ex);
        }
    }

    private static async Task CloseBlankTabsAsync(Browser browser, string main, CancellationToken ct)
    {
        foreach (var (id, url) in await GetPagesAsync(browser, ct).ConfigureAwait(false))
            if (id != main && url == "about:blank")
                await browser.Cdp.SendAsync("Target.closeTarget", new JsonObject { ["targetId"] = id }, ct: ct).ConfigureAwait(false);
    }

    private async Task WaitForMainPageAsync(Browser browser, string main, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        Stopwatch? readyWithoutSession = null;
        while (total.Elapsed < TimeSpan.FromSeconds(settings.LoginTimeoutSeconds))
        {
            ct.ThrowIfCancellationRequested();
            var state = await TryCallAsync(browser, main, "state", null, ct).ConfigureAwait(false);
            var usable = state is not null && (bool)state["ready"]! && !(bool)state["login"]! && (string?)state["origin"] == Origin;
            if (usable && (string?)state!["sid"] is { Length: > 0 }) return;
            // Some versions do not expose the session id. Accept a page that stays loaded with no login form.
            if (!usable) readyWithoutSession = null;
            else if ((readyWithoutSession ??= Stopwatch.StartNew()).Elapsed > TimeSpan.FromSeconds(5)) return;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        throw new UserFacingException("CA Service Desk לא נטען בזמן שהוקצב (או שלא בוצעה התחברות).");
    }

    private async Task<string> OpenFormAsync(Browser browser, string main, CancellationToken ct)
    {
        var known = (await GetPagesAsync(browser, ct).ConfigureAwait(false)).Select(p => p.Id).ToHashSet();
        foreach (var strategy in settings.OpenStrategies)
        {
            log.Info($"New ticket: trying {strategy}");
            var started = await StartStrategyAsync(browser, main, strategy, ct).ConfigureAwait(false);
            if (!started) continue;

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(settings.FormTimeoutSeconds))
            {
                foreach (var (id, _) in await GetPagesAsync(browser, ct).ConfigureAwait(false))
                {
                    if (id != main && known.Contains(id)) continue; // never touch a window that was already open
                    if (await TryCallAsync(browser, id, "isNewForm", Locators(), ct).ConfigureAwait(false) is { } r && (bool)r)
                    {
                        log.Info($"New ticket form opened by {strategy}");
                        return id;
                    }
                }
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
            // Close what this attempt opened (no ticket form was found in it) before trying another way.
            foreach (var (id, _) in await GetPagesAsync(browser, ct).ConfigureAwait(false))
                if (!known.Contains(id))
                    await browser.Cdp.SendAsync("Target.closeTarget", new JsonObject { ["targetId"] = id }, ct: ct).ConfigureAwait(false);
        }
        throw new UserFacingException("טופס קריאה חדשה לא זוהה ב-CA. ניתן לפתוח קריאה ידנית ולהעתיק את הערכים מהטבלה.");
    }

    private async Task<bool> StartStrategyAsync(Browser browser, string main, string strategy, CancellationToken ct)
    {
        switch (strategy)
        {
            case "PopupUrl": // from the CA window, because the popup reads its content from its opener
                return await CallAsync(browser, main, "open", new JsonObject { ["url"] = settings.NewTicketUrl }, ct).ConfigureAwait(false) is { } p && (bool)p;
            case "Menu":
                return await CallAsync(browser, main, "clickMenu", new JsonObject { ["texts"] = ToArray(settings.MenuTexts) }, ct).ConfigureAwait(false) is { } m && (bool)m;
            case "CreateNewUrl":
                var state = await CallAsync(browser, main, "state", null, ct).ConfigureAwait(false);
                var sid = (string?)state?["sid"];
                var session = string.IsNullOrEmpty(sid) ? "" : $"SID={sid}+FID=0+";
                var url = $"{_url.GetLeftPart(UriPartial.Path)}?{session}OP=CREATE_NEW+FACTORY={settings.TicketFactory}";
                return await CallAsync(browser, main, "open", new JsonObject { ["url"] = url }, ct).ConfigureAwait(false) is { } c && (bool)c;
            default:
                log.Error($"Unknown OpenStrategies value: {strategy}");
                return false;
        }
    }

    private async Task<IReadOnlyList<TicketFieldResult>> FillAsync(Browser browser, string form, IReadOnlyList<TicketField> fields, Action<string> report, CancellationToken ct)
    {
        var problems = new Dictionary<string, string>();
        async Task SetAsync(TicketField field)
        {
            report($"ממלא: {field.Caption}");
            var r = await TryCallAsync(browser, form, "set", FieldArguments(field), ct).ConfigureAwait(false);
            problems[field.Key] = (string?)r?["status"] switch
            {
                "set" => "",
                "nooption" => "הערך אינו קיים ברשימה",
                "readonly" => "השדה לקריאה בלבד",
                "missing" => "השדה לא נמצא בטופס",
                _ => "השדה לא מולא"
            };
            await SettleAsync(browser, form, ct).ConfigureAwait(false);
        }

        async Task<TicketFieldResult> CheckAsync(TicketField field)
        {
            var r = await TryCallAsync(browser, form, "read", FieldArguments(field), ct).ConfigureAwait(false);
            if ((string?)r?["status"] == "ok")
            {
                if ((bool)r["tooLong"]!) return new(field, false, "הערך ארוך מהמותר בשדה");
                return new(field, true, (bool)r["flattened"]! ? "מולא בשורה אחת (השדה ב-CA אינו תומך בשורות)" : "מולא");
            }
            if (problems.GetValueOrDefault(field.Key) is { Length: > 0 } problem) return new(field, false, problem);
            return new(field, false, r is null || (string?)r["status"] == "missing" ? "השדה לא נמצא בטופס" : $"הערך בטופס שונה: {(string?)r["actual"]}");
        }

        foreach (var field in fields)
            if (field.Value.Length > 0) await SetAsync(field).ConfigureAwait(false);

        // CA scripts may overwrite earlier fields, so verify everything at the end and repair once.
        var repaired = false;
        foreach (var field in fields)
        {
            if (field.Value.Length == 0 || (await CheckAsync(field).ConfigureAwait(false)).Filled || problems[field.Key].Length > 0) continue;
            await SetAsync(field).ConfigureAwait(false);
            repaired = true;
        }
        if (repaired) await SettleAsync(browser, form, ct).ConfigureAwait(false);

        var results = new List<TicketFieldResult>();
        foreach (var field in fields)
        {
            var result = field.Value.Length == 0 ? new(field, false, "אין ערך למילוי") : await CheckAsync(field).ConfigureAwait(false);
            results.Add(result);
            log.Info($"Ticket field {field.Key}: {(result.Filled ? "filled" : "NOT filled")}");
        }
        return results;
    }

    private static async Task SettleAsync(Browser browser, string form, CancellationToken ct)
    {
        await Task.Delay(500, ct).ConfigureAwait(false);
        for (var i = 0; i < 20; i++)
        {
            if (await TryCallAsync(browser, form, "settled", null, ct).ConfigureAwait(false) is { } r && (bool)r) return;
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
    }

    private JsonObject Locator(string key)
    {
        settings.Fields.TryGetValue(key, out var f);
        return new JsonObject { ["selectors"] = ToArray(f?.Selectors ?? []), ["labels"] = ToArray(f?.Labels ?? []) };
    }

    private JsonObject Locators() => new()
    {
        ["summary"] = Locator("Summary"), ["customer"] = Locator("Customer"), ["category"] = Locator("Category")
    };

    private JsonObject FieldArguments(TicketField field) => new()
    {
        ["locator"] = Locator(field.Key), ["value"] = field.Value, ["separator"] = settings.SingleLineSeparator
    };

    private static JsonArray ToArray(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    private static async Task<List<(string Id, string Url)>> GetPagesAsync(Browser browser, CancellationToken ct)
    {
        var r = await browser.Cdp.SendAsync("Target.getTargets", ct: ct).ConfigureAwait(false);
        return r!["targetInfos"]!.AsArray().Where(t => (string?)t!["type"] == "page")
            .Select(t => ((string)t!["targetId"]!, (string?)t["url"] ?? "")).ToList();
    }

    /// <summary>For polling: a page that is navigating, closed or on another site simply counts as "not yet".</summary>
    private static async Task<JsonNode?> TryCallAsync(Browser browser, string targetId, string function, JsonObject? arguments, CancellationToken ct)
    {
        try { return await CallAsync(browser, targetId, function, arguments, ct).ConfigureAwait(false); }
        catch (CdpException ex) when (ex.Message != "connection closed") { return null; }
    }

    private static async Task<JsonNode?> CallAsync(Browser browser, string targetId, string function, JsonObject? arguments, CancellationToken ct)
    {
        if (!browser.Sessions.TryGetValue(targetId, out var session))
        {
            var attached = await browser.Cdp.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, ct: ct).ConfigureAwait(false);
            browser.Sessions[targetId] = session = (string)attached!["sessionId"]!;
        }
        // Values travel as JSON literals, so directory data can never become script.
        var expression = $"({Script}).{function}({arguments?.ToJsonString() ?? ""})";
        var r = await browser.Cdp.SendAsync("Runtime.evaluate",
            new JsonObject { ["expression"] = expression, ["returnByValue"] = true, ["userGesture"] = true }, session, ct).ConfigureAwait(false);
        if (r?["exceptionDetails"] is { } details)
            throw new CdpException((string?)details["exception"]?["description"] ?? "script error");
        return r?["result"]?["value"]?.DeepClone();
    }

    private static string LoadScript()
    {
        using var stream = typeof(TicketAutomation).Assembly.GetManifestResourceStream("ca-ticket.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
