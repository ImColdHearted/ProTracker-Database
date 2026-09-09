using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Foot_Tracker.Views;

/// <summary>
/// §160. The switch-confirmation dialog: Close(true) on the action button,
/// Close(false) on Cancel - the same bool? dialog contract every other
/// Simulator dialog uses. No view model events needed; two clicks is the
/// whole surface.
/// </summary>
public partial class SimulatorSwitchConfirmWindow : Window
{
    public SimulatorSwitchConfirmWindow()
    {
        InitializeComponent();
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
