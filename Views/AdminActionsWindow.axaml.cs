using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.Services;

namespace Foot_Tracker.Views;

/// <summary>
/// Small launcher shown after a successful Admin Login (see
/// MainWindow.AdminLoginButton_Click) - lets the admin choose which gated
/// action to take instead of jumping straight into one. §101 added the
/// Admin Client toggle and the Admin Console entry, §194 the two scrapers;
/// §253 removed the two Events-board actions (Create Event, Remove Event)
/// the window was first built around. Everything here sits behind the same
/// single authenticated login (AdminAuthService - the credential itself is
/// unchanged). Mostly code-behind by design: like AdminLoginWindow/
/// ConfirmDialogWindow there's no real state here, just buttons that each
/// open another window or flip AdminModeService.
///
/// Stays open (non-modal) after any button is used, so taking two admin
/// actions doesn't need logging in twice.
/// </summary>
public partial class AdminActionsWindow : Window
{
    public AdminActionsWindow()
    {
        InitializeComponent();

        RefreshAdminClientButton();
    }

    private void RefreshAdminClientButton()
    {
        var button = this.FindControl<Button>("AdminClientButton")!;

        button.Content = AdminModeService.IsActive
            ? "Leave Admin Client"
            : "Enter Admin Client";
    }

    private void AdminClientButton_Click(object? sender, RoutedEventArgs e)
    {
        if (AdminModeService.IsActive)
            AdminModeService.Leave();
        else
            AdminModeService.Enter();

        RefreshAdminClientButton();
    }

    private void AdminConsoleButton_Click(object? sender, RoutedEventArgs e)
    {
        // §103: owned by the MAIN window, not by this launcher - Show(this)
        // made Avalonia treat the console as this window's owned child, so
        // closing Admin Actions silently closed the console with it. The
        // console is a sibling now: closing this window leaves it open,
        // closing the main window still closes both, and the registry keeps
        // it single-instance.
        Window owner = Owner as Window ?? this;

        WindowRegistry.ShowOrActivate(owner, () => new AdminConsoleWindow());
    }

    // §194: both scrapers, opened the same way the Admin Console is - owned
    // by the MAIN window rather than by this launcher, so closing Admin
    // Actions does not take them with it, and single-instanced by the
    // registry. The boss scraper had no opener at all until now; it was a
    // finished window nothing could reach.
    private void BossScraperButton_Click(object? sender, RoutedEventArgs e)
    {
        Window owner = Owner as Window ?? this;

        WindowRegistry.ShowOrActivate(owner, () => new BossScraperWindow());
    }

    private void GuideScraperButton_Click(object? sender, RoutedEventArgs e)
    {
        Window owner = Owner as Window ?? this;

        WindowRegistry.ShowOrActivate(owner, () => new GuideScraperWindow());
    }

    // §103: the explicit end of the authenticated admin session - see
    // AdminModeService.ClearAuthentication (Admin Client mode, if active,
    // deliberately stays active; only ACCESS is ending). This launcher is
    // itself a gated window, so it closes with the session it belonged to.
    private void LogOutButton_Click(object? sender, RoutedEventArgs e)
    {
        AdminModeService.ClearAuthentication();
        Close();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
