using Avalonia.Controls;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class BossDetailWindow : Window
{
    public BossDetailWindow()
    {
        InitializeComponent();
    }

    /// <summary>Call right after construction, before Show()/ShowDialog().</summary>
    public void LoadBoss(string bossId, ViewModels.BossDifficulty difficulty)
    {
        var vm = new BossDetailViewModel();
        vm.Load(bossId, difficulty);
        DataContext = vm;
    }

    // The old TeamMember_PointerPressed handler is gone: the redesigned team
    // column is a ListBox whose SelectedItem binds straight to
    // BossDetailViewModel.SelectedTeamMember (MIGRATION_GUIDE.md §92), so
    // selection needs no code-behind at all.
}
