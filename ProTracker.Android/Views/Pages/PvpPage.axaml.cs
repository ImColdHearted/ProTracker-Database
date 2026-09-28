using Avalonia.Controls;
using Avalonia.Input;
using Foot_Tracker.ViewModels;

namespace ProTracker.Companion.Views.Pages;

public partial class PvpPage : UserControl
{
    public PvpPage()
    {
        InitializeComponent();
        DataContext = new PreviouslyBattledUsersViewModel();
    }

    private void OpponentRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PvpOpponentDisplayItem item } && !string.IsNullOrWhiteSpace(item.Name))
            Nav.Push(new PvpDetailPage(item.Name), item.Name);
    }
}
