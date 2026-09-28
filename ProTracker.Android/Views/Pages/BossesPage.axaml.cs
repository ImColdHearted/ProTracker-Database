using Avalonia.Controls;
using Foot_Tracker.ViewModels;

namespace ProTracker.Companion.Views.Pages;

public partial class BossesPage : UserControl
{
    public BossesPage() : this(string.Empty)
    {
    }

    /// <summary>§288's filter constructor, kept: a Search hit lands here
    /// with the boss's name already in the box.</summary>
    public BossesPage(string filter)
    {
        InitializeComponent();

        var vm = new BossListViewModel();
        DataContext = vm;

        if (!string.IsNullOrWhiteSpace(filter))
            vm.SearchText = filter;

        vm.OpenRequested += (bossId, difficulty) =>
            Nav.Push(new BossDetailPage(bossId, difficulty), "Boss");
    }
}
