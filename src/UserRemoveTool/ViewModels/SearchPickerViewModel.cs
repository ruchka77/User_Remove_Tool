using System.Windows.Input;
using UserRemoveTool.Core;
using UserRemoveTool.Core.ActiveDirectory;
using UserRemoveTool.Core.Cache;
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

    private readonly UserCache? _cache;

    public SearchPickerViewModel(IDirectoryService directory, ActiveDirectorySettings settings, IAppLogger log, UserCache? cache = null)
    {
        _cache = cache;
        _directory = directory;
        _settings = settings;
        _log = log;
        _status = Hint;
        RefreshCommand = new RelayCommand(() => _ = SearchAsync(_text.Trim(), immediate: true), () => _text.Trim().Length >= _settings.MinSearchLength);
        // Contacts are listed like in ADUC, but they are not accounts and cannot be offboarded.
        ContinueCommand = new RelayCommand(() => { Chosen = Selected; RequestClose?.Invoke(true); }, () => Selected is { IsContact: false });
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    public Action<bool>? RequestClose { get; set; }
    public DirectoryUser? Chosen { get; private set; }
    public ICommand ContinueCommand { get; }
    public ICommand CancelCommand { get; }
    /// <summary>Queries AD again for the current text right away.</summary>
    public ICommand RefreshCommand { get; }
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

    private async Task SearchAsync(string term, bool immediate = false)
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
        var fromCache = false;
        try
        {
            // 1. Instant: what earlier searches already found.
            if (_cache is not null)
            {
                await _cache.Ready;
                var cached = _cache.Search(term, _settings.MaxResults);
                if (cts.IsCancellationRequested) return;
                if (cached.Count > 0)
                {
                    Results = cached;
                    fromCache = true;
                    Status = $"{cached.Count} תוצאות מהמטמון, מתעדכן מ-Active Directory...";
                }
            }
            if (!fromCache) Status = "מחפש...";

            // 2. Fresh: the debounced AD query replaces the cached list.
            if (!immediate) await Task.Delay(_settings.SearchDelayMs, cts.Token);
            if (!fromCache || immediate) Status = "מחפש ב-Active Directory...";
            var result = await _directory.SearchAsync(term, cts.Token);
            if (cts.IsCancellationRequested) return;
            var keep = Selected?.ObjectGuid;
            var found = result.Users;
            Results = found;
            if (keep is { } guid) Selected = found.FirstOrDefault(u => u.ObjectGuid == guid);
            _cache?.Merge(found);
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
            var reason = ex is UserFacingException ? ex.Message : $"החיפוש נכשל: {ex.Message}";
            Status = fromCache ? $"{reason} מוצגות תוצאות מהמטמון בלבד." : reason;
        }
    }
}