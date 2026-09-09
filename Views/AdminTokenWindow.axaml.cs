using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.Services;

namespace Foot_Tracker.Views;

/// <summary>
/// §143/§153. Signs this run in for admin actions on the shared Events
/// board. Two modes: a delegated admin login (§153 - username + password,
/// with EventsSyncService.DeriveLoginVerifier run off the UI thread so only
/// the derived verifier ever leaves this machine), or the Worker's master
/// ADMIN_TOKEN exactly as before. On success the credential is handed to
/// EventsSyncService (memory only) and ShowAsync answers true; the caller
/// then retries its action, and a wrong password surfaces as the server's
/// 401 the same way a wrong token always has. "Remember on this PC"
/// (Windows only - see AdminLoginStore) keeps the login for future runs,
/// and leaving it unticked removes any previously remembered one; the
/// master token is never remembered. This window keeps nothing: every box
/// is cleared before it closes.
/// </summary>
public partial class AdminTokenWindow : Window
{
    // Enter reaches Continue twice - once from the box's KeyDown, once
    // through the button's IsDefault - so the first one wins.
    private bool closed;
    private bool masterMode;
    private bool deriving;

    public AdminTokenWindow()
    {
        InitializeComponent();

        var usernameBox = this.FindControl<TextBox>("UsernameBox")!;
        var passwordBox = this.FindControl<TextBox>("PasswordBox")!;
        var tokenBox = this.FindControl<TextBox>("TokenBox")!;

        passwordBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = ContinueAsync(); };
        tokenBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = ContinueAsync(); };
        usernameBox.AttachedToVisualTree += (_, _) => usernameBox.Focus();

        // §153: no DPAPI off Windows, so no remember checkbox there.
        this.FindControl<CheckBox>("RememberCheck")!.IsVisible = AdminLoginStore.CanRemember;
    }

    /// <summary>True when this run now holds an admin credential.</summary>
    public static async Task<bool> ShowAsync(Window owner)
    {
        var dialog = new AdminTokenWindow();
        return await dialog.ShowDialog<bool?>(owner) == true;
    }

    private void ModeButton_Click(object? sender, RoutedEventArgs e)
    {
        masterMode = !masterMode;
        this.FindControl<StackPanel>("LoginPanel")!.IsVisible = !masterMode;
        this.FindControl<StackPanel>("TokenPanel")!.IsVisible = masterMode;
        this.FindControl<Button>("ModeButton")!.Content = masterMode
            ? "Use an admin login instead..."
            : "Use the master admin token instead...";
        this.FindControl<TextBlock>("ErrorText")!.IsVisible = false;
        (masterMode ? this.FindControl<TextBox>("TokenBox") : this.FindControl<TextBox>("UsernameBox"))!.Focus();
    }

    private void ContinueButton_Click(object? sender, RoutedEventArgs e) => _ = ContinueAsync();

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (closed || deriving)
            return;

        closed = true;
        ClearBoxes();
        Close(false);
    }

    private async Task ContinueAsync()
    {
        if (closed || deriving)
            return;

        var errorText = this.FindControl<TextBlock>("ErrorText")!;

        if (masterMode)
        {
            string token = (this.FindControl<TextBox>("TokenBox")!.Text ?? string.Empty).Trim();

            if (token.Length == 0)
            {
                errorText.Text = "Enter the master admin token, or Cancel.";
                errorText.IsVisible = true;
                return;
            }

            EventsSyncService.SetAdminToken(token);
            closed = true;
            ClearBoxes();
            Close(true);
            return;
        }

        string username = (this.FindControl<TextBox>("UsernameBox")!.Text ?? string.Empty).Trim();
        string password = this.FindControl<TextBox>("PasswordBox")!.Text ?? string.Empty;

        if (username.Length == 0 || password.Length == 0)
        {
            errorText.Text = "Enter the username and the password, or Cancel.";
            errorText.IsVisible = true;
            return;
        }

        // §153: a tenth of a second of deliberate key stretching - off the
        // UI thread, with the buttons held so a double press cannot race it.
        deriving = true;
        this.FindControl<Button>("ContinueButton")!.IsEnabled = false;

        string verifier = await Task.Run(() => EventsSyncService.DeriveLoginVerifier(username, password));

        EventsSyncService.SetAdminLogin(username, verifier);

        if (AdminLoginStore.CanRemember && this.FindControl<CheckBox>("RememberCheck")!.IsChecked == true)
            AdminLoginStore.Save(username, verifier);
        else
            AdminLoginStore.Forget();

        deriving = false;
        closed = true;
        ClearBoxes();
        Close(true);
    }

    private void ClearBoxes()
    {
        this.FindControl<TextBox>("TokenBox")!.Text = string.Empty;
        this.FindControl<TextBox>("UsernameBox")!.Text = string.Empty;
        this.FindControl<TextBox>("PasswordBox")!.Text = string.Empty;
    }
}
