using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Foot_Tracker.Views;

/// <summary>
/// §135. The "you normally do not need this" step in front of the Set Screen
/// Boundaries window. Same ShowAsync-returns-the-result shape as
/// ConfirmDialogWindow, returning two facts instead of one: whether the
/// player chose to continue, and whether they asked not to be shown this
/// again. The second is honoured whichever button closed the dialog - a
/// player who ticks it and then cancels has still said what they want.
/// </summary>
public partial class BoundariesWarningWindow : Window
{
    public BoundariesWarningWindow()
    {
        InitializeComponent();
    }

    private bool DontShowAgain =>
        this.FindControl<CheckBox>("DontShowAgainCheckBox")?.IsChecked == true;

    private void ContinueButton_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>Returns (proceed, dontShowAgain). Closing the window through
    /// its chrome counts as Cancel, exactly like ConfirmDialogWindow.</summary>
    public static async Task<(bool Proceed, bool DontShowAgain)> ShowAsync(Window owner)
    {
        var dialog = new BoundariesWarningWindow();

        bool proceed = await dialog.ShowDialog<bool?>(owner) == true;

        return (proceed, dialog.DontShowAgain);
    }
}
