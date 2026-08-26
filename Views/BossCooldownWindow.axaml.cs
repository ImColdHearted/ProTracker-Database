using Avalonia.Controls;
using Avalonia.Input;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class BossCooldownWindow : Window
{
    public BossCooldownWindow()
    {
        InitializeComponent();

        DataContext = new BossCooldownViewModel();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is BossCooldownViewModel vm)
            {
                vm.ConfirmAsync = message => ConfirmDialogWindow.ShowAsync(this, message);
            }
        };
    }

    private void BossCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: BossCooldownCardItem card } && DataContext is BossCooldownViewModel vm)
        {
            vm.CardClickedCommand.Execute(card);
        }
    }
}