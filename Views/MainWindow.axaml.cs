using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Foot_Tracker.Tracking;
using Foot_Tracker.Tracking.Capture;
using Foot_Tracker.ViewModels;
using SkiaSharp;

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

            vm.ActiveWindow = this;

            vm.ConfirmAsync = message => ConfirmDialogWindow.ShowAsync(vm.ActiveWindow ?? this, message);

            vm.RequestClientSelection = async () =>
            {
                var dialogVm = new ViewModels.ClientSelectorViewModel();
                var dialog = new ClientSelectorWindow { DataContext = dialogVm };
                bool confirmed = await dialog.ShowDialog<bool?>(vm.ActiveWindow ?? this) == true;
                return confirmed ? dialogVm.SelectedClientNumber : 0;
            };

            vm.RequestPokemonSelection = async () =>
            {
                var dialogVm = new ViewModels.PokemonSelectorViewModel();
                dialogVm.PreselectExisting(vm.CurrentTargetNames);
                var dialog = new PokemonSelectorWindow { DataContext = dialogVm };
                bool confirmed = await dialog.ShowDialog<bool?>(vm.ActiveWindow ?? this) == true;
                return confirmed ? dialogVm.SelectedPokemons : null;
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

            vm.RequestImportMode = message =>
                ImportModeDialogWindow.ShowAsync(vm.ActiveWindow ?? this, message);

            // Backs the hidden "click a Currently Hunting sprite to swap it"
            // feature - see TargetSprite_PointerPressed below.
            vm.RequestSwapTargetSelection = async currentName =>
            {
                var dialogVm = new ViewModels.SwapPokemonViewModel(currentName);
                var dialog = new SwapPokemonWindow { DataContext = dialogVm };
                bool confirmed = await dialog.ShowDialog<bool?>(vm.ActiveWindow ?? this) == true;
                return confirmed ? dialogVm.SelectedPokemon : null;
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

    // New (not from the original WinForms app): lets the user hide individual
    // stat blocks from the main window's stats panel without stopping them
    // from being tracked - see ExcludeStatsViewModel/UiPreferences. Refreshes
    // MainWindowViewModel's Show* flags immediately after a successful save so
    // the change is visible without restarting the app.
    private async void ExcludeStatsButton_Click(object? sender, RoutedEventArgs e)
    {
        var window = new ExcludeStatsWindow
        {
            DataContext = new ViewModels.ExcludeStatsViewModel()
        };

        bool? saved = await window.ShowDialog<bool?>(this);

        if (saved == true && DataContext is MainWindowViewModel vm)
            vm.RefreshExcludedStats();
    }

    // Undocumented quality-of-life feature (not from the original WinForms
    // app): clicking directly on one of the "Currently Hunting" sprites lets
    // the user swap just that one target for a different Pokémon, instead of
    // reopening "Set Target" and re-picking every one of the 2-4 targets from
    // scratch. Deliberately wired with no visual affordance anywhere (no hand
    // cursor, no tooltip, no hint text) - see SwapPokemonViewModel's remarks.
    // No-ops for the "None" placeholder shown when no targets are set yet.
    private async void TargetSprite_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ViewModels.TargetDisplayItem { Name: not "None" } item })
            return;

        if (DataContext is not MainWindowViewModel vm)
            return;

        await vm.SwapTargetAsync(item.Name);
    }

    // Replaces CompactModeButton_Click: hides the main window and shows the
    // compact overlay, which reuses this window's MainWindowViewModel directly.
    private void CompactModeButton_Click(object? sender, RoutedEventArgs e)
    {
        var compact = new CompactWindow(this) { DataContext = DataContext };

        // Dialogs triggered from Compact Mode (Set Target, Reset's confirmation,
        // multi-client Play) need to be owned by whichever window is actually
        // visible - see MainWindowViewModel.ActiveWindow.
        if (DataContext is MainWindowViewModel vm)
            vm.ActiveWindow = compact;

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

    // File > Magma Login - the same gate the boss wiki scraper used to sit
    // behind (removed, see MIGRATION_GUIDE.md §28). On a correct login,
    // opens AdminActionsWindow so a Team Magma member can choose to either
    // post a new entry to the guild Events board or remove an existing one.
    // Both of those stay behind this real login step on purpose; just
    // looking at the board (EventsButton_Click, below) never needs one - see
    // MIGRATION_GUIDE.md §29 for why this ended up split into two
    // windows/menu entries instead of the one §28 shipped, and the latest
    // section for why a correct login now opens a small chooser instead of
    // CreateEventWindow directly.
    private async void AdminLoginButton_Click(object? sender, RoutedEventArgs e)
    {
        bool loggedIn = await AdminLoginWindow.ShowAsync(this);
        if (loggedIn)
            new AdminActionsWindow().Show(this);
    }

    // New top-level menu, next to Appearance (see MIGRATION_GUIDE.md §29) -
    // opens the guild Events board directly, with no login required just to
    // look. The whole point is guild members can check it on their own
    // terms, with nothing pushed at them; posting a new entry is the
    // separate, gated action above.
    private void EventsButton_Click(object? sender, RoutedEventArgs e)
    {
        new EventsWindow().Show(this);
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

    private void PreviouslyBattledUsersButton_Click(object? sender, RoutedEventArgs e)
    {
        new PreviouslyBattledUsersWindow().Show(this);
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
            string proClientPath = Path.Combine(downloadsFolder, $"ProTracker-Report-{timestamp}-PROClient.png");

            await SaveScreenshotAsync(screenshotPath);
            bool logCopied = TryCopyLatestLog(logPath);

            // Same capture method the whole OCR pipeline uses (EncounterTracker,
            // BossCooldownTracker) - a raw screenshot of the actual PRO client, not
            // the tracker app itself. Lets a report show exactly what the OCR was
            // reading, including on a user's own GUI scale/resolution, which the
            // app's own screenshot alone can't show at all.
            bool proClientCaptured = TrySaveProClientScreenshot(proClientPath);

            var savedFiles = new List<string> { Path.GetFileName(screenshotPath) };
            if (proClientCaptured)
                savedFiles.Add(Path.GetFileName(proClientPath));
            if (logCopied)
                savedFiles.Add(Path.GetFileName(logPath));

            string missingNote = !proClientCaptured && !logCopied
                ? " (no PRO client window or log file was found)"
                : !proClientCaptured
                    ? " (no PRO client window was found to screenshot)"
                    : !logCopied
                        ? " (no log file was found)"
                        : string.Empty;

            // Short by request - the full filenames (each with its own
            // timestamp) made this unreadable in the status bar's limited
            // space. The files themselves still carry each detail; a user
            // sending a report just needs to know it worked and where to look.
            string fileWord = savedFiles.Count == 1 ? "file" : "files";

            vm.StatusMessage = $"{savedFiles.Count} report {fileWord} saved to Downloads{missingNote}. Please send these to the developer.";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"Could not save the problem report: {ex.Message}";
        }
    }

    /// <summary>Returns false (not an error - just nothing to save) if no PRO
    /// client is currently selected/capturable. Draws colored boxes on the saved
    /// screenshot showing exactly where each detector reads from - see
    /// DebugRegionOverlay.cs - so a report shows precisely what the OCR saw, not
    /// just a plain screenshot.</summary>
    private static bool TrySaveProClientScreenshot(string path)
    {
        byte[]? pngBytes = WindowCaptureServiceFactory.Instance.CaptureSelectedWindowPng();

        if (pngBytes is null || pngBytes.Length == 0)
            return false;

        using SKBitmap? screenshot = ImageOps.DecodePng(pngBytes);

        if (screenshot is null)
        {
            // Couldn't decode for some reason - still save the raw capture rather
            // than losing it entirely.
            File.WriteAllBytes(path, pngBytes);
            return true;
        }

        using SKBitmap annotated = DebugRegionOverlay.DrawDetectionRegions(screenshot);

        byte[] annotatedPngBytes = ImageOps.EncodePng(annotated);
        File.WriteAllBytes(path, annotatedPngBytes);
        return true;
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

        // PngBitmapEncoderOptions.Save was tried here (the officially documented
        // non-obsolete replacement for the single-argument Save() overload), but it
        // stopped resolving after the SkiaSharp version bump forced a different
        // Avalonia.Skia resolution, breaking the Linux build entirely. A harmless
        // "obsolete" warning is a far better outcome than a build error, so this
        // reverts to the simple, always-available single-argument overload. See
        // MIGRATION_GUIDE.md.
        bitmap.Save(stream);
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