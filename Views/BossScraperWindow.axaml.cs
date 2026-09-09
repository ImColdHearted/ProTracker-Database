using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// Opened after a successful AdminLoginWindow. See BossWikiScraperService for
/// the actual wiki fetch/parse logic and BossScraperViewModel for the boss
/// picker + preview + save workflow around it - this code-behind only wires
/// the ViewModel up and closes the window.
/// </summary>
public partial class BossScraperWindow : Window
{
    public BossScraperWindow()
    {
        InitializeComponent();
        DataContext = new BossScraperViewModel();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
