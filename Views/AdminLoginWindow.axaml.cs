using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.Services;

namespace Foot_Tracker.Views;

/// <summary>
/// Gate in front of the admin-only actions - originally the boss wiki
/// scraper dev tool (removed, see MIGRATION_GUIDE.md §28), then the Events
/// board tools (§29), and since §101 also the Admin Client toggle and Admin
/// Console reached through AdminActionsWindow (see
/// MainWindow.AdminLoginButton_Click). See AdminAuthService for why the
/// credential check works the way it does - the credential itself is
/// unchanged by any of the renames around it. §101 added a growing delay
/// after failed attempts (unlimited instant retries invited scripted
/// guessing for no benefit) and clears the password box after every attempt
/// and on close, so the typed secret doesn't linger in the control.
/// </summary>
public partial class AdminLoginWindow : Window
{
    // Doubles per consecutive failure: 1.5s, 3s, 6s, capped at 12s - enough
    // to make rapid guessing pointless without ever locking the real admin
    // out for long. Resets when the window closes (it is per-dialog state,
    // deliberately not persisted - see AdminAuthService's threat-model note).
    private int failedAttempts;

    // The Login button (and Enter key) stay physically clickable during the
    // failure delay - this guard is what actually enforces it, so a click
    // mid-delay can't start a second overlapping attempt against the
    // already-cleared password box.
    private bool attemptInProgress;

    public AdminLoginWindow()
    {
        InitializeComponent();

        // Enter in either field submits, same as clicking Login - a small
        // convenience since this dialog has nothing else to tab through.
        var usernameBox = this.FindControl<TextBox>("UsernameBox")!;
        var passwordBox = this.FindControl<TextBox>("PasswordBox")!;

        usernameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) TryLogin(); };
        passwordBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) TryLogin(); };
    }

    private void LoginButton_Click(object? sender, RoutedEventArgs e) => TryLogin();

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        ClearPasswordBox();
        Close(false);
    }

    private async void TryLogin()
    {
        if (attemptInProgress)
            return;

        var passwordBox = this.FindControl<TextBox>("PasswordBox")!;

        string username = this.FindControl<TextBox>("UsernameBox")!.Text ?? string.Empty;
        string password = passwordBox.Text ?? string.Empty;

        bool verified = AdminAuthService.Verify(username, password);

        // Never keep the typed secret in the control once it has been
        // handed to Verify - on success the dialog is closing anyway, on
        // failure the admin retypes it (§101).
        ClearPasswordBox();

        if (verified)
        {
            Close(true);
            return;
        }

        failedAttempts++;

        var errorText = this.FindControl<TextBlock>("ErrorText")!;
        errorText.IsVisible = true;

        // Growing delay with the whole dialog's inputs disabled - see
        // failedAttempts' declaration comment. async void is safe here for
        // the same reason it is on any Avalonia event handler: everything
        // inside is UI-thread state on this window, and the try/finally
        // guarantees the controls come back even if Delay is interrupted.
        double seconds = Math.Min(1.5 * Math.Pow(2, failedAttempts - 1), 12);

        try
        {
            attemptInProgress = true;
            SetInputsEnabled(false);
            errorText.Text = $"Incorrect username or password. Try again in {seconds:0.#}s.";

            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            attemptInProgress = false;
            SetInputsEnabled(true);
            errorText.Text = "Incorrect username or password.";
        }
    }

    private void SetInputsEnabled(bool enabled)
    {
        this.FindControl<TextBox>("UsernameBox")!.IsEnabled = enabled;
        this.FindControl<TextBox>("PasswordBox")!.IsEnabled = enabled;
    }

    private void ClearPasswordBox()
    {
        this.FindControl<TextBox>("PasswordBox")!.Text = string.Empty;
    }

    /// <summary>Shows the login dialog and returns true if the admin credentials were entered correctly.</summary>
    public static async Task<bool> ShowAsync(Window owner)
    {
        var dialog = new AdminLoginWindow();
        return await dialog.ShowDialog<bool?>(owner) == true;
    }
}
