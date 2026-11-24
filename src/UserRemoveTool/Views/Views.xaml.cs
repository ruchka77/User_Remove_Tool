using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using UserRemoveTool.ViewModels;

namespace UserRemoveTool.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is not MainViewModel main) return;
        // Never interrupt a directory change half way. Ticket filling changes nothing and may be abandoned.
        if (main.IsBusy && main.Page is not TicketViewModel)
        {
            e.Cancel = true;
            main.Services.Dialogs.Error("פעולה מתבצעת כעת. יש להמתין לסיומה לפני סגירת התוכנה.");
        }
        else if (main.Page is MoveViewModel &&
                 !main.Services.Dialogs.Confirm("יציאה", "התהליך לא הושלם: החשבון עדיין לא הועבר והושבת במלואו. לצאת בכל זאת?", "יציאה"))
        {
            e.Cancel = true;
        }
    }
}

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        Loaded += (_, _) => SearchBox.Focus();
    }
}

public partial class GroupsView : UserControl { public GroupsView() => InitializeComponent(); }
public partial class MoveView : UserControl { public MoveView() => InitializeComponent(); }
public partial class TicketView : UserControl { public TicketView() => InitializeComponent(); }

public partial class MessageWindow : Window
{
    public MessageWindow(string title, string message, string action, bool showCancel)
    {
        InitializeComponent();
        Title = Heading.Text = title;
        Body.Text = message;
        Ok.Content = action;
        // Enter must never confirm a destructive action by accident.
        Ok.IsDefault = !showCancel;
        Ok.IsCancel = !showCancel;
        Cancel.IsDefault = showCancel;
        Cancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => (showCancel ? Cancel : Ok).Focus();
        Ok.Click += (_, _) => DialogResult = true;
    }
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message, string actionText) => Show(title, message, actionText, true);
    public void Error(string message) => Show("הודעה", message, "סגור", false);

    public void Copy(string text)
    {
        try { Clipboard.SetDataObject(text, true); }
        catch (COMException) { Error("ההעתקה נכשלה כי לוח ההעתקה בשימוש. יש לנסות שוב."); }
    }

    private static bool Show(string title, string message, string action, bool showCancel)
    {
        var window = new MessageWindow(title, message, action, showCancel);
        if (Application.Current.MainWindow is { IsLoaded: true } owner && owner != window) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog() == true;
    }
}
