using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using UserRemoveTool.Core.ActiveDirectory;
using UserRemoveTool.Core.Cache;
using UserRemoveTool.Core.Configuration;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.Models;
using UserRemoveTool.ViewModels;

namespace UserRemoveTool.Views;

public partial class SearchWindow : Window
{
    private readonly SearchPickerViewModel _viewModel;

    public SearchWindow(SearchPickerViewModel viewModel, IAppLogger? log = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RequestClose = ok => DialogResult = ok;
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            Activate();
            log?.Info($"Search window ready {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms after process start");
        };
        Closed += (_, _) => viewModel.CancelPendingSearch();
    }

    // Down/Up move through the results straight from the search box, like an autocomplete list.
    private void SearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Down or System.Windows.Input.Key.Up) || _viewModel.Results.Count == 0) return;
        var index = _viewModel.Selected is null ? -1 : _viewModel.Results.ToList().IndexOf(_viewModel.Selected);
        index = e.Key == System.Windows.Input.Key.Down ? Math.Min(index + 1, _viewModel.Results.Count - 1) : Math.Max(index - 1, 0);
        _viewModel.Selected = _viewModel.Results[index];
        ResultsGrid.ScrollIntoView(_viewModel.Selected);
        e.Handled = true;
    }

    // Enter on a highlighted row continues, same as the Continue button.
    private void ResultsGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && _viewModel.ContinueCommand.CanExecute(null))
        {
            _viewModel.ContinueCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ResultsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Only a double-click on a row selects; the header and empty space do nothing.
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.DataGridRow)
            {
                if (_viewModel.ContinueCommand.CanExecute(null)) _viewModel.ContinueCommand.Execute(null);
                return;
            }
    }

    /// <summary>Shows the search window on its own UI thread and returns the chosen user, or null when cancelled.</summary>
    public static DirectoryUser? Pick(IDirectoryService directory, ActiveDirectorySettings settings, IAppLogger log, UserCache? cache = null)
    {
        DirectoryUser? chosen = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly; // faster start, less memory
                // The view model awaits directory calls and must resume on this thread.
                SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
                var viewModel = new SearchPickerViewModel(directory, settings, log, cache);
                if (new SearchWindow(viewModel, log).ShowDialog() == true) chosen = viewModel.Chosen;
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Search window failed", failure);
        return chosen;
    }
}
