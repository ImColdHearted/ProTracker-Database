using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Foot_Tracker.Tracking;
using Foot_Tracker.Tracking.Capture;
using Serilog;

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

    // §101 Admin Client isolation: the second, purely in-memory session every
    // hunting mutation routes to while IsolatedSession.Reason is Admin - see
    // the Session property below and MIGRATION_GUIDE.md §101/§249. Never persisted,
    // never merged into the real session; leaving admin mode simply routes
    // back to huntSession, which nothing modified in the meantime.
    private readonly HuntSession adminHuntSession = new();

    // §250 World Quest isolation: the third session, routed to while
    // IsolatedSession.Reason is WorldQuest. Unlike the admin one it IS
    // persisted - per client, per quest - by PersistSession below. The quest
    // species is forced as its only target every time it is loaded.
    private readonly HuntSession worldQuestHuntSession = new();
    private readonly DispatcherTimer huntTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // §150: while a hunt runs and an events server is configured, one
    // anonymous heartbeat every five minutes (EventsSyncService.
    // SendPresenceAsync - a random per-run id and nothing else), so the
    // admin's console can count how many trackers are hunting. Started
    // beside huntTimer, stopped beside it; a failed delivery is logged once
    // and never touches the hunt.
    private readonly DispatcherTimer presenceTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private bool presenceFailureLogged;

    // §207: the admin-published list of counterpart events that are actually
    // running, re-read every five minutes. Deliberately its own timer rather
    // than a second job on presenceTimer, despite the identical interval:
    // that one runs only while a hunt is on, and a tracker sitting idle when
    // the admin publishes should already know the list by the time its user
    // presses Play.
    private readonly DispatcherTimer activeEventsTimer = new() { Interval = TimeSpan.FromMinutes(5) };

    // §252: the World Quest menu item changes colour while a quest is
    // running and the mode is off. This timer asks the events server every
    // five minutes which quest that is - a quest lasts a day at most, so
    // five minutes late is soon enough - and the answer is kept, so leaving
    // the mode re-colours the item without another fetch.
    private readonly DispatcherTimer worldQuestPollTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private WorldQuest? runningWorldQuest;

    private readonly EncounterTracker encounterTracker = new();
    private readonly BossCooldownTracker bossCooldownTracker = new();
    private readonly PvpTracker pvpTracker = new();
    private readonly IWindowCaptureService captureService = WindowCaptureServiceFactory.Instance;

    // Passively looks for a PRO client every few seconds so boss cooldown tracking
    // (and everything downstream of AssignTrackerClient) starts genuinely
    // automatically - without this, "automatic" boss detection only ever ran if the
    // user happened to press Play or use the Assign Client menu item first, since
    // those were the only things that ever called AssignTrackerClient.
    //
    // §105: no longer stops once a client is assigned - after binding, the
    // same tick becomes the client-ownership watch (CheckForForcedTakeover),
    // which is how a window notices that another window force-claimed its
    // profile and hands over cleanly instead of both writing the same files.
    // One small file read per tick while bound; nothing else changed.
    private readonly DispatcherTimer autoClientDetectionTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    // §105: set when another window force-claimed this window's profile and
    // this one stepped down. It stops the passive auto-detect below from
    // silently grabbing CLIENT 1 (which is a different profile, and not one
    // the user asked for) just because this window is unbound again -
    // after a handover the window waits for an explicit Assign Client, which
    // is exactly what its status line tells the user to do. Cleared by the
    // next successful bind.
    private bool steppedDownAfterHandover;

    // §140: consecutive autoClientDetectionTimer ticks (five seconds apart)
    // on which no PROClient process was running while a hunt was on. The
    // second one stops the hunt - see StopHuntIfClientGone. One tick is not
    // trusted on its own: a process list can come back empty for a moment
    // while the game restarts or the machine is under load, and a hunt
    // stopped by mistake costs the player a Start click they did not expect
    // and did not see coming.
    private int clientMissingScans;
    private const int ClientMissingScansBeforeStop = 2;

    private LifetimeStats lifetimeStats = LifetimeStatsService.Load();
    private int lifetimeSaveTickCounter;
    private int selectedClientNumber;

    // Set by the EncounterLevelDetected handler below, and - as of
    // MIGRATION_GUIDE.md §99 - consumed by RegisterEncounter again: not for
    // the Hunting Log (that stayed catches-only with its own fresh catch-time
    // reading, see §76 and CatchLevelDetected/pendingCatchLevel below), but
    // as the initial level on the encounter's session-history record. This
    // sat as a deliberately-kept write-only field between §76 and §99; the
    // same fires-before-EncounterDetected ordering guarantee §76 documented
    // is exactly what makes reading it back per-encounter safe now.
    private int? pendingEncounterLevel;

    // Same reasoning as pendingEncounterLevel above, for
    // EncounterTracker.EncounterGenderDetected/GenderDetector.cs.
    private string? pendingEncounterGender;

    // Set by the CatchLevelDetected handler below, consumed (and cleared) by
    // OnCatchResultDetected's CatchResult.Success case - same pending-field
    // pattern and firing-order guarantee as pendingEncounterLevel above, just
    // for a fresh reading taken at catch time - see
    // EncounterTracker.CatchLevelDetected's declaration comment. Added by
    // MIGRATION_GUIDE.md §76.
    private int? pendingCatchLevel;

    // Same pattern as pendingCatchLevel above, for
    // EncounterTracker.CatchGenderDetected/GenderDetector.cs.
    private string? pendingCatchGender;

    // Which RareEncounterType (if any) OnRareEncounterDetected most recently
    // confirmed for the CURRENT encounter - unlike pendingCatchLevel/
    // pendingCatchGender above, this isn't set from a fresh catch-time
    // reading, because there's no way to re-detect a shiny sparkle or a form
    // difference from a single frame the way Level/Gender can be re-read -
    // RareEncounterDetector needs the multi-second confirmation window
    // EncounterTracker.RareCheckWindowMs already gives it, run once per
    // encounter while it's still ongoing. Reset to None every time a new
    // encounter is registered (see RegisterEncounter below), so a catch can
    // never be mislabeled with a previous encounter's Shiny/Form status.
    // Read (and left as-is, not cleared) by OnCatchResultDetected's
    // CatchResult.Success case when writing the Hunting Log entry - see
    // Models.HuntLogEntry.RareType. Added by MIGRATION_GUIDE.md §77.
    private RareEncounterType currentEncounterRareType;

    // §138. The event name CounterpartMatcher identified for the CURRENT
    // encounter's form ("Summer", "Pinkan", ...), or null - not identified,
    // not a form, or not answered yet. Reset with currentEncounterRareType
    // above; read at catch time so the Catch Log says "Summer Form" rather
    // than "Form" when the answer is known.
    private string? currentEncounterFormName;

    // Layout/display preferences (which side the stats panel docks to, which
    // stats are hidden) - separate from AppearanceSettings. Per-client (see
    // UiPreferencesService's remarks), so reassigned (not readonly) - see
    // AssignTrackerClient, which reloads and reapplies this every time the
    // active client changes, not just once at startup here.
    private UiPreferences uiPreferences = UiPreferencesService.Load();

    /// <summary>The hunting-data context every mutation and display read
    /// goes through (§101): the isolated in-memory admin session while Admin
    /// Client mode is active, the real per-client session otherwise. The
    /// persistence calls below deliberately DON'T use this - they always
    /// name huntSession explicitly, so admin activity can never be saved.
    ///
    /// §249: routed on IsolatedSession.Reason rather than a bare "is admin"
    /// check, because this is the one place that has to know WHICH isolated
    /// session is running. A second reason adds a second arm here and
    /// nothing anywhere else.</summary>
    private HuntSession Session => IsolatedSession.Reason switch
    {
        IsolationReason.Admin => adminHuntSession,
        IsolationReason.WorldQuest => worldQuestHuntSession,
        _ => huntSession,
    };

    /// <summary>§249/§250. The one place the ACTIVE session is written back
    /// after a hunting mutation. Ten call sites used to repeat "if not admin,
    /// save huntSession"; §249 folded them into one method so that the edit
    /// a second, persisted isolated session needed could be made once. This
    /// is that edit.
    ///
    /// Each session goes to its own file, and only the session that was
    /// actually mutated is written: the normal one to the client's session
    /// file, the World Quest one to its per-client, per-quest file, the admin
    /// one nowhere - it is in-memory by design (§101). Writing the normal
    /// session while an isolated one is active would be harmless in bytes
    /// but wrong in principle, and this is the line that keeps isolated
    /// activity out of the normal client's file.</summary>
    private void PersistSession()
    {
        switch (IsolatedSession.Reason)
        {
            case IsolationReason.None:
                SessionPersistenceService.Save(huntSession);
                break;

            case IsolationReason.WorldQuest when WorldQuestMode.Current is { } quest:
                SessionPersistenceService.SaveWorldQuest(worldQuestHuntSession, quest.MessageId);
                break;
        }
    }

    // §101 watchdog - see WatchdogTickAsync. One timer, one recovery at a
    // time, bounded per hour, and never a second detector loop: recovery
    // awaits StopAsync (with a timeout) before Start.
    private readonly DispatcherTimer watchdogTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private bool watchdogRecovering;
    private int watchdogRecoveryCount;
    private DateTime watchdogWindowStartUtc = DateTime.UtcNow;

    /// <summary>Every species this hunt, ordered by encounter count. The
    /// full list - what the table SHOWS is one page of it, below.</summary>
    public ObservableCollection<EncounterCountRow> SessionEncounters { get; } = new();

    /// <summary>§125. The page currently on screen.
    ///
    /// This replaces SessionEncountersLeft/SessionEncountersRight, a pair of
    /// collections feeding two DataGrids side by side. That shape came
    /// straight from the WinForms original, where the table filled a left
    /// panel and then continued into a right one, and it carried two
    /// problems with it. Fifty species was a hard ceiling - anything past
    /// the second column was tracked and simply never shown, with nothing
    /// on screen saying so. And the split is not a layout: reading down a
    /// column and then jumping back up to continue is a worse way to read a
    /// ranked list than reading straight down one.
    ///
    /// One column, one page at a time, and a count of how many pages there
    /// are - so a hunt with two hundred species says so instead of quietly
    /// showing the first fifty.</summary>
    public ObservableCollection<EncounterCountRow> SessionEncountersPage { get; } = new();

    /// <summary>Rows per page on the front table.
    ///
    /// §125 set this to 100. §132 brought it to 25, which is what the
    /// original request asked for - "the recent 25 by default showing" - and
    /// 100 remains the cap, still in force on the Encounter History window
    /// (EncounterDatabaseService.PageSize). The two are separate constants
    /// on purpose: this one is about how much of the main window the table
    /// is allowed to occupy, that one is about how much of a database to
    /// pull at once, and they answer to different pressures.
    ///
    /// Nothing is hidden by the smaller page the way the old 50-row ceiling
    /// hid it - there is a page count beside the pager saying how many more
    /// there are.</summary>
    public const int EncounterPageSize = 25;

    // §125. Which columns of the encounter table are shown. Default true,
    // so nothing changes for anyone who never opens the settings; §126
    // wires these to the Exclude Stats window and to UiPreferences.
    //
    // There is deliberately no ShowPokemonColumn. A row with its name hidden
    // is a row of numbers about nothing, so the name is not offered as a
    // toggle rather than being offered and then refused.
    [ObservableProperty] private bool showEncountersColumn = true;
    [ObservableProperty] private bool showCaughtColumn = true;
    [ObservableProperty] private bool showRanFromColumn = true;
    [ObservableProperty] private bool showLastEncounteredColumn = true;
    [ObservableProperty] private bool showRateColumn = true;

    [ObservableProperty] private int encounterPageIndex;

    [ObservableProperty] private int encounterPageCount = 1;

    /// <summary>"Page 1 of 3", or blank when everything fits on one page -
    /// a pager that says "Page 1 of 1" is noise.</summary>
    public string EncounterPageLabel =>
        EncounterPageCount <= 1
            ? string.Empty
            : $"Page {EncounterPageIndex + 1} of {EncounterPageCount}";

    public bool CanPageBack => EncounterPageIndex > 0;

    public bool CanPageForward => EncounterPageIndex + 1 < EncounterPageCount;

    /// <summary>The pager hides itself entirely on a hunt that fits.</summary>
    public bool ShowEncounterPager => EncounterPageCount > 1;

    partial void OnEncounterPageIndexChanged(int value) => RefreshPagerState();

    partial void OnEncounterPageCountChanged(int value) => RefreshPagerState();

    private void RefreshPagerState()
    {
        OnPropertyChanged(nameof(EncounterPageLabel));
        OnPropertyChanged(nameof(CanPageBack));
        OnPropertyChanged(nameof(CanPageForward));
        OnPropertyChanged(nameof(ShowEncounterPager));
    }

    [RelayCommand]
    private void EncounterPageBack()
    {
        if (!CanPageBack)
            return;

        EncounterPageIndex--;
        FillEncounterPage();
    }

    [RelayCommand]
    private void EncounterPageForward()
    {
        if (!CanPageForward)
            return;

        EncounterPageIndex++;
        FillEncounterPage();
    }

    /// <summary>Copies the current page out of SessionEncounters. Kept
    /// separate from UpdateSessionEncounters so paging does not rebuild every
    /// row - turning a page should not re-decode a hundred sprites.</summary>
    private void FillEncounterPage()
    {
        SessionEncountersPage.Clear();

        int start = EncounterPageIndex * EncounterPageSize;

        for (int i = start;
             i < SessionEncounters.Count && i < start + EncounterPageSize;
             i++)
        {
            SessionEncountersPage.Add(SessionEncounters[i]);
        }
    }

    public IReadOnlyList<string> AvailablePokemon =>
        PokemonSpriteService.AllPokemon
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();

    [ObservableProperty] private string windowTitle = "Pro Tracker & Database";
    [ObservableProperty] private string? targetPokemonInput;

    [ObservableProperty] private string totalEncounters = "0";
    [ObservableProperty] private string targetedEncountersFound = "0";
    // §364: the two companions to the stat above - of the targets found, how
    // many were caught and how many were fled from. Strings like every other
    // stat here because DisplayNumber.Count formats them.
    [ObservableProperty] private string targetedPokemonCaught = "0";
    [ObservableProperty] private string targetedPokemonFled = "0";
    [ObservableProperty] private string timeHunting = "00:00:00";
    [ObservableProperty] private string sinceShiny = "0";
    [ObservableProperty] private string sinceForm = "0";
    [ObservableProperty] private string successfulCatches = "0";
    [ObservableProperty] private string failedCatches = "0";

    // Replaces the old single CurrentlyHuntingSprite/CurrentlyHuntedLabel pair -
    // up to 4 simultaneous targets now, shown side by side. TargetSpriteSize
    // shrinks as targets are added, per the "keep the same size for 2, get
    // smaller for 3-4" request - bound directly by each Image in
    // MainWindow.axaml rather than needing a converter.
    //
    // §367: TargetsPanelMaxWidth is gone. It existed to cap the wrap panel at
    // exactly two items across so 4 targets formed a 2x2 grid; the ask now is
    // four in one row, which is what the panel does on its own once nothing
    // caps it. See UpdateTrackerDisplay for the sizes and where they come
    // from.
    public ObservableCollection<TargetDisplayItem> CurrentTargets { get; } = new();
    [ObservableProperty] private double targetSpriteSize = 90;
    // The width of a whole card - sprite, name and type icons. At one or two
    // targets it is wider than the sprite, because a bare sprite-width label
    // wraps longer names ("Charmeleon") onto two lines even with plenty of
    // vertical room. At three and four there is no width to spare for that -
    // see UpdateTrackerDisplay.
    [ObservableProperty] private double targetLabelMaxWidth = 110;

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

    // The last catalog-confirmed map name - see RouteDetector.cs and
    // EncounterTracker.CornerInfoDetected. Only updates while tracking is
    // running (Start pressed) since the underlying scan piggybacks on
    // EncounterTracker's own loop rather than running independently - see
    // OnCornerInfoDetected below. No window binds this directly (a stale
    // WinForms-era comment here used to claim the stats panel did): it is
    // the data source stamped onto session history records, Catch Log
    // entries, PVP battle rows and the Admin Console's last-accepted line.
    // §102 removed timeOfDayText, which really was bound nowhere - it was
    // write-only plumbing from the corner OCR's day/night parse, deleted at
    // the user's request along with the parse itself.
    [ObservableProperty] private string currentRouteText = "Unknown";

    // "Pause Since Form" - see HuntSession.SinceFormPaused for why this
    // deliberately isn't reset by Reset().
    [ObservableProperty] private bool sinceFormPaused;
    [ObservableProperty] private string sinceFormPauseButtonText = "Pause Since Form";

    // §136: the control is an icon beside the Since Form label now. Two
    // bars while counting (press to pause), a triangle while paused (press
    // to resume) - a Path, like the swap arrow (§103), so it is the same
    // on every OS rather than a font's pause character that draws as a box
    // wherever the font lacks it. The text above stays as the tooltip and
    // accessible name.
    public Geometry SinceFormPauseGlyph => StreamGeometry.Parse(
        SinceFormPaused
            ? "M 0,0 L 10,6 L 0,12 Z"
            : "M 0,0 H 4 V 12 H 0 Z M 7,0 H 11 V 12 H 7 Z");

    partial void OnSinceFormPausedChanged(bool value) =>
        OnPropertyChanged(nameof(SinceFormPauseGlyph));

    // §137: the Current Event selector (AvailableEvents/SelectedEvent, a
    // persisted name and nothing more) was removed from the stats panel with
    // its state here. §253 deleted the EventSettingsService and
    // Models/EventSettings.cs it left behind, with the Events board.

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string? statusMessage;

    // Stats panel side swap - see MainWindow.axaml's toolbar/content DockPanels
    // and UiPreferences.StatsPanelOnRight. Requested by multi-client hunters who
    // run several instances of this app side by side and want each instance's
    // stats column to sit toward the middle of the screen rather than always on
    // the right.
    [ObservableProperty] private bool statsPanelOnRight = true;

    /// <summary>Which side the stats panel + its toolbar button dock to - bound
    /// via DockPanel.Dock in MainWindow.axaml.</summary>
    public Dock StatsPanelDock => StatsPanelOnRight ? Dock.Right : Dock.Left;

    // §103: the swap button is icon-only now - these three drive the arrow's
    // direction (toward where the panel will MOVE), its hug-the-outer-edge
    // placement inside the reserved 220px Border, and the tooltip/accessible
    // name that replaced the old "⇄ Move Stats Left/Right" text.
    public Geometry SwapStatsArrowGeometry => StreamGeometry.Parse(
        StatsPanelOnRight ? "M 14,0 L 0,9 L 14,18 Z" : "M 0,0 L 14,9 L 0,18 Z");

    public Avalonia.Layout.HorizontalAlignment SwapStatsButtonAlignment =>
        StatsPanelOnRight ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;

    public string SwapStatsTooltip => StatsPanelOnRight
        ? "Move statistics to the left"
        : "Move statistics to the right";

    partial void OnStatsPanelOnRightChanged(bool value)
    {
        OnPropertyChanged(nameof(StatsPanelDock));
        OnPropertyChanged(nameof(SwapStatsArrowGeometry));
        OnPropertyChanged(nameof(SwapStatsButtonAlignment));
        OnPropertyChanged(nameof(SwapStatsTooltip));
    }

    // Exclude Stats (Stats menu) - each flag controls whether the corresponding
    // stat block is shown in MainWindow.axaml's stats panel. The underlying
    // counters in huntSession keep updating regardless of these flags - see
    // ApplyExcludedStats/RefreshExcludedStats below and ExcludeStatsViewModel.
    [ObservableProperty] private bool showTimeHunting = true;
    [ObservableProperty] private bool showTotalEncounters = true;
    [ObservableProperty] private bool showTargetedEncountersFound = true;
    [ObservableProperty] private bool showTargetedPokemonCaught = true;
    [ObservableProperty] private bool showTargetedPokemonFled = true;
    [ObservableProperty] private bool showSinceShiny = true;
    [ObservableProperty] private bool showSinceForm = true;
    [ObservableProperty] private bool showSuccessfulCatches = true;
    [ObservableProperty] private bool showPokemonBrokenFree = true;

    // Master switch for the whole stats panel (see UiPreferences.StatsPanelHidden
    // and ExcludeStatsWindow's own master checkbox) - distinct from the eight
    // per-stat flags above, which only ever apply while this is true. Also
    // drives IsVisible on the toolbar's swap-stats Border in MainWindow.axaml,
    // so hiding the panel cleanly reclaims that space too rather than leaving
    // an empty docked slot. Named the "shown" way (ShowStatsPanel, matching
    // the ShowXxx naming above) rather than as a StatsPanelHidden flag
    // directly, so every IsVisible binding here reads the same direction.
    // §256: hiding the panel hides Report a Problem with it - the menu bar's
    // fallback copy (§136) is gone, so nothing else reads this for it.
    [ObservableProperty] private bool showStatsPanel = true;

    // Which sound (see SoundNotificationService.SoundCatalog - "None" plus
    // whatever's in the catalog) plays via SoundNotificationService when
    // Since Form/Since Shiny resets, see OnRareEncounterDetected below. Set
    // independently per stat from the Sound Settings window (File menu) -
    // see SoundSettingsViewModel, RefreshSoundSelections below (called after
    // that window saves), UiPreferences.SinceFormSound/SinceShinySound, and
    // MIGRATION_GUIDE.md. Windows-only; SoundNotificationService no-ops
    // harmlessly elsewhere. This started as one shared on/off switch (see
    // MIGRATION_GUIDE.md #65), then a pair of File-menu submenus (#71),
    // before becoming this dedicated dialog (#72).
    [ObservableProperty] private string sinceFormSound = "None";
    [ObservableProperty] private string sinceShinySound = "None";

    // §152: how loud each plays, 0-100, from the same preference file and
    // refreshed at the same moments as the two names above. Plain fields -
    // nothing binds to them; they only travel to PlaySound and WarmUp.
    private int sinceFormSoundVolume = SoundNotificationService.MaxVolumePercent;
    private int sinceShinySoundVolume = SoundNotificationService.MaxVolumePercent;

    // §389: where they play - the device pinned in Sound Settings, or null
    // for the system default. Same file, same moments, same shape as the
    // volumes; travels to PlaySound (which checks the device is present)
    // and to WarmUp (which lists the devices at hunt start).
    private SoundOutputDevice? soundOutputDevice;

    // Unlike most other [ObservableProperty] pairs here, these two don't
    // persist through their own partial On...Changed hook - Sound Settings
    // is a Save/Cancel dialog with its own working copy (see
    // SoundSettingsViewModel), the same shape as Appearance/Exclude Stats,
    // so the only place these properties change is RefreshSoundSelections
    // below, after that dialog actually saves.

    /// <summary>
    /// Set by MainWindow.axaml.cs to show ClientSelectorWindow (a ViewModel shouldn't
    /// own a Window reference). Returns the chosen client number, or 0 if cancelled,
    /// plus (§105) whether the user confirmed taking that profile away from another
    /// running tracker window - the picker asks; this just carries the answer.
    /// Replaces: using ClientSelector form = new(); form.ShowDialog(this);
    /// </summary>
    public Func<Task<(int ClientNumber, bool Force)>>? RequestClientSelection { get; set; }

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
        presenceTimer.Tick += (_, _) => _ = SendPresenceHeartbeatAsync();

        // Set via the property (not the backing field) so OnStatsPanelOnRightChanged
        // computes the initial SwapStatsButtonText/StatsPanelDock consistently with
        // whatever was persisted - there's no matching save-on-load concern here
        // since that partial method only updates local display text, it never writes
        // back to disk (ToggleStatsPanelSideCommand is the only thing that saves).
        StatsPanelOnRight = uiPreferences.StatsPanelOnRight;
        ApplyExcludedStats(uiPreferences.ExcludedStats);
        ApplyExcludedTableColumns(uiPreferences.ExcludedTableColumns);
        ShowStatsPanel = !uiPreferences.StatsPanelHidden;
        sinceFormSound = uiPreferences.SinceFormSound;
        sinceShinySound = uiPreferences.SinceShinySound;
        sinceFormSoundVolume = uiPreferences.SinceFormSoundVolume;
        sinceShinySoundVolume = uiPreferences.SinceShinySoundVolume;
        soundOutputDevice = SoundOutputDevice.Parse(uiPreferences.SoundOutputDevice, uiPreferences.SoundOutputDeviceLabel);

        LoadTargetSpriteSkins();

        // §278: which way the encounter table is kept, from this client's own
        // preferences. TrackerSettings is what the running app reads; the
        // preference file is where the answer lives.
        TrackerSettings.Apply(uiPreferences.PerMapEncounterTable);
        TrackerSettings.ApplyLevelSharing(uiPreferences.ShareLevelData);
        MapEncounterService.CurrentMapChanged += OnMapEncountersChanged;
        TrackerSettings.Changed += OnMapEncountersChanged;

        // §135: the saved box (if any) reaches the locator with the rest
        // of this client's preferences, here and again on every client
        // switch in AssignTrackerClient.
        BattleWindowLocator.SetManualBounds(uiPreferences.ManualBattleBounds);

        // Must be posted (and therefore consumed by RegisterEncounter) before
        // the EncounterDetected post below - see both events' declaration
        // comments in EncounterTracker.cs.
        encounterTracker.EncounterLevelDetected += level =>
            Dispatcher.UIThread.Post(() => pendingEncounterLevel = level);

        // Same ordering requirement as EncounterLevelDetected above.
        encounterTracker.EncounterGenderDetected += gender =>
            Dispatcher.UIThread.Post(() =>
            {
                pendingEncounterGender = gender;

                // §124: the same reading the header icons already use,
                // now also recorded against the encounter itself so the
                // history has it. Costs nothing - the permanent row for
                // this encounter has not been written yet.
                SessionEncounterHistoryService.RefineCurrentGender(gender);
            });

        // Mid-battle level correction (§99) - the §96 vote consensus settling
        // on (or improving) a value while the battle is still on screen. Only
        // the session encounter history consumes this; the pending fields
        // above are deliberately untouched, since registration already
        // happened by the time any refinement can fire.
        encounterTracker.EncounterLevelRefined += level =>
            Dispatcher.UIThread.Post(() =>
            {
                SessionEncounterHistoryService.RefineCurrentLevel(level);

                // §103: the status line follows the same mid-battle
                // correction the history record just took.
                UpdateEncounterMessage(level: level);
            });

        encounterTracker.EncounterDetected += name =>
            Dispatcher.UIThread.Post(() => RegisterEncounter(name));

        encounterTracker.StatusChanged += status =>
            Dispatcher.UIThread.Post(() => StatusMessage = status);

        // Must be posted (and therefore consumed by OnCatchResultDetected)
        // before the CatchResultDetected post below - same ordering
        // requirement and reasoning as EncounterLevelDetected/
        // EncounterGenderDetected above, just for the fresh catch-time
        // reading added by MIGRATION_GUIDE.md §76.
        encounterTracker.CatchLevelDetected += level =>
            Dispatcher.UIThread.Post(() => pendingCatchLevel = level);

        encounterTracker.CatchGenderDetected += gender =>
            Dispatcher.UIThread.Post(() => pendingCatchGender = gender);

        encounterTracker.CatchResultDetected += result =>
            Dispatcher.UIThread.Post(() => OnCatchResultDetected(result));

        encounterTracker.RareEncounterDetected += (name, rareType) =>
            Dispatcher.UIThread.Post(() => OnRareEncounterDetected(rareType));

        // §138: arrives a moment after RareEncounterDetected for a form,
        // from the matcher's background task.
        encounterTracker.CounterpartIdentified += (name, result) =>
            Dispatcher.UIThread.Post(() => OnCounterpartIdentified(name, result));

        encounterTracker.CornerInfoDetected += (routeName, battleActive) =>
            Dispatcher.UIThread.Post(() => OnCornerInfoDetected(routeName, battleActive));

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
                    Session.PauseTimeAccrual();
                }
                else
                {
                    Session.ResumeTimeAccrual();
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
                    Session.PauseTimeAccrual();
                }
                else
                {
                    Session.ResumeTimeAccrual();
                }

                UpdateTrackerDisplay();
            });
        };

        // §101: entering/leaving the isolated Admin Client stops tracking at
        // the boundary (a running tracker must not straddle two data
        // contexts), pauses both sessions, and refreshes every display off
        // the newly-active context. Errors are contained - a failure here
        // must never take the UI loop down.
        AdminModeService.ActiveChanged += () => Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                huntTimer.Stop();
                await encounterTracker.StopAsync();
                huntSession.Pause();
                adminHuntSession.Pause();

                // §101: leaving admin mode reopens the lifetime-stats gate -
                // flush any normal-mode seconds that were still pending when
                // admin mode began (see FlushPendingLifetimeTime's own gate).
                //
                // §249: IsolatedSession, deliberately, even though this is
                // the ADMIN change handler. The flush below refuses while
                // ANY isolation is active; if a second reason were still
                // holding the gate shut when admin mode ended, flushing here
                // would zero the pending counter against an unchanged file
                // and silently drop the seconds. Every gate on this path
                // reads the same predicate, so they cannot disagree.
                if (!IsolatedSession.IsActive)
                    FlushPendingLifetimeTime();

                IsRunning = false;
                UpdateTrackerDisplay();
                UpdateSessionEncounters();
                PlayCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();

                StatusMessage = AdminModeService.IsActive
                    ? "Admin Client active - encounters now record to an isolated diagnostic session only."
                    : "Admin Client left - normal client data is active again, exactly as it was.";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Admin Client mode switch failed");
                StatusMessage = $"Admin Client switch problem: {ex.Message}";
            }
        });

        // §101: the Admin Console's two recovery hooks - the console never
        // sees the tracker itself, and the watchdog shares this exact
        // restart path, so a duplicate loop cannot exist by construction.
        TrackerRecoveryService.RestartTrackingAsync = RestartTrackingPipelineAsync;
        TrackerRecoveryService.ReacquireGameWindow = ReacquireGameWindow;
        // §105: "Force" now means force - it claims the client even from
        // another live tracker window (which steps down on its own next
        // tick). Admin-only, explicit, and named exactly what it does.
        TrackerRecoveryService.ForceClientAssign = clientNumber =>
            AssignTrackerClient(clientNumber, force: true)
                ? $"Tracker bound to client {clientNumber} (any other window on it will step down within a few seconds)."
                : "Could not bind client " + clientNumber + " - see the main window's status line for why.";

        // §298: and the World Quest one. The Admin Console can end a quest
        // for everyone; the admin who ends the quest they are hunting comes
        // out of the mode through the same path the menu item uses, rather
        // than the console reaching into a session it cannot see.
        WorldQuestMode.LeaveHandler = LeaveWorldQuestAsync;

        watchdogTimer.Tick += (_, _) => _ = WatchdogTickAsync();
        watchdogTimer.Start();

        // §187: Admin Client mode survives a restart by design (§101), but
        // the restore itself used to be silent - no log line, nothing in a
        // Report a Problem bundle. One line here means a later "why is
        // import refused / why is nothing being saved" is answerable from
        // the log the tester already sends.
        AdminModeService.LogStartupState();
        WorldQuestMode.LogStartupState();
        RefreshWorldQuestUi();

        LoadPreviousSession();
        UpdateTrackerDisplay();

        // §251: the quest view model's status line is this window's while
        // the mode is on. The stats panel has no room for a line of its own,
        // and the toolbar's is where every other message already goes - so
        // "Added 120 IVs by hand" lands where "Encountered Rattata" does.
        Quest.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorldQuestViewModel.StatusMessage) &&
                IsWorldQuestPanel &&
                !string.IsNullOrWhiteSpace(Quest.StatusMessage))
            {
                StatusMessage = Quest.StatusMessage;
            }
        };

        // §251: the mode restored from its marker (§250) restores its panel
        // too. Fetched by the marker's id, not by "whatever is running now" -
        // see WorldQuestViewModel.StartAsync. Fire-and-forget: StartAsync
        // catches its own failures and says so on the status line.
        if (WorldQuestMode.IsActive)
            _ = Quest.StartAsync();

        // §207: one read now, then one every five minutes for the life of
        // the window. No IsOnline gate: RefreshAsync is quiet, does nothing
        // without an events server, and keeps whatever was cached when the
        // server cannot be reached - which is what a hunt that loses its
        // network needs it to do.
        activeEventsTimer.Tick += (_, _) => _ = ActiveEventService.RefreshAsync();
        activeEventsTimer.Start();
        _ = ActiveEventService.RefreshAsync();

        // §252: the same shape for the running World Quest - one read now,
        // one every five minutes. RefreshWorldQuestLiveAsync is quiet, does
        // nothing without an events server, and keeps its last answer when
        // the server cannot be reached.
        worldQuestPollTimer.Tick += (_, _) => _ = RefreshWorldQuestLiveAsync();
        worldQuestPollTimer.Start();
        _ = RefreshWorldQuestLiveAsync();

        // §267: News rides the same timer rather than starting a third one.
        // Both ask the same events server, both are quiet when it is down,
        // and an announcement is not more urgent than five minutes.
        worldQuestPollTimer.Tick += (_, _) => _ = RefreshNewsLiveAsync();
        _ = RefreshNewsLiveAsync();

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
    ///
    /// §105: once a client IS bound, this same tick switches jobs and watches
    /// for a forced takeover instead - see CheckForForcedTakeover below.
    /// </summary>
    private void TryAutoAssignClient()
    {
        if (selectedClientNumber > 0)
        {
            // §105: bound already, so this tick does the other half of the
            // job - noticing if another window has force-claimed this
            // profile out from under us. The timer deliberately keeps
            // running now instead of stopping once bound; it is one small
            // file read every 5 seconds, and it is what makes a takeover a
            // clean handover rather than two windows writing one file.
            CheckForForcedTakeover();

            // §140: and, while a hunt is on, whether the game is still there
            // at all.
            StopHuntIfClientGone();
            return;
        }

        // §105: don't silently re-bind (to client 1, of all profiles) after a
        // deliberate handover - see steppedDownAfterHandover's declaration.
        if (steppedDownAfterHandover)
            return;

        var clients = captureService.FindClientWindows("PROClient");

        if (clients.Count == 0)
            return; // PRO isn't running yet - the timer will try again.

        // §110: bind the profile that actually OWNS this window rather than
        // assuming client 1. Same mapping the Assign Client picker shows, so
        // a player who moved the running client onto Client 2 is not put back
        // on Client 1 by the next startup - which would have undone the very
        // choice the picker just made. With no remembered claims anywhere
        // (every fresh install, and everyone who has never opened the picker)
        // pass 2 of the mapping still hands the first window to client 1, so
        // the ordinary single-client case behaves exactly as it always has.
        var ownership = ClientWindowAssignmentService.MapSlotsToWindows(
            clients, ClientSelectorViewModel.MaxClients);

        int owningClient = ownership
            .Where(pair => pair.Value.ProcessId == clients[0].ProcessId)
            .Select(pair => pair.Key)
            .FirstOrDefault();

        if (owningClient < 1)
            owningClient = 1;

        captureService.SelectWindow(clients[0].Handle);

        if (!AssignTrackerClient(owningClient))
        {
            // That client is already claimed by another running Pro Tracker
            // window (see AssignTrackerClient/SessionPersistenceService's
            // lock remarks) - this is exactly the "two trackers, one PRO
            // client" scenario that used to silently corrupt whichever
            // session saved last. Keep the timer running instead of stopping
            // it: as soon as that other window closes and releases the lock,
            // the next tick picks this client up automatically with no user
            // action needed.
            return;
        }
    }

    /// <summary>
    /// §105: the losing side of a confirmed takeover. Another tracker window
    /// claimed the profile this window was bound to, so this one flushes what
    /// it has and stops writing - it does NOT stop tracking and does not clear
    /// anything on screen: the counters, sprites and history stay exactly as
    /// they are, they simply stop being saved. Once stepped down,
    /// ActiveClientNumber reports 0 and every per-client store in the app
    /// (session, catch logs, PVP, boss cooldowns, encounter history) no-ops on
    /// its own, so there is never a second writer.
    ///
    /// Honest limit, and the reason the picker's prompt says "finish its last
    /// save": the window taking the profile loads it at claim time, so this
    /// final flush can be superseded by the new owner's next save. The taking
    /// window is the authority by design - what this prevents is the two
    /// windows interleaving writes indefinitely, which is the actual
    /// corruption case.
    /// </summary>
    private void CheckForForcedTakeover()
    {
        // Admin Client mode writes nothing to a normal client anyway (§101),
        // so there is no handover to perform and nothing to flush.
        if (AdminModeService.IsActive)
            return;

        if (SessionPersistenceService.StillOwnsClientLock())
            return;

        int lostClient = selectedClientNumber;

        try
        {
            if (huntSession.IsRunning)
                huntSession.Pause();

            FlushPendingLifetimeTime();
            SessionPersistenceService.Save(huntSession);
            SessionEncounterHistoryService.FlushToDisk();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Final save before client {Client} handover failed", lostClient);
        }

        SessionPersistenceService.StepDownFromForcedTakeover();

        selectedClientNumber = 0;
        steppedDownAfterHandover = true;
        huntTimer.Stop();

        Log.Information("Client {Client} was taken over by another tracker window - this window stopped saving to it", lostClient);

        StatusMessage =
            $"Client {lostClient} was taken over by another Pro Tracker window. " +
            "This window saved its last state and stopped recording - your data is intact. " +
            "Use Assign Client to pick another client.";

        UpdateTrackerDisplay();
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Play()
    {
        if (Session.TargetPokemons.Count == 0)
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
        List<string> pendingTargets = new(Session.TargetPokemons);

        // §110: a profile the player actually chose is never reassigned
        // here. This branch used to read `if (clients.Count == 1)` and force
        // client 1 - fair before §105, when one running window really did
        // mean "client 1", and wrong the moment the four slots became
        // switchable profiles. It is the whole of the reported bug: assign
        // Client 2, press Play, and AssignTrackerClient(1) saved the client-2
        // session and moved you to client 1, because the "already on this
        // client" early-out inside it cannot fire when 2 != 1. Play binds a
        // capture window; CHOOSING a profile belongs to the picker.
        if (selectedClientNumber > 0)
        {
            if (!BindCaptureToActiveProfile(clients))
                return;
        }
        else if (clients.Count == 1)
        {
            captureService.SelectWindow(clients[0].Handle);

            if (!AssignTrackerClient(1))
                return;
        }
        else
        {
            (int chosen, bool force) = RequestClientSelection is not null
                ? await RequestClientSelection()
                : (0, false);

            if (chosen == 0)
                return;

            // The picker is a DELIBERATE client choice - one of the three
            // sanctioned Admin Client exits (§101). force is only ever true
            // when the picker's own takeover prompt was accepted (§105).
            if (!AssignTrackerClient(chosen, leaveAdminMode: true, force: force))
                return;
        }

        if (Session.TargetPokemons.Count == 0 && pendingTargets.Count > 0)
        {
            Session.TargetPokemons = pendingTargets;

            PersistSession();
        }

        Session.Start();
        huntTimer.Start();

        // §150: the first heartbeat now, then one every five minutes.
        if (EventsSyncService.IsOnline)
        {
            Log.Information("Presence: this hunt sends an anonymous heartbeat to the events server every 5 minutes - a random id for this run, nothing else.");
            presenceTimer.Start();
            _ = SendPresenceHeartbeatAsync();
        }

        // Runs unconditionally on every platform now - the OCR pipeline was ported
        // to SkiaSharp + TesseractOCR (with native Linux/macOS binaries) a while
        // back specifically so this wasn't Windows-only anymore. This used to be
        // gated behind OperatingSystem.IsWindows() with a "not available on this
        // platform yet" status message, left over from before that port was done -
        // BossCooldownTracker already runs the same underlying detection
        // unconditionally on every platform, so this should too.
        encounterTracker.Start();

        // §103: pre-warm the selected alert clips' file cache off-thread the
        // moment a hunt starts, so the session's first shiny/form alert never
        // pays first-touch disk latency at the exact moment it matters.
        // §152: with their volumes, so the quieter copies are ready too.
        // §389: and the output device, so it is listed and checked now.
        SoundNotificationService.WarmUp(soundOutputDevice, (SinceShinySound, sinceShinySoundVolume), (SinceFormSound, sinceFormSoundVolume));

        // §146: which sounds this hunt will use and whose preference file
        // they came from - the line that was missing when forms went by in
        // silence and the log could not say whether a sound was selected.
        Log.Information(
            "Sound alerts for this hunt: form={FormSound} at {FormVolume}%, shiny={ShinySound} at {ShinyVolume}%, output={Output} (client {Client} preferences)",
            SinceFormSound, sinceFormSoundVolume, SinceShinySound, sinceShinySoundVolume,
            soundOutputDevice?.Label ?? "system default", SessionPersistenceService.AppearanceClientNumber);

        // Covers the edge case where Play is pressed while a boss fight is
        // already in progress (e.g. the user started hunting mid-battle) -
        // BossBattleActiveChanged only fires on a start/end transition, so a
        // freshly-started EncounterTracker/HuntSession would otherwise never
        // learn that one already happened, and the Time Hunting clock would
        // start ticking straight through the rest of that boss fight.
        if (bossCooldownTracker.IsBossBattleActive)
        {
            encounterTracker.SetBossBattleActive(true);
            Session.PauseTimeAccrual();
        }

        // Same edge case, for PVP - Play pressed while a PVP battle is already in
        // progress.
        if (pvpTracker.IsPvpBattleActive)
        {
            encounterTracker.SetPvpBattleActive(true);
            Session.PauseTimeAccrual();
        }

        IsRunning = true;
        UpdateTrackerDisplay();
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => !Session.IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        if (!Session.IsRunning)
            return;

        await StopHuntCoreAsync();
    }

    /// <summary>Everything Stop does once it has decided to stop: freeze the
    /// clock, end the tracking loop, save, flush, and re-poll the buttons.
    /// §140 split it out of Stop so the client-gone stop below runs the
    /// identical sequence rather than a copy that could drift.</summary>
    private async Task StopHuntCoreAsync()
    {
        Session.Pause();
        huntTimer.Stop();
        presenceTimer.Stop();

        await encounterTracker.StopAsync();

        PersistSession();
        SessionEncounterHistoryService.FlushToDisk();
        FlushPendingLifetimeTime();

        // §429: a stopped hunt's last ranges go now rather than on the
        // timer - fire and forget, the service keeps what does not go.
        _ = LevelShareService.FlushAsync();

        IsRunning = false;
        UpdateTrackerDisplay();
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private bool CanStop() => Session.IsRunning;

    /// <summary>§150. One heartbeat, if a hunt is still running. Failures
    /// are logged once per outage (not every five minutes) and change
    /// nothing about the hunt; the next tick simply tries again.</summary>
    private async Task SendPresenceHeartbeatAsync()
    {
        if (!Session.IsRunning || !EventsSyncService.IsOnline)
            return;

        try
        {
            await EventsSyncService.SendPresenceAsync();
            presenceFailureLogged = false;
        }
        catch (EventsSyncException ex)
        {
            if (presenceFailureLogged)
                return;

            presenceFailureLogged = true;
            Log.Information("Presence: heartbeat not delivered ({Reason}) - the hunt is unaffected; it will try again in five minutes.", ex.Message);
        }
    }

    /// <summary>
    /// §140. Runs on every autoClientDetectionTimer tick while a client is
    /// bound. With a hunt on and no PROClient process running for two ticks
    /// in a row, it stops the hunt exactly as the Stop button would - the
    /// clock froze at the second tick, the session is saved, the tracking
    /// loop has ended - and says so on the status line. Before this, closing
    /// the game with a hunt running left Time Hunting (and the lifetime
    /// total it feeds every 30 seconds) counting until someone pressed Stop,
    /// which for a player who closed PRO for the night meant all night. A
    /// minimized client does not count as gone - see
    /// IWindowCaptureService.IsClientProcessRunning. Nothing about the
    /// profile changes: the next Start binds the relaunched client's window
    /// through BindCaptureToActiveProfile as it always has.
    /// </summary>
    private void StopHuntIfClientGone()
    {
        if (!Session.IsRunning)
        {
            clientMissingScans = 0;
            return;
        }

        if (captureService.IsClientProcessRunning("PROClient"))
        {
            clientMissingScans = 0;
            return;
        }

        clientMissingScans++;

        if (clientMissingScans < ClientMissingScansBeforeStop)
            return;

        clientMissingScans = 0;
        _ = StopHuntForMissingClientAsync();
    }

    private async Task StopHuntForMissingClientAsync()
    {
        string timeHunting = TimeHunting;

        try
        {
            Log.Information("No PROClient process found while hunting - stopping the hunt at {TimeHunting}", timeHunting);

            await StopHuntCoreAsync();

            // Posted rather than assigned: StopAsync's own "Tracking stopped."
            // is already queued for the UI thread by the time it returns, and
            // an assignment here would be overwritten by it a moment later.
            // Queued behind it, this line is the one that stays.
            Dispatcher.UIThread.Post(() =>
                StatusMessage =
                    $"PROClient closed - the hunt was stopped at {timeHunting} so Time Hunting stays honest. " +
                    "Press Start when the game is running again.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Stopping the hunt after the PRO client closed failed");
            StatusMessage = "PROClient closed, but the hunt could not be stopped cleanly: " + ex.Message;
        }
    }

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

        // §101: in Admin Client mode, Reset means "reset the DIAGNOSTIC
        // session" - the normal client's counts, file, and history are not
        // in play and stay exactly as they were. §249: the condition is
        // "any isolated session" and the target is Session, which the router
        // already resolves to the right one - so a second isolated session
        // resets itself here without this block learning its name.
        if (IsolatedSession.IsActive)
        {
            Session.Reset();

            if (IsolatedSession.Reason == IsolationReason.WorldQuest && WorldQuestMode.Current is { } quest)
            {
                // §250: unlike the admin session this one has a file, so
                // Reset removes it the way the normal branch removes the
                // normal one - and HuntSession.Reset clears the targets, so
                // the quest species is forced straight back.
                SessionPersistenceService.DeleteWorldQuest(quest.MessageId);
                worldQuestHuntSession.TargetPokemons = new List<string> { quest.Pokemon };
                TargetPokemonInput = quest.Pokemon;
                StatusMessage = "World Quest session reset. Normal hunting data untouched.";
            }
            else
            {
                StatusMessage = "Admin diagnostic session reset. Normal hunting data untouched.";
            }
        }
        else
        {
            FlushPendingLifetimeTime();
            huntSession.Reset();
            SessionPersistenceService.Delete();

            // The per-Pokemon session encounter history lives and dies with the
            // session counts (§99) - cleared here and on a client/import switch,
            // nowhere else. Clears any open history window to its empty state in
            // place. Catch Logs (HuntLogService), the PVP battle log, boss data,
            // and settings are deliberately untouched.
            SessionEncounterHistoryService.Clear();
        }

        IsRunning = false;
        UpdateTrackerDisplay();
        UpdateSessionEncounters();
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    // ---- §250 World Quest mode ----

    /// <summary>§251. The quest half of the stats panel: the figures and the
    /// three ways of counting a catch that were the World Quest window's
    /// until §251 folded it in here. One instance for the life of the
    /// window - started on the way into the mode, stopped on the way out,
    /// disposed with this view model.</summary>
    public WorldQuestViewModel Quest { get; } = new();

    // §254: the World Quest menu item reads "World Quest" and nothing else.
    // §250 gave it a bound header - "Start World Quest Hunting" / "Leave
    // World Quest Hunting" - so one item could say which of the two it would
    // do; the answer was to drop the words. The state lives in the colour
    // (§252) and the tooltip (WorldQuestMenuTip) instead, and the title bar
    // and the stats panel already say when the mode is on.

    /// <summary>§251. True while the stats panel shows the quest's figures
    /// and controls in place of the hunt's stats and Report a Problem. It IS
    /// WorldQuestMode.IsActive, mirrored into a property the view can bind
    /// and refreshed at every transition by RefreshWorldQuestUi.
    ///
    /// §256: Report a Problem has one home now, the foot of the stats panel,
    /// visible there outside this mode (the button binds !IsWorldQuestPanel
    /// inside the panel's own ShowStatsPanel). The menu bar's fallback copy
    /// and the ReportProblemInMenu rule that placed it (§136, §251, §255)
    /// are gone: hiding the panel, or entering the mode, hides the button,
    /// and a player who wants it shows the panel or leaves the mode.</summary>
    [ObservableProperty] private bool isWorldQuestPanel;

    private void RefreshWorldQuestUi()
    {
        IsWorldQuestPanel = WorldQuestMode.IsActive;

        // §252: the colour follows the mode as much as the quest - off while
        // hunting it, back the moment the mode is left with the quest still
        // running. §254: so does the tooltip.
        UpdateWorldQuestLive();
    }

    /// <summary>§252. True while a World Quest is running AND the mode is
    /// off: the World Quest menu item wears its colour (red on a light menu,
    /// blue on a dark one - ThemeManager.WorldQuestLiveBrushKey) so a quest
    /// that began while the player was hunting something else is noticed
    /// without a window opening on them. A steady colour, not a flash: it is
    /// a state the player can act on any time in the next day, not an alarm.</summary>
    [ObservableProperty] private bool worldQuestLive;

    /// <summary>§252, §254. What the World Quest menu item says on hover,
    /// now that its label says nothing but the name: in the mode, that it is
    /// on and a click leaves it; with a quest running, which species, how
    /// long ago it started - the number the late-entry warning in
    /// ToggleWorldQuest is about - and that a click hunts it; otherwise that
    /// nothing is running and what the colour will mean when it comes.</summary>
    [ObservableProperty] private string worldQuestMenuTip = "No World Quest is running right now.";

    /// <summary>§252. A quest ends the moment the community goal is met, and
    /// the tracker cannot see that happen - it only knows when the quest
    /// began. This far in, a warning is due before a session is started for
    /// a quest that may already be over.</summary>
    private static readonly TimeSpan LateEntryWarningAge = TimeSpan.FromHours(10);

    /// <summary>§252. One poll. Quiet on failure - it repeats every five
    /// minutes for the life of the window, and a server that is down is not
    /// news - and the last answer stands until the next one.</summary>
    private async Task RefreshWorldQuestLiveAsync()
    {
        if (!EventsSyncService.IsOnline)
            return;

        WorldQuest? mine = null;

        try
        {
            // §298: the same fetch answers both questions - which quest is
            // running (the menu colour) and what has become of the one this
            // tracker is hunting, if any. A quest an admin ended is over for
            // the player in it too, and they are the last person who should
            // find out by noticing their count stopped moving.
            (runningWorldQuest, mine) =
                await WorldQuestService.FetchActiveAndAsync(WorldQuestMode.Current?.MessageId);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "World Quest poll: the running quest could not be fetched");
            return;
        }

        if (mine is { EndedUtc: not null })
            Quest.NoteEnded(mine);

        UpdateWorldQuestLive();
    }

    /// <summary>§252. Recomputes the colour from the last poll and the mode.
    /// Called by the poll and by every mode transition.</summary>
    private void UpdateWorldQuestLive()
    {
        WorldQuest? quest = runningWorldQuest;

        // The polls are five minutes apart; a quest whose derived end has
        // passed in between is not running, whatever the last answer said.
        if (quest?.EndsUtc is { } ends && ends <= DateTime.UtcNow)
            quest = null;

        // §267: and a quest the player has already been into stops colouring
        // the item for good. §252's rule put the colour BACK the moment the
        // mode was left with the quest still running, which is right for a
        // quest never entered and nagging for one already hunted - the item
        // had said what it had to say the first time.
        bool live = quest is not null
            && !WorldQuestMode.IsActive
            && !FirstRunNoticeService.HasSeen(FirstRunNoticeService.WorldQuestEntered(quest.MessageId));

        WorldQuestLive = live;

        // §254: three states, one sentence each - the label no longer says
        // which click this is, so the hover does.
        if (WorldQuestMode.Current is { } on)
        {
            WorldQuestMenuTip = $"World Quest hunting is on for {on.Pokemon} - click to leave it. Your normal session is paused until you do.";
        }
        else if (live)
        {
            WorldQuestMenuTip = $"A World Quest for {quest!.Pokemon} is running - it started {FormatAge(DateTime.UtcNow - quest.StartedUtc)} ago. Click to hunt it. It ends early if the community goal is met.";
        }
        else
        {
            WorldQuestMenuTip = runningWorldQuest is null
                ? "No World Quest is running right now. This turns red or blue when one is - click it then to hunt the quest."
                : $"A World Quest for {runningWorldQuest.Pokemon} is running - you have already hunted it, so this is not highlighted. Click to go back in.";
        }
    }

    /// <summary>§267. True while an announcement posted in the last day has
    /// not been opened: the News menu item wears yellow
    /// (ThemeManager.NewsLiveBrushKey) until the player reads it, or until the
    /// post is a day old - whichever comes first. A post nobody opened is not
    /// news forever, and a post that WAS opened stops being news at once.</summary>
    [ObservableProperty] private bool newsLive;

    [ObservableProperty] private string newsMenuTip = "PRO's announcements.";

    // ============================================================
    // §369. THE UPDATE MENU ITEM.
    // ============================================================
    //
    // Unlike World Quest and News, which are always in the bar and change
    // colour, this item is ABSENT until there is a newer build. A permanent
    // "Check for updates" that answers no nine times out of ten teaches
    // people not to look at it; an item that is only ever there when it
    // means something cannot.
    //
    // The view model holds the answer and formats the words; the check
    // itself runs in MainWindow.axaml.cs on Opened, and the press opens the
    // window that does the work. Nothing here downloads anything.

    /// <summary>True once a newer build has been found for this platform.
    /// Drives both the item's visibility and its green (see
    /// ThemeManager.UpdateAvailableBrushKey).</summary>
    [ObservableProperty] private bool updateAvailable;

    /// <summary>What the bar says. Carries the version, so the answer to
    /// "which update" is visible without opening anything.</summary>
    [ObservableProperty] private string updateMenuHeader = "Update";

    [ObservableProperty] private string updateMenuTip = string.Empty;

    /// <summary>The update the menu item is offering, held for the window
    /// the click opens. Null whenever UpdateAvailable is false.</summary>
    public UpdateService.AvailableUpdate? PendingUpdate { get; private set; }

    /// <summary>§369. Called with whatever the startup check found - an
    /// update, or null. Null is the ordinary case and clears everything,
    /// so a later check that finds nothing takes the item away again rather
    /// than leaving a stale offer in the bar.</summary>
    public void OfferUpdate(UpdateService.AvailableUpdate? update)
    {
        PendingUpdate = update;

        if (update is null)
        {
            UpdateAvailable = false;
            UpdateMenuHeader = "Update";
            UpdateMenuTip = string.Empty;
            return;
        }

        UpdateAvailable = true;
        UpdateMenuHeader = "Update " + update.Version;

        UpdateMenuTip =
            $"Version {update.Version} is out - you are on {update.CurrentVersion}. " +
            "Click to see what changed and install it." +
            (string.IsNullOrWhiteSpace(update.Notes) ? string.Empty : "\n\n" + update.Notes);
    }

    /// <summary>§267. How long an unread announcement keeps the menu item
    /// lit.</summary>
    private static readonly TimeSpan NewsHighlightAge = TimeSpan.FromHours(24);

    /// <summary>The newest announcement the last poll saw, or null.</summary>
    private Announcement? newestAnnouncement;

    /// <summary>§267. One poll, the same shape as the World Quest one: quiet
    /// on failure, nothing without an events server, last answer stands.</summary>
    private async Task RefreshNewsLiveAsync()
    {
        if (!EventsSyncService.IsOnline)
            return;

        try
        {
            IReadOnlyList<Announcement> posts = await AnnouncementsService.FetchAsync();

            newestAnnouncement = posts
                .Where(p => p.Published is not null)
                .OrderByDescending(p => p.Published!.Value)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "News poll: the announcements could not be fetched");
            return;
        }

        UpdateNewsLive();
    }

    /// <summary>§267. Recomputes the News colour from the last poll and what
    /// has been read. Called by the poll, and again the moment the window is
    /// opened.</summary>
    private void UpdateNewsLive()
    {
        Announcement? post = newestAnnouncement;

        if (post?.Published is not { } published)
        {
            NewsLive = false;
            NewsMenuTip = "PRO's announcements.";
            return;
        }

        TimeSpan age = DateTimeOffset.UtcNow - published.ToUniversalTime();

        bool read = FirstRunNoticeService.HasSeen(FirstRunNoticeService.NewsRead(post.Id));
        bool live = !read && age >= TimeSpan.Zero && age < NewsHighlightAge;

        NewsLive = live;

        NewsMenuTip = live
            ? $"A new announcement from {post.Title}, {FormatAge(age)} ago - click to read it."
            : read
                ? "PRO's announcements. You have read the latest one."
                : "PRO's announcements. Nothing new in the last day.";
    }

    /// <summary>§267. Called when the News window is opened: the newest post
    /// is read, so the highlight goes now rather than waiting out the day.
    /// Marking the NEWEST one is what the highlight is about - it lights for
    /// the newest unread post, so that is the one opening the window
    /// clears.</summary>
    public void MarkNewsRead()
    {
        if (newestAnnouncement is { } post)
            FirstRunNoticeService.MarkSeen(FirstRunNoticeService.NewsRead(post.Id));

        UpdateNewsLive();
    }

    /// <summary>§252. "3h 12m", or "45m" inside the first hour.</summary>
    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        int hours = (int)age.TotalHours;

        return hours > 0 ? $"{hours}h {age.Minutes}m" : $"{age.Minutes}m";
    }

    /// <summary>
    /// §250. Enters or leaves World Quest mode: a separate hunting session
    /// for the running quest, with the quest species forced as the target,
    /// isolated from the normal client's records by IsolatedSession.
    ///
    /// The ORDER on the way in is the whole design. The outgoing normal
    /// session is stopped and saved while it is still the active context -
    /// StopHuntCoreAsync persists it, flushes its history and flushes any
    /// pending lifetime seconds, all through gates that are still open.
    /// Entering first would shut those gates on the normal session's own
    /// last few seconds, which is exactly the §101 pending-seconds problem,
    /// created on purpose. Then the mode is entered, the quest session is
    /// loaded or started with the target forced, and hunting begins at once
    /// - "toggle in, and it is hunting the quest".
    ///
    /// On the way out the mirror: stop and save the QUEST session while it is
    /// still the context (so PersistSession writes the quest file), leave,
    /// and the normal session is simply there again - paused, exactly as it
    /// was saved. It is NOT resumed. Toggling out is a decision to stop
    /// quest hunting, not a decision to start normal hunting.
    /// </summary>
    [RelayCommand]
    private async Task ToggleWorldQuest()
    {
        if (WorldQuestMode.IsActive)
        {
            await LeaveWorldQuestAsync();
            return;
        }

        if (AdminModeService.IsActive)
        {
            StatusMessage = "Leave Admin Client before starting World Quest hunting.";
            return;
        }

        WorldQuest? quest;

        try
        {
            quest = await WorldQuestService.FetchActiveAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "World Quest mode: the running quest could not be fetched");
            quest = null;
        }

        if (quest is null)
        {
            StatusMessage = "No World Quest is running right now, or the events server could not be reached.";
            return;
        }

        // §252: the freshest possible answer for the menu colour.
        runningWorldQuest = quest;
        UpdateWorldQuestLive();

        // §252: the late-entry warning. A quest also ends the moment the
        // community goal is met, and the tracker cannot see that - it knows
        // only when the quest began. Ten hours in, that is worth a question
        // BEFORE the normal session is stopped and saved below, so declining
        // changes nothing at all.
        TimeSpan age = DateTime.UtcNow - quest.StartedUtc;

        if (age >= LateEntryWarningAge && ConfirmAsync is not null)
        {
            bool proceed = await ConfirmAsync(
                $"This World Quest ({quest.Pokemon}) started {FormatAge(age)} ago.\n\n" +
                "A quest ends as soon as the community goal is met, and the tracker cannot see that happen - " +
                "check in game that it is still running before hunting.\n\nStart World Quest hunting anyway?");

            if (!proceed)
            {
                StatusMessage = $"World Quest hunting not started. The quest for {quest.Pokemon} began {FormatAge(age)} ago - check in game whether it is still running.";
                return;
            }
        }

        if (Session.IsRunning)
        {
            await StopHuntCoreAsync();
        }
        else
        {
            PersistSession();
            FlushPendingLifetimeTime();
        }

        if (!WorldQuestMode.Enter(quest.MessageId, quest.Pokemon))
        {
            StatusMessage = "World Quest hunting could not start - see the log.";
            AfterModeChange();
            return;
        }

        // IsolatedSession.Reason is WorldQuest from here: Session is the
        // quest session, PersistSession writes the quest file, and every
        // §249 gate is shut on the normal client's records.
        LoadWorldQuestSession(quest.MessageId, quest.Pokemon);
        AfterModeChange();

        // §251: the quest figures and the three ways of counting a catch,
        // in the stats panel now that IsWorldQuestPanel is true. Started
        // with the quest already in hand, so nothing is fetched twice.
        await Quest.StartAsync(quest);

        await Play();

        StatusMessage = $"World Quest hunting {quest.Pokemon} (the quest began {FormatAge(age)} ago) - your normal session is paused and untouched. Turn on Auto Detect to read catches from the preview panel.";
    }

    private async Task LeaveWorldQuestAsync()
    {
        if (!WorldQuestMode.IsActive)
            return;

        if (Session.IsRunning)
            await StopHuntCoreAsync();
        else
            PersistSession();

        // §251: the watcher and the countdown stop with the mode.
        Quest.Stop();

        WorldQuestMode.Leave();

        // Reason is None again: Session is huntSession, paused and saved
        // before the mode was entered, with nothing having touched it since.
        TargetPokemonInput = string.Join(", ", Session.TargetPokemons);
        AfterModeChange();

        StatusMessage = "World Quest hunting left. Your normal session is back, paused where you left it.";
    }

    /// <summary>The refresh both transitions need, and the same one the end
    /// of AssignTrackerClient does for the same reason: CanStart/CanStop/
    /// CanSelectTarget read plain flags the toolkit cannot track, so without
    /// an explicit re-poll the buttons stay stuck in their previous state -
    /// the softlock §101's note there describes.</summary>
    private void AfterModeChange()
    {
        IsRunning = false;
        UpdateTrackerDisplay();
        UpdateSessionEncounters();
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        SelectTargetCommand.NotifyCanExecuteChanged();
        RefreshWorldQuestUi();
    }

    /// <summary>§250. The quest species is the target, full stop. Set Target
    /// is greyed out while the mode is on rather than silently ignored.</summary>
    private bool CanSelectTarget() => !WorldQuestMode.IsActive;

    [RelayCommand(CanExecute = nameof(CanSelectTarget))]
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

        // §272: the picker's forms column can change how a target is DRAWN
        // without any target being chosen, so the display is refreshed even
        // when the dialog was cancelled. The skin itself is already saved by
        // then - TargetSpriteService writes as it is picked.
        if (chosen is null || chosen.Count == 0)
        {
            UpdateTrackerDisplay();
            return;
        }

        Session.TargetPokemons = chosen.Take(4).ToList();
        TargetPokemonInput = string.Join(", ", Session.TargetPokemons);
        UpdateTrackerDisplay();

        PersistSession();
    }

    /// <summary>Current targets, exposed read-only for the View to pre-fill the
    /// multi-select dialog when re-opening "Set Target" to edit an existing list.</summary>
    public IReadOnlyList<string> CurrentTargetNames => Session.TargetPokemons;

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

        // §250: the sprite click is the other way a target changes, and it
        // has no CanExecute to grey it out - so it says why instead.
        if (WorldQuestMode.IsActive)
        {
            StatusMessage = "The World Quest species is the target while World Quest mode is on. Leave the mode to hunt something else.";
            return;
        }

        string? newName = await RequestSwapTargetSelection(currentName);

        // §272: same as SelectTarget - this window's skins column may have
        // changed the picture while leaving the target alone.
        if (string.IsNullOrWhiteSpace(newName) ||
            string.Equals(newName, currentName, StringComparison.OrdinalIgnoreCase))
        {
            UpdateTrackerDisplay();
            return;
        }

        // §361: still refused, and for the same reason as ever - swapping a
        // target for one already in the list would leave two slots of the
        // same species with the same picture, which is the state §361 exists
        // to make reachable only on purpose, from the picker, where the
        // second slot gets a picture of its own.
        if (Session.TargetPokemons.Any(p => string.Equals(p, newName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"{newName} is already one of your hunting targets - "
                + "use Set Target to hunt it more than once.";
            return;
        }

        bool confirmed = ConfirmAsync is not null
            ? await ConfirmAsync($"Replace {currentName} with {newName} in your hunting targets?")
            : true;

        if (!confirmed)
            return;

        int index = Session.TargetPokemons.FindIndex(p => string.Equals(p, currentName, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            return;

        Session.TargetPokemons[index] = newName;
        TargetPokemonInput = string.Join(", ", Session.TargetPokemons);
        UpdateTrackerDisplay();

        PersistSession();
        StatusMessage = $"Swapped {currentName} for {newName}.";
    }

    /// <summary>§271. Hands this client's saved skins to TargetSpriteService,
    /// and gives it the way back to disk. Called at startup and again on every
    /// client switch, beside the other per-client display preferences - a
    /// second client's skins are its own.</summary>
    private void LoadTargetSpriteSkins() =>
        TargetSpriteService.Load(uiPreferences, () => UiPreferencesService.Save(uiPreferences));

    [RelayCommand]
    private void ToggleSinceFormPaused()
    {
        Session.SinceFormPaused = !Session.SinceFormPaused;
        UpdateTrackerDisplay();

        PersistSession();
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
        // §364: new keys, so a preferences file written before this section
        // has neither of them and both stats default to shown - which is what
        // a new stat should do.
        ShowTargetedPokemonCaught = !excludedStatKeys.Contains("TargetedPokemonCaught");
        ShowTargetedPokemonFled = !excludedStatKeys.Contains("TargetedPokemonFled");
        ShowSinceShiny = !excludedStatKeys.Contains("SinceShiny");
        ShowSinceForm = !excludedStatKeys.Contains("SinceForm");
        ShowSuccessfulCatches = !excludedStatKeys.Contains("SuccessfulCatches");
        ShowPokemonBrokenFree = !excludedStatKeys.Contains("PokemonBrokenFree");
    }

    /// <summary>§126. Same shape as ApplyExcludedStats above, for the
    /// encounter table's columns. Kept as its own method rather than folded
    /// into that one because the two lists are persisted separately and are
    /// refreshed from separate keys.</summary>
    private void ApplyExcludedTableColumns(IReadOnlyCollection<string> excludedColumnKeys)
    {
        ShowEncountersColumn = !excludedColumnKeys.Contains("Encounters");
        ShowCaughtColumn = !excludedColumnKeys.Contains("AmountCaught");
        ShowRanFromColumn = !excludedColumnKeys.Contains("RanFrom");
        ShowLastEncounteredColumn = !excludedColumnKeys.Contains("LastEncountered");
        ShowRateColumn = !excludedColumnKeys.Contains("Rate");
    }

    /// <summary>Called by MainWindow.axaml.cs after the Exclude Stats dialog
    /// (Stats menu) saves - re-reads the persisted preference so the main
    /// form's visible stats update immediately, without needing an app
    /// restart. The hunt itself is untouched; only display flags change.</summary>
    public void RefreshExcludedStats()
    {
        UiPreferences refreshed = UiPreferencesService.Load();
        uiPreferences.ExcludedStats = refreshed.ExcludedStats;
        uiPreferences.ExcludedTableColumns = refreshed.ExcludedTableColumns;
        uiPreferences.StatsPanelHidden = refreshed.StatsPanelHidden;
        ApplyExcludedStats(uiPreferences.ExcludedStats);
        ApplyExcludedTableColumns(uiPreferences.ExcludedTableColumns);
        ShowStatsPanel = !uiPreferences.StatsPanelHidden;
    }

    // ---- §135 Set Screen Boundaries ----------------------------------

    /// <summary>False once the player has ticked "Don't show this again" on
    /// the warning in front of Set Screen Boundaries (per client).</summary>
    public bool ShouldShowBoundariesWarning => !uiPreferences.SuppressBoundariesWarning;

    public void SuppressBoundariesWarning()
    {
        uiPreferences.SuppressBoundariesWarning = true;
        UiPreferencesService.Save(uiPreferences);
    }

    /// <summary>The box saved for the active client, or null. Read by the
    /// Set Screen Boundaries window to draw what is already saved.</summary>
    public ManualBattleBounds? ManualBattleBounds => uiPreferences.ManualBattleBounds;

    /// <summary>Saves (or, with null, clears) the manual box for the active
    /// client and hands it to the locator at once - no restart, no client
    /// switch needed. Nothing about the hunt changes: this is a display
    /// preference of the capture, stored beside the other display
    /// preferences, and the trackers keep running throughout.</summary>
    public void SaveManualBattleBounds(ManualBattleBounds? bounds)
    {
        uiPreferences.ManualBattleBounds = bounds;
        UiPreferencesService.Save(uiPreferences);
        BattleWindowLocator.SetManualBounds(bounds);

        StatusMessage = bounds is null
            ? "Manual screen boundaries cleared - automatic detection only."
            : $"Manual screen boundaries saved: {bounds.Width}x{bounds.Height} at ({bounds.X},{bounds.Y}). " +
              "They are used only when automatic detection finds no battle window.";
    }

    /// <summary>Called by MainWindow.axaml.cs after the Sound Settings window
    /// (File menu) saves - re-reads the persisted preference so
    /// OnRareEncounterDetected immediately uses whatever was just picked,
    /// without needing an app restart. §109 moved this and the settings
    /// reload below off the backing fields and onto the generated properties:
    /// the property setter assigns and then raises PropertyChanged, which is
    /// precisely what the hand-written pair did, so the behaviour is the same
    /// with one fewer moving part - and it clears MVVMTK0034, the analyzer
    /// warning that flags a direct write to an [ObservableProperty] field. The
    /// only difference is that the setter skips the notification when the
    /// value has not actually changed, which no binding can tell apart. The
    /// constructor still writes the fields directly, deliberately: the
    /// analyzer allows that, and nothing is subscribed that early.</summary>
    public void RefreshSoundSelections()
    {
        UiPreferences refreshed = UiPreferencesService.Load();
        uiPreferences.SinceFormSound = refreshed.SinceFormSound;
        uiPreferences.SinceShinySound = refreshed.SinceShinySound;
        uiPreferences.SinceFormSoundVolume = refreshed.SinceFormSoundVolume;
        uiPreferences.SinceShinySoundVolume = refreshed.SinceShinySoundVolume;
        uiPreferences.SoundOutputDevice = refreshed.SoundOutputDevice;
        uiPreferences.SoundOutputDeviceLabel = refreshed.SoundOutputDeviceLabel;

        SinceFormSound = uiPreferences.SinceFormSound;
        SinceShinySound = uiPreferences.SinceShinySound;
        sinceFormSoundVolume = uiPreferences.SinceFormSoundVolume;
        sinceShinySoundVolume = uiPreferences.SinceShinySoundVolume;
        soundOutputDevice = SoundOutputDevice.Parse(uiPreferences.SoundOutputDevice, uiPreferences.SoundOutputDeviceLabel);
    }

    // ============================================================
    // IMPORT / EXPORT - ported from saveDataToolStripMenuItem1/2_Click and
    // saveJSONDataToolStripMenuItem/importJSONToolStripMenuItem_Click.
    // ============================================================

    [RelayCommand]
    private Task ExportCsv() =>
        ExportHuntData(HuntDataExportService.ExportCsv, "csv");

    [RelayCommand]
    private Task ImportCsv() =>
        ImportHuntData(HuntDataExportService.ImportCsv, "csv");

    [RelayCommand]
    private Task ExportJson() =>
        ExportHuntData(HuntDataExportService.ExportJson, "json");

    [RelayCommand]
    private Task ImportJson() =>
        ImportHuntData(HuntDataExportService.ImportJson, "json");

    /// <summary>
    /// §187. Both export commands used to be the same twenty lines twice, and
    /// three of the four ways they could end were a bare <c>return</c>: no
    /// status line, no log entry, nothing. From the outside "the dialog never
    /// opened", "the dialog crashed" and "I pressed Cancel" were one single
    /// symptom - the window simply carried on as though the button had not
    /// been clicked - and a Report a Problem bundle could not tell them apart
    /// either. A Linux tester reported being unable to export and the log he
    /// sent held 8,051 lines with not one word about it.
    ///
    /// Every ending is now named on the status line, written to the log, and
    /// left on TrackerDiagnostics for the next tracking check to include.
    /// </summary>
    private async Task ExportHuntData(Action<HuntSession, string> exporter, string extension)
    {
        string suggestedName = $"ProTracker-Hunt-{DateTime.Now:yyyy-MM-dd-HHmmss}.{extension}";

        var hook = RequestSaveFilePath;

        if (hook is null)
        {
            ReportFileDialogMissing("export", "exported", extension);
            return;
        }

        Func<string, string, Task<string?>> picker = hook;

        string? path = await AskForFilePathAsync(
            () => picker(suggestedName, extension), "export", "exported", extension);

        if (path is null)
            return;

        try
        {
            exporter(Session, path);
            StatusMessage = "Hunt data exported successfully.";
            Log.Information("Hunt data exported as {Extension}", extension);
            TrackerDiagnostics.RecordFileDialog("export (" + extension + ") wrote the file");
        }
        catch (Exception ex)
        {
            // §187: writing is where a permission problem, a read-only mount
            // or a full disk shows up - all of which are the user's to fix,
            // and none of which used to reach the log.
            StatusMessage = $"The hunt data could not be exported: {ex.Message}";
            Log.Error(ex, "Hunt data export as {Extension} failed while writing the file", extension);
            TrackerDiagnostics.RecordFileDialog(
                "export (" + extension + ") could not write the file - " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// §187: shows a file picker and turns every way it can end into a named
    /// one. Returns the chosen path, or null after having already told the
    /// user, the log and TrackerDiagnostics what happened instead.
    ///
    /// The endings, in the order they are handled:
    ///   - the dialog threw. An AsyncRelayCommand swallows a faulted task
    ///     whole, so before this the app stayed perfectly silent - the single
    ///     most likely shape of "I click Export and nothing happens".
    ///   - nothing came back. That is Cancel almost every time, so it stays
    ///     quiet on the status line. It is NOT always Cancel on Linux, where
    ///     the platform can hand back a file whose local path it will not
    ///     give up (see MainWindow.axaml.cs, which logs that case with the
    ///     URI scheme that caused it) - which is why this still leaves a
    ///     diagnostics entry even for the ordinary one.
    /// </summary>
    private async Task<string?> AskForFilePathAsync(
        Func<Task<string?>> picker, string action, string past, string extension)
    {
        string? path;

        try
        {
            path = await picker();
        }
        catch (Exception ex)
        {
            StatusMessage = $"The file dialog could not be opened, so the hunt data was not {past}: {ex.Message}";
            Log.Error(ex, "The {Action} file dialog failed to open ({Extension})", action, extension);
            TrackerDiagnostics.RecordFileDialog(
                action + " (" + extension + ") - the file dialog itself failed: " + ex.GetBaseException().Message);
            return null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            TrackerDiagnostics.RecordFileDialog(
                action + " (" + extension + ") - the file dialog closed without a usable path " +
                "(cancelled, or the platform would not give a local path for the chosen file)");
            return null;
        }

        return path;
    }

    /// <summary>§187: the View never wired the picker up. Nothing the user
    /// can do about it from here, so this says so plainly rather than looking
    /// like a dead button, and logs it as an error because it means the
    /// window is half-constructed.</summary>
    private void ReportFileDialogMissing(string action, string past, string extension)
    {
        StatusMessage =
            $"This window has no file dialog available, so the hunt data cannot be {past}. " +
            "Please restart Pro Tracker, and use Report a Problem if it happens again.";

        Log.Error("The {Action} file dialog hook was never wired up by the view ({Extension})", action, extension);
        TrackerDiagnostics.RecordFileDialog(
            action + " (" + extension + ") - the window never wired a file dialog up");
    }

    private async Task ImportHuntData(Func<string, HuntSessionSaveData> importer, string extension)
    {
        // §101: imports write the REAL session and its persistence - exactly
        // what Admin Client mode promises not to touch. Leave admin mode
        // first, deliberately, if an import is really wanted. §249: refused
        // under any isolation, for the same reason.
        if (IsolatedSession.IsActive)
        {
            // §187: it used to stop at "leave Admin Client first", which is
            // only useful to someone who already knows where that is - and
            // the mode restores itself silently at startup, so the person
            // reading this may not even know they are in it. Name the way
            // out, and leave the reason in the log where a report will find
            // it.
            StatusMessage =
                "Hunt data import is disabled in Admin Client mode" +
                (AdminModeService.RestoredFromMarker
                    ? " (restored from your last session)"
                    : string.Empty) +
                " - use Assign Client to pick a normal client first.";

            Log.Information("Hunt data import refused: Admin Client mode is active ({Extension})", extension);
            TrackerDiagnostics.RecordFileDialog(
                "import (" + extension + ") refused - Admin Client mode is active");
            return;
        }

        var hook = RequestOpenFilePath;

        if (hook is null)
        {
            ReportFileDialogMissing("import", "imported", extension);
            return;
        }

        Func<string, Task<string?>> picker = hook;

        string? path = await AskForFilePathAsync(
            () => picker(extension), "import", "imported", extension);

        if (path is null)
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
            {
                TrackerDiagnostics.RecordFileDialog(
                    "import (" + extension + ") - the Add/Replace prompt was cancelled");
                return;
            }
        }
        else
        {
            bool confirmed = ConfirmAsync is not null
                ? await ConfirmAsync("Importing this file will replace the current hunt statistics.\n\nContinue?")
                : true;

            if (!confirmed)
            {
                TrackerDiagnostics.RecordFileDialog(
                    "import (" + extension + ") - the replace confirmation was declined");
                return;
            }

            mode = "Replace";
        }

        try
        {
            HuntSessionSaveData data = importer(path);
            bool isAdd = mode.Equals("Add", StringComparison.OrdinalIgnoreCase);

            huntTimer.Stop();

            if (isAdd)
            {
                // Keep the history already recorded this session - the
                // imported file only carries aggregate counts, so its
                // encounters simply arrive with no per-encounter records
                // (§99).
                huntSession.MergeFrom(data);
            }
            else
            {
                huntSession.Restore(data);

                // A replaced session's counts come from a file with no
                // per-encounter detail - the old session's history must not
                // pose as the imported hunt's, so it clears with the rest.
                SessionEncounterHistoryService.Clear();
            }

            IsRunning = false;
            UpdateTrackerDisplay();
            UpdateSessionEncounters();
            SessionPersistenceService.Save(huntSession);

            PlayCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();

            StatusMessage = isAdd
                ? "Hunt data added to the current session."
                : "Hunt data imported successfully.";

            Log.Information("Hunt data imported as {Extension} in {Mode} mode", extension, mode);
            TrackerDiagnostics.RecordFileDialog(
                "import (" + extension + ") read the file, mode " + mode);
        }
        catch (Exception ex)
        {
            StatusMessage = $"The hunt data could not be imported: {ex.Message}";
            Log.Error(ex, "Hunt data import as {Extension} failed while reading the file", extension);
            TrackerDiagnostics.RecordFileDialog(
                "import (" + extension + ") could not read the file - " + ex.GetBaseException().Message);
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
        (int chosen, bool force) = RequestClientSelection is not null
            ? await RequestClientSelection()
            : (0, false);

        if (chosen == 0)
            return;

        // The Assign Client menu is a deliberate choice - see §101; force
        // carries the picker's confirmed takeover, see §105.
        AssignTrackerClient(chosen, leaveAdminMode: true, force: force);
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
    ///
    /// §105: <paramref name="force"/> claims the client even when another live
    /// window holds it, because the four client slots are switchable profiles
    /// now. It never arrives on its own - the picker asks first (see
    /// ClientSelectorViewModel.Confirm), and the displaced window steps down
    /// on its next binding tick rather than the two racing each other, so the
    /// bug above stays fixed: still exactly one writer per profile.
    /// </summary>
    /// <summary>
    /// §110. Points capture at the window belonging to the profile this
    /// tracker is ALREADY on, using the same slot-to-window mapping the
    /// Assign Client picker displays - so "Client 2 - no PRO window" in the
    /// picker and what Play captures for client 2 cannot disagree.
    ///
    /// Returns false, with a message, when this profile has no window of its
    /// own. It deliberately does not borrow another profile's client and does
    /// not switch profiles to whatever happens to be running: silently doing
    /// either is what produced the bug this replaced. The caller has already
    /// handled "no PRO client at all", which keeps its own long-standing
    /// message - this one only ever speaks about a profile with nothing
    /// mapped to it.
    /// </summary>
    private bool BindCaptureToActiveProfile(IReadOnlyList<ClientWindowInfo> clients)
    {
        var ownership = ClientWindowAssignmentService.MapSlotsToWindows(
            clients, ClientSelectorViewModel.MaxClients);

        if (ownership.TryGetValue(selectedClientNumber, out ClientWindowInfo? mine))
        {
            captureService.SelectWindow(mine.Handle);
            return true;
        }

        StatusMessage =
            $"{ClientNamesService.GetDisplayName(selectedClientNumber)} has no PRO client " +
            "window to capture. Open one, or use Assign Client to move a running client " +
            "onto this profile.";

        return false;
    }

    private bool AssignTrackerClient(int clientNumber, bool leaveAdminMode = false, bool force = false)
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
        // have nowhere to switch to. Skipped for a confirmed takeover, which
        // is the user overriding exactly this check on purpose.
        if (!force && !SessionPersistenceService.IsClientLockAvailable(clientNumber, out int? heldByProcessId))
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

            // §250: a deliberate client choice ends World Quest mode, as it
            // ends Admin Client below (§101). It has to happen HERE, before
            // SetActiveClient changes the number, because the quest session
            // file is per client and must be written under the client the
            // hunting actually happened on. Automatic binding (leaveAdminMode
            // false) leaves the mode alone, exactly as it does for admin.
            if (leaveAdminMode && WorldQuestMode.IsActive)
            {
                worldQuestHuntSession.Pause();
                huntTimer.Stop();
                PersistSession();
                Quest.Stop();
                WorldQuestMode.Leave();
            }
        }

        if (!SessionPersistenceService.SetActiveClient(clientNumber, force))
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
        steppedDownAfterHandover = false;

        // §101: only a DELIBERATE client choice (the picker dialogs) ends the
        // Admin Client override - TryAutoAssignClient and Play's single-
        // client auto-bind never pass leaveAdminMode, so automatic detection
        // can bind the capture window without ever kicking the admin out of
        // the isolated context (the data gates hold either way).
        if (leaveAdminMode && AdminModeService.IsActive)
            AdminModeService.Leave();

        // Boss cooldowns and the PVP battle log are both per-client (each
        // client this app instance might be assigned to is presumed to be a
        // different PRO account) - reloading here, not just once at app
        // startup, is what makes switching clients actually swap in that
        // client's own cooldown/battle data instead of continuing to
        // show/save over whichever client was active before. See
        // BossCooldownService/PvpOpponentService's GetSavePath remarks.
        BossCooldownService.Load();

        // §277: the record follows the client the same way the cooldowns do -
        // a boss beaten on one account is not a win on another.
        BossRecordService.ReloadForActiveClient();
        PvpOpponentService.ReloadForActiveClient();
        HuntLogService.ReloadForActiveClient();

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
        ApplyExcludedTableColumns(uiPreferences.ExcludedTableColumns);
        ShowStatsPanel = !uiPreferences.StatsPanelHidden;
        LoadTargetSpriteSkins();

        // §278: the mode is per client like every other preference here, and
        // the map being hunted belongs to the client that was hunting it - the
        // next confirmed map reloads from the new client's own files.
        TrackerSettings.Apply(uiPreferences.PerMapEncounterTable);
        TrackerSettings.ApplyLevelSharing(uiPreferences.ShareLevelData);
        MapEncounterService.ClearCurrentMap();

        BattleWindowLocator.SetManualBounds(uiPreferences.ManualBattleBounds);

        // §109: the generated properties, same as RefreshSoundSelections.
        SinceFormSound = uiPreferences.SinceFormSound;
        SinceShinySound = uiPreferences.SinceShinySound;
        sinceFormSoundVolume = uiPreferences.SinceFormSoundVolume;
        sinceShinySoundVolume = uiPreferences.SinceShinySoundVolume;
        soundOutputDevice = SoundOutputDevice.Parse(uiPreferences.SoundOutputDevice, uiPreferences.SoundOutputDeviceLabel);

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
        SelectTargetCommand.NotifyCanExecuteChanged();
        RefreshWorldQuestUi();

        return true;
    }

    // Shared by both lock-conflict checks in AssignTrackerClient above (they
    // used to duplicate this exact ternary inline). Uses ClientNamesService
    // so a renamed client shows its actual name here too - the message that
    // most needs to say *which* client is the one you can't switch to.
    private static string ClientLockedMessage(int clientNumber, int? heldByProcessId)
    {
        string name = ClientNamesService.GetDisplayName(clientNumber);

        // §223: this used to say "Close that window first, or pick a
        // different client", which names two remedies and omits the one
        // that works. Worse, when the lock is stale - a crashed run whose
        // PID Linux could not tell was dead - there IS no window to close,
        // so the first instruction is impossible and the second is a
        // surrender. §105 built a confirmed takeover into the client picker
        // and this message never mentioned it. It does now.
        return heldByProcessId is int pid
            ? $"{name} is already being tracked by another Pro Tracker window (PID {pid}). Open File > Assign Client and pick it again to take it over, or choose a different client."
            : $"{name} is already being tracked by another Pro Tracker window. Open File > Assign Client and pick it again to take it over, or choose a different client.";
    }

    private void HuntTimer_Tick(object? sender, EventArgs e)
    {
        // IsAccruingTime (not IsRunning) - while paused for a boss battle
        // (PauseTimeAccrual, see the BossBattleActiveChanged handler below),
        // IsRunning stays true but the clock itself is frozen, and lifetime
        // stats should stay in sync with the on-screen Time Hunting stat rather
        // than counting boss-fight time as hunting time.
        if (Session.IsAccruingTime && !IsolatedSession.IsActive)
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

        // Restore (or clear) this client's session encounter history in the
        // same breath as the counts themselves, so the two can never disagree
        // about whether a hunt is in progress - no saved session means any
        // stale history file is deleted rather than loaded (§99). Also what
        // swaps histories on a client switch, since AssignTrackerClient comes
        // through here.
        SessionEncounterHistoryService.LoadForActiveClient(saved is not null);

        // §250: the quest session lives per client too, so it is (re)loaded
        // here for the same reasons the normal one is - at startup before a
        // client is bound (nothing found, a fresh session with the forced
        // target), again when auto-bind names the client (the real file),
        // and on a deliberate client switch, except that a deliberate switch
        // leaves the mode first - see AssignTrackerClient.
        if (WorldQuestMode.Current is { } quest)
            LoadWorldQuestSession(quest.MessageId, quest.Pokemon);

        UpdateTrackerDisplay();
        UpdateSessionEncounters();
    }

    /// <summary>§250. Loads the World Quest session for the active client and
    /// quest, or starts a fresh one, and FORCES the quest species as its only
    /// target either way. A restored session already carries that target;
    /// forcing it again is the guarantee, not a repair.</summary>
    private void LoadWorldQuestSession(string questId, string pokemon)
    {
        HuntSessionSaveData? saved = SessionPersistenceService.LoadWorldQuest(questId);

        if (saved is null)
            worldQuestHuntSession.Reset();
        else
            worldQuestHuntSession.Restore(saved);

        worldQuestHuntSession.TargetPokemons = new List<string> { pokemon };

        if (IsolatedSession.Reason == IsolationReason.WorldQuest)
            TargetPokemonInput = pokemon;
    }

    private void FlushPendingLifetimeTime()
    {
        if (lifetimeSaveTickCounter <= 0)
            return;

        // §101: these seconds can only have been earned in normal mode
        // (HuntTimer_Tick's own admin gate), but LifetimeStatsService.Update
        // refuses ALL writes while Admin Client mode is active - so flushing
        // now would silently DROP them (the counter would be zeroed against
        // an unchanged file). Keep them pending instead; the ActiveChanged
        // handler re-flushes the moment admin mode is left. Only closing the
        // app while still IN admin mode loses them (at most 29 seconds,
        // display-only lifetime stats - documented in MIGRATION_GUIDE.md).
        // §249: the same predicate LifetimeStatsService refuses on.
        if (IsolatedSession.IsActive)
            return;

        lifetimeStats = LifetimeStatsService.AddHuntingTime(
            TimeSpan.FromSeconds(lifetimeSaveTickCounter));

        lifetimeSaveTickCounter = 0;
    }

    // ---- §103 encounter status message ----
    // "Encountered Lv. 36 Diglett (Route 11)" - level, resolved name, and the
    // location the record captured, updated IN PLACE if the §96 consensus
    // refines the level or a §102 battle-time corner read corrects the map
    // while this same encounter is still the one on the status line. The
    // guard is exact-match: the moment any other status text lands (a catch,
    // a warning, a report save), refinements stop touching it.
    private string? lastEncounterName;
    private int? lastEncounterLevel;
    private string lastEncounterLocation = "Unknown";
    private string? lastComposedEncounterMessage;

    // §138. The message's first words and its tail. "Encountered" for an
    // ordinary encounter; "Shiny detected" / "Special form detected" once
    // the rare check confirms one; the tail names the counterpart the
    // matcher identified. Both live here, beside the level and map, so a
    // later level or map refinement re-composes the whole line with the
    // rare wording intact instead of quietly turning a form back into an
    // ordinary "Encountered".
    private string encounterMessagePrefix = "Encountered";
    private string encounterMessageSuffix = string.Empty;

    private string ComposeEncounterMessage(string name, int? level, string location)
    {
        string levelText = level is int l ? $"Lv. {l}" : "Lv. ?";
        string locationText = string.IsNullOrWhiteSpace(location) || location == "Unknown"
            ? "Unknown Location"
            : location;

        return $"{encounterMessagePrefix} {levelText} {name} ({locationText}){encounterMessageSuffix}";
    }

    private void ShowEncounterMessage(string name, int? level, string location)
    {
        lastEncounterName = name;
        lastEncounterLevel = level;
        lastEncounterLocation = location;
        encounterMessagePrefix = "Encountered";
        encounterMessageSuffix = string.Empty;
        lastComposedEncounterMessage = ComposeEncounterMessage(name, level, location);
        StatusMessage = lastComposedEncounterMessage;
    }

    /// <summary>§138. Re-composes the encounter line after the prefix or
    /// suffix changed, but only while the line is still the one on screen -
    /// the same exact-match guard UpdateEncounterMessage applies, so a
    /// catch result, a warning or a report save is never overwritten.</summary>
    private void RecomposeEncounterMessage()
    {
        if (lastEncounterName is null || StatusMessage != lastComposedEncounterMessage)
            return;

        lastComposedEncounterMessage = ComposeEncounterMessage(lastEncounterName, lastEncounterLevel, lastEncounterLocation);
        StatusMessage = lastComposedEncounterMessage;
    }

    private void UpdateEncounterMessage(int? level = null, string? location = null)
    {
        if (lastEncounterName is null || StatusMessage != lastComposedEncounterMessage)
            return;

        if (level is int l)
            lastEncounterLevel = l;

        if (location is not null)
            lastEncounterLocation = location;

        lastComposedEncounterMessage = ComposeEncounterMessage(lastEncounterName, lastEncounterLevel, lastEncounterLocation);
        StatusMessage = lastComposedEncounterMessage;
    }

    private void RegisterEncounter(string pokemonName)
    {
        if (!Session.IsRunning)
            return;

        // Permanent, near-free instrumentation for the §99 performance
        // contract - Log.Debug below is suppressed at the default Information
        // level, so this costs two timestamp reads per encounter unless
        // someone flips Program.cs to Debug to actually measure.
        var pipelineTimer = Stopwatch.StartNew();

        string resolvedName = PokemonSpriteService.ResolveEncounterName(pokemonName);

        Session.PreviousEncounter = Session.CurrentEncounter;
        Session.CurrentEncounter = resolvedName;

        // §139: the cards' form goes with the name, so a rare form is still
        // on the Previous card while the ordinary encounter that followed
        // it is on the Current one. The new encounter is ordinary until the
        // shiny check or the counterpart matcher says otherwise.
        Session.PreviousEncounterForm = Session.CurrentEncounterForm;
        Session.PreviousEncounterFormImage = Session.CurrentEncounterFormImage;
        Session.CurrentEncounterForm = string.Empty;
        Session.CurrentEncounterFormImage = string.Empty;

        // A new encounter starting must not inherit the previous one's
        // Shiny/Form status - see currentEncounterRareType's declaration
        // comment. Added by MIGRATION_GUIDE.md §77. §138: nor its form name.
        currentEncounterRareType = RareEncounterType.None;
        currentEncounterFormName = null;

        Session.TotalEncounters++;
        Session.EncountersSinceShiny++;

        if (!Session.SinceFormPaused)
            Session.EncountersSinceForm++;

        Session.RegisterPokemonEncounter(resolvedName);

        // §278: and the map's own table, which is a SECOND tally rather than a
        // move - the session keeps counting everything, so every stat, the
        // export and the import are untouched by this section.
        if (TrackerSettings.PerMapEncounterTable)
            MapEncounterService.RegisterEncounter(resolvedName);

        // Update the on-screen display before either save below, not after -
        // see MIGRATION_GUIDE.md §75. Both methods only read Session (the
        // active context's session, already fully updated above by this
        // point - §101) and PokemonSpriteService,
        // so calling them here instead of at the end changes nothing about
        // what they show - it just stops a slow save from delaying the moment
        // the Current/Previous Encounter sprites actually update.
        UpdateTrackerDisplay();
        UpdateSessionEncounters();

        // §103: the dynamic encounter message replaces EncounterTracking's
        // old generic status line - composed AFTER the sprite/display update
        // so it can never sit on the image path, from the same values the
        // history record below captures.
        ShowEncounterMessage(resolvedName, pendingEncounterLevel, CurrentRouteText);

        long displayDoneTicks = pipelineTimer.ElapsedTicks;

        // Session encounter history (§99) - one tiny in-memory record per
        // detected encounter, added AFTER the sprite/table updates above so
        // nothing new sits on the image-display path, and with no I/O of its
        // own (the service persists on a debounced background write, never
        // here). The level is this encounter's own pre-registration reading -
        // see the EncounterLevelDetected handler's ordering guarantee - and
        // both the level and an unknown location can still be corrected in
        // place later (EncounterLevelRefined/OnCornerInfoDetected). Distinct
        // from the Hunting Log write in OnCatchResultDetected: that one is
        // catches only, persistent, and untouched by Reset.
        SessionEncounterHistoryService.Append(resolvedName, pendingEncounterLevel, CurrentRouteText);

        long historyDoneTicks = pipelineTimer.ElapsedTicks;

        lifetimeStats = LifetimeStatsService.AddEncounter(resolvedName);

        // The granular per-encounter Hunting Log used to be written right
        // here, for every single encounter. See MIGRATION_GUIDE.md §76 for
        // why that moved to OnCatchResultDetected's CatchResult.Success case
        // instead: it now logs only what the player actually catches, using
        // a Level/Gender reading taken fresh at catch time (by far the more
        // reliable moment for it - see that section's own comment) rather
        // than pendingEncounterLevel/pendingEncounterGender below, which
        // this method no longer reads at all. Skipped entirely in Admin
        // Client mode (§101) - there is nothing of the normal session to
        // save, and the admin session is never saved anywhere.
        PersistSession();

        // §101 Admin Console status feed - three field writes, off the
        // sprite path like everything after the display update.
        TrackerDiagnostics.RecordEncounter(
            resolvedName,
            pendingEncounterLevel is int lvl ? $"Lv. {lvl}" : "Lv. ?",
            CurrentRouteText);

        // Suppressed at the default Information log level (see Program.cs) -
        // flip MinimumLevel to Debug to measure. Guards the §99 requirement
        // that history recording adds nothing visible to the sprite path:
        // "display" ends when the sprites/tables are set, "history" is the
        // in-memory append alone, "saves" is the pre-existing lifetime +
        // session persistence that always ran after the display.
        Log.Debug(
            "Encounter pipeline for {Pokemon}: display={DisplayMs:F1}ms, history={HistoryMs:F2}ms, saves={SaveMs:F1}ms",
            resolvedName,
            displayDoneTicks * 1000.0 / Stopwatch.Frequency,
            (historyDoneTicks - displayDoneTicks) * 1000.0 / Stopwatch.Frequency,
            (pipelineTimer.ElapsedTicks - historyDoneTicks) * 1000.0 / Stopwatch.Frequency);
    }

    private void OnCatchResultDetected(CatchResult result)
    {
        if (!Session.IsRunning)
            return;

        // §123. The species this outcome belongs to. CurrentEncounter is
        // set by RegisterEncounter and nothing moves it between an encounter
        // appearing and its result arriving, so it is still the right name
        // here - the same assumption the hunt-log write below has always
        // made.
        string species = Session.CurrentEncounter;

        switch (result)
        {
            case CatchResult.Success:
                Session.SuccessfulCatches++;
                Session.RegisterCatch(species);

                if (TrackerSettings.PerMapEncounterTable)
                    MapEncounterService.RegisterCatch(species);
                lifetimeStats = LifetimeStatsService.AddSuccessfulCatch();
                break;

            case CatchResult.Failed:
                Session.FailedCatches++;
                lifetimeStats = LifetimeStatsService.AddFailedCatch();
                break;

            // §123. This arm is new, and so is the fact that anything
            // reaches it at all: the tracking loop never raised
            // CatchResultDetected for a run, so runs were detected, logged
            // and dropped. Both halves had to change before a single run
            // could be counted.
            case CatchResult.RunAway:
                Session.RegisterRunAway(species);

                if (TrackerSettings.PerMapEncounterTable)
                    MapEncounterService.RegisterRunAway(species);
                break;

            // A knockout, or the EXP line. Deliberately counted as neither a
            // catch nor a run - see CatchResult.BattleEnded and
            // HuntSession's note on why these columns do not sum to
            // Encounters. It still falls through to the save below, because
            // the encounter that produced it is worth persisting.
            case CatchResult.BattleEnded:
                break;

            default:
                return;
        }

        // The front table reads CaughtCounts/RanFromCounts, so it has to be
        // rebuilt for the new number to appear - UpdateTrackerDisplay below
        // only refreshes the sprites and the stats column.
        UpdateSessionEncounters();

        UpdateTrackerDisplay();

        // Granular per-encounter log (species/level/gender/map/timestamp) -
        // separate from huntSession.EncounterCounts (a running per-species
        // tally with no per-event detail). Logs only successful catches as
        // of MIGRATION_GUIDE.md §76 - previously every encounter was logged,
        // back in RegisterEncounter, the moment it first appeared. Runs after
        // UpdateTrackerDisplay above, same reasoning as §75: nothing here
        // affects what that call shows, so there is no reason to make the
        // sprite update wait for it. pendingCatchLevel/pendingCatchGender
        // were stashed by the CatchLevelDetected/CatchGenderDetected handlers
        // in the constructor, both fired for this exact catch just before
        // CatchResultDetected - see those handlers' comments for why the
        // ordering is safe. Cleared immediately after so a future catch that
        // doesn't get a fresh reading (shouldn't normally happen - all three
        // fire together - but this avoids silently attaching stale data to
        // it if it ever does) logs as unknown instead. huntSession.
        // CurrentEncounter is still whatever RegisterEncounter last set it
        // to - nothing changes it between an encounter appearing and its
        // catch result registering. currentEncounterRareType - added by
        // MIGRATION_GUIDE.md §77 - is NOT cleared afterward the way the two
        // pending fields above are: it's reset by RegisterEncounter instead,
        // the moment the NEXT encounter starts, so it stays correct if this
        // same encounter somehow reports CatchResultDetected more than once
        // (shouldn't happen - see EncounterTracker's catchResultAlreadyRegistered
        // guard - but there's no reason to rely on that here too).
        if (result == CatchResult.Success)
        {
            // §138: "Summer Form" when the matcher named it, "Form" when not.
            string? rareType = currentEncounterRareType switch
            {
                RareEncounterType.Shiny => "Shiny",
                RareEncounterType.Form => currentEncounterFormName is null
                    ? "Form"
                    : currentEncounterFormName + " Form",
                _ => null
            };

            HuntLogService.RegisterEncounter(
                Session.CurrentEncounter, pendingCatchLevel, pendingCatchGender, CurrentRouteText, rareType);

            // The catch-to-form association in one greppable line - see
            // MIGRATION_GUIDE.md §94. "rare=none" on a catch the player KNOWS
            // was a form/shiny means the encounter-side detection never set
            // currentEncounterRareType (RareEncounterDetector's own OCR log
            // shows what its crop read); anything else here means the
            // association worked and the Hunting Log has the marker.
            Log.Information(
                "Hunt log catch recorded: {Pokemon} (rare={RareType}, level={Level}, gender={Gender}, route={Route})",
                Session.CurrentEncounter,
                rareType ?? "none",
                pendingCatchLevel,
                pendingCatchGender ?? "unknown",
                CurrentRouteText);

            pendingCatchLevel = null;
            pendingCatchGender = null;
        }

        PersistSession();
    }

    private void OnRareEncounterDetected(RareEncounterType rareType)
    {
        if (!Session.IsRunning)
            return;

        // Stashed for OnCatchResultDetected to read if/when this encounter
        // is caught - see currentEncounterRareType's declaration comment.
        // Added by MIGRATION_GUIDE.md §77.
        currentEncounterRareType = rareType;

        // §124. Until now a shiny or a form was only ever recorded if the
        // player CAUGHT it - the hunt log's rare marker is written from
        // OnCatchResultDetected's success arm. A shiny that broke free and
        // fled left no trace anywhere. The encounter history keeps it now,
        // whatever happens next.
        SessionEncounterHistoryService.RefineCurrentRareType(rareType.ToString());

        string soundToPlay;
        int soundVolume;
        SoundNotificationService.SoundPriority soundPriority;

        switch (rareType)
        {
            case RareEncounterType.Shiny:
                Session.EncountersSinceShiny = 0;
                lifetimeStats = LifetimeStatsService.AddShinyEncounter();
                soundToPlay = SinceShinySound;
                soundVolume = sinceShinySoundVolume;
                soundPriority = SoundNotificationService.SoundPriority.Shiny;

                // §139: the Current Encounter card switches to the shiny
                // sprite (UpdateTrackerDisplay below) and its label to
                // "Shiny <name>". A shiny outranks any form on the card.
                Session.CurrentEncounterForm = HuntSession.ShinyForm;
                Session.CurrentEncounterFormImage = string.Empty;
                break;

            case RareEncounterType.Form:
                Session.EncountersSinceForm = 0;
                lifetimeStats = LifetimeStatsService.AddFormEncounter();
                soundToPlay = SinceFormSound;
                soundVolume = sinceFormSoundVolume;
                soundPriority = SoundNotificationService.SoundPriority.Form;

                // §139: "<name> (special form)" on the card until the
                // matcher names the event (OnCounterpartIdentified). Never
                // downgrades an answer that already arrived.
                if (Session.CurrentEncounterForm.Length == 0)
                    Session.CurrentEncounterForm = HuntSession.UnidentifiedForm;
                break;

            default:
                return;
        }

        // See SinceFormSound/SinceShinySound's own declaration comment
        // above. Only reached for a real Shiny/Form case (the switch above
        // already returned for anything else) - always called rather than
        // gated on an on/off flag, since SoundNotificationService.PlaySound
        // already treats "None" (each property's default) as a no-op, and
        // it's defensive about a missing sound file or a playback failure
        // too, so this can't interrupt the stat reset/save below even if
        // something goes wrong.
        // §103: Shiny outranks Form - see SoundNotificationService's
        // priority policy. Playback itself now runs off the UI thread, so
        // this call is a cheap enqueue either way. §152: at the volume the
        // Sound Settings slider set for that sound. §389: through the device
        // it pinned, if that device is present.
        SoundNotificationService.PlaySound(soundToPlay, soundPriority, soundVolume, soundOutputDevice);

        UpdateTrackerDisplay();

        // §138: "Special form detected Lv. 30 Wingull (Vulcan Cove)" - the
        // encounter's own level and map, the same fields the ordinary line
        // uses, so a later level refinement keeps the wording. The tracker
        // used to send a bare "Special form encounter detected: Wingull"
        // from its own thread; it no longer sends anything for this.
        encounterMessagePrefix = rareType == RareEncounterType.Shiny
            ? "Shiny detected"
            : "Special form detected";
        encounterMessageSuffix = string.Empty;

        if (lastEncounterName is not null &&
            string.Equals(lastEncounterName, Session.CurrentEncounter, StringComparison.OrdinalIgnoreCase))
        {
            lastComposedEncounterMessage = ComposeEncounterMessage(lastEncounterName, lastEncounterLevel, lastEncounterLocation);
            StatusMessage = lastComposedEncounterMessage;
        }
        else
        {
            StatusMessage = $"{encounterMessagePrefix} {Session.CurrentEncounter}";
        }

        PersistSession();
    }

    /// <summary>§138. The matcher's answer for the current encounter's
    /// form. Names the counterpart on the status line and in the encounter
    /// history ("Summer Form"), and remembers it for the Catch Log at catch
    /// time. Ignored if the encounter has already moved on - the answer
    /// arrives from a background task and a fast run-away can beat it.</summary>
    private void OnCounterpartIdentified(string species, CounterpartMatcher.MatchResult result)
    {
        if (!Session.IsRunning)
            return;

        // The tracker names the OCR alias; the session holds the resolved
        // species name (RegisterEncounter). Either has to match.
        string resolved = PokemonSpriteService.ResolveEncounterName(species);

        // §139: the answer is for the current encounter only if that
        // encounter has had its own rare event - currentEncounterRareType is
        // set by OnRareEncounterDetected, which the tracker always raises
        // before it starts the matcher - and the species matches. Without
        // the first condition an ordinary Wingull that followed a Summer
        // Wingull would inherit the late answer meant for its predecessor.
        bool forCurrentEncounter =
            currentEncounterRareType != RareEncounterType.None &&
            (string.Equals(species, Session.CurrentEncounter, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(resolved, Session.CurrentEncounter, StringComparison.OrdinalIgnoreCase));

        if (!forCurrentEncounter)
        {
            // §139: the encounter can have moved on before the background
            // match returned. If it is the Previous card now and still
            // waiting for a name, the card gets it. Nothing else does - the
            // status line and the catch-time form belong to the current
            // encounter.
            if (result.Identified && result.EventName is { Length: > 0 } lateName &&
                Session.PreviousEncounterForm == HuntSession.UnidentifiedForm &&
                (string.Equals(species, Session.PreviousEncounter, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(resolved, Session.PreviousEncounter, StringComparison.OrdinalIgnoreCase)))
            {
                Session.PreviousEncounterForm = lateName;
                Session.PreviousEncounterFormImage = result.ImagePath ?? string.Empty;
                UpdateTrackerDisplay();

                PersistSession();
            }

            return;
        }

        if (result.Identified && result.EventName is { Length: > 0 } eventName)
        {
            currentEncounterFormName = eventName;
            encounterMessageSuffix = $" - {eventName} {species}";

            SessionEncounterHistoryService.RefineCurrentRareType(eventName + " Form");

            // §139: from here the Current Encounter card shows the
            // counterpart's own sprite, labelled "<event> <name>", and hands
            // both to the Previous card at the next encounter. A shiny keeps
            // its shiny card - that is the rarer news of the two.
            if (Session.CurrentEncounterForm != HuntSession.ShinyForm)
            {
                Session.CurrentEncounterForm = eventName;
                Session.CurrentEncounterFormImage = result.ImagePath ?? string.Empty;
                UpdateTrackerDisplay();

                PersistSession();
            }
        }
        else
        {
            encounterMessageSuffix = result.SavedCropPath is null
                ? " - form not identified"
                : " - form not identified (sprite saved for the library)";
        }

        RecomposeEncounterMessage();
    }

    // Unlike RegisterEncounter/OnCatchResultDetected/OnRareEncounterDetected
    // above, this deliberately does NOT gate on huntSession.IsRunning - it
    // just reflects whatever RouteDetector last CONFIRMED, with no counters
    // to protect from a stale/late event during shutdown. Since §102 the
    // event only ever fires with a catalog-confirmed map name, and carries
    // whether a wild battle was in progress when that corner frame was read.
    private void OnCornerInfoDetected(string? routeName, bool battleActive)
    {
        if (string.IsNullOrWhiteSpace(routeName))
            return;

        CurrentRouteText = routeName;

        // §278: the map the table follows. No-ops when the map has not
        // actually changed, which is most of the time - RouteDetector confirms
        // the same corner over and over while the player stands still - and
        // no-ops entirely in classic mode and in an isolated session.
        if (TrackerSettings.PerMapEncounterTable)
            MapEncounterService.SetCurrentMap(routeName);

        if (battleActive)
        {
            // §102: the player cannot move during a battle, so a map name
            // confirmed WHILE this encounter's battle is still running is
            // that encounter's true location - even when its record was
            // created seconds earlier with the previous map still current
            // (the evidence log's Seadra: recorded "Route 11" at 17:48:26,
            // "Vermilion City" confirmed at 17:48:30). The tracker side
            // suppresses this flag until the encounter's own events have
            // fired, so it can never relabel the PREVIOUS record.
            SessionEncounterHistoryService.RefineCurrentLocation(routeName);

            // §103: the status line follows the same correction.
            UpdateEncounterMessage(location: routeName);
        }
        else
        {
            // If the current encounter registered before any route reading
            // existed, this first reading afterwards is where it happened -
            // fill the blank in on its history record (§99). Never overwrites
            // a known location - see the service's own guard.
            SessionEncounterHistoryService.RefineCurrentLocationIfUnknown(routeName);
        }
    }

    /// <summary>§278. Where the front table's four facts come from - the
    /// hunt's own dictionaries in classic mode, the current map's in per-map
    /// mode. Falling back to the session's when no map is confirmed yet is
    /// deliberate: the table is not blank while the tracker waits for a corner
    /// reading, it just is not partitioned yet.</summary>
    private IReadOnlyDictionary<string, int> EncounterCountsSource =>
        MapTally?.EncounterCounts ?? Session.EncounterCounts;

    private IReadOnlyDictionary<string, int> CaughtCountsSource =>
        MapTally?.CaughtCounts ?? Session.CaughtCounts;

    private IReadOnlyDictionary<string, int> RanFromCountsSource =>
        MapTally?.RanFromCounts ?? Session.RanFromCounts;

    private IReadOnlyDictionary<string, DateTime> LastEncounteredSource =>
        MapTally?.LastEncounteredUtc ?? Session.LastEncounteredUtc;

    /// <summary>The map tally the table should read, or null whenever the
    /// table should read the hunt - classic mode, or per-map mode before any
    /// map has been confirmed.</summary>
    private Models.MapEncounterTally? MapTally =>
        TrackerSettings.PerMapEncounterTable ? MapEncounterService.Current : null;

    /// <summary>§278. What the table is a table OF. Empty in classic mode, so
    /// the heading reads as it always has; the map's name in per-map mode, and
    /// a plain note while no map has been confirmed yet - because a table that
    /// silently shows the whole hunt under a per-map setting would be the
    /// worst of both.</summary>
    [ObservableProperty] private string encounterTableScope = string.Empty;

    /// <summary>§278. The map changed, or the mode did. Both mean the table is
    /// now a table of something else.</summary>
    private void OnMapEncountersChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            EncounterPageIndex = 0;
            UpdateSessionEncounters();
        });
    }

    private void UpdateSessionEncounters()
    {
        EncounterTableScope =
            !TrackerSettings.PerMapEncounterTable ? string.Empty
            : MapEncounterService.Current is { } tally ? $"on {tally.MapName}"
            : "waiting for a map reading";

        // Replaces ResetEncounterTable/UpdateSessionEncounters, which manually
        // built TableLayoutPanel rows. MainWindow.axaml binds directly to these
        // collections instead - split across two columns the same way the
        // original continued into a second SessionEncountersTableRight panel
        // once the left one filled up.
        SessionEncounters.Clear();

        // §278: the same table, from whichever tally the mode names. Classic
        // is the hunt's own dictionaries, exactly as before; per map is the
        // map being stood on. Everything below this - the ordering, the rate,
        // the paging - is identical either way, which is the point: one table,
        // two sources, and no second rendering path to keep in step.
        var counts = EncounterCountsSource;
        var caughtCounts = CaughtCountsSource;
        var ranCounts = RanFromCountsSource;
        var lastSeen = LastEncounteredSource;

        int total = counts.Values.Sum();

        foreach (var kvp in counts.OrderByDescending(k => k.Value))
        {
            SessionEncounters.Add(new EncounterCountRow
            {
                PokemonName = kvp.Key,
                Count = kvp.Value,
                RatePercent = total > 0 ? kvp.Value / (double)total * 100.0 : 0,
                // §425. GetEncounterSprite, not GetSprite: the table's names
                // are encounter names, and an encounter can be a regional
                // form - "Zorua-Hisui", "Linoone-Galarian" - which lives in
                // the forms dictionary GetSprite never opens. Every regional
                // row drew blank here while its species drew fine.
                Sprite = PokemonSpriteService.GetEncounterSprite(kvp.Key),

                // §125. TryGetValue rather than an indexer: a species with
                // no catches and no runs simply has no entry, and that is
                // the common case - most of a hunt is encounters you did
                // nothing about.
                CaughtCount =
                    caughtCounts.TryGetValue(kvp.Key, out int caught)
                        ? caught
                        : 0,

                RanFromCount =
                    ranCounts.TryGetValue(kvp.Key, out int ran)
                        ? ran
                        : 0,

                LastEncounteredUtc =
                    lastSeen.TryGetValue(kvp.Key, out DateTime seen)
                        ? seen
                        : null
            });
        }

        // §125. Recompute the page count, then keep the reader where they
        // were if that page still exists. Snapping back to page one on every
        // encounter would make the pager unusable while hunting - a new
        // species appears every few seconds.
        EncounterPageCount =
            Math.Max(
                1,
                (SessionEncounters.Count + EncounterPageSize - 1) / EncounterPageSize);

        if (EncounterPageIndex >= EncounterPageCount)
            EncounterPageIndex = EncounterPageCount - 1;

        FillEncounterPage();
    }

    private void UpdateTrackerDisplay()
    {
        // ClientNamesService falls back to "Client N" on its own when no
        // custom name has been set, so this reads exactly as before for
        // anyone who hasn't renamed anything.
        string clientText = selectedClientNumber > 0
            ? $" - {ClientNamesService.GetDisplayName(selectedClientNumber)}"
            : string.Empty;

        string targetsText = string.Join(", ", Session.TargetPokemons);

        // §101: the unobtrusive-but-unmissable Admin Client indicator - the
        // title carries it everywhere (taskbar included) without covering
        // any hunting UI.
        // §250: keyed on the reason now that there are two. ADMIN MODE was
        // the §101 indicator; WORLD QUEST is the same idea for the same
        // purpose - unmissable in the taskbar, covering no hunting UI.
        string modePrefix = IsolatedSession.Reason switch
        {
            IsolationReason.Admin => "ADMIN MODE - ",
            IsolationReason.WorldQuest => "WORLD QUEST - ",
            _ => string.Empty,
        };

        WindowTitle = Session.IsRunning && Session.TargetPokemons.Count > 0
            ? $"{modePrefix}Pro Tracker & Database - Hunting {targetsText}{clientText}"
            : $"{modePrefix}Pro Tracker & Database{clientText}";

        TotalEncounters = DisplayNumber.Count(Session.TotalEncounters);
        TargetedEncountersFound = DisplayNumber.Count(Session.GetTargetedEncounterCount());
        TargetedPokemonCaught = DisplayNumber.Count(Session.GetTargetedCaughtCount());
        TargetedPokemonFled = DisplayNumber.Count(Session.GetTargetedRanFromCount());
        TimeHunting = TimeFormatHelper.FormatElapsed(Session.GetCurrentElapsedTime());
        SinceShiny = DisplayNumber.Count(Session.EncountersSinceShiny);
        SinceForm = DisplayNumber.Count(Session.EncountersSinceForm);

        SinceFormPaused = Session.SinceFormPaused;
        SinceFormPauseButtonText = Session.SinceFormPaused ? "Resume Since Form" : "Pause Since Form";

        // §364: Catch Rate was computed here from SuccessfulCatches and
        // FailedCatches and shown as its own stat. It is gone at the user's
        // request - the two numbers it was derived from are both still on the
        // panel, so nothing was lost that was not already visible.
        SuccessfulCatches = DisplayNumber.Count(Session.SuccessfulCatches);
        FailedCatches = DisplayNumber.Count(Session.FailedCatches);

        // Up to 4 targets shown side by side - sprite size shrinks once there are
        // more than 2, so 3-4 targets still fit comfortably in the same area.
        CurrentTargets.Clear();

        if (Session.TargetPokemons.Count == 0)
        {
            CurrentTargets.Add(new TargetDisplayItem("None", null));
        }
        else
        {
            // §361: the same species can hold more than one slot now, so each
            // one is drawn with ITS OWN picture rather than the species'. The
            // occurrence is how many slots of this species came before - see
            // TargetSpriteService.SkinFor. Counted here rather than asked for,
            // because this loop is the only place that knows the order.
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string name in Session.TargetPokemons)
            {
                seen.TryGetValue(name, out int occurrence);
                seen[name] = occurrence + 1;

                // §271: the sprite the player chose for this slot, which is
                // the species' ordinary one until they choose otherwise.
                CurrentTargets.Add(new TargetDisplayItem(
                    name, TargetSpriteService.SpriteFor(name, occurrence)));
            }
        }

        // ============================================================
        // §367. HOW BIG A TARGET SPRITE IS.
        // ============================================================
        //
        // Users hunting three or four Pokemon asked for bigger sprites now
        // that the window is wider. Both numbers below are the largest that
        // fit the space there actually is, which is a smaller space than it
        // looks:
        //
        //   window width                     1010
        //   - the main Grid's Margin=16        -32
        //   - the stats panel                 -220
        //   - the content Grid's Margin        -32
        //   = the sprite row                   726
        //   / three even columns               242   <- this is the budget
        //
        // A card is TargetLabelMaxWidth wide (the StackPanel in
        // MainWindow.axaml binds its Width to it) plus its 5px margin each
        // side, so N of them need N * (label + 10) and that has to stay
        // inside 242:
        //
        //   2 targets   label 110   card 120   240   fits
        //   3 targets   label  70   card  80   240   fits
        //   4 targets   label  50   card  60   240   fits
        //
        // Which is why 3 goes to 70 and 4 to 50 rather than the 90 and 64
        // the mockups drew: those need a 300px column, and taking 58px from
        // somewhere means the Current and Previous Encounter headings move.
        // Keeping the three columns even was the choice; nothing else on the
        // window shifts by a pixel.
        //
        // The trade at four targets is the label. It used to get 70 (the
        // sprite's 50 plus 20) because it sat in a 2x2 grid with room to
        // spare; in one row of four it gets 50, so a long name wraps onto a
        // second line where it did not before. That is the cost of the row,
        // and it is a cost this comment would rather name than have somebody
        // rediscover. Two type icons are 23 each plus 2 of spacing = 48, so
        // they still fit a 50px card.
        //
        // These are derived for the window at its default width with the
        // stats panel showing - the tightest case. Hide the panel or widen
        // the window and the columns grow; the cards keep these sizes and
        // simply sit in more space.
        TargetSpriteSize =
            Session.TargetPokemons.Count <= 2 ? 90
            : Session.TargetPokemons.Count == 3 ? 70
            : 50;

        // One and two targets keep the old +20 for names. Three and four
        // spend every pixel of it on the sprite instead, which is what was
        // asked for.
        TargetLabelMaxWidth =
            Session.TargetPokemons.Count <= 2
                ? TargetSpriteSize + 20
                : TargetSpriteSize;

        if (Session.TargetPokemons.Count == 0)
        {
            PrimaryTargetSprite = null;
            PrimaryTargetLabel = "None";
            PrimaryTargetTypes = Array.Empty<string>();
        }
        else
        {
            // §271: CompactWindow's single sprite follows the same choice -
            // it is the same target, shown smaller.
            PrimaryTargetSprite = TargetSpriteService.SpriteFor(Session.TargetPokemons[0]);
            PrimaryTargetLabel = Session.TargetPokemons.Count > 1
                ? $"{Session.TargetPokemons[0]} +{Session.TargetPokemons.Count - 1}"
                : Session.TargetPokemons[0];
            PrimaryTargetTypes = PokemonSpriteService.GetTypes(Session.TargetPokemons[0]);
        }

        // §139: the sprite and label follow the encounter's form - the
        // counterpart image for a named event, the shiny sprite for a shiny,
        // the species sprite otherwise. Every branch is cached, so the
        // once-a-second refresh still costs a dictionary lookup.
        CurrentEncounterSprite = EncounterCardSprite(
            Session.CurrentEncounter, Session.CurrentEncounterForm, Session.CurrentEncounterFormImage);
        PreviousEncounterSprite = EncounterCardSprite(
            Session.PreviousEncounter, Session.PreviousEncounterForm, Session.PreviousEncounterFormImage);
        CurrentEncounterTypes = PokemonSpriteService.GetTypes(Session.CurrentEncounter);
        PreviousEncounterTypes = PokemonSpriteService.GetTypes(Session.PreviousEncounter);

        CurrentEncounteredLabel = EncounterCardLabel(Session.CurrentEncounter, Session.CurrentEncounterForm);
        PreviouslyEncounteredLabel = EncounterCardLabel(Session.PreviousEncounter, Session.PreviousEncounterForm);
    }

    /// <summary>§139. The image for an encounter card: the identified
    /// counterpart's own sprite when there is one, the shiny sprite for a
    /// shiny (the species sprite when the library has no shinies), the
    /// species sprite for everything else - including a special form the
    /// matcher could not name, whose label says so instead.</summary>
    private static Bitmap? EncounterCardSprite(string name, string form, string formImage)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (formImage.Length > 0)
        {
            Bitmap? counterpart = CounterpartSpriteService.GetCardSprite(formImage);

            if (counterpart is not null)
                return counterpart;
        }

        return form == HuntSession.ShinyForm
            ? PokemonSpriteService.GetShinyEncounterSprite(name)
            : PokemonSpriteService.GetEncounterSprite(name);
    }

    /// <summary>§139. "Wingull", "Shiny Wingull", "Summer Wingull" or
    /// "Wingull (special form)" - and "None" for an empty slot, as before.</summary>
    private static string EncounterCardLabel(string name, string form)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "None";

        if (string.IsNullOrEmpty(form))
            return name;

        if (form == HuntSession.ShinyForm)
            return "Shiny " + name;

        if (form == HuntSession.UnidentifiedForm)
            return name + " (special form)";

        return form + " " + name;
    }

    // ============================================================
    // §101 - RECOVERY HOOKS AND TRACKING WATCHDOG
    // ============================================================

    /// <summary>Admin Console "Restart Tracking Pipeline" and the watchdog's
    /// shared restart: awaits the old loop's genuine end (bounded) before
    /// starting a new one, so two detector loops can never coexist.</summary>
    private async Task<string> RestartTrackingPipelineAsync()
    {
        if (!encounterTracker.IsRunning)
            return "Tracking is not running - nothing to restart.";

        try
        {
            await encounterTracker.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // The loop is wedged inside a native call (most plausibly a hung
            // OCR Process). Starting a second loop against the same detector
            // state is the one forbidden move - report instead.
            TrackerDiagnostics.SetState("Faulted");
            TrackerDiagnostics.RecordError("Restart timed out - tracking loop is wedged; an app restart is needed");
            return "The tracking loop did not stop within 10s (likely wedged in OCR). Not starting a second loop - restart the app.";
        }

        encounterTracker.Start();
        return "Tracking pipeline restarted - old loop stopped cleanly first.";
    }

    /// <summary>Admin Console "Reacquire Game Window": re-runs PRO window
    /// discovery and rebinds capture, without touching client assignment or
    /// any hunting data.</summary>
    private string ReacquireGameWindow()
    {
        var clients = captureService.FindClientWindows("PROClient");

        if (clients.Count == 0)
            return "No PROClient window was found to bind.";

        captureService.SelectWindow(clients[0].Handle);
        return $"Capture rebound to the first of {clients.Count} PROClient window(s).";
    }

    /// <summary>The §101 tracking watchdog. Fires every 20s; only ever acts
    /// when tracking claims to be running but the per-scan heartbeat
    /// (TrackerDiagnostics.RecordScan, stamped BEFORE each scan) has been
    /// silent for 45s - many times the longest legitimate scan-plus-delay -
    /// so quiet routes, paused hunts, boss/PVP standdowns and plain "no
    /// encounters" can never look like stalls. Recovery is the same
    /// stop-await-start path the console button uses, capped at three per
    /// hour before declaring Faulted, and always single-flight.</summary>
    private async Task WatchdogTickAsync()
    {
        if (watchdogRecovering || !encounterTracker.IsRunning)
            return;

        DateTime lastScan = TrackerDiagnostics.LastScanUtc;

        if (lastScan == DateTime.MinValue)
            return;

        TimeSpan sinceScan = DateTime.UtcNow - lastScan;

        if (sinceScan < TimeSpan.FromSeconds(45))
            return;

        // Classify before acting, with the capture facts the pipeline left.
        string reason =
            TrackerDiagnostics.LastCaptureFailUtc > TrackerDiagnostics.LastCaptureOkUtc
                ? $"capture failing ({TrackerDiagnostics.LastCaptureError})"
                : "scan loop silent (wedged task or swallowed fault)";

        if (DateTime.UtcNow - watchdogWindowStartUtc > TimeSpan.FromHours(1))
        {
            watchdogWindowStartUtc = DateTime.UtcNow;
            watchdogRecoveryCount = 0;
        }

        if (watchdogRecoveryCount >= 3)
        {
            TrackerDiagnostics.SetState("Faulted");
            StatusMessage = "Tracking watchdog: repeated stalls this hour - see the Admin Console Status tab.";
            return;
        }

        watchdogRecovering = true;

        try
        {
            TrackerDiagnostics.SetState("Recovering");
            TrackerDiagnostics.RecordError($"Watchdog: no scan for {sinceScan.TotalSeconds:F0}s - {reason}");
            Log.Warning("Tracking watchdog engaging: no scan for {Seconds:F0}s - {Reason}", sinceScan.TotalSeconds, reason);

            string outcome = await RestartTrackingPipelineAsync();

            watchdogRecoveryCount++;
            StatusMessage = $"Tracking watchdog: {reason} - {outcome}";
        }
        catch (Exception ex)
        {
            TrackerDiagnostics.SetState("Faulted");
            TrackerDiagnostics.RecordError($"Watchdog recovery failed: {ex.Message}");
            Log.Error(ex, "Tracking watchdog recovery failed");
            StatusMessage = $"Tracking watchdog could not recover: {ex.Message}";
        }
        finally
        {
            watchdogRecovering = false;
        }
    }

    /// <summary>Call from MainWindow's Closing event - replaces OnFormClosing.</summary>
    public void OnClosing()
    {
        if (huntSession.IsRunning)
            huntSession.Pause();

        // §101: the in-memory admin session just stops existing with the
        // process - pausing keeps its clock honest if OnClosing is followed
        // by anything else this run. Nothing about it is saved, ever.
        if (adminHuntSession.IsRunning)
            adminHuntSession.Pause();

        // §251: the quest session's running stretch is folded into its
        // ElapsedTime the same way, and it IS saved below - §250 persisted
        // it after every mutation but not at shutdown, so closing the app
        // mid-quest lost the hunting time since the last catch.
        if (worldQuestHuntSession.IsRunning)
            worldQuestHuntSession.Pause();

        huntTimer.Stop();
        FlushPendingLifetimeTime();
        SessionPersistenceService.Save(huntSession);

        // §251: while the mode is on, PersistSession routes to the quest
        // file; the normal session was saved on the way in and again just
        // now, unchanged since.
        if (IsolatedSession.Reason == IsolationReason.WorldQuest)
            PersistSession();

        SessionEncounterHistoryService.FlushToDisk();

        // §429: the last post, waited for briefly - the process is going.
        LevelShareService.FlushOnExit();

        // Release this process's claim on its client number (if any) so the
        // next tracker to start doesn't have to wait for the stale-PID check
        // to notice this one is gone - see SessionPersistenceService's
        // client-lock remarks.
        SessionPersistenceService.ReleaseActiveClient();
    }

    public void Dispose()
    {
        watchdogTimer.Stop();
        presenceTimer.Stop();
        activeEventsTimer.Stop();
        worldQuestPollTimer.Stop();
        huntTimer.Tick -= HuntTimer_Tick;
        autoClientDetectionTimer.Stop();
        encounterTracker.Dispose();
        bossCooldownTracker.Dispose();
        pvpTracker.Dispose();
        Quest.Dispose();
    }
}