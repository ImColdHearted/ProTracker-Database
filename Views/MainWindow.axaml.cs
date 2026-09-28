using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
// §264: TryGetBitmapAsync lives in ClipboardExtensions, the same place
// SetTextAsync moved to in the Avalonia 12 clipboard rework - IClipboard
// itself carries neither (see AdminConsoleWindow.axaml.cs).
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Foot_Tracker.Models;
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

        // §369. One GET of a small JSON file, once, after the window is up.
        //
        // On Opened rather than in a constructor, for §345's reason - a
        // constructor cannot await - and after the window is showing rather
        // than before, because nothing about this should delay the tracker
        // appearing. CheckAsync answers null for every failure, and
        // OfferUpdate(null) leaves the bar exactly as it was, so a player
        // with no connection sees nothing at all rather than an error about
        // something they did not ask for.
        Opened += async (_, _) =>
        {
            UpdateService.AvailableUpdate? found = await UpdateService.CheckAsync();

            if (DataContext is MainWindowViewModel vm)
                vm.OfferUpdate(found);
        };

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

            // §251: World Quest mode's Submit Screenshot button. The same
            // shape as the import picker above, §187's Linux notes included:
            // a missing portal is reported, and a picked file the app cannot
            // address by path is logged by LocalPathOf and returns null,
            // which the view model treats as a cancel.
            // §264: the clipboard route behind Ctrl+V. Avalonia 12 hands back
            // an Avalonia Bitmap; the detectors want SkiaSharp, so it is
            // re-encoded to PNG bytes here and decoded on the worker thread.
            // Save(stream) is the single-argument overload on purpose - see
            // the note on CaptureWindowPngAsync below for why the documented
            // replacement is not used in this project.
            vm.Quest.RequestClipboardImage = async () =>
            {
                if (Clipboard is null)
                    return null;

                using Bitmap? image = await Clipboard.TryGetBitmapAsync();

                if (image is null)
                    return null;

                using var stream = new MemoryStream();
                image.Save(stream, new PngBitmapEncoderOptions());

                return stream.ToArray();
            };

            vm.Quest.RequestScreenshotFile = async () =>
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
                    Title = "Submit a World Quest Screenshot",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Screenshots")
                        {
                            Patterns = new[] { "*.png", "*.bmp", "*.jpg", "*.jpeg", "*.webp" }
                        }
                    }
                });

                return files.Count > 0 ? LocalPathOf(files[0], "screenshot") : null;
            };
        };
    }

    /// <summary>
    /// §264. Ctrl+V submits whatever image is on the clipboard, so Win+Shift+S
    /// then Ctrl+V counts a catch without a file ever being saved.
    ///
    /// Only in World Quest mode, and only when the focus is not in a text box:
    /// Ctrl+V inside the IV total box is pasting a number, which is what the
    /// player meant. A TextBox marks the paste gesture handled and this is a
    /// bubbling handler, so it would usually not be reached anyway - the focus
    /// test is the part that does not depend on that staying true.
    /// </summary>
    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        if (DataContext is not MainWindowViewModel vm || !vm.IsWorldQuestPanel)
            return;

        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        if (vm.Quest.PasteScreenshotCommand.CanExecute(null))
            vm.Quest.PasteScreenshotCommand.Execute(null);

        e.Handled = true;
    }

    /// <summary>§251. Enter in the World Quest IV total box is Add IVs, the
    /// way Enter in the admin login boxes is Login - a player adding a run of
    /// catches by hand should not have to reach for the mouse between them.</summary>
    private void WorldQuestIvTotalBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainWindowViewModel vm)
            return;

        if (vm.Quest.SubmitPokemonCommand.CanExecute(null))
            vm.Quest.SubmitPokemonCommand.Execute(null);

        e.Handled = true;
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
    /// <summary>§369. The green Update item. It only exists while
    /// PendingUpdate does, but the null check stays: the item's visibility
    /// and the view model's state are two things, and a handler that trusts
    /// the first to imply the second is one race away from a
    /// NullReferenceException.</summary>
    private async void UpdateButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.PendingUpdate is null)
            return;

        await new UpdateWindow(vm.PendingUpdate).ShowDialog(this);
    }

    private async void AppearanceButton_Click(object? sender, RoutedEventArgs e)
    {
        var window = new AppearanceWindow
        {
            DataContext = new ViewModels.AppearanceViewModel()
        };

        await window.ShowDialog<bool?>(this);
    }

    // §345. The community gallery. Same ShowDialog shape as Appearance
    // above; it applies through AppearanceSettingsRepository and
    // ThemeManager.Reload exactly as that window's Apply does, so there is
    // nothing to refresh here afterwards - the theme is already live by the
    // time this returns.
    private async void CustomAppearancesButton_Click(object? sender, RoutedEventArgs e)
    {
        var window = new CustomAppearancesWindow
        {
            DataContext = new ViewModels.CustomAppearancesViewModel()
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
        // mislead - say why instead. §249: this decision is downstream of
        // SessionEncounterHistoryService's own gate and must agree with it,
        // so it reads the same predicate.
        if (Services.IsolatedSession.IsActive)
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
    // §280: the Search item on the menu bar.
    private void SearchButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new SearchWindow());
    }

    // §278: File - Tracker Settings. Non-modal Show(this), the same shape as
    // the other small settings windows.
    private void TrackerSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new TrackerSettingsWindow());
    }

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

    // Replaces the boss menu tree (OpenBoss/BossDifficultyMenuItem_Click) - see
    // BossListViewModel for why this is one browsable list instead of ~150 menu items.
    private void BossDatabaseButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new BossListWindow());
    }

    // File > Admin Login - the same gate the boss wiki scraper used to sit
    // behind (removed, see MIGRATION_GUIDE.md §28). On a correct login,
    // marks this session authenticated (AdminModeService, §101) and opens
    // AdminActionsWindow: the Admin Client toggle, the Admin Console and the
    // scrapers. §253: the Events board tools that were the window's first
    // two buttons are gone with the board.
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

    // §253: EventsButton_Click, which opened the guild Events board from the
    // top-level Events menu (§29), is gone with the board.

    // File > Announcements - reads PRO's real update history straight from
    // the Update Logs forum topic, not Discord and not the general
    // Announcements RSS feed this started out reading (see
    // MIGRATION_GUIDE.md §83/§84 for why). Same non-modal Show(this),
    // self-constructed-DataContext pattern as
    // BossDatabaseButton_Click/HuntingStatsButton_Click below - nothing here
    // needs a result back the way SoundSettingsButton_Click's dialog does.
    private void AnnouncementsButton_Click(object? sender, RoutedEventArgs e)
    {
        // §267: opening the window reads the newest post, so the yellow on
        // the menu item goes now rather than waiting out its day.
        (DataContext as MainWindowViewModel)?.MarkNewsRead();

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

    private void IvCalculatorButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new IvCalculatorWindow());
    }

    // §233's WorldQuestButton_Click opened the World Quest window here.
    // §251 folded that window into the stats panel (MainWindowViewModel.
    // Quest, World Quest mode); the window and its opener are gone.

    /// <summary>§254. The stats panel's Remove Catch button in World Quest
    /// mode: the list of every catch counted for the quest, with a Remove on
    /// each row. The window's DataContext is the SAME quest view model the
    /// panel shows, so the list is live - a catch Auto Detect counts while
    /// the window is open appears in it - and a removal changes the figures
    /// on the panel the moment it happens. Single-instance through the
    /// registry; it closes itself when the mode is left.</summary>
    private void WorldQuestCatchesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        WindowRegistry.ShowOrActivate(this, () => new WorldQuestCatchesWindow { DataContext = vm.Quest });
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

    // §397. Game Data -> Spawns -> one region. The item's Tag names the
    // region; one window per region, so a second click brings the open page
    // forward. An unknown tag opens nothing rather than a blank page.
    /// <summary>§407. Game Data → Maps: the world picture and the search
    /// panel, one window.</summary>
    private void MapsMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        WindowRegistry.ShowOrActivate(this, () => new MapsWindow());
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

            // §391: where this window is, for the check - read here, on the
            // UI thread, before the check runs off it.
            string pinned = Topmost ? "yes" : "no";
            string trackerWindow =
                $"{Bounds.Width:F0}x{Bounds.Height:F0} logical at ({Position.X},{Position.Y}), " +
                $"scaling {RenderScaling:0.##}, pinned on top: {pinned}, state {WindowState}";

            // §134: the one-shot pipeline check runs BEFORE the log is
            // copied, so the copy carries its block, and is kept as its own
            // small file as well - it is the first thing to read. Off the UI
            // thread: it captures a frame and runs OCR once.
            string trackingCheck = await Task.Run(() => TrackerDiagnostics.RunTrackingCheck(trackerWindow));
            File.WriteAllText(checkPath, trackingCheck);

            // Same capture method the whole OCR pipeline uses (EncounterTracker,
            // BossCooldownTracker) - a raw screenshot of the actual PRO client, not
            // the tracker app itself. Lets a report show exactly what the OCR was
            // reading, including on a user's own GUI scale/resolution, which the
            // app's own screenshot alone can't show at all. §391: before the
            // log copy, so the log can be cut to what the bundle has room for.
            bool proClientCaptured = TrySaveProClientScreenshot(proClientPath);

            long logBudget = LogCopyBudget(screenshotPath, checkPath, proClientPath);
            bool logCopied = TryCopyLatestLog(logPath, logBudget, out bool logTrimmed);

            if (logTrimmed)
                Serilog.Log.Information("Report: today's log is over the upload cap - the copy carries its first and last parts ({Budget} bytes).", logBudget);

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

            IReadOnlyList<string>? sent = await BugReportUploadService.TrySendAsync(uploaded, note);

            if (sent is null)
            {
                vm.StatusMessage = savedMessage;
                return;
            }

            // §229: the copies in Downloads existed so the player had
            // something to send by hand. Once the server has confirmed it
            // took them, they are litter in a folder nobody asked to have
            // filled. Anything that will not delete is left where it is -
            // the report has already arrived, and a stuck file is not worth
            // interrupting anyone over. §391: only what went - a file the
            // upload left out for its size stays, and is named.
            int removed = BugReportUploadService.DeleteSentFiles(sent);
            int leftOut = uploaded.Count - sent.Count;
            string leftOutNote = string.Empty;

            if (leftOut > 0)
            {
                string names = string.Join(", ", uploaded.Where(path => !sent.Contains(path)).Select(path => Path.GetFileName(path)));

                leftOutNote = leftOut == 1
                    ? $" 1 file was too large to send and stays in Downloads: {names}."
                    : $" {leftOut} files were too large to send and stay in Downloads: {names}.";
            }

            string sentWord = sent.Count == 1 ? "file" : "files";

            vm.StatusMessage = removed == sent.Count
                ? $"{sent.Count} report {sentWord} sent to the developer{missingNote}. The sent copies in Downloads have been cleaned up.{leftOutNote}"
                : $"{sent.Count} report {sentWord} sent to the developer{missingNote}. Some copies are still in your Downloads folder.{leftOutNote}";
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

        // §291: the BitmapEncoderOptions overload, which is what the obsolete
        // warning on the single-argument Save asks for. §16 tried this once
        // and it failed to resolve - but that was Avalonia 11 under a
        // SkiaSharp bump that dragged Avalonia.Skia to a mismatched version;
        // the project is on a single Avalonia 12.1.2 now and the type is
        // where the warning says it is.
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    // §391. How much of a long log the copy keeps: the first part, which
    // has the start-up lines (platform, capture environment, the sound and
    // appearance that were loaded), and then as much of the end as fits
    // the budget - the end is where whatever prompted the report is.
    private const int LogCopyHeadBytes = 64 * 1024;

    // Room for the marker line and the multipart framing, and the least a
    // copy is worth cutting down to - below that the whole log goes as it
    // is, and the upload names it as left out rather than send a stub.
    private const long LogCopyMarginBytes = 64 * 1024;
    private const long LogCopyLeastBytes = 256 * 1024;

    /// <summary>§391. What the bundle has room for once the screenshots and
    /// the check are in it: the per-file cap, or what is left of the total
    /// cap, whichever is smaller, less the margin. A 1920x1080 client
    /// screenshot is easily three megabytes on its own, and the log used to
    /// be the last file added - so it was the one the total cap dropped.</summary>
    private static long LogCopyBudget(params string[] otherFiles)
    {
        long others = 0;

        foreach (string path in otherFiles)
        {
            try
            {
                if (File.Exists(path))
                    others += new FileInfo(path).Length;
            }
            catch
            {
                // A file that cannot be sized is counted as nothing; the
                // upload's own checks still hold.
            }
        }

        long budget = Math.Min(BugReportUploadService.MaxFileBytes, BugReportUploadService.MaxTotalBytes - others) - LogCopyMarginBytes;

        return Math.Max(budget, LogCopyLeastBytes);
    }

    /// <summary>Copies today's log for the report. §391: a log over the
    /// upload cap used to be copied whole, silently left out of the upload
    /// for its size, and then deleted from Downloads with the files that
    /// went - so a report arrived with no log and said nothing about it.
    /// The copy now fits <paramref name="limitBytes"/>: whole when it can
    /// be, otherwise its first <see cref="LogCopyHeadBytes"/> and its last
    /// part, cut at line ends, with a line between saying how much is
    /// missing. <paramref name="trimmed"/> says which it was.</summary>
    private static bool TryCopyLatestLog(string destinationPath, long limitBytes, out bool trimmed)
    {
        trimmed = false;

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

        if (latestLog.Length <= limitBytes)
        {
            File.Copy(latestLog.FullName, destinationPath, overwrite: true);
            return true;
        }

        // Read-shared: Serilog still has the file open for writing.
        using var source = new FileStream(latestLog.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using FileStream target = File.Create(destinationPath);

        var head = new byte[LogCopyHeadBytes];
        int headRead = ReadFully(source, head);
        int headEnd = LastLineEnd(head, headRead);
        target.Write(head, 0, headEnd);

        long tailLength = Math.Min(source.Length - headEnd, limitBytes - headEnd);
        var tail = new byte[tailLength];
        source.Position = source.Length - tailLength;
        int tailRead = ReadFully(source, tail);

        // Start the tail on a whole line.
        int tailStart = 0;

        while (tailStart < tailRead && tail[tailStart] != (byte)'\n')
            tailStart++;

        if (tailStart < tailRead)
            tailStart++;

        long omitted = source.Length - headEnd - (tailRead - tailStart);

        string marker =
            Environment.NewLine + Environment.NewLine +
            $"[... {omitted:N0} bytes of this log are left out here so the report fits the upload limit: " +
            $"the file is {latestLog.Length:N0} bytes, and this copy carries its first {headEnd:N0} and last {tailRead - tailStart:N0} ...]" +
            Environment.NewLine + Environment.NewLine;

        byte[] markerBytes = System.Text.Encoding.UTF8.GetBytes(marker);
        target.Write(markerBytes, 0, markerBytes.Length);
        target.Write(tail, tailStart, tailRead - tailStart);

        trimmed = true;
        return true;
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        int total = 0;

        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);

            if (read <= 0)
                break;

            total += read;
        }

        return total;
    }

    /// <summary>The index just past the last newline within the first
    /// <paramref name="length"/> bytes, or <paramref name="length"/> when
    /// there is none - so a cut lands after a whole line.</summary>
    private static int LastLineEnd(byte[] buffer, int length)
    {
        for (int i = length - 1; i >= 0; i--)
        {
            if (buffer[i] == (byte)'\n')
                return i + 1;
        }

        return length;
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

    // §386. How big the themed panels are right now, for the Appearance
    // editor to say beside its Picture… buttons - so a user making a
    // picture knows what size covers the thing it is for. Read off the
    // live controls (Bounds is layout, not a guess), in device pixels via
    // RenderScaling, since a picture is pixels. A hidden stats panel
    // measures 0x0, which the editor reports as not showing.
    public AppearancePanelSizes MeasureAppearancePanels()
    {
        Size Of(string name) => this.FindControl<Control>(name)?.Bounds.Size ?? default;

        double targetBox = DataContext is MainWindowViewModel vm ? vm.TargetSpriteSize : 0;

        return new AppearancePanelSizes(
            ClientSize,
            Of("StatsPanelHost"),
            Of("EncounterTableHost"),
            Of("CurrentEncounterBox"),
            targetBox,
            Of("SpriteRowHost"),
            RenderScaling);
    }


    // §392. The encounter table's column grips - see the header's comments
    // in MainWindow.axaml for why these are not GridSplitters. Each grip
    // resizes the column named by its Tag and nothing else: the pointer is
    // captured on press, and every move sets the column to its width at the
    // press plus the pointer's travel since, measured in the header grid's
    // own coordinates (so it is exact however many moves arrive between
    // layouts), within the column's MinWidth and MaxWidth and the room the
    // trailing filler had at the press - a column can grow only into empty
    // space, never past the table's edge. Making room means narrowing
    // another column first, which is its own grip.
    private int gripColumn = -1;
    private double gripStartX;
    private double gripStartWidth;
    private double gripRoom;

    private Grid? EncounterHeader => this.FindControl<Grid>("EncounterHeaderGrid");

    private void ColumnGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control grip || grip.Tag is not string tag || !int.TryParse(tag, out int column))
            return;

        if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
            return;

        Grid? header = EncounterHeader;

        if (header is null || column < 0 || column >= header.ColumnDefinitions.Count - 1)
            return;

        ColumnDefinition definition = header.ColumnDefinitions[column];
        ColumnDefinition filler = header.ColumnDefinitions[header.ColumnDefinitions.Count - 1];

        gripColumn = column;
        gripStartX = e.GetPosition(header).X;
        gripStartWidth = definition.ActualWidth;
        gripRoom = Math.Max(0, filler.ActualWidth);

        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void ColumnGrip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (gripColumn < 0 || sender is not Control grip || !ReferenceEquals(e.Pointer.Captured, grip))
            return;

        Grid? header = EncounterHeader;

        if (header is null || gripColumn >= header.ColumnDefinitions.Count)
            return;

        ColumnDefinition definition = header.ColumnDefinitions[gripColumn];

        double travel = e.GetPosition(header).X - gripStartX;
        double ceiling = Math.Min(definition.MaxWidth, gripStartWidth + gripRoom);
        double floor = Math.Min(definition.MinWidth, ceiling);
        double width = Math.Clamp(gripStartWidth + travel, floor, ceiling);

        definition.Width = new GridLength(width, GridUnitType.Pixel);
        e.Handled = true;
    }

    private void ColumnGrip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (gripColumn < 0)
            return;

        gripColumn = -1;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void ColumnGrip_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        gripColumn = -1;
    }
}