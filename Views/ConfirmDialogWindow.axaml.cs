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

    /// <summary>§275. The same dialog as a plain notice: one OK button, and
    /// no answer to read. Used where something the player asked for cannot
    /// happen and saying so in a status line is not enough - see
    /// SimulatorStorageViewModel's full-team refusal.</summary>
    public static async Task NotifyAsync(Window owner, string message, string title = "Notice")
    {
        var dialog = new ConfirmDialogWindow(message, title);

        dialog.FindControl<Button>("YesButton")!.Content = "OK";
        dialog.FindControl<Button>("NoButton")!.IsVisible = false;

        await dialog.ShowDialog<bool?>(owner);
    }

    /// <summary>Replaces MessageBox.Show(msg, title, MessageBoxButtons.YesNo, ...) == DialogResult.Yes.</summary>
    public static async Task<bool> ShowAsync(Window owner, string message, string title = "Confirm")
    {
        var dialog = new ConfirmDialogWindow(message, title);
        return await dialog.ShowDialog<bool?>(owner) == true;
    }
}
