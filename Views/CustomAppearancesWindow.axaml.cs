using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class CustomAppearancesWindow : Window
{
    public CustomAppearancesWindow()
    {
        InitializeComponent();

        // §345. The first load happens when the window opens rather than in
        // the view model's constructor: a constructor cannot await, and
        // kicking off an un-awaited task from one means the window can be
        // shown, closed and disposed while a response is still in flight.
        Opened += async (_, _) =>
        {
            if (DataContext is CustomAppearancesViewModel vm)
                await vm.RefreshAsync();
        };
    }

    /// <summary>§366. View Image, on a card that has one.
    ///
    /// A click handler rather than a command on the view model, because what
    /// it does is open a window - and a view model that opens windows has to
    /// hold one to be the owner. The card the button belongs to is its own
    /// DataContext, which is the same route MainWindow's encounter rows take
    /// to find the row that was clicked.</summary>
    private async void ViewImageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control ||
            control.DataContext is not CommunityThemeItem item)
        {
            return;
        }

        if (!item.HasImage)
            return;

        // Modal, like every other dialog this app opens from a button: the
        // gallery behind it has nothing to do while a picture is being
        // looked at, and one owner means one preview rather than a pile of
        // them behind the gallery.
        await new SharedImageWindow(item).ShowDialog(this);
    }
}
