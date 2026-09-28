using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>§278. File - Tracker Settings. The choice saves as it is made, so
/// there is no OK button to forget - see TrackerSettingsViewModel.</summary>
public partial class TrackerSettingsWindow : Window
{
    public TrackerSettingsWindow()
    {
        InitializeComponent();

        DataContext = new TrackerSettingsViewModel();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
