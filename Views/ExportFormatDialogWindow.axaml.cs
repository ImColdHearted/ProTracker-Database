using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Foot_Tracker.Views;

/// <summary>
/// Small in-app popup letting the user pick CSV or JSON before the save-file
/// dialog opens - PreviouslyBattledUsersViewModel.Export shows this first
/// (via RequestExportFormat), then uses the returned format to narrow the
/// save dialog's own FileTypeChoices/DefaultExtension to just that one
/// choice. The OS save dialog's FileTypeChoices dropdown already technically
/// let a user pick either extension on its own; this just makes that choice
/// its own explicit step instead, per what was asked for. Same
/// ShowAsync-returns-the-result shape as ConfirmDialogWindow, just returning
/// a format string instead of a bool.
/// </summary>
public partial class ExportFormatDialogWindow : Window
{
    public ExportFormatDialogWindow()
    {
        InitializeComponent();
    }

    private void ExportButton_Click(object? sender, RoutedEventArgs e)
    {
        bool isJson = this.FindControl<RadioButton>("JsonRadioButton")!.IsChecked == true;
        Close(isJson ? "json" : "csv");
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    /// <summary>Replaces the OS-native save dialog's FileTypeChoices dropdown
    /// with an explicit in-app format picker, per what was asked for - shown
    /// before the save dialog itself so that dialog can then be narrowed to
    /// just the chosen format. Returns "csv" or "json", or null if the dialog
    /// was cancelled (Cancel button or closed via the window chrome).</summary>
    public static async Task<string?> ShowAsync(Window owner)
    {
        var dialog = new ExportFormatDialogWindow();
        return await dialog.ShowDialog<string?>(owner);
    }
}
