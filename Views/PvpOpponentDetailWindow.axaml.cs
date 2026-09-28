using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>§276. One opponent's whole history, opened by clicking their name
/// in Previously Battled Users - see PvpOpponentDetailViewModel.</summary>
public partial class PvpOpponentDetailWindow : Window
{
    public PvpOpponentDetailWindow()
    {
        InitializeComponent();
    }

    public PvpOpponentDetailWindow(string opponentName) : this()
    {
        DataContext = new PvpOpponentDetailViewModel(opponentName);
    }

    private void BattleRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PvpBattleRow row } &&
            DataContext is PvpOpponentDetailViewModel vm)
        {
            vm.SelectBattleCommand.Execute(row);
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
