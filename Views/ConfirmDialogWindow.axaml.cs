using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Foot_Tracker.Views;

public partial class ConfirmDialogWindow : Window
{
    public ConfirmDialogWindow()
    {
        InitializeComponent();
    }

    public ConfirmDialogWindow(string message, string title = "Confirm") : this()
    {
        Title = title;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
    }

    private void YesButton_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void NoButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>Replaces MessageBox.Show(msg, title, MessageBoxButtons.YesNo, ...) == DialogResult.Yes.</summary>
    public static async Task<bool> ShowAsync(Window owner, string message, string title = "Confirm")
    {
        var dialog = new ConfirmDialogWindow(message, title);
        return await dialog.ShowDialog<bool?>(owner) == true;
    }
}
