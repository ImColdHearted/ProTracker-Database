using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// §194. Opened from Admin Actions after a successful Admin Login. See
/// ForumGuideScraperService for the fetch and the conversion, and
/// GuideScraperViewModel for the fetch/preview/save workflow around it - this
/// code-behind only wires the ViewModel up and closes the window, exactly as
/// BossScraperWindow's does.
/// </summary>
public partial class GuideScraperWindow : Window
{
    public GuideScraperWindow()
    {
        InitializeComponent();
        DataContext = new GuideScraperViewModel();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
