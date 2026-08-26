using Avalonia.Controls;
using Avalonia.Input;
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

    private void TeamMember_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: BossTeamMemberItem item } && DataContext is BossDetailViewModel vm)
        {
            vm.SelectTeamMemberCommand.Execute(item);
        }
    }
}
