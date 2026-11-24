using System.Collections.ObjectModel;
using System.Windows.Input;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.Models;
using UserRemoveTool.Mvvm;

namespace UserRemoveTool.ViewModels;

public sealed class GroupRow(GroupMembership group) : ObservableObject
{
    private OperationStatus _status = group.IsDomainUsers ? OperationStatus.Kept : OperationStatus.Pending;
    private string _message = group.IsDomainUsers ? "נשמרת תמיד" : group.IsPrimary ? "קבוצה ראשית, תוחלף ב-Domain Users" : "";

    public string DistinguishedName => group.DistinguishedName;
    public string Name => group.Name;
    public OperationStatus Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(StatusText)); } }
    public string Message { get => _message; set => Set(ref _message, value); }

    public string StatusText => Status switch
    {
        OperationStatus.Running => "מסיר...",
        OperationStatus.Success => "✔ הוסרה",
        OperationStatus.Failed => "✖ נכשלה",
        OperationStatus.Kept => "נשמרת",
        _ => "ממתינה"
    };
}

public sealed class GroupsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _loaded, _done;
    private string _status = "טוען קבוצות...";
    private int _total = 1, _handled;

    public GroupsViewModel(MainViewModel main)
    {
        _main = main;
        User = main.Services.Workflow.User!;
        Record = main.Services.Workflow.Record!;
        RemoveCommand = main.Command(RemoveAsync, () => _loaded && !_done);
        ReloadCommand = main.Command(LoadAsync, () => !_done);
        ContinueCommand = main.Command(() => { main.ShowMove(); return Task.CompletedTask; }, () => _done);
        BackCommand = main.Command(() => { main.ShowSearch(); return Task.CompletedTask; });
    }

    public DirectoryUser User { get; }
    public OffboardingRecord Record { get; }
    public ObservableCollection<GroupRow> Groups { get; } = new();
    public ICommand RemoveCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand ContinueCommand { get; }
    public ICommand BackCommand { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public int Total { get => _total; private set => Set(ref _total, value); }
    public int Handled { get => _handled; private set => Set(ref _handled, value); }

    public async Task LoadAsync()
    {
        _loaded = false;
        Status = "טוען קבוצות...";
        try
        {
            var groups = await _main.Services.Workflow.GetGroupsAsync();
            Groups.Clear();
            foreach (var g in groups) Groups.Add(new GroupRow(g));
            Handled = 0;
            Status = $"{groups.Count(g => !g.IsDomainUsers)} קבוצות להסרה";
            _loaded = true;
        }
        catch
        {
            Status = "טעינת הקבוצות נכשלה";
            throw;
        }
    }

    private async Task RemoveAsync()
    {
        var count = Groups.Count(g => g.Status is OperationStatus.Pending or OperationStatus.Failed);
        var warning = User.IsPrivileged ? "\n\nשים לב: למשתמש יש או היו הרשאות ניהול (adminCount=1)." : "";
        if (!_main.Services.Dialogs.Confirm("הסרת קבוצות",
                $"המשתמש {User.DisplayName} ({User.SamAccountName}) יוסר מ-{count} קבוצות. Domain Users לא תוסר.{warning}", "הסר קבוצות"))
        {
            _main.Services.Workflow.Audit("RemoveGroups", AuditOutcome.Cancelled);
            return;
        }

        Total = Math.Max(1, count);
        Handled = 0;
        Status = "מסיר קבוצות...";
        var progress = new Progress<GroupRemovalResult>(Apply);
        try
        {
            _done = await _main.Services.Workflow.RemoveGroupsAsync(((IProgress<GroupRemovalResult>)progress).Report);
        }
        catch
        {
            Status = "הסרת הקבוצות לא הושלמה";
            throw;
        }
        if (_done) Handled = Total;
        Status = _done ? "כל הקבוצות הוסרו" : "חלק מהקבוצות לא הוסרו. ניתן לנסות שוב; לא ניתן להמשיך עד להשלמה.";
    }

    private void Apply(GroupRemovalResult result)
    {
        var row = Groups.FirstOrDefault(g => g.DistinguishedName.Equals(result.Group.DistinguishedName, StringComparison.OrdinalIgnoreCase));
        if (row is null) Groups.Add(row = new GroupRow(result.Group));
        var finished = row.Status == OperationStatus.Running && result.Status is OperationStatus.Success or OperationStatus.Failed;
        row.Status = result.Status;
        row.Message = result.Message;
        if (finished) Handled = Math.Min(Total, Handled + 1);
    }
}
