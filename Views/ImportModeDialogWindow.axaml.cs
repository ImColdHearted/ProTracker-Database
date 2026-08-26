using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Foot_Tracker.Views;

/// <summary>
/// Ported alongside ConfirmDialogWindow's pattern, but three-way instead of
/// Yes/No - lets Import Hunt Data (CSV/JSON) ask whether the imported file
/// should be added to the current hunt's totals or replace them outright,
/// instead of always silently replacing (the old behavior). See
/// MainWindowViewModel.ImportHuntData/HuntSession.MergeFrom.
/// </summary>
public partial class ImportModeDialogWindow : Window
{
    public ImportModeDialogWindow()
    {
        InitializeComponent();
    }

    public ImportModeDialogWindow(string message, string title = "Import Hunt Data") : this()
    {
        Title = title;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
    }

    private void AddButton_Click(object? sender, RoutedEventArgs e) => Close("Add");
    private void ReplaceButton_Click(object? sender, RoutedEventArgs e) => Close("Replace");
    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    /// <summary>Returns "Add", "Replace", or null if the user cancelled/closed the dialog.</summary>
    public static async Task<string?> ShowAsync(Window owner, string message, string title = "Import Hunt Data")
    {
        var dialog = new ImportModeDialogWindow(message, title);
        return await dialog.ShowDialog<string?>(owner);
    }
}
