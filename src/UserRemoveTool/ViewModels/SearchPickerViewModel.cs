using System.Windows.Input;
using UserRemoveTool.Core;
using UserRemoveTool.Core.ActiveDirectory;
using UserRemoveTool.Core.Configuration;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.Models;
using UserRemoveTool.Mvvm;

namespace UserRemoveTool.ViewModels;

/// <summary>Live user search and selection for the pop-up window.</summary>
public sealed class SearchPickerViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly ActiveDirectorySettings _settings;
    private readonly IAppLogger _log;
    private CancellationTokenSource? _search;
    private string _text = "";
    private string _status;
    private IReadOnlyList<DirectoryUser> _results = [];
    private DirectoryUser? _selected;

    public SearchPickerViewModel(IDirectoryService directory, ActiveDirectorySettings settings, IAppLogger log)
    {
        _directory = directory;
        _settings = settings;
        _log = log;
        _status = Hint;
        // Contacts are listed like in ADUC, but they are not accounts and cannot be offboarded.
        ContinueCommand = new RelayCommand(() => { Chosen = Selected; RequestClose?.Invoke(true); }, () => Selected is { IsContact: false });
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    public Action<bool>? RequestClose { get; set; }
    public DirectoryUser? Chosen { get; private set; }
    public ICommand ContinueCommand { get; }
    public ICommand CancelCommand { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public IReadOnlyList<DirectoryUser> Results { get => _results; private set => Set(ref _results, value); }

    public DirectoryUser? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value)) _ = SearchAsync(value.Trim()); }
    }

    private string Hint => $"יש להקליד לפחות {_settings.MinSearchLength} תווים";

    public void CancelPendingSearch() => _search?.Cancel();

    private async Task SearchAsync(string term)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();

        // Results of an older term must never stay selectable under a new one.
        Selected = null;
        Results = [];
        if (term.Length < _settings.MinSearchLength)
        {
            Status = Hint;
            return;
        }
        try
        {
            await Task.Delay(_settings.SearchDelayMs, cts.Token);
            Status = "מחפש...";
            var result = await _directory.SearchAsync(term, cts.Token);
            if (cts.IsCancellationRequested) return;
            var found = result.Users;
            Results = found;
            var contacts = found.Count(u => u.IsContact);
            var extra = contacts > 0 ? $", מהם {contacts} אנשי קשר שלא ניתן לבחור" : "";
            Status = found.Count == 0 ? $"לא נמצאו משתמשים ({result.Source})"
                : found.Count >= _settings.MaxResults ? $"מוצגות {found.Count} התוצאות הראשונות, יש לצמצם את החיפוש ({result.Source})"
                : $"נמצאו {found.Count} משתמשים{extra} ({result.Source})";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            _log.Error("Search failed", ex);
            Status = ex is UserFacingException ? ex.Message : $"החיפוש נכשל: {ex.Message}";
        }
    }
}
