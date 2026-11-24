using System.Collections.ObjectModel;
using System.Windows.Input;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.ServiceDesk;
using UserRemoveTool.Mvvm;

namespace UserRemoveTool.ViewModels;

public sealed class TicketRow(TicketField field) : ObservableObject
{
    private string _result = "";
    public TicketField Field => field;
    public string Result { get => _result; set => Set(ref _result, value); }
}

public sealed class TicketViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly IReadOnlyList<TicketField> _fields;
    private CancellationTokenSource? _cts;
    private string _status = "";

    public TicketViewModel(MainViewModel main)
    {
        _main = main;
        var services = main.Services;
        _fields = TicketContent.Build(services.Workflow.LoadRecordForTicket(), services.Settings.ServiceDesk);
        foreach (var field in _fields) Rows.Add(new TicketRow(field));

        RetryCommand = main.Command(OpenAsync);
        ManualCommand = main.Command(() => { services.Tickets.OpenManually(); return Task.CompletedTask; });
        FinishCommand = main.Command(() => { main.ShowSearch(); return Task.CompletedTask; });
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), () => main.IsBusy);
        CopyCommand = new RelayCommand(p => services.Dialogs.Copy(((TicketRow)p!).Field.Value));
    }

    public ObservableCollection<TicketRow> Rows { get; } = new();
    public ICommand RetryCommand { get; }
    public ICommand ManualCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CopyCommand { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public async Task OpenAsync()
    {
        var workflow = _main.Services.Workflow;
        _main.CompletedSteps = 4;
        foreach (var row in Rows) row.Result = "";
        workflow.Audit("PopulateTicket", AuditOutcome.Attempt);
        using var cts = _cts = new CancellationTokenSource();
        var active = true; // drops progress that is still queued after the outcome is known
        var progress = (IProgress<string>)new Progress<string>(s => { if (active) Status = s; });
        try
        {
            var results = await _main.Services.Tickets.RunAsync(_fields, progress.Report, cts.Token);
            active = false;
            foreach (var r in results)
                Rows.First(row => row.Field.Key == r.Field.Key).Result = (r.Filled ? "✔ " : "✖ ") + r.Message;
            var missing = results.Where(r => !r.Filled).Select(r => r.Field.Caption).ToList();
            workflow.Audit("PopulateTicket", missing.Count == 0 ? AuditOutcome.Success : AuditOutcome.Failure,
                missing.Count == 0 ? "All fields filled; not submitted" : $"Not filled: {string.Join(", ", missing)}; not submitted");
            _main.CompletedSteps = missing.Count == 0 ? 6 : 5;
            Status = missing.Count == 0
                ? "כל השדות מולאו. יש לבדוק את הקריאה וללחוץ ידנית על \"שלח קריאה\"."
                : $"יש להשלים ידנית: {string.Join(", ", missing)}. לאחר מכן לבדוק וללחוץ על \"שלח קריאה\".";
        }
        catch (OperationCanceledException)
        {
            active = false;
            Status = "מילוי הקריאה בוטל. ניתן לנסות שוב או להעתיק את הערכים ידנית.";
            workflow.Audit("PopulateTicket", AuditOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            active = false;
            Status = "מילוי הקריאה לא הושלם. ניתן לנסות שוב או להעתיק את הערכים ידנית.";
            workflow.Audit("PopulateTicket", AuditOutcome.Failure, ex.Message);
            throw;
        }
        finally { _cts = null; }
    }
}
