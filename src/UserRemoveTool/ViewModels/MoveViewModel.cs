using System.Windows.Input;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.Models;
using UserRemoveTool.Mvvm;

namespace UserRemoveTool.ViewModels;

public sealed class MoveViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _status = "";

    public MoveViewModel(MainViewModel main)
    {
        _main = main;
        User = main.Services.Workflow.User!;
        ExecuteCommand = main.Command(ExecuteAsync);
    }

    public string Title => "העברת משתמש והשבתת חשבון";
    public string Message => "פעולה זו תעביר את המשתמש ל-Disabled Users ותבצע Disable לחשבון.";
    public string ActionText => "בצע העברה והשבתה";
    public DirectoryUser User { get; }
    public string TargetOu => _main.Services.Settings.ActiveDirectory.TargetOu;
    public ICommand ExecuteCommand { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }

    private async Task ExecuteAsync()
    {
        var workflow = _main.Services.Workflow;
        if (!_main.Services.Dialogs.Confirm(Title, $"{Message}\n\n{User.DisplayName} ({User.SamAccountName})", ActionText))
        {
            workflow.Audit("MoveAndDisable", AuditOutcome.Cancelled);
            return;
        }
        try
        {
            await workflow.MoveAndDisableAsync(((IProgress<string>)new Progress<string>(s => Status = s)).Report);
        }
        catch
        {
            Status = workflow.Moved ? "המשתמש הועבר, אך ההשבתה לא אושרה. יש לנסות שוב." : "ההעברה לא הושלמה והחשבון לא הושבת.";
            throw;
        }
        _main.ShowTicket();
        await ((TicketViewModel)_main.Page).OpenAsync();
    }
}
