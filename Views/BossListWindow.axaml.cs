using Avalonia.Controls;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class BossListWindow : Window
{
    public BossListWindow() : this(string.Empty)
    {
    }

    /// <summary>§288. Opened from a Search hit with the boss's name already
    /// in the box, so the database shows that one card - its difficulties,
    /// its cooldown, its record - and nothing has to be typed twice. An empty
    /// filter is the ordinary window.</summary>
    public BossListWindow(string filter)
    {
        InitializeComponent();

        var vm = new BossListViewModel();
        DataContext = vm;

        if (!string.IsNullOrWhiteSpace(filter))
            vm.SearchText = filter;

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
}
