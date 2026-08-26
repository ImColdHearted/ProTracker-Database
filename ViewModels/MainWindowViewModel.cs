using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Foot_Tracker.Tracking;
using Foot_Tracker.Tracking.Capture;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Ported from ProTrackerandDatabase.cs (the WinForms main form). The hunt-session
/// domain logic (HuntSession, LifetimeStatsService, SessionPersistenceService,
/// EncounterTracker) is untouched - only the "glue" that used to live directly in
/// the form's code-behind (InvokeRequired/BeginInvoke, direct control.Text = ...,
/// MessageBox.Show, and hand-built TableLayoutPanel rows) has been rewritten as
/// bindable properties/commands for MainWindow.axaml.
///
/// NOT yet ported from the original form (tracked in MIGRATION_GUIDE.md):
///   - Admin-elevation warning dialog (ShowAdministratorWarning)
///   - Multi-client picker dialog (ClientSelector) - currently auto-picks client 1
///   - The 20+ menu items that open the other WinForms child forms
///     (Interactive Maps, Boss Cooldowns, Counterparts, Lifetime Stats, etc.)
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly HuntSession huntSession = new();
    private readonly DispatcherTimer huntTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly EncounterTracker encounterTracker = new();
    private readonly BossCooldownTracker bossCooldownTracker = new();
    private readonly PvpTracker pvpTracker = new();
    private readonly IWindowCaptureService captureService = WindowCaptureServiceFactory.Instance;

    // Passively looks for a PRO client every few seconds so boss cooldown tracking
    // (and everything downstream of AssignTrackerClient) starts genuinely
    // automatically - without this, "automatic" boss detection only ever ran if the
    // user happened to press Play or use the Assign Client menu item first, since
    // those were the only things that ever called AssignTrackerClient. Stops once a
    // client is actually found/assigned.
    private readonly DispatcherTimer autoClientDetectionTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private LifetimeStats lifetimeStats = LifetimeStatsService.Load();
    private int lifetimeSaveTickCounter;
    private int selectedClientNumber;

    // Layout/display preferences (which side the stats panel docks to, which
    // stats are hidden) - separate from AppearanceSettings. Per-client (see
    // UiPreferencesService's remarks), so reassigned (not readonly) - see
    // AssignTrackerClient, which reloads and reapplies this every time the
    // active client changes, not just once at startup here.
    private UiPreferences uiPreferences = UiPreferencesService.Load();

    public ObservableCollection<EncounterCountRow> SessionEncounters { get; } = new();
    public ObservableCollection<EncounterCountRow> SessionEncountersLeft { get; } = new();
    public ObservableCollection<EncounterCountRow> SessionEncountersRight { get; } = new();

    // Matches the original's TableLayoutPanel behavior: fill the left column
    // first, then continue filling the right column once the left one is full.
    // Bumped from 15 to 25 per column (50 total) - also fixes a real bug where
    // only the left column was ever actually capped; the right column's `else`
    // branch below had no capacity check at all and grew unbounded past 15.
    private const int EncounterColumnCapacity = 25;

    public IReadOnlyList<string> AvailablePokemon =>
        PokemonSpriteService.AllPokemon
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();

    [ObservableProperty] private string windowTitle = "Pro Tracker & Database";
    [ObservableProperty] private string? targetPokemonInput;

    [ObservableProperty] private string totalEncounters = "0";
    [ObservableProperty] private string targetedEncountersFound = "0";
    [ObservableProperty] private string timeHunting = "00:00:00";
    [ObservableProperty] private string sinceShiny = "0";
    [ObservableProperty] private string sinceForm = "0";
    [ObservableProperty] private string catchRate = "0.00%";
    [ObservableProperty] private string successfulCatches = "0";
    [ObservableProperty] private string failedCatches = "0";

    // Replaces the old single CurrentlyHuntingSprite/CurrentlyHuntedLabel pair -
    // up to 4 simultaneous targets now, shown side by side. TargetSpriteSize
    // shrinks once there are more than 2, per the "keep the same size for 2, get
    // smaller for 3-4" request - bound directly by each Image in MainWindow.axaml
    // rather than needing a converter. TargetsPanelMaxWidth forces exactly 2 per
    // row (a genuine 2x2 grid) specifically at 4 targets, rather than however a
    // single wide row happens to wrap.
    public ObservableCollection<TargetDisplayItem> CurrentTargets { get; } = new();
    [ObservableProperty] private double targetSpriteSize = 90;
    // Wider than the sprite itself - a bare TargetSpriteSize-width label wraps
    // longer names ("Charmeleon") onto two lines even with plenty of vertical
    // room, since the sprite box (e.g. 50px at 4 targets) is narrower than most
    // Pokemon names need to render on one line.
    [ObservableProperty] private double targetLabelMaxWidth = 90;
    [ObservableProperty] private double targetsPanelMaxWidth = 9999;

    [ObservableProperty] private Bitmap? currentEncounterSprite;
    [ObservableProperty] private Bitmap? previousEncounterSprite;

    // Type lists for the two sprites above (TypeIconConverter turns each name
    // into an icon in XAML) - kept as separate observable properties (rather
    // than computed off a name string) because, unlike TargetDisplayItem/
    // EncounterCountRow/etc., there's no small per-row record here to hang a
    // computed property off; CurrentEncounteredLabel below can carry a "None"
    // placeholder that shouldn't be looked up, so these are set explicitly
    // from the raw encounter name alongside the sprite instead. See
    // UpdateEncounterDisplays.
    [ObservableProperty] private IReadOnlyList<string> currentEncounterTypes = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<string> previousEncounterTypes = Array.Empty<string>();

    // For CompactWindow specifically - its fixed, tiny layout only has room for
    // one sprite, not all 4 possible targets. Shows the first target with a
    // "+N" suffix if there are more, so at least the count is visible.
    [ObservableProperty] private Bitmap? primaryTargetSprite;
    [ObservableProperty] private string primaryTargetLabel = "None";
    // Not currently shown in CompactWindow.axaml (no room in its fixed 160px
    // height) - kept here in case that changes; MainWindow's own "Currently
    // Hunting" row already gets its icons via TargetDisplayItem.Types.
    [ObservableProperty] private IReadOnlyList<string> primaryTargetTypes = Array.Empty<string>();

    [ObservableProperty] private string currentEncounteredLabel = "None";
    [ObservableProperty] private string previouslyEncounteredLabel = "None";

    // Route name / time-of-day corner OCR - see RouteDetector.cs and
    // EncounterTracker.CornerInfoDetected. Only updates while tracking is
    // running (Start pressed) since the underlying scan piggybacks on
    // EncounterTracker's own loop rather than running independently - see
    // OnCornerInfoDetected below. Default to "Unknown" so the stats panel
    // shows a labeled placeholder instead of a blank line before the first
    // successful read.
    [ObservableProperty] private string currentRouteText = "Unknown";
    [ObservableProperty] private string timeOfDayText = "Unknown";

    // "Pause Since Form" - see HuntSession.SinceFormPaused for why this
    // deliberately isn't reset by Reset().
    [ObservableProperty] private bool sinceFormPaused;
    [ObservableProperty] private string sinceFormPauseButtonText = "Pause Since Form";

    // Event selector - currently just a persisted preference (see
    // EventSettingsService.cs to add/edit the list of events). Swapping in a
    // counterpart-form sprite when one is found is a documented follow-up, not
    // implemented yet - no shiny/form sprites exist for that yet.
    public IReadOnlyList<string> AvailableEvents => EventSettingsService.CurrentEventOptions;
    [ObservableProperty] private string selectedEvent = "None";

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string? statusMessage;

    // Stats panel side swap - see MainWindow.axaml's toolbar/content DockPanels
    // and UiPreferences.StatsPanelOnRight. Requested by multi-client hunters who
    // run several instances of this app side by side and want each instance's
    // stats column to sit toward the middle of the screen rather than always on
    // the right.
    [ObservableProperty] private bool statsPanelOnRight = true;
    [ObservableProperty] private string swapStatsButtonText = "⇄ Move Stats Left";

    /// <summary>Which side the stats panel + its toolbar button dock to - bound
    /// via DockPanel.Dock in MainWindow.axaml.</summary>
    public Dock StatsPanelDock => StatsPanelOnRight ? Dock.Right : Dock.Left;

    partial void OnStatsPanelOnRightChanged(bool value)
    {
        OnPropertyChanged(nameof(StatsPanelDock));
        SwapStatsButtonText = value ? "⇄ Move Stats Left" : "⇄ Move Stats Right";
    }

    // Exclude Stats (Stats menu) - each flag controls whether the corresponding
    // stat block is shown in MainWindow.axaml's stats panel. The underlying
    // counters in huntSession keep updating regardless of these flags - see
    // ApplyExcludedStats/RefreshExcludedStats below and ExcludeStatsViewModel.
    [ObservableProperty] private bool showTimeHunting = true;
    [ObservableProperty] private bool showTotalEncounters = true;
    [ObservableProperty] private bool showTargetedEncountersFound = true;
    [ObservableProperty] private bool showSinceShiny = true;
    [ObservableProperty] private bool showSinceForm = true;
    [ObservableProperty] private bool showSuccessfulCatches = true;
    [ObservableProperty] private bool showPokemonBrokenFree = true;
    [ObservableProperty] private bool showCatchRate = true;

    /// <summary>
    /// Set by MainWindow.axaml.cs to show ClientSelectorWindow (a ViewModel shouldn't
    /// own a Window reference). Returns the chosen client number, or 0 if cancelled.
    /// Replaces: using ClientSelector form = new(); form.ShowDialog(this);
    /// </summary>
    public Func<Task<int>>? RequestClientSelection { get; set; }

    /// <summary>
    /// Whichever window is currently visible - MainWindow normally, or CompactWindow
    /// while Compact Mode is active (MainWindow gets Hide()'d, not closed, when
    /// switching to Compact). Used as the owner for every dialog shown via the
    /// Request*/ConfirmAsync hooks below - a dialog can't be owned by a hidden
    /// window (Avalonia throws), which is exactly what happened before this existed:
    /// Compact Mode's magnifying-glass/Reset buttons crashed because they went
    /// through hooks hardcoded to use the (by-then hidden) MainWindow as owner.
    /// Kept updated by MainWindow.axaml.cs and CompactWindow.axaml.cs as the user
    /// switches between the two.
    /// </summary>
    public Window? ActiveWindow { get; set; }

    /// <summary>
    /// Set by MainWindow.axaml.cs to show PokemonSelectorWindow. Returns the chosen
    /// Pokémon name, or null if cancelled. Replaces: using var selector = new PokemonSelectorForm();
    /// </summary>
    public Func<Task<List<string>?>>? RequestPokemonSelection { get; set; }

    /// <summary>Set by MainWindow.axaml.cs - replaces MessageBox.Show(..., MessageBoxButtons.YesNo).</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Set by MainWindow.axaml.cs to show a save-file dialog. Takes a suggested
    /// file name and the extension ("csv"/"json"), returns the chosen path or
    /// null if cancelled. Replaces WinForms' SaveFileDialog.
    /// </summary>
    public Func<string, string, Task<string?>>? RequestSaveFilePath { get; set; }

    /// <summary>
    /// Set by MainWindow.axaml.cs to show an open-file dialog for the given
    /// extension ("csv"/"json"), returning the chosen path or null if cancelled.
    /// Replaces WinForms' OpenFileDialog.
    /// </summary>
    public Func<string, Task<string?>>? RequestOpenFilePath { get; set; }

    /// <summary>
    /// Set by MainWindow.axaml.cs to show ImportModeDialogWindow before an
    /// Import Hunt Data (CSV/JSON) finishes - lets the user choose whether the
    /// imported file's numbers should be added to the current hunt or replace
    /// it outright. Returns "Add", "Replace", or null if cancelled. See
    /// ImportHuntData/HuntSession.MergeFrom.
    /// </summary>
    public Func<string, Task<string?>>? RequestImportMode { get; set; }

    /// <summary>
    /// Set by MainWindow.axaml.cs to show SwapPokemonWindow for a single
    /// "Currently Hunting" slot - see TargetSprite_PointerPressed/SwapTargetAsync.
    /// Takes the name currently in that slot, returns the chosen replacement's
    /// name, or null if cancelled.
    /// </summary>
    public Func<string, Task<string?>>? RequestSwapTargetSelection { get; set; }

    public MainWindowViewModel()
    {
        huntTimer.Tick += HuntTimer_Tick;

        // Loaded here (not via a partial-property-changed-triggered save) so
        // setting the initial value from the persisted file doesn't immediately
        // re-save it right back - see OnSelectedEventChanged below.
        selectedEvent = EventSettingsService.Load().CurrentEvent;

        // Set via the property (not the backing field) so OnStatsPanelOnRightChanged
        // computes the initial SwapStatsButtonText/StatsPanelDock consistently with
        // whatever was persisted - there's no matching save-on-load concern here
        // since that partial method only updates local display text, it never writes
        // back to disk (ToggleStatsPanelSideCommand is the only thing that saves).
        StatsPanelOnRight = uiPreferences.StatsPanelOnRight;
        ApplyExcludedStats(uiPreferences.ExcludedStats);

        encounterTracker.EncounterDetected += name =>
            Dispatcher.UIThread.Post(() => RegisterEncounter(name));

        encounterTracker.StatusChanged += status =>
            Dispatcher.UIThread.Post(() => StatusMessage = status);

        encounterTracker.CatchResultDetected += result =>
            Dispatcher.UIThread.Post(() => OnCatchResultDetected(result));

        encounterTracker.RareEncounterDetected += (name, rareType) =>
            Dispatcher.UIThread.Post(() => OnRareEncounterDetected(rareType));

        encounterTracker.CornerInfoDetected += (routeName, timeOfDay) =>
            Dispatcher.UIThread.Post(() => OnCornerInfoDetected(routeName, timeOfDay));

        // Boss cooldown tracking is deliberately independent of hunting - it
        // starts as soon as a PRO client is assigned (see AssignTrackerClient),
        // not tied to Play/a hunting target. Its own StatusChanged messages share
        // the same toolbar StatusMessage the hunting tracker uses.
        bossCooldownTracker.StatusChanged += status =>
            Dispatcher.UIThread.Post(() => StatusMessage = status);

        // Both trackers watch the same battle window independently, so without
        // this, EncounterTracker has no idea a boss fight (rather than a wild
        // encounter) is in progress and tries to OCR the boss's active Pokemon as
        // if it were a wild target - confirmed via a real tester's log.
        bossCooldownTracker.BossBattleActiveChanged += active =>
        {
            // SetBossBattleActive just flips a volatile bool on a background
            // tracker - no UI thread needed for that part.
            encounterTracker.SetBossBattleActive(active);

            // Freeze/resume the Time Hunting clock for the duration of the boss
            // fight - it isn't a wild encounter, so it shouldn't count toward
            // hunting time. PauseTimeAccrual/ResumeTimeAccrual are no-ops if the
            // user hasn't pressed Play (nothing to pause/resume), so this stays
            // free while hunting isn't active. Posted to the UI thread because
            // UpdateTrackerDisplay writes UI-bound properties (TimeHunting etc.),
            // same as every other cross-thread tracker callback in this file.
            Dispatcher.UIThread.Post(() =>
            {
                if (active)
                {
                    huntSession.PauseTimeAccrual();
                }
                else
                {
                    huntSession.ResumeTimeAccrual();
                }

                UpdateTrackerDisplay();
            });
        };

        // PVP tracking is likewise independent of hunting - it starts alongside
        // BossCooldownTracker as soon as a PRO client is assigned (see
        // AssignTrackerClient). Same StatusChanged/PvpBattleActiveChanged wiring
        // as bossCooldownTracker above, for the same two reasons: EncounterTracker
        // needs to stand down during a PVP battle (it shows real Pokemon sprites
        // too, so without this it would OCR the opponent's active Pokemon as a
        // wild encounter), and Time Hunting shouldn't tick through a PVP battle
        // any more than it should through a boss battle.
        pvpTracker.StatusChanged += status =>
            Dispatcher.UIThread.Post(() => StatusMessage = status);

        pvpTracker.PvpBattleActiveChanged += active =>
        {
            encounterTracker.SetPvpBattleActive(active);

            Dispatcher.UIThread.Post(() =>
            {
                if (active)
                {
                    huntSession.PauseTimeAccrual();
                }
                else
                {
                    huntSession.ResumeTimeAccrual();
                }

                UpdateTrackerDisplay();
            });
        };

        LoadPreviousSession();
        UpdateTrackerDisplay();

        autoClientDetectionTimer.Tick += (_, _) => TryAutoAssignClient();
        autoClientDetectionTimer.Start();

        // Also try once immediately, in case PRO is already running when the app
        // starts - no need to wait for the first timer tick.
        TryAutoAssignClient();
    }

    /// <summary>
    /// Passive background client detection - see autoClientDetectionTimer's
    /// declaration comment. Unlike Play()/the "Assign Client" menu item, this never
    /// shows the multi-client picker dialog (that requires View-wired hooks that may
    /// not be ready yet this early, and popping up a dialog unprompted at startup
    /// would be a poor first impression anyway) - if more than one PRO client is
    /// running, this silently picks the first one. Use "Assign Client" manually to
    /// choose a specific one instead.
    /// </summary>
    private void TryAutoAssignClient()
    {
        if (selectedClientNumber > 0)
        {
            autoClientDetectionTimer.Stop();
            return;
        }

        var clients = captureService.FindClientWindows("PROClient");

        if (clients.Count == 0)
            return; // PRO isn't running yet - the timer will try again.

        captureService.SelectWindow(clients[0].Handle);

        if (!AssignTrackerClient(1))
        {
            // Client 1 is already claimed by another running Pro Tracker
            // window (see AssignTrackerClient/SessionPersistenceService's
            // lock remarks) - this is exactly the "two trackers, one PRO
            // client" scenario that used to silently corrupt whichever
            // session saved last. Keep the timer running instead of stopping
            // it: as soon as that other window closes and releases the lock,
            // the next tick picks this client up automatically with no user
            // action needed.
            return;
        }

        autoClientDetectionTimer.Stop();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Play()
    {
        if (huntSession.TargetPokemons.Count == 0)
        {
            StatusMessage = "Select at least one Pokémon before starting the tracker.";
            return;
        }

        var clients = captureService.FindClientWindows("PROClient");

        if (clients.Count == 0)
        {
            StatusMessage = !captureService.IsAvailable
                ? captureService.LastError
                : "No Pokémon Revolution Online client was found.";
            return;
        }

        // AssignTrackerClient may reload a previously-saved session for this
        // client (e.g. the very first time it's assigned, or after switching
        // clients). Remember whatever the user just picked so it isn't lost
        // if nothing had been persisted for this client yet.
        List<string> pendingTargets = new(huntSession.TargetPokemons);

        if (clients.Count == 1)
        {
            captureService.SelectWindow(clients[0].Handle);

            if (!AssignTrackerClient(1))
                return;
        }
        else if (selectedClientNumber == 0)
        {
            int chosen = RequestClientSelection is not null
                ? await RequestClientSelection()
                : 0;

            if (chosen == 0)
                return;

            if (!AssignTrackerClient(chosen))
                return;
        }

        if (huntSession.TargetPokemons.Count == 0 && pendingTargets.Count > 0)
        {
            huntSession.TargetPokemons = pendingTargets;
            SessionPersistenceService.Save(huntSession);
        }

        huntSession.Start();
        huntTimer.Start();

        // Runs unconditionally on every platform now - the OCR pipeline was ported
        // to SkiaSharp + TesseractOCR (with native Linux/macOS binaries) a while
        // back specifically so this wasn't Windows-only anymore. This used to be
        // gated behind OperatingSystem.IsWindows() with a "not available on this
        // platform yet" status message, left over from before that port was done -
        // BossCooldownTracker already runs the same underlying detection
        // unconditionally on every platform, so this should too.
        encounterTracker.Start();

        // Covers the edge case where Play is pressed while a boss fight is
        // already in progress (e.g. the user started hunting mid-battle) -
        // BossBattleActiveChanged only fires on a start/end transition, so a
        // freshly-started EncounterTracker/HuntSession would otherwise never
        // learn that one already happened, and the Time Hunting clock would
        // start ticking straight through the rest of that boss fight.
        if (bossCooldownTracker.IsBossBattleActive)
        {
            encounterTracker.SetBossBattleActive(true);
            huntSession.PauseTimeAccrual();
        }

        // Same edge case, for PVP - Play pressed while a PVP battle is already in
        // progress.
        if (pvpTracker.IsPvpBattleActive)
        {
            encounterTracker.SetPvpBattleActive(true);
            huntSession.PauseTimeAccrual();
        }

        IsRunning = true;
        UpdateTrackerDisplay();
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => !huntSession.IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        if (!huntSession.IsRunning)
            return;

        huntSession.Pause();
        huntTimer.Stop();

        await encounterTracker.StopAsync();

        SessionPersistenceService.Save(huntSession);
        FlushPendingLifetimeTime();

        IsRunning = false;
        UpdateTrackerDisplay();
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private bool CanStop() => huntSession.IsRunning;

    [RelayCommand]
    private async Task Reset()
    {
        bool confirmed = ConfirmAsync is not null
            ? await ConfirmAsync("Reset the current hunt?")
            : true;

        if (!confirmed)
            return;

        huntTimer.Stop();
        await encounterTracker.StopAsync();

        FlushPendingLifetimeTime();
        huntSession.Reset();
        SessionPersistenceService.Delete();

        IsRunning = false;
        UpdateTrackerDisplay();
        UpdateSessionEncounters();
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task SelectTarget()
    {
        // Prefer the sprite-picker dialog (ported PokemonSelectorForm, now
        // multi-select up to 4) if the View wired one up; otherwise fall back to
        // whatever was typed in the text box as a single target.
        List<string>? chosen = RequestPokemonSelection is not null
            ? await RequestPokemonSelection()
            : (string.IsNullOrWhiteSpace(TargetPokemonInput)
                ? null
                : new List<string> { TargetPokemonInput.Trim() });

        if (chosen is null || chosen.Count == 0)
            return;

        huntSession.TargetPokemons = chosen.Take(4).ToList();
        TargetPokemonInput = string.Join(", ", huntSession.TargetPokemons);
        UpdateTrackerDisplay();
        SessionPersistenceService.Save(huntSession);
    }

    /// <summary>Current targets, exposed read-only for the View to pre-fill the
    /// multi-select dialog when re-opening "Set Target" to edit an existing list.</summary>
    public IReadOnlyList<string> CurrentTargetNames => huntSession.TargetPokemons;

    /// <summary>
    /// Undocumented quality-of-life feature: clicking directly on one of the
    /// "Currently Hunting" sprites (see MainWindow.axaml.cs's
    /// TargetSprite_PointerPressed) lets the user swap just that one target for
    /// a different Pokémon, instead of reopening "Set Target" and re-picking
    /// every one of the 2-4 targets from scratch. Deliberately has no visual
    /// hint anywhere in the UI - it's meant to be a "didn't know we could do
    /// that" discovery, not an advertised feature, so it's not wired to any
    /// command/tooltip in MainWindow.axaml.
    /// </summary>
    public async Task SwapTargetAsync(string currentName)
    {
        if (RequestSwapTargetSelection is null)
            return;

        string? newName = await RequestSwapTargetSelection(currentName);

        if (string.IsNullOrWhiteSpace(newName) ||
            string.Equals(newName, currentName, StringComparison.OrdinalIgnoreCase))
            return;

        if (huntSession.TargetPokemons.Any(p => string.Equals(p, newName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"{newName} is already one of your hunting targets.";
            return;
        }

        bool confirmed = ConfirmAsync is not null
            ? await ConfirmAsync($"Replace {currentName} with {newName} in your hunting targets?")
            : true;

        if (!confirmed)
            return;

        int index = huntSession.TargetPokemons.FindIndex(p => string.Equals(p, currentName, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            return;

        huntSession.TargetPokemons[index] = newName;
        TargetPokemonInput = string.Join(", ", huntSession.TargetPokemons);
        UpdateTrackerDisplay();
        SessionPersistenceService.Save(huntSession);
        StatusMessage = $"Swapped {currentName} for {newName}.";
    }

    [RelayCommand]
    private void ToggleSinceFormPaused()
    {
        huntSession.SinceFormPaused = !huntSession.SinceFormPaused;
        UpdateTrackerDisplay();
        SessionPersistenceService.Save(huntSession);
    }

    partial void OnSelectedEventChanged(string value)
    {
        EventSettingsService.Save(new EventSettings { CurrentEvent = value });
    }

    // ============================================================
    // STATS PANEL LAYOUT / VISIBILITY - see UiPreferences.cs.
    // ============================================================

    [RelayCommand]
    private void ToggleStatsPanelSide()
    {
        StatsPanelOnRight = !StatsPanelOnRight;

        uiPreferences.StatsPanelOnRight = StatsPanelOnRight;
        UiPreferencesService.Save(uiPreferences);
    }

    private void ApplyExcludedStats(IReadOnlyCollection<string> excludedStatKeys)
    {
        ShowTimeHunting = !excludedStatKeys.Contains("TimeHunting");
        ShowTotalEncounters = !excludedStatKeys.Contains("TotalEncounters");
        ShowTargetedEncountersFound = !excludedStatKeys.Contains("TargetedEncountersFound");
        ShowSinceShiny = !excludedStatKeys.Contains("SinceShiny");
        ShowSinceForm = !excludedStatKeys.Contains("SinceForm");
        ShowSuccessfulCatches = !excludedStatKeys.Contains("SuccessfulCatches");
        ShowPokemonBrokenFree = !excludedStatKeys.Contains("PokemonBrokenFree");
        ShowCatchRate = !excludedStatKeys.Contains("CatchRate");
    }

    /// <summary>Called by MainWindow.axaml.cs after the Exclude Stats dialog
    /// (Stats menu) saves - re-reads the persisted preference so the main
    /// form's visible stats update immediately, without needing an app
    /// restart. The hunt itself is untouched; only display flags change.</summary>
    public void RefreshExcludedStats()
    {
        UiPreferences refreshed = UiPreferencesService.Load();
        uiPreferences.ExcludedStats = refreshed.ExcludedStats;
        ApplyExcludedStats(uiPreferences.ExcludedStats);
    }

    // ============================================================
    // IMPORT / EXPORT - ported from saveDataToolStripMenuItem1/2_Click and
    // saveJSONDataToolStripMenuItem/importJSONToolStripMenuItem_Click.
    // ============================================================

    [RelayCommand]
    private async Task ExportCsv()
    {
        string suggestedName = $"ProTracker-Hunt-{DateTime.Now:yyyy-MM-dd-HHmmss}.csv";

        string? path = RequestSaveFilePath is not null
            ? await RequestSaveFilePath(suggestedName, "csv")
            : null;

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            HuntDataExportService.ExportCsv(huntSession, path);
            StatusMessage = "Hunt data exported successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"The hunt data could not be exported: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task ImportCsv() =>
        ImportHuntData(HuntDataExportService.ImportCsv, "csv");

    [RelayCommand]
    private async Task ExportJson()
    {
        string suggestedName = $"ProTracker-Hunt-{DateTime.Now:yyyy-MM-dd-HHmmss}.json";

        string? path = RequestSaveFilePath is not null
            ? await RequestSaveFilePath(suggestedName, "json")
            : null;

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            HuntDataExportService.ExportJson(huntSession, path);
            StatusMessage = "Hunt data exported successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"The hunt data could not be exported: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task ImportJson() =>
        ImportHuntData(HuntDataExportService.ImportJson, "json");

    private async Task ImportHuntData(Func<string, HuntSessionSaveData> importer, string extension)
    {
        string? path = RequestOpenFilePath is not null
            ? await RequestOpenFilePath(extension)
            : null;

        if (string.IsNullOrWhiteSpace(path))
            return;

        // Add/Replace/Cancel - see ImportModeDialogWindow/HuntSession.MergeFrom.
        // Falls back to the original always-replace behavior (via ConfirmAsync)
        // if the View never wired RequestImportMode up, same "degrade gracefully
        // instead of throwing" pattern every other Request*/ConfirmAsync hook
        // in this class already follows.
        string? mode;

        if (RequestImportMode is not null)
        {
            mode = await RequestImportMode(
                "Add this file's encounters and totals to the current hunt, " +
                "or replace the current hunt with it entirely?");

            if (mode is null)
                return;
        }
        else
        {
            bool confirmed = ConfirmAsync is not null
                ? await ConfirmAsync("Importing this file will replace the current hunt statistics.\n\nContinue?")
                : true;

            if (!confirmed)
                return;

            mode = "Replace";
        }

        try
        {
            HuntSessionSaveData data = importer(path);
            bool isAdd = mode.Equals("Add", StringComparison.OrdinalIgnoreCase);

            huntTimer.Stop();

            if (isAdd)
                huntSession.MergeFrom(data);
            else
                huntSession.Restore(data);

            IsRunning = false;
            UpdateTrackerDisplay();
            UpdateSessionEncounters();
            SessionPersistenceService.Save(huntSession);

            PlayCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();

            StatusMessage = isAdd
                ? "Hunt data added to the current session."
                : "Hunt data imported successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"The hunt data could not be imported: {ex.Message}";
        }
    }

    // Ported from ProTrackerandDatabase.cs's assignClientToolStripMenuItem_Click -
    // manually (re)opens the client picker at any time, not just automatically
    // on the first Play. Matches the original: does NOT stop an in-progress
    // encounterTracker poll when switching clients mid-hunt (huntSession itself
    // gets paused, so any stray detections are ignored - see AssignTrackerClient).
    [RelayCommand]
    private async Task AssignClient()
    {
        int chosen = RequestClientSelection is not null
            ? await RequestClientSelection()
            : 0;

        if (chosen == 0)
            return;

        AssignTrackerClient(chosen);
    }

    /// <summary>
    /// Binds this tracker instance to a specific PRO client, reloading every
    /// per-client data set (session, boss cooldowns, PVP log, appearance, UI
    /// prefs) for that client. Returns false without changing anything if
    /// another still-running Pro Tracker window already owns this client
    /// number - see SessionPersistenceService's client-lock remarks for the
    /// real bug this prevents: two tracker windows pointed at the same single
    /// PRO client, where closing the unused one silently overwrote the active
    /// one's data because both defaulted to "client 1" with nothing stopping
    /// them from sharing that file.
    /// </summary>
    private bool AssignTrackerClient(int clientNumber)
    {
        if (clientNumber < 1)
            return false;

        // Already tracking this client - nothing to reload. Without this guard,
        // every Play click re-ran LoadPreviousSession() below and clobbered
        // whatever the user had just picked with the last-saved file.
        if (selectedClientNumber == clientNumber)
            return true;

        // Check BEFORE touching anything about the client this instance is
        // currently on (if any) - a failed reassignment (e.g. via the
        // "Assign Client" menu while already hunting on a different client)
        // should leave that hunt running untouched, not pause it and then
        // have nowhere to switch to.
        if (!SessionPersistenceService.IsClientLockAvailable(clientNumber, out int? heldByProcessId))
        {
            StatusMessage = ClientLockedMessage(clientNumber, heldByProcessId);
            return false;
        }

        if (selectedClientNumber > 0)
        {
            if (huntSession.IsRunning)
            {
                huntSession.Pause();
                huntTimer.Stop();
            }

            FlushPendingLifetimeTime();
            SessionPersistenceService.Save(huntSession);
        }

        if (!SessionPersistenceService.SetActiveClient(clientNumber))
        {
            // Extremely unlikely (another Pro Tracker window claimed this
            // exact client in the instant between the check above and this
            // call) - same warning as above. Nothing to undo: the previous
            // client's session was just safely saved, not switched away from.
            int? heldBy = SessionPersistenceService.LastLockConflictProcessId;

            StatusMessage = ClientLockedMessage(clientNumber, heldBy);
            return false;
        }

        selectedClientNumber = clientNumber;

        // Boss cooldowns and the PVP battle log are both per-client (each
        // client this app instance might be assigned to is presumed to be a
        // different PRO account) - reloading here, not just once at app
        // startup, is what makes switching clients actually swap in that
        // client's own cooldown/battle data instead of continuing to
        // show/save over whichever client was active before. See
        // BossCooldownService/PvpOpponentService's GetSavePath remarks.
        BossCooldownService.Load();
        PvpOpponentService.ReloadForActiveClient();

        // Appearance and the stats-panel/exclude-stats preferences are
        // per-client too, for the same "one account for hunting, one for PVP"
        // reasoning - each client's own look and stat display should follow
        // that client, not whichever one loaded first. ThemeManager.Reload()
        // re-reads AppearanceSettingsRepository and re-pushes the resulting
        // brushes/fonts into Application.Resources, so every DynamicResource
        // binding across the app updates immediately. See
        // AppearanceSettingsRepository/UiPreferencesService's GetSettingsPath
        // remarks.
        ThemeManager.Reload();

        uiPreferences = UiPreferencesService.Load();
        StatsPanelOnRight = uiPreferences.StatsPanelOnRight;
        ApplyExcludedStats(uiPreferences.ExcludedStats);

        // Runs automatically as soon as a client is assigned - independent of
        // Play/hunting, so boss cooldowns get tracked even if the user never
        // sets a hunting target at all. No-ops if already running (e.g. this
        // fires again when switching between two clients).
        bossCooldownTracker.Start();

        // PVP tracking is the same story - automatic, independent of Play/hunting.
        pvpTracker.Start();

        LoadPreviousSession();
        UpdateTrackerDisplay();
        UpdateSessionEncounters();

        // Without this, switching clients while a hunt was already running
        // left the Play/Stop buttons stuck: huntSession.IsRunning correctly
        // becomes false above (Pause()/Reset()/Restore() all clear it), but
        // CanStart()/CanStop() read that flag directly on a plain model
        // CommunityToolkit can't auto-track, so they're never re-polled
        // without an explicit NotifyCanExecuteChanged call. Left unfixed,
        // Stop stayed visually enabled but did nothing (it already checks
        // huntSession.IsRunning and no-ops), and Play stayed visually
        // disabled and unclickable - exactly the "start a hunt, switch
        // clients, it softlocks" report this fixes. Reset() happens to call
        // these same two lines, which is why hitting Reset "fixed" it even
        // though nothing else about Reset was actually necessary.
        IsRunning = false;
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();

        return true;
    }

    // Shared by both lock-conflict checks in AssignTrackerClient above (they
    // used to duplicate this exact ternary inline). Uses ClientNamesService
    // so a renamed client shows its actual name here too - the message that
    // most needs to say *which* client is the one you can't switch to.
    private static string ClientLockedMessage(int clientNumber, int? heldByProcessId)
    {
        string name = ClientNamesService.GetDisplayName(clientNumber);

        return heldByProcessId is int pid
            ? $"{name} is already being tracked by another Pro Tracker window (PID {pid}). Close that window first, or pick a different client."
            : $"{name} is already being tracked by another Pro Tracker window. Close that window first, or pick a different client.";
    }

    private void HuntTimer_Tick(object? sender, EventArgs e)
    {
        // IsAccruingTime (not IsRunning) - while paused for a boss battle
        // (PauseTimeAccrual, see the BossBattleActiveChanged handler below),
        // IsRunning stays true but the clock itself is frozen, and lifetime
        // stats should stay in sync with the on-screen Time Hunting stat rather
        // than counting boss-fight time as hunting time.
        if (huntSession.IsAccruingTime)
        {
            lifetimeSaveTickCounter++;

            if (lifetimeSaveTickCounter >= 30)
            {
                lifetimeStats = LifetimeStatsService.AddHuntingTime(
                    TimeSpan.FromSeconds(lifetimeSaveTickCounter));

                lifetimeSaveTickCounter = 0;
            }
        }

        UpdateTrackerDisplay();
    }

    private void LoadPreviousSession()
    {
        HuntSessionSaveData? saved = SessionPersistenceService.Load();

        if (saved is null)
        {
            huntSession.Reset();
        }
        else
        {
            huntSession.Restore(saved);
        }

        UpdateTrackerDisplay();
        UpdateSessionEncounters();
    }

    private void FlushPendingLifetimeTime()
    {
        if (lifetimeSaveTickCounter <= 0)
            return;

        lifetimeStats = LifetimeStatsService.AddHuntingTime(
            TimeSpan.FromSeconds(lifetimeSaveTickCounter));

        lifetimeSaveTickCounter = 0;
    }

    private void RegisterEncounter(string pokemonName)
    {
        if (!huntSession.IsRunning)
            return;

        string resolvedName = PokemonSpriteService.ResolveEncounterName(pokemonName);

        huntSession.PreviousEncounter = huntSession.CurrentEncounter;
        huntSession.CurrentEncounter = resolvedName;

        huntSession.TotalEncounters++;
        huntSession.EncountersSinceShiny++;

        if (!huntSession.SinceFormPaused)
            huntSession.EncountersSinceForm++;

        huntSession.RegisterPokemonEncounter(resolvedName);

        lifetimeStats = LifetimeStatsService.AddEncounter(resolvedName);

        UpdateTrackerDisplay();
        UpdateSessionEncounters();
        SessionPersistenceService.Save(huntSession);
    }

    private void OnCatchResultDetected(CatchResult result)
    {
        if (!huntSession.IsRunning)
            return;

        switch (result)
        {
            case CatchResult.Success:
                huntSession.SuccessfulCatches++;
                lifetimeStats = LifetimeStatsService.AddSuccessfulCatch();
                break;

            case CatchResult.Failed:
                huntSession.FailedCatches++;
                lifetimeStats = LifetimeStatsService.AddFailedCatch();
                break;

            default:
                return;
        }

        UpdateTrackerDisplay();
        SessionPersistenceService.Save(huntSession);
    }

    private void OnRareEncounterDetected(RareEncounterType rareType)
    {
        if (!huntSession.IsRunning)
            return;

        switch (rareType)
        {
            case RareEncounterType.Shiny:
                huntSession.EncountersSinceShiny = 0;
                lifetimeStats = LifetimeStatsService.AddShinyEncounter();
                break;

            case RareEncounterType.Form:
                huntSession.EncountersSinceForm = 0;
                lifetimeStats = LifetimeStatsService.AddFormEncounter();
                break;

            default:
                return;
        }

        UpdateTrackerDisplay();
        SessionPersistenceService.Save(huntSession);
    }

    // Unlike RegisterEncounter/OnCatchResultDetected/OnRareEncounterDetected
    // above, this deliberately does NOT gate on huntSession.IsRunning - it just
    // reflects whatever RouteDetector last read, with no counters to protect
    // from a stale/late event during shutdown. Route name and time of day can
    // each arrive independently (see RouteDetector.TryDetectCorner), so only
    // the one that actually got a fresh reading this tick is updated - a
    // temporarily unreadable route name shouldn't blank out a time-of-day
    // reading that came through fine, or vice versa.
    private void OnCornerInfoDetected(string? routeName, string? timeOfDay)
    {
        if (!string.IsNullOrWhiteSpace(routeName))
            CurrentRouteText = routeName;

        if (!string.IsNullOrWhiteSpace(timeOfDay))
            TimeOfDayText = timeOfDay;
    }

    private void UpdateSessionEncounters()
    {
        // Replaces ResetEncounterTable/UpdateSessionEncounters, which manually
        // built TableLayoutPanel rows. MainWindow.axaml binds directly to these
        // collections instead - split across two columns the same way the
        // original continued into a second SessionEncountersTableRight panel
        // once the left one filled up.
        SessionEncounters.Clear();
        SessionEncountersLeft.Clear();
        SessionEncountersRight.Clear();

        int total = huntSession.EncounterCounts.Values.Sum();

        foreach (var kvp in huntSession.EncounterCounts.OrderByDescending(k => k.Value))
        {
            var row = new EncounterCountRow
            {
                PokemonName = kvp.Key,
                Count = kvp.Value,
                RatePercent = total > 0 ? kvp.Value / (double)total * 100.0 : 0,
                Sprite = PokemonSpriteService.GetSprite(kvp.Key)
            };

            SessionEncounters.Add(row);

            if (SessionEncountersLeft.Count < EncounterColumnCapacity)
                SessionEncountersLeft.Add(row);
            else if (SessionEncountersRight.Count < EncounterColumnCapacity)
                SessionEncountersRight.Add(row);
            // else: both columns are full (50 shown total) - still tracked in
            // SessionEncounters above (used elsewhere, e.g. lifetime stats),
            // just not displayed in either visible column.
        }
    }

    private void UpdateTrackerDisplay()
    {
        // ClientNamesService falls back to "Client N" on its own when no
        // custom name has been set, so this reads exactly as before for
        // anyone who hasn't renamed anything.
        string clientText = selectedClientNumber > 0
            ? $" - {ClientNamesService.GetDisplayName(selectedClientNumber)}"
            : string.Empty;

        string targetsText = string.Join(", ", huntSession.TargetPokemons);

        WindowTitle = huntSession.IsRunning && huntSession.TargetPokemons.Count > 0
            ? $"Pro Tracker & Database - Hunting {targetsText}{clientText}"
            : $"Pro Tracker & Database{clientText}";

        TotalEncounters = huntSession.TotalEncounters.ToString();
        TargetedEncountersFound = huntSession.GetTargetedEncounterCount().ToString();
        TimeHunting = TimeFormatHelper.FormatElapsed(huntSession.GetCurrentElapsedTime());
        SinceShiny = huntSession.EncountersSinceShiny.ToString();
        SinceForm = huntSession.EncountersSinceForm.ToString();

        SinceFormPaused = huntSession.SinceFormPaused;
        SinceFormPauseButtonText = huntSession.SinceFormPaused ? "Resume Since Form" : "Pause Since Form";

        int totalCatchAttempts = huntSession.SuccessfulCatches + huntSession.FailedCatches;
        double rate = totalCatchAttempts > 0
            ? huntSession.SuccessfulCatches / (double)totalCatchAttempts * 100.0
            : 0;

        CatchRate = $"{rate:F2}%";
        SuccessfulCatches = huntSession.SuccessfulCatches.ToString();
        FailedCatches = huntSession.FailedCatches.ToString();

        // Up to 4 targets shown side by side - sprite size shrinks once there are
        // more than 2, so 3-4 targets still fit comfortably in the same area.
        CurrentTargets.Clear();

        if (huntSession.TargetPokemons.Count == 0)
        {
            CurrentTargets.Add(new TargetDisplayItem("None", null));
        }
        else
        {
            foreach (string name in huntSession.TargetPokemons)
            {
                CurrentTargets.Add(new TargetDisplayItem(name, PokemonSpriteService.GetEncounterSprite(name)));
            }
        }

        TargetSpriteSize = huntSession.TargetPokemons.Count <= 2 ? 90 : 50;

        // Labels get more room than the bare sprite width - see
        // TargetLabelMaxWidth's declaration comment for why.
        TargetLabelMaxWidth = TargetSpriteSize + 20;

        // At exactly 4 targets, constrain the wrap panel to fit precisely 2 per
        // row - forces a genuine 2x2 grid instead of however a single wide row
        // happens to wrap based on the window's current width. Budgeted against
        // TargetLabelMaxWidth (the widest element per item, wider than the
        // sprite itself) plus a 10px gap between adjacent items.
        TargetsPanelMaxWidth = huntSession.TargetPokemons.Count == 4
            ? (TargetLabelMaxWidth + 10) * 2
            : 9999;

        if (huntSession.TargetPokemons.Count == 0)
        {
            PrimaryTargetSprite = null;
            PrimaryTargetLabel = "None";
            PrimaryTargetTypes = Array.Empty<string>();
        }
        else
        {
            PrimaryTargetSprite = PokemonSpriteService.GetEncounterSprite(huntSession.TargetPokemons[0]);
            PrimaryTargetLabel = huntSession.TargetPokemons.Count > 1
                ? $"{huntSession.TargetPokemons[0]} +{huntSession.TargetPokemons.Count - 1}"
                : huntSession.TargetPokemons[0];
            PrimaryTargetTypes = PokemonSpriteService.GetTypes(huntSession.TargetPokemons[0]);
        }

        CurrentEncounterSprite = PokemonSpriteService.GetEncounterSprite(huntSession.CurrentEncounter);
        PreviousEncounterSprite = PokemonSpriteService.GetEncounterSprite(huntSession.PreviousEncounter);
        CurrentEncounterTypes = PokemonSpriteService.GetTypes(huntSession.CurrentEncounter);
        PreviousEncounterTypes = PokemonSpriteService.GetTypes(huntSession.PreviousEncounter);

        CurrentEncounteredLabel = string.IsNullOrWhiteSpace(huntSession.CurrentEncounter) ? "None" : huntSession.CurrentEncounter;
        PreviouslyEncounteredLabel = string.IsNullOrWhiteSpace(huntSession.PreviousEncounter) ? "None" : huntSession.PreviousEncounter;
    }

    /// <summary>Call from MainWindow's Closing event - replaces OnFormClosing.</summary>
    public void OnClosing()
    {
        if (huntSession.IsRunning)
            huntSession.Pause();

        huntTimer.Stop();
        FlushPendingLifetimeTime();
        SessionPersistenceService.Save(huntSession);

        // Release this process's claim on its client number (if any) so the
        // next tracker to start doesn't have to wait for the stale-PID check
        // to notice this one is gone - see SessionPersistenceService's
        // client-lock remarks.
        SessionPersistenceService.ReleaseActiveClient();
    }

    public void Dispose()
    {
        huntTimer.Tick -= HuntTimer_Tick;
        autoClientDetectionTimer.Stop();
        encounterTracker.Dispose();
        bossCooldownTracker.Dispose();
        pvpTracker.Dispose();
    }
}