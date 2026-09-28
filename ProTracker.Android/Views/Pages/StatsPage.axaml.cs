using Avalonia.Controls;
using Foot_Tracker.ViewModels;

namespace ProTracker.Companion.Views.Pages;

public partial class StatsPage : UserControl
{
    public StatsPage()
    {
        InitializeComponent();
        DataContext = new HuntingStatsViewModel();
    }
}
