using System.IO;
using System.Text;
using UserRemoveTool.Core;
using UserRemoveTool.Core.ActiveDirectory;
using UserRemoveTool.Core.Configuration;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.Models;
using UserRemoveTool.Core.ServiceDesk;
using UserRemoveTool.Core.Workflow;
using UserRemoveTool.Views;

namespace UserRemoveTool;

internal static class Program
{
    private static int Main()
    {
        ConsoleOut.Setup();
        int code;
        try { code = new Cli().RunAsync().GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Cli.Fail($"אירעה שגיאה לא צפויה: {ex.Message}");
            code = 1;
        }
        ConsoleOut.Line();
        ConsoleOut.Line("לחץ Enter לסגירה");
        Console.Write("> ");
        Console.ReadLine();
        return code;
    }
}

/// <summary>Runs the whole offboarding in the console. Only the user search opens a window.</summary>
internal sealed class Cli
{
    private const int TotalSteps = 6;
    private IAppLogger? _log;

    public async Task<int> RunAsync()
    {
        AppSettings settings;
        OffboardingWorkflow workflow;
        LdapDirectoryService directory;
        try
        {
            settings = AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
            var logDirectory = Resolve(settings.Logging.Directory, @"%LOCALAPPDATA%\UserRemoveTool\Logs");
            _log = new FileLogger(logDirectory);
            var audit = new AuditLogger(Resolve(settings.Logging.AuditDirectory, logDirectory));
            var store = new RecordStore();
            store.DeleteStale(TimeSpan.FromDays(1));
            directory = new LdapDirectoryService(settings.ActiveDirectory, _log);
            workflow = new OffboardingWorkflow(directory, audit, _log, store);
            ConsoleOut.VisualHebrew = settings.Terminal.VisualHebrew;
            _log.Info("Application started");
        }
        catch (UserFacingException ex)
        {
            Fail(ex.Message);
            return 1;
        }

        var tickets = new TicketAutomation(settings.ServiceDesk, Resolve(null, @"%LOCALAPPDATA%\UserRemoveTool\EdgeProfile"), _log);
        try
        {
            return await RunWorkflowAsync(settings, directory, workflow, tickets);
        }
        catch (UserFacingException ex)
        {
            _log.Error(ex.Message, ex.InnerException);
            Fail(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            _log.Error("Unexpected error", ex);
            Fail($"אירעה שגיאה לא צפויה: {ex.Message}");
            return 1;
        }
        finally
        {
            workflow.Reset(); // deletes the temporary JSON file
            directory.Dispose();
        }
    }

    private async Task<int> RunWorkflowAsync(AppSettings settings, IDirectoryService directory, OffboardingWorkflow workflow, ITicketAutomation tickets)
    {
        ConsoleOut.Line("=== הסרת משתמש ===");

        // Step 1: the only pop-up window
        Step(1, "חיפוש משתמש (נפתח חלון חיפוש)");
        var picked = SearchWindow.Pick(directory, settings.ActiveDirectory, _log!);
        if (picked is null)
        {
            ConsoleOut.Line("החיפוש בוטל. לא בוצע שינוי.");
            return 2;
        }

        // Step 2
        Step(2, "קריאת נתוני המשתמש");
        await workflow.StartAsync(picked.ObjectGuid);
        var record = workflow.Record!;
        Field("שם מלא", record.DisplayName);
        Field("שם משתמש", record.SamAccountName);
        Field("מייל", record.Mail);
        Field("תיקיית בית", record.HomeDirectory);
        Field("NationalId (extensionAttribute1)", record.NationalId);
        Field("OU נוכחי", record.CurrentOu);
        Field("קובץ נתונים זמני", workflow.RecordPath!);
        if (!CheckNationalId(workflow, record)) return 2;

        // Step 3
        Step(3, "הסרת קבוצות");
        var groups = await workflow.GetGroupsAsync();
        foreach (var g in groups) ConsoleOut.Line(g.IsDomainUsers ? $"  = {g.Name} (נשמרת תמיד)" : $"  - {g.Name}");
        var removable = groups.Count(g => !g.IsDomainUsers);
        var warning = workflow.User!.IsPrivileged ? " שים לב: למשתמש יש או היו הרשאות ניהול (adminCount=1)." : "";
        if (!Confirm($"להסיר את {record.DisplayName} ({record.SamAccountName}) מ-{removable} קבוצות? Domain Users לא תוסר.{warning}", true))
        {
            workflow.Audit("RemoveGroups", AuditOutcome.Cancelled);
            ConsoleOut.Line("בוטל. לא בוצע שינוי.");
            return 2;
        }
        while (true)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var handled = 0;
            var done = await workflow.RemoveGroupsAsync(r =>
            {
                switch (r.Status)
                {
                    case OperationStatus.Success when seen.Add(r.Group.DistinguishedName):
                        Ok($"[{++handled}/{removable}] הוסרה: {r.Group.Name}");
                        break;
                    case OperationStatus.Failed when seen.Add(r.Group.DistinguishedName):
                        Bad($"[{++handled}/{removable}] נכשלה: {r.Group.Name} - {r.Message}");
                        break;
                    case OperationStatus.Kept:
                        ConsoleOut.Line($"  = נשמרת: {r.Group.Name}");
                        break;
                }
            });
            if (done) break;
            if (!Confirm("חלק מהקבוצות לא הוסרו, ולא ניתן להמשיך עד להשלמה. לנסות שוב?", false))
            {
                ConsoleOut.Line("התהליך נעצר לפני ההעברה. ניתן להפעיל שוב ולבחור את אותו משתמש.");
                return 3;
            }
        }
        Ok("כל הקבוצות הוסרו.");

        // Step 4
        Step(4, "העברת משתמש והשבתת חשבון");
        ConsoleOut.Line("פעולה זו תעביר את המשתמש ל-Disabled Users ותבצע Disable לחשבון.");
        Field("OU יעד", settings.ActiveDirectory.TargetOu);
        if (!Confirm($"בצע העברה והשבתה של {record.DisplayName} ({record.SamAccountName})?", true))
        {
            workflow.Audit("MoveAndDisable", AuditOutcome.Cancelled);
            ConsoleOut.Line("בוטל. הקבוצות כבר הוסרו; החשבון לא הועבר ולא הושבת.");
            return 3;
        }
        while (true)
        {
            try
            {
                await workflow.MoveAndDisableAsync(text => ConsoleOut.Line(text));
                break;
            }
            catch (UserFacingException ex)
            {
                _log!.Error(ex.Message, ex.InnerException);
                Bad(ex.Message);
                if (!Confirm("לנסות שוב? (שלב שכבר הצליח לא יבוצע פעמיים)", false)) return 3;
            }
        }
        Ok(workflow.Moved && workflow.Disabled ? "המשתמש הועבר והחשבון הושבת." : "לא הושלם.");

        // Steps 5-6
        return await FillTicketAsync(settings, workflow, tickets);
    }

    private async Task<int> FillTicketAsync(AppSettings settings, OffboardingWorkflow workflow, ITicketAutomation tickets)
    {
        Step(5, "פתיחת CA Service Desk ב-Edge");
        var fields = TicketContent.Build(workflow.LoadRecordForTicket(), settings.ServiceDesk);
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); ConsoleOut.Line("מבטל את מילוי הקריאה..."); };
        Console.CancelKeyPress += onCancel;
        IReadOnlyList<TicketFieldResult>? results = null;
        workflow.Audit("PopulateTicket", AuditOutcome.Attempt);
        try
        {
            results = await tickets.RunAsync(fields, text => ConsoleOut.Line("  " + text), cts.Token);
            Step(6, "מילוי הקריאה הושלם");
            foreach (var r in results)
            {
                if (r.Filled) Ok($"{r.Field.Caption}: {r.Message}");
                else Bad($"{r.Field.Caption}: {r.Message}");
            }
            var missing = results.Where(r => !r.Filled).ToList();
            workflow.Audit("PopulateTicket", missing.Count == 0 ? AuditOutcome.Success : AuditOutcome.Failure,
                missing.Count == 0 ? "All fields filled; not submitted" : $"Not filled: {string.Join(", ", missing.Select(m => m.Field.Caption))}; not submitted");
            if (missing.Count > 0)
            {
                ConsoleOut.Line();
                ConsoleOut.Line("יש להשלים ידנית את השדות הבאים:");
                foreach (var m in missing) ConsoleOut.Line($"[{m.Field.Caption}]{Environment.NewLine}{m.Field.Value}{Environment.NewLine}");
            }
            ConsoleOut.Line();
            ConsoleOut.Line("הקריאה לא נשלחה. יש לבדוק אותה ב-Edge וללחוץ ידנית על \"שלח קריאה\".");
            return missing.Count == 0 ? 0 : 4;
        }
        catch (OperationCanceledException)
        {
            workflow.Audit("PopulateTicket", AuditOutcome.Cancelled);
            ConsoleOut.Line("מילוי הקריאה בוטל. החשבון כבר הועבר והושבת.");
            PrintManual(fields);
            return 4;
        }
        catch (Exception ex)
        {
            workflow.Audit("PopulateTicket", AuditOutcome.Failure, ex.Message);
            _log!.Error("Ticket automation failed", ex);
            Bad(ex is UserFacingException ? ex.Message : $"מילוי הקריאה נכשל: {ex.Message}");
            ConsoleOut.Line("החשבון כבר הועבר והושבת. יש לפתוח קריאה ידנית עם הערכים הבאים:");
            PrintManual(fields);
            return 4;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    /// <summary>The ID is validated as an Israeli ID number. A bad or missing value needs an explicit decision.</summary>
    private bool CheckNationalId(OffboardingWorkflow workflow, OffboardingRecord record)
    {
        var status = NationalIdValidator.Check(record.NationalId);
        if (status == NationalIdStatus.Valid)
        {
            Ok("NationalId תקין.");
            workflow.Audit("NationalIdCheck", AuditOutcome.Success);
            return true;
        }
        var reason = status == NationalIdStatus.Missing ? "NationalId (extensionAttribute1) ריק" : "NationalId אינו תקין";
        workflow.Audit("NationalIdCheck", AuditOutcome.Failure, status.ToString());
        Warn(reason + ".");
        if (Confirm("להמשיך בכל זאת?", true)) return true;
        workflow.Audit("NationalIdCheck", AuditOutcome.Cancelled, "Stopped by operator");
        ConsoleOut.Line("בוטל. לא בוצע שינוי.");
        return false;
    }

    private static void PrintManual(IReadOnlyList<TicketField> fields)
    {
        foreach (var f in fields) ConsoleOut.Line($"[{f.Caption}]{Environment.NewLine}{f.Value}{Environment.NewLine}");
    }

    private static bool Confirm(string question, bool danger)
    {
        ConsoleOut.Line($"{question} (כן/y או לא/n)", danger ? ConsoleColor.Red : ConsoleColor.Cyan);
        Console.Write("> ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return answer is "כן" or "y" or "yes";
    }

    private static void Step(int n, string text)
    {
        ConsoleOut.Line();
        ConsoleOut.Line($"שלב {n} מתוך {TotalSteps}: {text}", ConsoleColor.Cyan);
        ConsoleOut.Line($"[{new string('#', n)}{new string('-', TotalSteps - n)}]", ConsoleColor.Cyan);
    }

    private static void Field(string caption, string value) => ConsoleOut.Line($"  {caption}: {value}");
    private static void Ok(string text) => Colored(ConsoleColor.Green, text);
    private static void Bad(string text) => Colored(ConsoleColor.Red, text);
    private static void Warn(string text) => Colored(ConsoleColor.Yellow, text);
    public static void Fail(string text) => Colored(ConsoleColor.Red, text);

    private static void Colored(ConsoleColor color, string text)
    {
        ConsoleOut.Line(text, color);
    }

    private static string Resolve(string? path, string fallback) =>
        Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(path) ? fallback : path);
}
