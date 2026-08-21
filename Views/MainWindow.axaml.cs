using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) => (DataContext as MainWindowViewModel)?.OnClosing();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            vm.ConfirmAsync = message => ConfirmDialogWindow.ShowAsync(this, message);

            vm.RequestClientSelection = async () =>
            {
                var dialogVm = new ViewModels.ClientSelectorViewModel();
                var dialog = new ClientSelectorWindow { DataContext = dialogVm };
                bool confirmed = await dialog.ShowDialog<bool?>(this) == true;
                return confirmed ? dialogVm.SelectedClientNumber : 0;
            };

            vm.RequestPokemonSelection = async () =>
            {
                var dialogVm = new ViewModels.PokemonSelectorViewModel();
                var dialog = new PokemonSelectorWindow { DataContext = dialogVm };
                bool confirmed = await dialog.ShowDialog<bool?>(this) == true;
                return confirmed ? dialogVm.SelectedPokemon : null;
            };

            vm.RequestSaveFilePath = async (suggestedFileName, extension) =>
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export Hunt Data",
                    SuggestedFileName = suggestedFileName,
                    DefaultExtension = extension,
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType($"{extension.ToUpperInvariant()} File")
                        {
                            Patterns = new[] { $"*.{extension}" }
                        }
                    }
                });

                return file?.TryGetLocalPath();
            };

            vm.RequestOpenFilePath = async extension =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import Hunt Data",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType($"{extension.ToUpperInvariant()} File")
                        {
                            Patterns = new[] { $"*.{extension}" }
                        }
                    }
                });

                return files.Count > 0 ? files[0].TryGetLocalPath() : null;
            };
        };
    }

    // Replaces the WinForms menu item that did:
    //   using var form = new AppearanceForm();
    //   if (form.ShowDialog(this) == DialogResult.OK) ThemeManager already reloaded internally
    private async void AppearanceButton_Click(object? sender, RoutedEventArgs e)
    {
        var window = new AppearanceWindow
        {
            DataContext = new ViewModels.AppearanceViewModel()
        };

        await window.ShowDialog<bool?>(this);
    }

    // Replaces CompactModeButton_Click: hides the main window and shows the
    // compact overlay, which reuses this window's MainWindowViewModel directly.
    private void CompactModeButton_Click(object? sender, RoutedEventArgs e)
    {
        var compact = new CompactWindow(this) { DataContext = DataContext };
        compact.Show();
        Hide();
    }

    // Replaces bossesToolStripMenuItem1_Click -> new BossCooldownForm().Show(this)
    private void BossCooldownsButton_Click(object? sender, RoutedEventArgs e)
    {
        new BossCooldownWindow().Show(this);
    }

    // Replaces the boss menu tree (OpenBoss/BossDifficultyMenuItem_Click) - see
    // BossListViewModel for why this is one browsable list instead of ~150 menu items.
    private void BossDatabaseButton_Click(object? sender, RoutedEventArgs e)
    {
        new BossListWindow().Show(this);
    }

    // Replaces the various *ToolStripMenuItem_Click handlers that each did
    // `new Counterparts("<group>"); form.Show();`
    private void CounterpartsMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string groupName })
            return;

        var window = new CounterpartsWindow();
        window.LoadGroup(groupName);
        window.Show(this);
    }

    // Replaces kantoToolStripMenuItem_Click (which was commented out/unwired in
    // the original - this actually wires it up).
    private void KantoMapButton_Click(object? sender, RoutedEventArgs e)
    {
        var window = new RegionMapWindow();
        window.LoadRegion("Kanto", "Kanto");
        window.Show(this);
    }

    // Replaces testToolStripMenuItem_Click -> new Test().Show(this)
    private void MegaStonesGuideButton_Click(object? sender, RoutedEventArgs e)
    {
        var window = new GuideWindow();
        window.LoadGuide("Test", "Mega Stones Guide");
        window.Show(this);
    }

    // Replaces huntingToolStripMenuItem_Click -> new HuntingStats().Show(this)
    private void HuntingStatsButton_Click(object? sender, RoutedEventArgs e)
    {
        new HuntingStatsWindow().Show(this);
    }

    private void PvpStatsButton_Click(object? sender, RoutedEventArgs e)
    {
        new PvpStatsWindow().Show(this);
    }

    // Replaces excavationsToolStripMenuItem1_Click and the other forms that were
    // empty placeholders in the original project too - see MIGRATION_GUIDE.md.
    private void PlaceholderMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        string title = sender is MenuItem { Tag: string t } ? t : "Coming Soon";
        new PlaceholderWindow(title).Show(this);
    }

    // New (not from the original WinForms app): a self-service bug-report button.
    // Deliberately does NOT send anything over the network or embed any email
    // credentials in the app - it just saves a screenshot of the window and a
    // copy of today's log file to the user's Downloads folder, and tells them
    // to send those files over however they'd normally reach the developer
    // (e.g. Discord). No outbound email flow at all, by request - avoids adding
    // yet another "click here" pattern to an app that gets shared around a
    // Discord community already dealing with phishing attempts.
    private async void ReportProblemButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        try
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string downloadsFolder = GetDownloadsFolder();
            Directory.CreateDirectory(downloadsFolder);

            string screenshotPath = Path.Combine(downloadsFolder, $"ProTracker-Report-{timestamp}.png");
            string logPath = Path.Combine(downloadsFolder, $"ProTracker-Report-{timestamp}.log");

            await SaveScreenshotAsync(screenshotPath);
            bool logCopied = TryCopyLatestLog(logPath);

            vm.StatusMessage = logCopied
                ? $"Report saved to Downloads: {Path.GetFileName(screenshotPath)} + {Path.GetFileName(logPath)}. Please send both to the developer."
                : $"Screenshot saved to Downloads: {Path.GetFileName(screenshotPath)} (no log file was found). Please send it to the developer.";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"Could not save the problem report: {ex.Message}";
        }
    }

    private async Task SaveScreenshotAsync(string path)
    {
        var pixelSize = new PixelSize(
            Math.Max(1, (int)(Bounds.Width * RenderScaling)),
            Math.Max(1, (int)(Bounds.Height * RenderScaling)));

        var dpi = new Vector(96 * RenderScaling, 96 * RenderScaling);

        using var bitmap = new RenderTargetBitmap(pixelSize, dpi);
        bitmap.Render(this);

        await using var stream = File.Create(path);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private static bool TryCopyLatestLog(string destinationPath)
    {
        // Serilog (see Program.cs) writes rolling daily logs here.
        string logsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker",
            "Logs");

        if (!Directory.Exists(logsFolder))
            return false;

        FileInfo? latestLog = new DirectoryInfo(logsFolder)
            .GetFiles("protracker-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latestLog is null)
            return false;

        File.Copy(latestLog.FullName, destinationPath, overwrite: true);
        return true;
    }

    private static string GetDownloadsFolder()
    {
        // Env.SpecialFolder.UserProfile maps to the user's home directory on
        // Windows, Linux, and macOS alike - "Downloads" under it is the standard
        // convention on all three (there's no dedicated cross-platform
        // SpecialFolder.Downloads in .NET).
        try
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (!string.IsNullOrWhiteSpace(userProfile))
                return Path.Combine(userProfile, "Downloads");
        }
        catch
        {
            // Fall through to the fallback below.
        }

        // Last resort if the user profile folder couldn't be resolved - still
        // somewhere findable, just not the conventional Downloads location.
        return Path.Combine(AppContext.BaseDirectory, "Reports");
    }
}