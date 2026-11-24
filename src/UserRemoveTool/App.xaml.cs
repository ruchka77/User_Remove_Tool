using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using UserRemoveTool.Core;
using UserRemoveTool.Core.ActiveDirectory;
using UserRemoveTool.Core.Configuration;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.ServiceDesk;
using UserRemoveTool.Core.Workflow;
using UserRemoveTool.ViewModels;
using UserRemoveTool.Views;

namespace UserRemoveTool;

public partial class App : Application
{
    private LdapDirectoryService? _directory;
    private OffboardingWorkflow? _workflow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly; // skips GPU setup: faster start, less memory
        var dialogs = new DialogService();
        IAppLogger? log = null;

        DispatcherUnhandledException += (_, args) =>
        {
            log?.Error("Unhandled error", args.Exception);
            dialogs.Error($"אירעה שגיאה לא צפויה: {args.Exception.Message}");
            args.Handled = true;
        };

        try
        {
            var settings = AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
            var logDirectory = Resolve(settings.Logging.Directory, @"%LOCALAPPDATA%\UserRemoveTool\Logs");
            log = new FileLogger(logDirectory);
            var audit = new AuditLogger(Resolve(settings.Logging.AuditDirectory, logDirectory));

            var store = new RecordStore();
            store.DeleteStale(TimeSpan.FromDays(1));
            _directory = new LdapDirectoryService(settings.ActiveDirectory, log);
            _workflow = new OffboardingWorkflow(_directory, audit, log, store);
            var tickets = new TicketAutomation(settings.ServiceDesk, Resolve(null, @"%LOCALAPPDATA%\UserRemoveTool\EdgeProfile"), log);

            log.Info("Application started");
            MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(new AppServices(settings, _directory, _workflow, tickets, log, dialogs))
            };
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            log?.Error("Startup failed", ex);
            dialogs.Error(ex is UserFacingException ? ex.Message : $"הפעלת התוכנה נכשלה: {ex.Message}");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _workflow?.Reset(); // deletes the temporary JSON file
        _directory?.Dispose();
        base.OnExit(e);
    }

    private static string Resolve(string? path, string fallback) =>
        Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(path) ? fallback : path);
}
