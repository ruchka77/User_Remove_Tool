using System.Windows.Input;
using UserRemoveTool.Core;
using UserRemoveTool.Core.ActiveDirectory;
using UserRemoveTool.Core.Configuration;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.ServiceDesk;
using UserRemoveTool.Core.Workflow;
using UserRemoveTool.Mvvm;

namespace UserRemoveTool.ViewModels;

public interface IDialogService
{
    bool Confirm(string title, string message, string actionText);
    void Error(string message);
    void Copy(string text);
}

/// <summary>Everything the pages need; created once in App.</summary>
public sealed record AppServices(AppSettings Settings, IDirectoryService Directory, OffboardingWorkflow Workflow,
    ITicketAutomation Tickets, IAppLogger Log, IDialogService Dialogs);

/// <summary>Navigation, workflow progress and the single busy gate shared by all pages.</summary>
public sealed class MainViewModel : ObservableObject
{
    private static readonly string[] Steps =
    [
        "חיפוש משתמש", "קריאת נתוני המשתמש", "הסרת קבוצות", "העברה והשבתה", "פתיחת CA Service Desk", "מילוי קריאה"
    ];

    private object _page = null!;
    private int _step;
    private bool _isBusy;

    public MainViewModel(AppServices services)
    {
        Services = services;
        ShowSearch();
    }

    public AppServices Services { get; }
    public object Page { get => _page; private set => Set(ref _page, value); }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); } }

    /// <summary>Number of completed steps (0-6).</summary>
    public int CompletedSteps
    {
        get => _step;
        set { if (Set(ref _step, value)) Raise(nameof(StepText)); }
    }

    public int TotalSteps => Steps.Length;
    public string StepText => _step >= Steps.Length ? "התהליך הושלם" : $"שלב {_step + 1} מתוך {Steps.Length}: {Steps[_step]}";

    public void ShowSearch()
    {
        Services.Workflow.Reset();
        CompletedSteps = 0;
        Page = new SearchViewModel(this);
    }

    public void ShowGroups() { CompletedSteps = 2; Page = new GroupsViewModel(this); }
    public void ShowMove() { CompletedSteps = 3; Page = new MoveViewModel(this); }
    public void ShowTicket() { CompletedSteps = 4; Page = new TicketViewModel(this); }

    public AsyncCommand Command(Func<Task> execute, Func<bool>? canExecute = null) =>
        new(RunAsync, execute, () => !IsBusy && (canExecute?.Invoke() ?? true));

    public async Task RunAsync(Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (UserFacingException ex)
        {
            Services.Log.Error(ex.Message, ex.InnerException);
            Services.Dialogs.Error(ex.Message);
        }
        catch (Exception ex)
        {
            Services.Log.Error("Unexpected error", ex);
            Services.Dialogs.Error($"אירעה שגיאה לא צפויה: {ex.Message}");
        }
        finally { IsBusy = false; }
    }
}
