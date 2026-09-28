using Avalonia.Controls;
using Avalonia.Input;
using Foot_Tracker.ViewModels;

namespace ProTracker.Companion.Views.Pages;

public partial class PvpDetailPage : UserControl
{
    public PvpDetailPage()
    {
        InitializeComponent();
    }

    public PvpDetailPage(string opponentName) : this()
    {
        DataContext = new PvpOpponentDetailViewModel(opponentName);
    }

    private void BattleRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PvpBattleRow row } && DataContext is PvpOpponentDetailViewModel vm)
            vm.SelectBattleCommand.Execute(row);
    }
}
