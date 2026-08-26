using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class BossListWindow : Window
{
    public BossListWindow()
    {
        InitializeComponent();

        var vm = new BossListViewModel();
        DataContext = vm;

        vm.OpenRequested += (bossId, difficulty) =>
        {
            var detail = new BossDetailWindow();
            detail.LoadBoss(bossId, difficulty);
            detail.Show(this);
        };
    }

    private void BossList_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        (DataContext as BossListViewModel)?.OpenCommand.Execute(null);
    }
}
