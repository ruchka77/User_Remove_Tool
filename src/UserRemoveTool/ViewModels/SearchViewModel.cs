using System.Windows.Input;
using UserRemoveTool.Core;
using UserRemoveTool.Core.Models;
using UserRemoveTool.Mvvm;

namespace UserRemoveTool.ViewModels;

public sealed class SearchViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _search;
    private string _text = "";
    private string _status;
    private bool _isSearching;
    private IReadOnlyList<DirectoryUser> _results = [];
    private DirectoryUser? _selected;

    public SearchViewModel(MainViewModel main)
    {
        _main = main;
        _status = Hint;
        ContinueCommand = main.Command(ContinueAsync, () => Selected is not null);
    }

    public MainViewModel Main => _main;
    public ICommand ContinueCommand { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsSearching { get => _isSearching; private set => Set(ref _isSearching, value); }
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

    private string Hint => $"יש להקליד לפחות {_main.Services.Settings.ActiveDirectory.MinSearchLength} תווים";

    private async Task SearchAsync(string term)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        var settings = _main.Services.Settings.ActiveDirectory;

        // Results of an older term must never stay selectable under a new one.
        Selected = null;
        Results = [];
        IsSearching = false;
        if (term.Length < settings.MinSearchLength)
        {
            Status = Hint;
            return;
        }
        try
        {
            await Task.Delay(settings.SearchDelayMs, cts.Token);
            IsSearching = true;
            Status = "מחפש...";
            var found = await _main.Services.Directory.SearchAsync(term, cts.Token);
            if (cts.IsCancellationRequested) return;
            Results = found;
            Status = found.Count == 0 ? "לא נמצאו משתמשים"
                : found.Count >= settings.MaxResults ? $"מוצגות {found.Count} התוצאות הראשונות, יש לצמצם את החיפוש"
                : $"נמצאו {found.Count} משתמשים";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            _main.Services.Log.Error("Search failed", ex);
            Status = ex is UserFacingException ? ex.Message : $"החיפוש נכשל: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_search, cts)) IsSearching = false;
        }
    }

    private async Task ContinueAsync()
    {
        var user = Selected!;
        _main.CompletedSteps = 1;
        try
        {
            await _main.Services.Workflow.StartAsync(user.ObjectGuid);
        }
        catch
        {
            _main.CompletedSteps = 0;
            throw;
        }
        _main.ShowGroups();
        await ((GroupsViewModel)_main.Page).LoadAsync();
    }
}
