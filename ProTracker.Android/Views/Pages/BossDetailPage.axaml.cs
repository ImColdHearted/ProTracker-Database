using Avalonia.Controls;
using Foot_Tracker.ViewModels;

namespace ProTracker.Companion.Views.Pages;

public partial class BossDetailPage : UserControl
{
    public BossDetailPage()
    {
        InitializeComponent();
    }

    public BossDetailPage(string bossId, BossDifficulty difficulty) : this()
    {
        var vm = new BossDetailViewModel();
        vm.Load(bossId, difficulty);
        DataContext = vm;
    }
}
