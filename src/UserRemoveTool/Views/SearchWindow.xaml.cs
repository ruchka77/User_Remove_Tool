using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using UserRemoveTool.Core.ActiveDirectory;
using UserRemoveTool.Core.Configuration;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.Models;
using UserRemoveTool.ViewModels;

namespace UserRemoveTool.Views;

public partial class SearchWindow : Window
{
    private readonly SearchPickerViewModel _viewModel;

    public SearchWindow(SearchPickerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RequestClose = ok => DialogResult = ok;
        Loaded += (_, _) => { SearchBox.Focus(); Activate(); };
        Closed += (_, _) => viewModel.CancelPendingSearch();
    }

    /// <summary>Shows the search window on its own UI thread and returns the chosen user, or null when cancelled.</summary>
    public static DirectoryUser? Pick(IDirectoryService directory, ActiveDirectorySettings settings, IAppLogger log)
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
                var viewModel = new SearchPickerViewModel(directory, settings, log);
                if (new SearchWindow(viewModel).ShowDialog() == true) chosen = viewModel.Chosen;
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
