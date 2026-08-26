using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// Team Magma-only "delete a post" list - see RemoveEventViewModel for the
/// actual delete logic. Only reachable via AdminActionsWindow, itself only
/// reachable via a successful MainWindow.AdminLoginButton_Click login;
/// nothing in this window checks that itself.
/// </summary>
public partial class RemoveEventWindow : Window
{
    public RemoveEventWindow()
    {
        InitializeComponent();

        var vm = new RemoveEventViewModel
        {
            // this = RemoveEventWindow itself, not whatever window opened it -
            // the confirm popup should be owned by (and appear over) the
            // window the admin is actually looking at when they click Delete.
            ConfirmAsync = message => ConfirmDialogWindow.ShowAsync(this, message)
        };

        DataContext = vm;
    }

    private RemoveEventViewModel? ViewModel => DataContext as RemoveEventViewModel;

    private void DeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ViewModels.GuildEventCardItem item })
        {
            ViewModel?.DeleteEventCommand.Execute(item);
        }
    }
}
