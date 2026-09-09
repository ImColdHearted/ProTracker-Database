using Avalonia.Controls;

namespace Foot_Tracker.Views;

public partial class HuntingStatsWindow : Window
{
    public HuntingStatsWindow()
    {
        InitializeComponent();
        DataContext = new ViewModels.HuntingStatsViewModel();
    }
}
