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

            // Show() without an owner, not Show(this) - Avalonia (like WPF/Win32)
            // auto-closes every window a closed window owns, so Show(this) meant
            // closing Boss Database force-closed whatever boss detail window(s)
            // it had opened. This window has no CenterOwner/other Owner-dependent
            // behavior (see BossDetailWindow.axaml - no WindowStartupLocation set
            // at all), so dropping the owner relationship has no other effect:
            // the detail window just becomes fully independent of Boss Database's
            // lifecycle, which is the point.
            detail.Show();
        };
    }

    private void BossList_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        (DataContext as BossListViewModel)?.OpenCommand.Execute(null);
    }
}
