using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Foot_Tracker.Services;
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

                // §105: the picker also reports whether the user confirmed
                // taking the profile from another running tracker window.
                return confirmed
                    ? (dialogVm.SelectedClientNumber, dialogVm.ForceTakeover)
                    : (0, false);
            };

            vm.RequestPokemonSelection = async () =>
            {
                var dialogVm = new ViewModels.PokemonSelectorViewModel();
                dialogVm.PreselectExisting(vm.CurrentTargetNames);
                var dialog = new PokemonSelectorWindow { DataContext = dialogVm };
                bool confirmed = await dialog.ShowDialog<bool?>(vm.ActiveWindow ?? this) == true;
                return confirmed ? dialogVm.SelectedPokemons : null;
            };

            // §187: both pickers used to end in a bare TryGetLocalPath()
            // whose null is indistinguishable from Cancel. On Windows that is
            // fine, because a picked file always has a local path. On Linux
            // it is not: the dialog is xdg-desktop-portal over D-Bus, and it
            // can hand back a file the app cannot address by path at all -
            // a recent:// entry, a gvfs mount, a portal handle Avalonia
            // cannot resolve - in which case the user has picked a file, seen
            // the dialog close, and had nothing happen. Worse, if the portal
            // is not installed the StorageProvider cannot show a dialog in
            // the first place and returns immediately, so the button looks
            // dead. Both now say which one it was, here and in the log.
            vm.RequestSaveFilePath = async (suggestedFileName, extension) =>
            {
                if (!StorageProvider.CanSave)
                {
                    Serilog.Log.Error(
                        "This platform's save dialog is unavailable (StorageProvider.CanSave is false) - " +
                        "on Linux this usually means xdg-desktop-portal is not installed or not running");

                    throw new PlatformNotSupportedException(
                        "this system has no save dialog available (on Linux, xdg-desktop-portal is usually the missing piece)");
                }

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

                return LocalPathOf(file, "export");
            };

            vm.RequestOpenFilePath = async extension =>
            {
                if (!StorageProvider.CanOpen)
                {
                    Serilog.Log.Error(
                        "This platform's open dialog is unavailable (StorageProvider.CanOpen is false) - " +
                        "on Linux this usually means xdg-desktop-portal is not installed or not running");

                    throw new PlatformNotSupportedException(
                        "this system has no file-open dialog available (on Linux, xdg-desktop-portal is usually the missing piece)");
                }

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

                return files.Count > 0 ? LocalPathOf(files[0], "import") : null;
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

    /// <summary>
    /// §187: turns a picked file into a path the export/import services can
    /// open, and says so in the log when it cannot.
    ///
    /// TryGetLocalPath is the right first try - it is what resolves a portal
    /// document handle back to a real path when the portal supports it. When
    /// it comes back empty the file's own URI is tried, because a plain
    /// file: URI is perfectly usable and Avalonia has been known not to
    /// unwrap every one of them. Anything still unresolved (recent:, a gvfs
    /// or smb mount, a bare portal handle) genuinely has no path this app can
    /// hand to File.WriteAllText, so it returns null - but it returns null
    /// having written down the scheme that caused it, which is the fact that
    /// was missing from the Linux report that prompted all this.
    ///
    /// The URI is logged, and the URI contains a folder name. That is the
    /// same class of detail the log already carries in its own file paths,
    /// it stays on the user's machine unless they choose to send a report,
    /// and without it the failure is unfixable.
    /// </summary>
    private static string? LocalPathOf(IStorageFile? file, string action)
    {
        if (file is null)
            return null;

        string? local = file.TryGetLocalPath();

        if (!string.IsNullOrWhiteSpace(local))
            return local;

        Uri uri = file.Path;

        if (uri.IsAbsoluteUri && uri.IsFile)
        {
            Serilog.Log.Warning(
                "The {Action} dialog returned a file with no local path; using its file: URI instead - {Uri}",
                action, uri);

            return uri.LocalPath;
        }

        Serilog.Log.Error(
            "The {Action} dialog returned a file this app cannot open by path - scheme '{Scheme}', {Uri}. " +
            "Choosing a file in an ordinary folder (not Recent, and not a network or cloud mount) avoids this",
            action, uri.IsAbsoluteUri ? uri.Scheme : "(relative)", uri);

        return null;
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

    // Originally a single File-menu checkbox (MIGRATION_GUIDE.md #65), then a
    // pair of File-menu submenus (#71) - now this dedicated window (#72), the
    // same ShowDialog+refresh-on-save shape as ExcludeStatsButton_Click below.
    // Refreshes MainWindowViewModel's SinceFormSound/SinceShinySound
    // immediately after a successful save, so OnRareEncounterDetected picks up
    // the change without needing an app restart.
    private async void SoundSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        var window = new SoundSettingsWindow
        {
            DataContext = new ViewModels.SoundSettingsViewModel()
        };

        bool? saved = await window.ShowDialog<bool?>(this);

        if (saved == true && DataContext is MainWindowViewModel vm)
            vm.RefreshSoundSelections();
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

    /// <summary>§135. Warning first - unless the player has asked not to
    /// see it - then the Set Screen Boundaries window. The checkbox is
    /// honoured whichever way the warning closes; Cancel still stops here.
    /// The picker saves through the view model itself, so there is nothing
    /// to refresh afterwards.</summary>
    private async void SetScreenBoundariesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (vm.ShouldShowBoundariesWarning)
        {
            (bool proceed, bool dontShowAgain) = await BoundariesWarningWindow.ShowAsync(this);

            if (dontShowAgain)
                vm.SuppressBoundariesWarning();

            if (!proceed)
                return;
        }

        var window = new ScreenBoundariesWindow(vm);

        await window.ShowDialog<bool?>(this);
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

    // One session-history window per Pokemon at most - clicking the same row
    // again brings the existing window forward instead of stacking a
    // duplicate. Keyed by the same resolved name the Session Encounters
    // tables aggregate by; entries remove themselves when their window
    // closes.
    private readonly Dictionary<string, SessionEncounterHistoryWindow> openEncounterHistoryWindows =
        new(StringComparer.OrdinalIgnoreCase);

    // Opens the per-Pokemon SESSION encounter history for whichever Session
    // Encounters row was clicked - every detected encounter of the current
    // hunt (see MIGRATION_GUIDE.md §99), NOT the Catch Logs drilldown this
    // used to open: HuntLogSpeciesDetailWindow ("Catch History", successful
    // catches only, survives a hunt reset) stays reachable by clicking a row
    // inside the Catch Logs window itself, where catch-specific data is what
    // the click came from. The two are separate features over separate data
    // on purpose - a caught Pokemon appears in both.
    private void EncounterRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ViewModels.EncounterCountRow row })
            return;

        // §101: in Admin Client mode the session history deliberately records
        // nothing (it is normal-client data), so opening a window that could
        // only show the NORMAL client's rows under admin counters would
        // mislead - say why instead.
        if (Services.AdminModeService.IsActive)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.StatusMessage = "Per-Pokémon session history does not record in Admin Client mode.";

            return;
        }

        if (openEncounterHistoryWindows.TryGetValue(row.PokemonName, out SessionEncounterHistoryWindow? existing))
        {
            existing.Activate();
            return;
        }

        var window = new SessionEncounterHistoryWindow(row.PokemonName);

        openEncounterHistoryWindows[row.PokemonName] = window;
        window.Closed += (_, _) => openEncounterHistoryWindows.Remove(row.PokemonName);

        // §189: unowned, so minimizing the tracker leaves it up.
        WindowRegistry.ShowUnowned(window, this);
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

    // Replaces bossesToolStripMenuItem1_Click -> new BossCooldownForm().Show(this).
    // Every non-modal opener below goes through WindowRegistry (§103): one
    // window per type, repeat clicks activate the existing one.
    private void BossCooldownsButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new BossCooldownWindow());
    }

    // Replaces the boss menu tree (OpenBoss/BossDifficultyMenuItem_Click) - see
    // BossListViewModel for why this is one browsable list instead of ~150 menu items.
    private void BossDatabaseButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new BossListWindow());
    }

    // File > Admin Login - the same gate the boss wiki scraper used to sit
    // behind (removed, see MIGRATION_GUIDE.md §28). On a correct login,
    // marks this session authenticated (AdminModeService, §101) and opens
    // AdminActionsWindow: the Events board tools, the Admin Client toggle,
    // and the Admin Console.
    // Both of those stay behind this real login step on purpose; just
    // looking at the board (EventsButton_Click, below) never needs one - see
    // MIGRATION_GUIDE.md §29 for why this ended up split into two
    // windows/menu entries instead of the one §28 shipped, and the latest
    // section for why a correct login now opens a small chooser instead of
    // CreateEventWindow directly.
    private async void AdminLoginButton_Click(object? sender, RoutedEventArgs e)
    {
        // §103: authentication is a per-process SESSION, not a per-click
        // hurdle - once this run of the app has passed Admin Login, opening
        // the admin tools again goes straight through until Admin Logout
        // (AdminActionsWindow) or the app closes. Nothing about this is
        // persisted; a restart always asks again (AdminModeService).
        if (!AdminModeService.IsAuthenticated)
        {
            bool loggedIn = await AdminLoginWindow.ShowAsync(this);

            if (!loggedIn)
                return;

            AdminModeService.MarkAuthenticated();
        }

        WindowRegistry.ShowOrActivate(this, () => new AdminActionsWindow());
    }

    // New top-level menu, next to Appearance (see MIGRATION_GUIDE.md §29) -
    // opens the guild Events board directly, with no login required just to
    // look. The whole point is guild members can check it on their own
    // terms, with nothing pushed at them; posting a new entry is the
    // separate, gated action above.
    private void EventsButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new EventsWindow());
    }

    // File > Announcements - reads PRO's real update history straight from
    // the Update Logs forum topic, not Discord and not the general
    // Announcements RSS feed this started out reading (see
    // MIGRATION_GUIDE.md §83/§84 for why). Same non-modal Show(this),
    // self-constructed-DataContext pattern as
    // BossDatabaseButton_Click/HuntingStatsButton_Click below - nothing here
    // needs a result back the way SoundSettingsButton_Click's dialog does.
    private void AnnouncementsButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new AnnouncementsWindow());
    }

    // §154: the Simulator - one window, re-activated on repeat clicks, no
    // Admin Login (it is not an admin tool and touches no live data).
    // Closing it cancels only its own battle (SimulatorWindow disposes its
    // view model); hunting, capture and every other window are untouched.
    // §221: once per machine, a note about where the Simulator's Pokemon
    // come from, in front of the window rather than inside it - six empty
    // team slots invite exactly the wrong guess, and the answer needs to
    // arrive before the guess. ShowOnceAsync is a no-op after the first
    // time, so this stays a plain open on every launch after that.
    private async void SimulatorButton_Click(object? sender, RoutedEventArgs e)
    {
        await SimulatorFirstRunWindow.ShowOnceAsync(this);

        WindowRegistry.ShowOrActivate(this, () => new SimulatorWindow());
    }

    // The three Calculators menu entries - same non-modal Show(this) shape as
    // AnnouncementsButton_Click above; each window builds its own ViewModel.
    // Species data, move data and the ported battle math are covered in
    // MIGRATION_GUIDE.md §89.
    private void DamageCalculatorButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new DamageCalculatorWindow());
    }

    private void WorldQuestCalculatorButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new WorldQuestCalculatorWindow());
    }

    private void IvCalculatorButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new IvCalculatorWindow());
    }

    /// <summary>§233. The live World Quest tracker - not the calculator above.
    /// Through WindowRegistry like every other window here, which matters more
    /// than usual for this one: it polls the PRO client while it is open, and
    /// two copies would poll twice for nothing.</summary>
    private void WorldQuestButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new WorldQuestWindow());
    }

    /// <summary>§236. The Auction Tracker. Through WindowRegistry like every
    /// other window here, which matters for this one in particular: it polls
    /// the Trade Zone while it is open, and two copies would double the
    /// traffic against someone else's forum for no extra information.</summary>
    private void AuctionTrackerButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new AuctionTrackerWindow());
    }

    // Replaces the various *ToolStripMenuItem_Click handlers that each did
    // `new Counterparts("<group>"); form.Show();`
    private void CounterpartsMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string groupName })
            return;

        // Keyed per event group - one window per GROUP is the deliberate
        // multi-instance case the registry supports (§103).
        WindowRegistry.ShowOrActivate(this, () =>
        {
            var window = new CounterpartsWindow();
            window.LoadGroup(groupName);
            return window;
        }, contentKey: groupName);
    }

    // Replaces kantoToolStripMenuItem_Click (which was commented out/unwired in
    // the original - this actually wires it up).
    private void KantoMapButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () =>
        {
            var window = new RegionMapWindow();
            window.LoadRegion("Kanto", "Kanto");
            return window;
        }, contentKey: "Kanto");
    }

    // Replaces testToolStripMenuItem_Click -> new Test().Show(this)
    private void MegaStonesGuideButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () =>
        {
            var window = new GuideWindow();
            window.LoadGuide("Test", "Mega Stones Guide");
            return window;
        }, contentKey: "MegaStones");
    }

    // Replaces huntingToolStripMenuItem_Click -> new HuntingStats().Show(this)
    private void HuntingStatsButton_Click(object? sender, RoutedEventArgs e)
    {
        // §103: this used to open TWICE per click - the old Stats menu's
        // PARENT item carried this same Click handler, and MenuItem.Click is
        // a routed event, so a child click bubbled up and ran it again (and
        // Exclude Stats opened Lifetime Stats alongside itself the same
        // way). The parent handler is gone with the Statistics menu rebuild;
        // the registry additionally makes repeat clicks activate instead of
        // duplicate.
        WindowRegistry.ShowOrActivate(this, () => new HuntingStatsWindow());
    }

    private void PreviouslyBattledUsersButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new PreviouslyBattledUsersWindow());
    }

    // Opens the Hunting Log window (Hunting Logs menu, between Counterparts and
    // Guides) - the full per-encounter log behind the Session Encounters table
    // below (species, level, map, timestamp), distinct from that table's
    // running per-species tally. See HuntLogWindow/HuntLogService.
    private void HuntingLogButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new HuntLogWindow());
    }

    // Replaces excavationsToolStripMenuItem1_Click and the other forms that were
    // empty placeholders in the original project too - see MIGRATION_GUIDE.md.
    private void PlaceholderMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        string title = sender is MenuItem { Tag: string t } ? t : "Coming Soon";
        WindowRegistry.ShowOrActivate(this, () => new PlaceholderWindow(title), contentKey: title);
    }

    // New (not from the original WinForms app): a self-service bug-report
    // button. Saves a screenshot of the window and a copy of today's log
    // file to the user's Downloads folder, and tells the player to send
    // those files over however they'd normally reach the developer (e.g.
    // Discord).
    //
    // §229: sending is limited to one report every ten minutes, and a
    // delivered report's copies are removed from Downloads afterwards.
    //
    // §228: and it asks what happened before it sends anything - see
    // ReportDescriptionWindow for why that question comes after the capture
    // rather than before it.
    //
    // §226: it now also tries to deliver them. The files are written to
    // Downloads FIRST and the upload is attempted afterwards, so a player
    // whose upload fails is in exactly the position they were in before -
    // holding the files, reading the message that says so. Nothing about
    // this path can fail in a way that loses a report.
    //
    // BugReportEmailService and ReportEmailSettings (§73) are what this
    // replaces and are now dead code. §73's own comment gave the reason it
    // mailed from a throwaway Gmail - "this project has no existing backend
    // to build it on top of" - and that stopped being true when the events
    // Worker shipped. The App Password was never filled in and the sender
    // was never called from here, so nothing that ever ran is being taken
    // away. Both files, and the MailKit package reference they are the only
    // users of, can be deleted; see MIGRATION_GUIDE.md §226.
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
            string checkPath = Path.Combine(downloadsFolder, $"ProTracker-Report-{timestamp}-TrackingCheck.txt");

            await SaveScreenshotAsync(screenshotPath);

            // §134: the one-shot pipeline check runs BEFORE the log is
            // copied, so the copy carries its block, and is kept as its own
            // small file as well - it is the first thing to read. Off the UI
            // thread: it captures a frame and runs OCR once.
            string trackingCheck = await Task.Run(TrackerDiagnostics.RunTrackingCheck);
            File.WriteAllText(checkPath, trackingCheck);

            bool logCopied = TryCopyLatestLog(logPath);

            // Same capture method the whole OCR pipeline uses (EncounterTracker,
            // BossCooldownTracker) - a raw screenshot of the actual PRO client, not
            // the tracker app itself. Lets a report show exactly what the OCR was
            // reading, including on a user's own GUI scale/resolution, which the
            // app's own screenshot alone can't show at all.
            bool proClientCaptured = TrySaveProClientScreenshot(proClientPath);

            var savedFiles = new List<string> { Path.GetFileName(screenshotPath), Path.GetFileName(checkPath) };
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

            string savedMessage =
                $"{savedFiles.Count} report {fileWord} saved to Downloads{missingNote}. Please send these to the developer.";

            // §226. Every path is already written; the send only ever adds.
            var uploaded = new List<string> { screenshotPath, checkPath };

            if (proClientCaptured)
                uploaded.Add(proClientPath);

            if (logCopied)
                uploaded.Add(logPath);

            // §228: with no server configured there is nothing to send to,
            // so asking what happened would be asking for nothing. Stop
            // where this button stopped before §226.
            if (!EventsSyncService.IsOnline)
            {
                vm.StatusMessage = savedMessage;
                return;
            }

            // §229: inside the cooldown the capture is still worth having -
            // something just went wrong and these files are the evidence -
            // so it is written and kept, and only the sending is refused.
            // Checked here rather than after the description box, so nobody
            // types a paragraph to be told it was not wanted.
            TimeSpan wait = BugReportUploadService.RemainingCooldown();

            if (wait > TimeSpan.Zero)
            {
                int minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));

                vm.StatusMessage =
                    $"{savedFiles.Count} report {fileWord} saved to Downloads{missingNote}. " +
                    $"Another report can be sent in {minutes} minute{(minutes == 1 ? "" : "s")}.";

                return;
            }

            // §228. Nothing leaves the machine until the player has said
            // what went wrong. The capture above already happened, which is
            // the whole reason this question comes second: the screenshot
            // has to show the problem, not a dialog asking about it.
            string? description = await ReportDescriptionWindow.AskAsync(this, savedFiles);

            if (string.IsNullOrWhiteSpace(description))
            {
                // Just save locally, or the window closed. Exactly the
                // outcome the message below already describes.
                vm.StatusMessage = savedMessage;
                return;
            }

            vm.StatusMessage =
                $"{savedFiles.Count} report {fileWord} saved to Downloads{missingNote}. Sending...";

            // What the player wrote, then whatever could not be captured -
            // the one piece of context a bundle cannot show by being opened.
            // Trimmed to the server's own REPORT_MAX_NOTE so nothing is cut
            // somewhere the player cannot see it happen.
            string note = description.Trim();

            if (missingNote.Length > 0)
                note = note + " " + missingNote.Trim();

            if (note.Length > 500)
                note = note[..500];

            bool sent = await BugReportUploadService.TrySendAsync(uploaded, note);

            if (!sent)
            {
                vm.StatusMessage = savedMessage;
                return;
            }

            // §229: the copies in Downloads existed so the player had
            // something to send by hand. Once the server has confirmed it
            // took them, they are litter in a folder nobody asked to have
            // filled. Anything that will not delete is left where it is -
            // the report has already arrived, and a stuck file is not worth
            // interrupting anyone over.
            int removed = BugReportUploadService.DeleteSentFiles(uploaded);

            vm.StatusMessage = removed == uploaded.Count
                ? $"{savedFiles.Count} report {fileWord} sent to the developer{missingNote}. The copies in Downloads have been cleaned up."
                : $"{savedFiles.Count} report {fileWord} sent to the developer{missingNote}. Some copies are still in your Downloads folder.";
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