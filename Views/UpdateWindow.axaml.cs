using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Foot_Tracker.Services;

namespace Foot_Tracker.Views;

/// <summary>
/// §369. The window behind the green Update item: what changed, and the one
/// button that installs it.
///
/// No view model, the same call SharedImageWindow made in §366 - one input,
/// no commands, and a single sequence to run. What it runs is four steps in
/// UpdateService, in order, each of which can refuse:
///
///   DownloadAsync   the zip, verified against the manifest's SHA-256
///   Stage           unpacked, and CHECKED to contain this executable
///   Apply           the helper script written and started
///   Shutdown        the tracker closes; the helper does the swap
///
/// The order matters and it is the order of increasing commitment. Nothing
/// touches the install folder until the download is complete, verified and
/// unpacked, so every failure before the last step leaves the tracker
/// exactly as it was, running, with a line saying what happened.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateService.AvailableUpdate? update;

    private bool working;

    public UpdateWindow()
    {
        InitializeComponent();
    }

    public UpdateWindow(UpdateService.AvailableUpdate source) : this()
    {
        update = source;

        HeadlineText.Text = "Version " + source.Version + " is available";

        SubText.Text =
            "You are running " + source.CurrentVersion +
            (string.IsNullOrWhiteSpace(source.Published)
                ? string.Empty
                : "  -  released " + source.Published);

        NotesText.Text = string.IsNullOrWhiteSpace(source.Notes)
            ? "No release notes were published with this version."
            : source.Notes;
    }

    private async void UpdateNowButton_Click(object? sender, RoutedEventArgs e)
    {
        if (update is null || working)
            return;

        working = true;
        UpdateNowButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        Progress.IsVisible = true;
        Progress.Value = 0;
        StatusText.Text = "Downloading…";

        // Created here, on the UI thread, so its callback comes back here -
        // the download itself runs off it.
        var progress = new Progress<double>(fraction => Progress.Value = fraction);

        string? zip = await UpdateService.DownloadAsync(update, progress);

        if (zip is null)
        {
            Fail("The download did not complete, or did not match what the " +
                 "server said it should be. Nothing has been changed.");
            return;
        }

        StatusText.Text = "Unpacking…";
        Progress.IsIndeterminate = true;

        // Off the UI thread: unpacking a self-contained build is a hundred
        // megabytes of file writes and would otherwise freeze the window
        // that is showing the progress.
        UpdateService.StageResult staged = await Task.Run(() => UpdateService.Stage(zip));

        if (!staged.Succeeded)
        {
            // §370: the reason, not a summary of it. The first real run of
            // this window ended in "not a tracker" and nobody could tell
            // whether that meant not-a-zip, a zip with the wrong shape, or a
            // zip with no exe in it. Stage says which now, and this shows it.
            Fail(staged.Problem + " It has been thrown away. Nothing has been changed.");
            return;
        }

        StatusText.Text = "Closing to finish the update…";

        if (!UpdateService.Apply(staged.Root!))
        {
            Fail("The update could not be started. Nothing has been changed.");
            return;
        }

        // Past this line the helper is already waiting for this process to
        // exit. Shutdown rather than Close so the main window's Closing
        // handler runs and the session is persisted first.
        if (Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Close();
        }
    }

    private void Fail(string message)
    {
        Progress.IsIndeterminate = false;
        Progress.IsVisible = false;
        StatusText.Text = message;
        UpdateNowButton.IsEnabled = true;
        LaterButton.IsEnabled = true;
        working = false;
    }

    private void LaterButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!working)
            Close();
    }
}
