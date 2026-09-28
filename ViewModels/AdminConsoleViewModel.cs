using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Foot_Tracker.Services.Simulator;
using Foot_Tracker.Tracking;
using Foot_Tracker.Tracking.Capture;
using PokemonSim.Engine.Strategies;
using PokemonSim.Evaluation;
using PokemonSim.Observation;
using PokemonSim.Shadow;
using PokemonSim.Simulation;
using Serilog;

namespace Foot_Tracker.ViewModels;

/// <summary>One OCR-inspector result row - top-level (not nested) so the
/// window's compiled DataTemplate binding stays plain. See
/// AdminConsoleViewModel.RunInspector.</summary>
public sealed class AdminReplayItem
{
    public string Title { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public Bitmap? Overlay { get; init; }
}

/// <summary>§153. One row of the Admin Logins list - top-level for the
/// same compiled-DataTemplate reason as AdminReplayItem. Text is the whole
/// line, precomputed (AdminConsoleViewModel.DescribeAdminLogin).</summary>
public sealed record AdminLoginListItem(AdminLoginInfo Login, string Text);

/// <summary>§347. One row in the Appearances section.</summary>
public sealed record AdminThemeListItem(AdminThemeInfo Theme, string Text);

/// <summary>§397. One row of the Spawns section's published list - the
/// map as the server holds it, and the line that describes it.</summary>
public sealed record AdminSpawnMapItem(SpawnMap Map, string Text);

/// <summary>
/// Backs AdminConsoleWindow (MIGRATION_GUIDE.md §101) - six tabs of
/// diagnostics and safe recovery over the services built for them:
/// TrackerDiagnostics (live status + opt-in recording),
/// TrackerRecoveryService (the two pipeline hooks MainWindowViewModel
/// registers), DiagnosticReplayService (the OCR inspector - production
/// detectors, never a parallel implementation), DataHealthService and
/// SupportBundleService. Everything here is authenticated-only: the window
/// refuses to construct its content when the session has not passed Admin
/// Login this run (IsUnlocked), which is what makes the remembered
/// Admin Client marker safe across restarts - isolation resumes on its own,
/// controls do not.
///
/// The 1-second status refresh runs ONLY while the window is open
/// (StartRefresh/StopRefresh from the window's Opened/Closed) and reads
/// plain fields - production code never pushes events at this class, per
/// the §101 performance contract.
/// </summary>
public sealed partial class AdminConsoleViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public bool IsUnlocked => AdminModeService.IsAuthenticated;

    // ---- §104 section switching ----
    // The console's six sections used to be TabControl tabs; they are plain
    // text buttons now (same look as the main window's Set Target), so the
    // "which section is showing" state lives here instead of in the control.
    // One string rather than six bools: the per-section flags below are
    // derived from it, so two sections can never both think they are active.
    public static readonly string[] SectionNames =
        ["Status", "Recovery", "OCR Inspector", "Diagnostics", "Data Health", "Support Bundle", "Admin Logins", "Active Events", "World Quest", "Appearances", "Pokedex Scraper", "Spawns", "Battle Lab", "AI Evaluation"];

    [ObservableProperty] private string activeSection = "Status";

    public bool IsStatusSection => ActiveSection == "Status";
    public bool IsRecoverySection => ActiveSection == "Recovery";
    public bool IsInspectorSection => ActiveSection == "OCR Inspector";
    public bool IsDiagnosticsSection => ActiveSection == "Diagnostics";
    public bool IsDataHealthSection => ActiveSection == "Data Health";
    public bool IsSupportBundleSection => ActiveSection == "Support Bundle";
    public bool IsAdminLoginsSection => ActiveSection == "Admin Logins";
    public bool IsActiveEventsSection => ActiveSection == "Active Events";
    public bool IsWorldQuestSection => ActiveSection == "World Quest";
    /// <summary>§282. Was "Sprite Scraper". It reads TWO things off the same
    /// Pokedex panel now - the sprite, and the Area list - so it is named for
    /// the panel rather than for one of the two jobs.</summary>
    public bool IsPokedexScraperSection => ActiveSection == "Pokedex Scraper";
    /// <summary>§397. Beside the Pokedex Scraper because it publishes what
    /// the scraper scanned: which maps go on which Spawns page, and every
    /// species the scans name each map for.</summary>
    public bool IsSpawnsSection => ActiveSection == "Spawns";
    /// <summary>§347. Approving happens in Discord, where you already are.
    /// This is the other direction: seeing what is live and taking one back
    /// down, whether it was approved by mistake or approved by someone
    /// else.</summary>
    public bool IsAppearancesSection => ActiveSection == "Appearances";

    public bool IsBattleLabSection => ActiveSection == "Battle Lab";

    /// <summary>§325. Separate from the Battle Lab on purpose. That panel
    /// COLLECTS - every battle is written to the training corpus. This one
    /// MEASURES and records nothing, and the two being one panel is how an
    /// evaluation run would quietly add 60 MB of random play to the corpus
    /// it was supposed to be judging.</summary>
    public bool IsEvaluationSection => ActiveSection == "AI Evaluation";

    partial void OnActiveSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsStatusSection));
        OnPropertyChanged(nameof(IsRecoverySection));
        OnPropertyChanged(nameof(IsInspectorSection));
        OnPropertyChanged(nameof(IsDiagnosticsSection));
        OnPropertyChanged(nameof(IsDataHealthSection));
        OnPropertyChanged(nameof(IsSupportBundleSection));
        OnPropertyChanged(nameof(IsAdminLoginsSection));
        OnPropertyChanged(nameof(IsActiveEventsSection));
        OnPropertyChanged(nameof(IsWorldQuestSection));
        OnPropertyChanged(nameof(IsPokedexScraperSection));
        OnPropertyChanged(nameof(IsSpawnsSection));
        OnPropertyChanged(nameof(IsAppearancesSection));
        OnPropertyChanged(nameof(IsBattleLabSection));
        OnPropertyChanged(nameof(IsEvaluationSection));

        if (IsEvaluationSection)
            RefreshEvaluationModel();

        if (IsActiveEventsSection)
            PrepareActiveEventsSection();

        if (IsPokedexScraperSection)
            PrepareSpriteScraperSection();

        if (IsSpawnsSection)
            PrepareSpawnsSection();

        if (IsBattleLabSection)
        {
            RefreshLabStored();
            RefreshModelStatus();
            RefreshOpponentMemory();
        }
    }

    /// <summary>Bound to each section button with the section name as its
    /// CommandParameter - a null/unknown name is ignored rather than blanking
    /// the console.</summary>
    [RelayCommand]
    private void ShowSection(string? section)
    {
        if (!string.IsNullOrWhiteSpace(section) && Array.IndexOf(SectionNames, section) >= 0)
            ActiveSection = section;
    }

    public string LockedMessage =>
        "Admin Console is locked. Use File > Admin Login first - the Admin Client " +
        "selection is remembered across restarts, but admin controls always need a fresh login.";

    // ---- Status tab ----

    [ObservableProperty] private string trackerState = "Stopped";
    [ObservableProperty] private string dataContextText = "Normal client";
    [ObservableProperty] private string boundWindowText = "-";
    [ObservableProperty] private string lastFrameText = "-";
    [ObservableProperty] private string heartbeatText = "-";
    [ObservableProperty] private string lastOcrText = "-";
    [ObservableProperty] private string lastEncounterText = "-";
    [ObservableProperty] private string lastAcceptedText = "-";
    [ObservableProperty] private string recentErrorsText = "(none)";
    [ObservableProperty] private string healthSummary = "OK";

    // ---- Status tab (§150 active trackers) ----

    // The events server's count of trackers that sent a heartbeat in its
    // window. Read on demand, never automatically: it needs the admin token
    // (asked for once per run through RequestAdminToken, exactly as Create
    // Event does), and one request per press is all it costs.
    [ObservableProperty] private string activeTrackersText =
        EventsSyncService.IsOnline ? "Press Refresh." : "No events server configured.";

    /// <summary>Set by the window - AdminTokenWindow.ShowAsync (§153: a bool -
    /// the window hands the credential to EventsSyncService itself).</summary>
    public Func<Task<bool>>? RequestAdminSignIn { get; set; }

    /// <summary>§399. Set by the window - opens the Map Boxes editor, where
    /// a region's picture gets a box per map.</summary>
    public Action? OpenMapBoxes { get; set; }

    // ---- Admin Logins tab (§153; "Event Logins" until §253) ----

    // The delegated admin sign-ins for the events server. Everything here
    // needs the MASTER token server-side; a delegated login gets the
    // server's own 403 sentence instead, and "Sign In Differently" is the
    // way back to the master prompt.
    public ObservableCollection<AdminLoginListItem> AdminLogins { get; } = new();

    [ObservableProperty] private AdminLoginListItem? selectedAdminLogin;
    [ObservableProperty] private string newLoginUsername = string.Empty;
    [ObservableProperty] private string newLoginPassword = string.Empty;
    [ObservableProperty] private bool newLoginCanViewStatus;

    /// <summary>§347. Two more, and note what changed underneath them:
    /// posting events used to come free with any login. It is a permission
    /// now, so a login created with nothing ticked can do nothing at all -
    /// which is the right default for a credential you hand someone.</summary>
    [ObservableProperty] private bool newLoginCanModerateThemes;
    [ObservableProperty] private bool newLoginCanManageEvents;

    public ObservableCollection<AdminThemeListItem> AdminThemes { get; } = new();

    [ObservableProperty] private AdminThemeListItem? selectedAdminTheme;

    [ObservableProperty] private string appearancesStatus = "Press Refresh.";

    /// <summary>Which pile to list: approved, pending or rejected.</summary>
    [ObservableProperty] private string appearancesState = "approved";

    public IReadOnlyList<string> AppearancesStates { get; } =
        new[] { "approved", "pending", "rejected" };
    [ObservableProperty] private string adminLoginsStatus =
        EventsSyncService.IsOnline ? "Press Refresh List." : "No events server configured.";

    // ---- Sprite Scraper tab (§211) ----

    // Cuts the Pokemon out of the game's own Pokedex panel. The heavy lifting
    // is PokedexSpriteScraper; everything here is the loop around it - grab,
    // look at what came out, name it, save it into the counterpart library.

    [ObservableProperty] private Bitmap? scrapedSprite;
    [ObservableProperty] private bool hasScrapedSprite;
    [ObservableProperty] private string scraperStatus =
        "Open the Pokedex in game with a Pokemon selected, then press Grab.";
    [ObservableProperty] private string scrapeEventName = string.Empty;
    [ObservableProperty] private string scrapePokemonName = string.Empty;

    // ---- §282: the Area list, off the same panel and the same name box ----

    /// <summary>§282. What the last Area scan came to. Its own line rather
    /// than ScraperStatus's, because the two halves of this section run
    /// independently and one reporting over the other would lose whichever
    /// was read first.</summary>
    [ObservableProperty] private string areaScanStatus =
        "Type the Pokemon's name above, then Scan areas - once per screenful.";

    /// <summary>Everything known for the Pokemon in the name box, refreshed
    /// after every scan so the list grows under the reader as they scroll.</summary>
    public ObservableCollection<Models.PokedexSpawn> KnownSpawns { get; } = new();

    [ObservableProperty] private string knownSpawnsSummary = string.Empty;

    // ---- §420: areas typed by hand, for a Pokemon the panel cannot show ----

    // The scanner reads the Pokedex of whoever is logged in, and a Pokedex
    // only lists a species its owner has seen. A spawn added to the game
    // last week, or a Pokemon nobody on this machine owns, therefore cannot
    // be scanned - but its areas are usually known (the announcement names
    // them), and the Spawns pages should not wait on a catch. So the same
    // name box takes a typed list of maps and files them under the species
    // exactly as a scan would, marked as hand-typed so a later scan of the
    // real panel can be seen to confirm or correct them.

    /// <summary>One map per line, or several on a line separated by commas
    /// or semicolons - the shape an announcement lists them in, so a list
    /// can be pasted rather than typed.</summary>
    [ObservableProperty] private string manualMapNames = string.Empty;

    /// <summary>Grass and surf - the two icons a scanned row carries.
    /// Neither ticked means fishing, as it does on the panel (§287).</summary>
    [ObservableProperty] private bool manualLand = true;
    [ObservableProperty] private bool manualWater;

    /// <summary>All three on by default: an announcement rarely says when,
    /// and "all day" is the reading that hides nothing from a hunter.</summary>
    [ObservableProperty] private bool manualMorning = true;
    [ObservableProperty] private bool manualDay = true;
    [ObservableProperty] private bool manualNight = true;

    [ObservableProperty] private bool manualMembersOnly;

    [ObservableProperty] private string manualAreaStatus =
        "For a Pokemon the Pokedex cannot show yet: type its name above, list its maps here, then Add areas.";

    /// <summary>§284. Where scanned areas are written, shown in the panel.
    /// Asked for in as many words; and a scraper whose output the user cannot
    /// find is one they cannot check, back up or delete.</summary>
    public string PokedexStoreLocation => PokedexService.StoreLocation;

    /// <summary>§282. One name box for both halves of the section. Typing a
    /// Pokemon here shows what is already known about it, so a second scan of
    /// the same species is obviously a second scan rather than a fresh
    /// start.</summary>
    partial void OnScrapePokemonNameChanged(string value) => RefreshKnownSpawns();

    partial void OnScrapedSpriteChanged(Bitmap? value) => HasScrapedSprite = value is not null;

    // ---- Spawns tab (§397) ----

    // Files maps under the five pages of Game Data -> Spawns and publishes
    // each map's page - the species this machine's Pokedex scans (§281)
    // name it for - to the events server, where every tracker reads it.
    // Master token only, server-side; the console asks for a sign-in and
    // the server says no to anything else.

    public IReadOnlyList<string> SpawnRegionChoices => SpawnRegions.All;

    [ObservableProperty] private string spawnRegion = SpawnRegions.Kanto;

    /// <summary>The location catalog's names, for the map box to offer as
    /// the admin types. A name the catalog lacks can still be typed.</summary>
    public ObservableCollection<string> SpawnMapChoices { get; } = new();

    [ObservableProperty] private string spawnMapName = string.Empty;

    /// <summary>What Add would publish for the name in the box, before it
    /// is pressed: how many species the scans name the map for, and
    /// whether the map is already published.</summary>
    [ObservableProperty] private string spawnMapPreview = string.Empty;

    public ObservableCollection<AdminSpawnMapItem> PublishedSpawnMaps { get; } = new();

    [ObservableProperty] private AdminSpawnMapItem? selectedPublishedSpawnMap;

    [ObservableProperty] private string spawnsStatus =
        EventsSyncService.IsOnline ? "Press Read Published." : "No events server configured.";

    /// <summary>True while a request is out, so the buttons cannot start a
    /// second one over it - Republish All in particular is a loop.</summary>
    [ObservableProperty] private bool spawnsBusy;

    private bool spawnsSeeded;

    /// <summary>The preview reads every scanned species file to answer,
    /// so it waits for the typing to pause rather than running per key.</summary>
    private readonly DispatcherTimer spawnPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    partial void OnSpawnMapNameChanged(string value)
    {
        spawnPreviewTimer.Stop();
        spawnPreviewTimer.Start();
    }

    /// <summary>The catalog's event folders, so a scrape lands beside the ones
    /// already there rather than in a folder of its own spelling.</summary>
    public ObservableCollection<string> ScrapeEventOptions { get; } = new();

    // ---- Active Events tab (§207) ----

    // Which counterpart events the game is running right now, published once
    // and read by every tracker on its heartbeat. Three slots because three
    // was the ask; the server caps it there as well, so a hand-made request
    // cannot publish more.
    //
    // Publishing needs only an ORDINARY admin login, unlike the tab above -
    // an event starting at 3am should not need the master token to be
    // fetched out of wherever it lives.

    /// <summary>The catalog's event names with a blank first entry, so a
    /// slot can be left empty. Filled when the section is first shown -
    /// CounterpartSpriteService.Load has certainly run by then.</summary>
    public ObservableCollection<string> EventChoices { get; } = new();

    // Nullable on purpose: a ComboBox with nothing selected writes null
    // through SelectedItem, and a non-nullable string would take it anyway
    // and then throw on the first Trim().
    [ObservableProperty] private string? activeEventSlot1;
    [ObservableProperty] private string? activeEventSlot2;
    [ObservableProperty] private string? activeEventSlot3;
    [ObservableProperty] private string? activeEventSlot4;
    [ObservableProperty] private string? activeEventSlot5;
    [ObservableProperty] private string? activeEventSlot6;

    [ObservableProperty] private string activeEventsPublished = "(not read yet)";
    [ObservableProperty] private string activeEventsStatus =
        EventsSyncService.IsOnline ? "Press Read Published." : "No events server configured.";

    // ---- Status tab (§103 Force client) ----

    [ObservableProperty] private string forceClientNumberText = "1";
    [ObservableProperty] private string forceClientStatus = string.Empty;

    // ---- Recovery tab ----

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string recoveryStatus = string.Empty;

    // ---- Inspector tab ----

    public ObservableCollection<AdminReplayItem> ReplayResults { get; } = new();
    [ObservableProperty] private bool showRegionOverlay; // off by default (§101 spec)
    [ObservableProperty] private AdminReplayItem? selectedReplay;

    // ---- Diagnostics tab ----

    [ObservableProperty] private bool recordingEnabled;
    [ObservableProperty] private string recordingStatus = "0 entries retained";

    // ---- Data health / bundle tabs ----

    [ObservableProperty] private string dataHealthReport = "Press Refresh to run the read-only checks.";
    [ObservableProperty] private bool bundleEnvironment = true;
    [ObservableProperty] private bool bundleLogs = true;
    [ObservableProperty] private bool bundleDiagnostics = true;
    [ObservableProperty] private bool bundleDataHealth = true;
    [ObservableProperty] private bool bundleSettings = true;
    [ObservableProperty] private bool bundleHuntingData; // deliberately opt-in
    [ObservableProperty] private string bundleStatus = string.Empty;

    public string BundleCategories => SupportBundleService.DescribeCategories();

    /// <summary>Set by the window - opens the OS file picker for PNGs.</summary>
    public Func<bool, Task<string[]>>? RequestScreenshotPaths { get; set; }

    /// <summary>Set by the window - renders the tracker's own main window to
    /// a PNG in the Diagnostics folder and returns (path, bitmap), or null if
    /// it could not be captured. A view concern (§103 "Screenshot Tracker"):
    /// rendering a Window needs the visual tree, which no service should
    /// hold.</summary>
    public Func<Task<(string Path, Bitmap Image)?>>? RequestTrackerScreenshot { get; set; }

    public AdminConsoleViewModel()
    {
        recordingEnabled = TrackerDiagnostics.RecordingEnabled;
        refreshTimer.Tick += (_, _) => RefreshStatus();

        // §397: the Spawns preview waits for the typing to pause.
        spawnPreviewTimer.Tick += (_, _) =>
        {
            spawnPreviewTimer.Stop();
            RefreshSpawnMapPreview();
        };

        // §103: lock/unlock LIVE with the admin session - an explicit Admin
        // Logout must lock an already-open console immediately, not on its
        // next construction. Static event, instance handler: Dispose
        // unsubscribes (the window calls it on Closed), so a closed console
        // never lingers via the subscription.
        AdminModeService.AuthenticationChanged += OnAuthenticationChanged;
    }

    private void OnAuthenticationChanged()
    {
        OnPropertyChanged(nameof(IsUnlocked));

        if (!AdminModeService.IsAuthenticated)
            refreshTimer.Stop();
        else if (!refreshTimer.IsEnabled)
            StartRefresh();
    }

    partial void OnRecordingEnabledChanged(bool value)
    {
        TrackerDiagnostics.RecordingEnabled = value;
    }

    public void StartRefresh()
    {
        if (!IsUnlocked)
            return;

        RefreshPermissions();
        RefreshStatus();
        refreshTimer.Start();
    }

    // ------------------------------------------------ §348 permissions

    /// <summary>§348. What the credential this run signed in with may do.
    ///
    /// Falls back to LocalOwner - everything - rather than to nothing when
    /// no identity is known. A console that greys itself out because it has
    /// not asked yet is worse than one that lets you click and be told no:
    /// the first looks broken, the second looks like a permission.</summary>
    private static AdminIdentity Identity =>
        EventsSyncService.CurrentIdentity ?? AdminIdentity.LocalOwner;

    public bool MayViewStatus => Identity.CanViewStatus;

    public bool MayManageEvents => Identity.CanManageEvents;

    public bool MayModerateAppearances => Identity.CanModerateThemes;

    /// <summary>Master only - §347's rule, restated where the button is.</summary>
    public bool MayManageLogins => Identity.Master;

    /// <summary>Who the footer says you are.</summary>
    public string SignedInAs =>
        EventsSyncService.CurrentIdentity is null ? "Signed in locally."
        : Identity.Master ? "Signed in with the master token - every section."
        : $"Signed in as {Identity.Username} ({DescribeIdentityPermissions()}).";

    private static string DescribeIdentityPermissions()
    {
        var held = new List<string>();

        if (Identity.CanManageEvents) held.Add("events");
        if (Identity.CanViewStatus) held.Add("status");
        if (Identity.CanModerateThemes) held.Add("appearances");

        return held.Count == 0 ? "no server permissions" : string.Join(" + ", held);
    }

    public void RefreshPermissions()
    {
        OnPropertyChanged(nameof(MayViewStatus));
        OnPropertyChanged(nameof(MayManageEvents));
        OnPropertyChanged(nameof(MayModerateAppearances));
        OnPropertyChanged(nameof(MayManageLogins));
        OnPropertyChanged(nameof(SignedInAs));
    }

    public void StopRefresh() => refreshTimer.Stop();

    private void RefreshStatus()
    {
        DateTime now = DateTime.UtcNow;

        TrackerState = TrackerDiagnostics.StateText;
        DataContextText = AdminModeService.IsActive
            ? "ADMIN CLIENT (isolated in-memory session - normal data write-protected)"
            : $"Normal client {SessionPersistenceService.ActiveClientNumber}";

        BoundWindowText = TrackerDiagnostics.LastFrameWidth > 0
            ? $"PROClient frame {TrackerDiagnostics.LastFrameWidth}x{TrackerDiagnostics.LastFrameHeight}"
            : "(no frame captured yet)";

        LastFrameText = Describe(TrackerDiagnostics.LastCaptureOkUtc, now) +
            (TrackerDiagnostics.LastCaptureFailUtc > TrackerDiagnostics.LastCaptureOkUtc
                ? $" | last failure: {TrackerDiagnostics.LastCaptureError}"
                : string.Empty);

        HeartbeatText = $"scan #{TrackerDiagnostics.ScanCount}, {Describe(TrackerDiagnostics.LastScanUtc, now)}";
        LastOcrText = Describe(TrackerDiagnostics.LastOcrUtc, now);
        LastEncounterText = Describe(TrackerDiagnostics.LastEncounterUtc, now);

        LastAcceptedText = TrackerDiagnostics.LastAcceptedPokemon.Length > 0
            ? $"{TrackerDiagnostics.LastAcceptedPokemon}, {TrackerDiagnostics.LastAcceptedLevel}, {TrackerDiagnostics.LastAcceptedLocation}"
            : "(none this run)";

        var errors = TrackerDiagnostics.GetRecentErrors();
        RecentErrorsText = errors.Count == 0 ? "(none)" : string.Join("\n", errors);

        RecordingStatus = $"{TrackerDiagnostics.RecordingCount} entries retained (cap 120)";

        // Text-first health verdict; the window adds color, never color alone.
        TimeSpan sinceScan = now - TrackerDiagnostics.LastScanUtc;
        HealthSummary = TrackerDiagnostics.StateText != "Tracking"
            ? "Idle - tracking is not running"
            : sinceScan < TimeSpan.FromSeconds(10)
                ? "OK - scan loop is live"
                : $"STALE - no scan for {sinceScan.TotalSeconds:F0}s (watchdog will intervene)";
    }

    private static string Describe(DateTime utc, DateTime now) =>
        utc == DateTime.MinValue ? "never" : $"{(now - utc).TotalSeconds:F1}s ago";

    // ---- Recovery commands ----

    [RelayCommand]
    private async Task RestartTracking()
    {
        if (TrackerRecoveryService.RestartTrackingAsync is null)
            return;

        IsBusy = true;

        try
        {
            RecoveryStatus = await TrackerRecoveryService.RestartTrackingAsync();
        }
        catch (Exception ex)
        {
            RecoveryStatus = $"Restart failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ReacquireWindow()
    {
        RecoveryStatus = TrackerRecoveryService.ReacquireGameWindow?.Invoke()
            ?? "Reacquire hook not available.";
    }

    [RelayCommand]
    private void RestartOcr()
    {
        SharedOcrEngine.Restart();
        RecoveryStatus = "OCR engine disposed - the next read creates a fresh one.";
    }

    [RelayCommand]
    private void ReloadLocationDictionary()
    {
        LocationDictionaryService.Reload();
        RecoveryStatus = "Location dictionary will rebuild from disk on the next read.";
    }

    [RelayCommand]
    private void ReloadPokemonData()
    {
        try
        {
            PokemonSpriteService.Load();
            RecoveryStatus = "Pokemon library and sprite caches reloaded.";
        }
        catch (Exception ex)
        {
            RecoveryStatus = $"Reload failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearDiagnosticCache()
    {
        TrackerDiagnostics.ClearRecording();
        RecoveryStatus = "Diagnostic recording cleared. Hunting data is untouched.";
    }

    /// <summary>§284. Opens the folder the scanned areas live in. One file
    /// per species, shared by every profile on this machine - where a Pokemon
    /// spawns is a fact about the game, not about an account. Failed scans
    /// leave their captured frame in a Diagnostics folder beside them.</summary>
    [RelayCommand]
    private void OpenPokedexFolder()
    {
        try
        {
            Directory.CreateDirectory(PokedexService.StoreLocation);

            Process.Start(new ProcessStartInfo
            {
                FileName = PokedexService.StoreLocation,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AreaScanStatus = $"Could not open the Pokedex folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProTracker", "Logs");

            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RecoveryStatus = $"Could not open the log folder: {ex.Message}";
        }
    }

    /// <summary>The console's textual snapshot for Copy Diagnostic Summary -
    /// the window puts it on the clipboard (a view concern).</summary>
    public string BuildDiagnosticSummary()
    {
        RefreshStatus();

        var summary = new StringBuilder();
        summary.AppendLine($"ProTracker diagnostic summary - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        summary.AppendLine($"State: {TrackerState} | Health: {HealthSummary}");
        summary.AppendLine($"Data context: {DataContextText}");
        summary.AppendLine($"Window: {BoundWindowText}");
        summary.AppendLine($"Heartbeat: {HeartbeatText}");
        summary.AppendLine($"Last frame: {LastFrameText}");
        summary.AppendLine($"Last OCR: {LastOcrText}");
        summary.AppendLine($"Last encounter: {LastEncounterText} ({LastAcceptedText})");
        summary.AppendLine($"Recent errors: {RecentErrorsText}");
        return summary.ToString();
    }

    // ---- Status command (§103) ----

    [RelayCommand]
    private void ForceClient()
    {
        if (TrackerRecoveryService.ForceClientAssign is null)
        {
            ForceClientStatus = "Force-assign hook not available.";
            return;
        }

        if (!int.TryParse(ForceClientNumberText.Trim(), out int clientNumber) || clientNumber < 1 || clientNumber > 9)
        {
            ForceClientStatus = "Enter a client number from 1 to 9.";
            return;
        }

        // Deliberately does NOT leave Admin Client mode - binding a client
        // for diagnostics is about the CAPTURE TARGET, not the data context
        // (the §101 gates keep isolating either way).
        ForceClientStatus = TrackerRecoveryService.ForceClientAssign(clientNumber);
    }

    // ---- Status command (§150) ----

    [RelayCommand]
    private async Task RefreshActiveTrackers()
    {
        if (!EventsSyncService.IsOnline)
        {
            ActiveTrackersText = "No events server configured.";
            return;
        }

        if (!await EnsureAdminSignInAsync())
        {
            ActiveTrackersText = "Needs an events-server admin sign-in - press Refresh to enter it.";
            return;
        }

        ActiveTrackersText = "Asking the events server...";

        try
        {
            PresenceSummary summary = await EventsSyncService.FetchActiveTrackersAsync();

            ActiveTrackersText = DescribePresence(summary);
        }
        catch (EventsSyncException ex)
        {
            if (ex.Unauthorized)
                EventsSyncService.ForgetAdminCredentials();

            ActiveTrackersText = ex.Unauthorized
                ? "The events server rejected the sign-in - press Refresh to sign in again."
                : $"Could not read it - {ex.Message}.";
        }
    }

    /// <summary>§151. The live count on the first line, the week underneath:
    /// its peak (local time), the average whenever anyone is hunting, how
    /// much of the covered time anyone was, and the three busiest hours of
    /// the day in local time - the answer to "am I looking at a good time".
    /// A Worker without samples (or older than §151) gets the first line
    /// and a note.</summary>
    internal static string DescribePresence(PresenceSummary summary, DateTime? localNow = null, TimeSpan? utcOffset = null)
    {
        DateTime now = localNow ?? DateTime.Now;
        TimeSpan offset = utcOffset ?? TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);

        var lines = new List<string>
        {
            summary.ActiveTrackers == 1
                ? $"1 tracker hunting now (last {summary.WindowMinutes} minutes, as of {now:HH:mm})."
                : $"{summary.ActiveTrackers} trackers hunting now (last {summary.WindowMinutes} minutes, as of {now:HH:mm})."
        };

        // §231: which builds those trackers are running. Directly under the
        // count, because it is the same sentence read a second way - and the
        // only place that answers "is anyone actually updating".
        if (summary.Versions.Count > 0)
        {
            lines.Add(
                "Versions: " +
                string.Join(
                    ", ",
                    summary.Versions.Select(v => $"{v.Version} x{v.Trackers}")));
        }

        PresenceHistory? h = summary.History;

        if (h is null || h.SampleCount == 0 || h.PeakTrackers is null || h.PeakAtUtc is null)
        {
            lines.Add("Last 7 days: no history yet - it builds from heartbeats once the section 151 Worker is deployed.");
            return string.Join(Environment.NewLine, lines);
        }

        string span = h.CoveredDays >= h.Days - 0.05
            ? $"Last {h.Days} days"
            : $"Last {h.CoveredDays:0.#} days (all the history there is so far)";

        DateTime peakLocal = h.PeakAtUtc.Value + offset;
        int activePercent = (int)Math.Round(100.0 * h.ActiveBuckets / Math.Max(1, h.CoveredBuckets));

        lines.Add(
            $"{span}: peak {h.PeakTrackers} ({peakLocal:ddd HH:mm}); average {h.AverageWhenActive:0.#} whenever anyone is hunting; " +
            $"someone hunting {activePercent}% of the time.");

        // Hour-of-day averages, shifted from UTC into local hours (whole
        // hours - a half-hour zone lands on the nearer hour).
        if (h.ByHourUtc.Count == 24)
        {
            int shift = (int)Math.Round(offset.TotalHours);
            var local = new double[24];

            for (int hourUtc = 0; hourUtc < 24; hourUtc++)
                local[((hourUtc + shift) % 24 + 24) % 24] = h.ByHourUtc[hourUtc];

            string busiest = string.Join(", ",
                Enumerable.Range(0, 24)
                    .Where(hour => local[hour] > 0)
                    .OrderByDescending(hour => local[hour])
                    .ThenBy(hour => hour)
                    .Take(3)
                    .Select(hour => $"{hour:00}:00 (avg {local[hour]:0.#})"));

            lines.Add(busiest.Length == 0
                ? "Busiest hours: none yet."
                : $"Busiest hours (your local time): {busiest}.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<bool> EnsureAdminSignInAsync()
    {
        if (EventsSyncService.HasAdminCredentials)
            return true;

        if (RequestAdminSignIn is null)
            return false;

        return await RequestAdminSignIn();
    }

    // ---- Admin Logins commands (§153) ----

    private static readonly Regex AdminLoginNamePattern = new("^[A-Za-z0-9._-]{3,24}$");

    /// <summary>§153, §347. "bob  (events + appearances; created 1 Sep 2026;
    /// last used 1 Sep 18:40)" - the whole row as one line, local time. The
    /// permissions come from DescribePermissions above; this example was
    /// updated with it, because a doc comment showing output the code cannot
    /// produce is worse than none.</summary>
    /// <summary>§347. The permissions a login actually holds, listed.
    ///
    /// This used to read "events only" or "events + status", which was true
    /// while every login could post events. §347 made that a permission of
    /// its own, so a login can now hold none of them - and a sentence that
    /// says "events only" about a login that cannot post events is worse
    /// than no sentence at all.</summary>
    internal static string DescribePermissions(AdminLoginInfo login)
    {
        var held = new List<string>();

        if (login.CanManageEvents) held.Add("events");
        if (login.CanViewStatus) held.Add("status");
        if (login.CanModerateThemes) held.Add("appearances");

        return held.Count == 0 ? "no permissions yet" : string.Join(" + ", held);
    }

    internal static string DescribeAdminLogin(AdminLoginInfo login)
    {
        string permission = DescribePermissions(login);
        string lastUsed = login.LastUsedUtc is null
            ? "never used"
            : $"last used {login.LastUsedUtc.Value.ToLocalTime():d MMM HH:mm}";

        return $"{login.Username}  ({permission}; created {login.CreatedUtc.ToLocalTime():d MMM yyyy}; {lastUsed}){(login.Revoked ? " - REVOKED" : "")}";
    }

    [RelayCommand]
    private async Task RefreshAdminLogins()
    {
        if (!await PrepareAdminLoginsCallAsync())
            return;

        AdminLoginsStatus = "Asking the events server...";

        try
        {
            int count = await FillAdminLoginsAsync();

            AdminLoginsStatus = count == 0
                ? "No admin logins yet - create the first below."
                : $"{count} admin login{(count == 1 ? "" : "s")}.";
        }
        catch (EventsSyncException ex)
        {
            HandleAdminLoginsError(ex);
        }
    }

    [RelayCommand]
    private async Task SaveAdminLogin()
    {
        string username = NewLoginUsername.Trim();

        if (!AdminLoginNamePattern.IsMatch(username))
        {
            AdminLoginsStatus = "Username: 3-24 letters, digits, dots, dashes or underscores.";
            return;
        }

        // The server never sees the password, so only its length can be
        // checked - and only here.
        if (NewLoginPassword.Length < 8)
        {
            AdminLoginsStatus = "Password: at least 8 characters.";
            return;
        }

        if (!await PrepareAdminLoginsCallAsync())
            return;

        AdminLoginsStatus = "Saving...";

        try
        {
            string password = NewLoginPassword;
            string verifier = await Task.Run(() => EventsSyncService.DeriveLoginVerifier(username, password));
            AdminLoginInfo saved = await EventsSyncService.SaveAdminLoginAsync(
                username,
                verifier,
                NewLoginCanViewStatus,
                NewLoginCanModerateThemes,
                NewLoginCanManageEvents);

            NewLoginPassword = string.Empty;
            await FillAdminLoginsAsync();

            AdminLoginsStatus =
                $"\"{saved.Username}\" is ready ({DescribePermissions(saved)}) - " +
                "hand that person the username and the password you chose. Posting the same name again resets it.";
        }
        catch (EventsSyncException ex)
        {
            HandleAdminLoginsError(ex);
        }
    }

    [RelayCommand]
    private async Task RevokeSelectedAdminLogin()
    {
        AdminLoginListItem? selected = SelectedAdminLogin;

        if (selected is null)
        {
            AdminLoginsStatus = "Select a login in the list first.";
            return;
        }

        if (!await PrepareAdminLoginsCallAsync())
            return;

        AdminLoginsStatus = "Revoking...";

        try
        {
            AdminLoginInfo revoked = await EventsSyncService.RevokeAdminLoginAsync(selected.Login.Id);
            await FillAdminLoginsAsync();

            AdminLoginsStatus = $"\"{revoked.Username}\" is revoked and stops working immediately. Posting the name again would re-enable it with a new password.";
        }
        catch (EventsSyncException ex)
        {
            HandleAdminLoginsError(ex);
        }
    }

    /// <summary>§153. Drops whatever credential this run holds (a
    /// remembered login included) and asks fresh - the way from a delegated
    /// login back to the master token, for this tab and the Status tab
    /// alike.</summary>
    [RelayCommand]
    private async Task SwitchAdminSignIn()
    {
        EventsSyncService.ForgetAdminCredentials();

        AdminLoginsStatus = RequestAdminSignIn is not null && await RequestAdminSignIn()
            ? "Signed in - press Refresh List."
            : "Not signed in.";
    }

    // ------------------------------------------------- §347 appearances

    private async Task<bool> PrepareAppearancesCallAsync()
    {
        if (!EventsSyncService.IsOnline)
        {
            AppearancesStatus = "No events server configured.";
            return false;
        }

        if (!await EnsureAdminSignInAsync())
        {
            AppearancesStatus =
                "Sign in with the master token, or with a login that can moderate appearances.";
            return false;
        }

        return true;
    }

    [RelayCommand]
    private async Task RefreshAppearances()
    {
        if (!await PrepareAppearancesCallAsync())
            return;

        AppearancesStatus = "Loading...";
        armedThemeId = null;

        try
        {
            IReadOnlyList<AdminThemeInfo> themes =
                await EventsSyncService.FetchAdminThemesAsync(AppearancesState);

            AdminThemes.Clear();

            foreach (AdminThemeInfo theme in themes)
                AdminThemes.Add(new AdminThemeListItem(theme, DescribeAdminTheme(theme)));

            AppearancesStatus = themes.Count == 0
                ? $"Nothing {AppearancesState}."
                : $"{themes.Count} {AppearancesState} appearance{(themes.Count == 1 ? "" : "s")}.";
        }
        catch (EventsSyncException ex)
        {
            AppearancesStatus = ex.Message;
        }
    }

    /// <summary>§347. Which appearance the next Remove press will actually
    /// delete.
    ///
    /// Removing is permanent - the row goes and so does the image in R2 -
    /// and there is no dialog here because a ViewModel has no TopLevel to
    /// hang one from (the same reason the file pickers live in code-behind,
    /// see AppearanceWindow). So the confirmation is the button itself:
    /// the first press arms one specific id, the second press removes it,
    /// and selecting a different row or refreshing disarms.</summary>
    private string? armedThemeId;

    [RelayCommand]
    private async Task RemoveSelectedAppearance()
    {
        AdminThemeListItem? selected = SelectedAdminTheme;

        if (selected is null)
        {
            AppearancesStatus = "Select an appearance in the list first.";
            return;
        }

        if (armedThemeId != selected.Theme.Id)
        {
            armedThemeId = selected.Theme.Id;
            AppearancesStatus =
                $"Press Remove again to delete \"{selected.Theme.Name}\" permanently. " +
                "This cannot be undone.";
            return;
        }

        if (!await PrepareAppearancesCallAsync())
            return;

        AppearancesStatus = "Removing...";

        try
        {
            await EventsSyncService.DeleteAdminThemeAsync(selected.Theme.Id);

            armedThemeId = null;
            AdminThemes.Remove(selected);
            SelectedAdminTheme = null;

            AppearancesStatus = $"\"{selected.Theme.Name}\" is gone.";
        }
        catch (EventsSyncException ex)
        {
            armedThemeId = null;
            AppearancesStatus = ex.Message;
        }
    }

    partial void OnSelectedAdminThemeChanged(AdminThemeListItem? value)
    {
        // Moving the selection cancels an arming - otherwise a second press
        // meant for a different row would delete the one armed before it.
        if (armedThemeId is not null && value?.Theme.Id != armedThemeId)
        {
            armedThemeId = null;
            AppearancesStatus = "Removal cancelled.";
        }
    }

    partial void OnAppearancesStateChanged(string value)
    {
        armedThemeId = null;
        AdminThemes.Clear();
        SelectedAdminTheme = null;
        AppearancesStatus = "Press Refresh.";
    }

    internal static string DescribeAdminTheme(AdminThemeInfo theme)
    {
        string author = string.IsNullOrWhiteSpace(theme.Author) ? "no author given" : "by " + theme.Author;
        string image = string.IsNullOrWhiteSpace(theme.ImageUrl) ? "colours only" : "has a picture";

        return $"{theme.Name}  ({author}; {image}; tracker {theme.Tracker}; " +
               $"{theme.SubmittedUtc.ToLocalTime():d MMM yyyy HH:mm})";
    }

    private async Task<bool> PrepareAdminLoginsCallAsync()
    {
        if (!EventsSyncService.IsOnline)
        {
            AdminLoginsStatus = "No events server configured.";
            return false;
        }

        if (!await EnsureAdminSignInAsync())
        {
            AdminLoginsStatus = "Managing logins needs the MASTER admin token - sign in with it to continue.";
            return false;
        }

        return true;
    }

    private async Task<int> FillAdminLoginsAsync()
    {
        IReadOnlyList<AdminLoginInfo> logins = await EventsSyncService.FetchAdminLoginsAsync();

        AdminLogins.Clear();

        foreach (AdminLoginInfo login in logins)
            AdminLogins.Add(new AdminLoginListItem(login, DescribeAdminLogin(login)));

        return logins.Count;
    }

    private void HandleAdminLoginsError(EventsSyncException ex)
    {
        if (ex.Unauthorized)
            EventsSyncService.ForgetAdminCredentials();

        // A 403 lands here with the server's own sentence - "only the master
        // admin token can manage admin logins" - which is exactly the answer.
        AdminLoginsStatus = ex.Unauthorized
            ? "The events server rejected the sign-in - try again to sign in."
            : $"Could not do it - {ex.Message}.";
    }

    // ---- Sprite Scraper commands (§211) ----

    /// <summary>The 120x120 canvas the last grab produced, kept so Save writes
    /// exactly what was shown rather than scraping a second time off a frame
    /// the player has since changed.</summary>
    private byte[]? scrapedCanvasPng;

    private void PrepareSpriteScraperSection()
    {
        if (ScrapeEventOptions.Count > 0)
            return;

        // §213: EventNames, not AllVariants. A category declared in the
        // catalog with no sprites yet has to be pickable, or a new event can
        // never be started - scraping into it was how it was meant to stop
        // being empty.
        foreach (string name in CounterpartSpriteService.EventNames
                     .Where(evt => !string.IsNullOrWhiteSpace(evt))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(evt => evt, StringComparer.OrdinalIgnoreCase))
        {
            ScrapeEventOptions.Add(name);
        }
    }

    /// <summary>
    /// §282. Reads every Area row on screen and merges it into this Pokemon's
    /// spawn record - see Tracking/PokedexAreaReader and PokedexService.
    ///
    /// The Area list scrolls, so a species that spawns in sixty places takes
    /// several passes: scroll a few rows in the client and scan again.
    /// Overlapping passes cost nothing, because the store merges by map name
    /// and a row already known with the same facts is counted rather than
    /// written - which is why the status line reports what was already known
    /// as well as what was new.
    ///
    /// The species is the name box above, typed rather than read off the
    /// panel. Its name IS on screen, but far from the Area block, and reading
    /// it would be a second OCR that could quietly file sixty areas under the
    /// wrong Pokemon - a mistake invisible in the result.
    ///
    /// Not gated on the Admin Client: where a Pokemon spawns is a fact about
    /// the game, not about an account, so PokedexService keeps one record for
    /// the machine and nothing here touches a client's hunting data.
    /// </summary>
    [RelayCommand]
    private async Task ScanPokedexAreas()
    {
        string species = ScrapePokemonName.Trim();

        if (species.Length == 0)
        {
            AreaScanStatus = "Type the Pokemon's name first - it is what the areas are filed under.";
            return;
        }

        IsBusy = true;
        AreaScanStatus = "Reading the Area list...";

        try
        {
            // §284. The captured bytes come back out with the reading. A
            // scan that reads nothing needs the frame it read nothing FROM,
            // and that frame exists for a few milliseconds inside this lambda
            // and nowhere else - reasoning about a screenshot the user pasted
            // instead cost this section an entire round trip and answered the
            // wrong question, because a pasted desktop screenshot is not the
            // frame the window capture produces.
            (PokedexScanReading? reading, PokedexMergeResult? merged, byte[]? captured) =
                await Task.Run(() =>
            {
                byte[]? png = WindowCaptureServiceFactory.Instance.CaptureSelectedWindowPng();

                if (png is null)
                    return ((PokedexScanReading?)null, (PokedexMergeResult?)null, (byte[]?)null);

                using SKBitmap? frame = ImageOps.DecodePng(png);

                if (frame is null)
                    return ((PokedexScanReading?)null, (PokedexMergeResult?)null, png);

                PokedexScanReading read = PokedexAreaReader.Read(frame);

                return (read, PokedexService.Merge(species, read.Spawns), png);
            });

            if (reading is null)
            {
                AreaScanStatus =
                    "The client could not be captured - the same bound window the sprite grab uses.";
                return;
            }

            if (reading.RowsFound == 0)
            {
                // §284. Say what was actually seen, and keep the frame. The
                // old message named one cause - "open the Pokedex" - of three
                // that produce this outcome, so a user whose Pokedex WAS open
                // was told something plainly untrue and had nowhere to go.
                string? saved = captured is null
                    ? null
                    : PokedexService.SaveDiagnosticFrame(captured);

                AreaScanStatus =
                    "No Area rows were read. " + reading.Diagnosis
                    + (saved is null
                        ? string.Empty
                        : $" The frame it captured was saved to {saved}.");

                return;
            }

            string skipped = reading.RowsUnread == 0
                ? string.Empty
                : $" {DisplayNumber.Count(reading.RowsUnread)} row(s) were partly off screen and skipped.";

            // §421: a regional form typed the way the game says it is filed
            // under the library's name, and the status says so once.
            string filed = PokedexService.Canonical(species);
            string filedAs = string.Equals(filed, species, StringComparison.Ordinal)
                ? string.Empty
                : $" Filed as {filed}.";

            AreaScanStatus = $"{species}: {merged!.Summary}{filedAs}{skipped}";

            RefreshKnownSpawns();
        }
        catch (Exception ex)
        {
            AreaScanStatus = $"The area scan failed - {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshKnownSpawns()
    {
        KnownSpawns.Clear();

        string species = ScrapePokemonName.Trim();

        if (species.Length == 0)
        {
            KnownSpawnsSummary = string.Empty;
            return;
        }

        Models.PokedexEntry? entry = PokedexService.Load(species);

        if (entry is null)
        {
            KnownSpawnsSummary = "No areas scanned for this Pokemon yet.";
            return;
        }

        foreach (Models.PokedexSpawn spawn in entry.Spawns)
            KnownSpawns.Add(spawn);

        int members = entry.Spawns.Count(s => s.MembersOnly);

        int manual = entry.Spawns.Count(s => s.Manual);

        KnownSpawnsSummary =
            $"{DisplayNumber.Count(entry.Spawns.Count)} "
            + $"{(entry.Spawns.Count == 1 ? "area" : "areas")} known"
            + (members == 0 ? string.Empty : $", {DisplayNumber.Count(members)} needing membership")
            + (manual == 0 ? string.Empty : $", {DisplayNumber.Count(manual)} typed by hand")
            + ".";
    }

    /// <summary>
    /// §420. Files the typed maps under the Pokemon in the name box, through
    /// the same merge a scan uses - so a map already known is corrected
    /// rather than duplicated, and adding the same list twice changes
    /// nothing.
    ///
    /// Each name is passed through LocationDictionaryService first, the
    /// bounded matcher the scanner's own OCR reads go through (§286): a
    /// catalog spelling wins over a typed one ("Mt Pyre 1F" is filed as
    /// "Mt. Pyre 1F"), so the row lands on the same Spawns page a scan of it
    /// would. A name the catalog does not know is kept as typed, for the
    /// same reason the scanner keeps one - the announcement is usually
    /// ahead of the catalog. Every correction is named in the status line,
    /// because a matcher that rewrites silently is one that cannot be
    /// checked.
    ///
    /// Not gated on the Admin Client, as the scan is not: where a Pokemon
    /// spawns is a fact about the game.
    /// </summary>
    [RelayCommand]
    private void AddManualAreas()
    {
        string species = ScrapePokemonName.Trim();

        if (species.Length == 0)
        {
            ManualAreaStatus = "Type the Pokemon's name first - it is what the areas are filed under.";
            return;
        }

        List<string> typed = ManualMapNames
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (typed.Count == 0)
        {
            ManualAreaStatus = "List at least one map - one per line, or separated by commas.";
            return;
        }

        DateTime now = DateTime.UtcNow;
        var rows = new List<Models.PokedexSpawn>(typed.Count);
        var corrected = new List<string>();

        foreach (string name in typed)
        {
            string filedAs = LocationDictionaryService.TryMatch(name) ?? name;

            if (!string.Equals(filedAs, name, StringComparison.Ordinal))
                corrected.Add($"{name} → {filedAs}");

            rows.Add(new Models.PokedexSpawn
            {
                MapName = filedAs,
                Land = ManualLand,
                Water = ManualWater,
                Morning = ManualMorning,
                Day = ManualDay,
                Night = ManualNight,
                MembersOnly = ManualMembersOnly,
                Manual = true,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
        }

        try
        {
            PokedexMergeResult merged = PokedexService.Merge(species, rows);

            string corrections = corrected.Count == 0
                ? string.Empty
                : $" Spelled as the catalog does: {string.Join("; ", corrected)}.";

            // §421: the library's name for a regional form is what the
            // record is filed under, and what the Map Explorer will answer to.
            string filed = PokedexService.Canonical(species);
            string filedAs = string.Equals(filed, species, StringComparison.Ordinal)
                ? string.Empty
                : $" Filed as {filed} - the library's name for it.";

            ManualAreaStatus = $"{species}: {merged.Summary}{filedAs}{corrections}";

            ManualMapNames = string.Empty;

            RefreshKnownSpawns();
        }
        catch (Exception ex)
        {
            ManualAreaStatus = $"The areas could not be added - {ex.Message}";
        }
    }

    /// <summary>§420. Takes one area off the Pokemon in the name box - the
    /// undo for a map typed wrong. Works on scanned rows too, because a
    /// scan can misread a name (§286 catches most, not all), and the
    /// alternative was editing the JSON by hand.</summary>
    [RelayCommand]
    private void RemoveKnownSpawn(Models.PokedexSpawn? spawn)
    {
        if (spawn is null)
            return;

        string species = ScrapePokemonName.Trim();

        if (species.Length == 0)
            return;

        ManualAreaStatus = PokedexService.Remove(species, spawn.MapName)
            ? $"{species}: {spawn.MapName} removed."
            : $"{species}: {spawn.MapName} was not in the record.";

        RefreshKnownSpawns();
    }

    /// <summary>Captures the bound client and cuts. Nothing is written here -
    /// the result is shown first, because two frames of evidence is not the
    /// same as a guarantee and a bad cut should cost a second look, not a bad
    /// file in the library.</summary>
    [RelayCommand]
    private async Task GrabSprite()
    {
        IsBusy = true;
        ScraperStatus = "Capturing the client...";

        try
        {
            PokedexSpriteScraper.ScrapeResult result = await Task.Run(() =>
            {
                byte[]? png = WindowCaptureServiceFactory.Instance.CaptureSelectedWindowPng();

                if (png is null || png.Length == 0)
                    return new PokedexSpriteScraper.ScrapeResult
                    {
                        Summary = "No PRO client window is bound or capturable right now."
                    };

                using SKBitmap? frame = ImageOps.DecodePng(png);

                return frame is null
                    ? new PokedexSpriteScraper.ScrapeResult { Summary = "The capture could not be decoded." }
                    : PokedexSpriteScraper.Scrape(frame);
            });

            ScrapedSprite?.Dispose();
            ScrapedSprite = null;
            scrapedCanvasPng = null;

            if (result.Found && result.Canvas is not null)
            {
                scrapedCanvasPng = ImageOps.EncodePng(result.Canvas);
                ScrapedSprite = new Bitmap(new MemoryStream(scrapedCanvasPng));

                result.Sprite?.Dispose();
                result.Canvas.Dispose();
            }

            ScraperStatus = result.Summary;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sprite scrape failed");
            ScraperStatus = $"The scrape failed - {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes the canvas into the counterpart library under the event
    /// folder and name given. Refuses to overwrite: a scrape that lands on an
    /// existing name is far more likely to be a mistake than an intended
    /// replacement, and the existing file may be the one §138 was measured
    /// against.</summary>
    [RelayCommand]
    private void SaveScrapedSprite()
    {
        if (scrapedCanvasPng is null)
        {
            ScraperStatus = "Nothing has been grabbed yet.";
            return;
        }

        string eventName = ScrapeEventName.Trim();
        string pokemonName = ScrapePokemonName.Trim();

        if (eventName.Length == 0 || pokemonName.Length == 0)
        {
            ScraperStatus = "Choose an event and type the Pokemon's name first.";
            return;
        }

        if (eventName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            pokemonName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ScraperStatus = "That event or name has characters a folder cannot hold.";
            return;
        }

        try
        {
            // §213: the folder is the event name with its spaces removed -
            // AprilFools, BidoofDay, FIFAWorldCup. §211 used the spaced name
            // here, so the first scrape into any multi-word event would have
            // made a second folder the catalog was not looking at.
            string folder = Path.Combine(
                AppContext.BaseDirectory, "SharedPokemonLibrary", "Assets", "Counterparts",
                CounterpartSpriteService.FolderFor(eventName));

            Directory.CreateDirectory(folder);

            string path = Path.Combine(folder, pokemonName + ".png");

            if (File.Exists(path))
            {
                ScraperStatus = $"{CounterpartSpriteService.FolderFor(eventName)}/{pokemonName}.png already exists - rename it or delete it first. Nothing was written.";
                return;
            }

            File.WriteAllBytes(path, scrapedCanvasPng);

            ScraperStatus =
                $"Saved to Counterparts/{CounterpartSpriteService.FolderFor(eventName)}/{pokemonName}.png. Add it to counterparts.json and restart for the matcher to use it.";

            Log.Information("Sprite scraper: wrote {Path}", path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sprite scraper: could not save");
            ScraperStatus = $"It could not be saved - {ex.Message}";
        }
    }

    // ---- Active Events commands (§207) ----

    private bool activeEventsSeeded;

    /// <summary>Opens the section: the dropdowns get the catalog's event
    /// names, and the slots get whatever this tracker already believes so
    /// the tab is not blank before the first Read.</summary>
    private void PrepareActiveEventsSection()
    {
        FillEventChoices();

        if (activeEventsSeeded)
            return;

        activeEventsSeeded = true;

        ActiveEvents cached = ActiveEventService.Current;

        LoadSlotsFrom(cached);
        ShowPublished(cached, fromServer: false);
    }

    /// <summary>Adds any catalog event name the dropdown does not have yet.
    /// Deliberately never clears: a name the server carries but the catalog
    /// does not still has to be selectable, or the next Publish would drop
    /// it without saying so.</summary>
    private void FillEventChoices()
    {
        if (EventChoices.Count == 0)
            EventChoices.Add(string.Empty);

        // §213: EventNames, not AllVariants. A category declared in the
        // catalog with no sprites yet has to be pickable, or a new event can
        // never be started - scraping into it was how it was meant to stop
        // being empty.
        foreach (string name in CounterpartSpriteService.EventNames
                     .Where(evt => !string.IsNullOrWhiteSpace(evt))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(evt => evt, StringComparer.OrdinalIgnoreCase))
        {
            EnsureChoice(name);
        }
    }

    /// <summary>Makes sure a name is in the dropdown, and hands back the
    /// list's own spelling of it - a ComboBox can only select an item it
    /// actually holds.</summary>
    private string EnsureChoice(string? name)
    {
        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return string.Empty;

        string? existing = EventChoices.FirstOrDefault(
            choice => string.Equals(choice, trimmed, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
            return existing;

        EventChoices.Add(trimmed);

        return trimmed;
    }

    private void LoadSlotsFrom(ActiveEvents published)
    {
        ActiveEventSlot1 = EnsureChoice(published.Events.ElementAtOrDefault(0));
        ActiveEventSlot2 = EnsureChoice(published.Events.ElementAtOrDefault(1));
        ActiveEventSlot3 = EnsureChoice(published.Events.ElementAtOrDefault(2));
        ActiveEventSlot4 = EnsureChoice(published.Events.ElementAtOrDefault(3));
        ActiveEventSlot5 = EnsureChoice(published.Events.ElementAtOrDefault(4));
        ActiveEventSlot6 = EnsureChoice(published.Events.ElementAtOrDefault(5));
    }

    private void ShowPublished(ActiveEvents published, bool fromServer)
    {
        if (!published.Any)
        {
            ActiveEventsPublished = fromServer
                ? "Nothing published - every tracker considers every event skin, exactly as it did before this setting existed."
                : "Nothing known yet - press Read Published.";
            return;
        }

        string when = string.Empty;

        if (DateTime.TryParse(
                published.UpdatedUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime utc))
        {
            string by = string.IsNullOrWhiteSpace(published.UpdatedBy)
                ? string.Empty
                : " by " + published.UpdatedBy;

            when = $" (set {utc.ToLocalTime():d MMM HH:mm}{by})";
        }

        ActiveEventsPublished =
            (fromServer ? "Running: " : "Last known here: ") + string.Join(", ", published.Events) + when;
    }

    /// <summary>Reads the published list. No sign-in: every tracker reads
    /// this route anonymously, which is the whole point of it.</summary>
    [RelayCommand]
    private async Task ReadActiveEvents()
    {
        if (!EventsSyncService.IsOnline)
        {
            ActiveEventsStatus = "No events server configured.";
            return;
        }

        ActiveEventsStatus = "Asking the events server...";

        try
        {
            ActiveEvents published = await EventsSyncService.FetchActiveEventsAsync();

            ActiveEventService.Apply(published);
            LoadSlotsFrom(published);
            ShowPublished(published, fromServer: true);

            ActiveEventsStatus = "Read from the server.";
        }
        catch (EventsSyncException ex)
        {
            ActiveEventsStatus = $"Could not read it - {ex.Message}.";
        }
    }

    [RelayCommand]
    private Task PublishActiveEvents() =>
        SendActiveEvents(
            new[]
            {
                ActiveEventSlot1, ActiveEventSlot2, ActiveEventSlot3,
                ActiveEventSlot4, ActiveEventSlot5, ActiveEventSlot6
            },
            "Published. Every tracker picks it up on its next heartbeat, within about five minutes.");

    /// <summary>Publishes an empty list. Not the same as "no events are
    /// running": it puts every tracker back to considering every event skin,
    /// which is what an install that never saw this setting does.</summary>
    [RelayCommand]
    private Task ClearActiveEvents() =>
        SendActiveEvents(
            Array.Empty<string>(),
            "Cleared. Trackers go back to considering every event skin.");

    private async Task SendActiveEvents(IEnumerable<string?> wanted, string success)
    {
        if (!EventsSyncService.IsOnline)
        {
            ActiveEventsStatus = "No events server configured.";
            return;
        }

        // An ordinary admin login is enough here, unlike the Admin Logins
        // tab - an event that starts at 3am should not need the master token
        // fetched out of wherever it lives.
        if (!await EnsureAdminSignInAsync())
        {
            ActiveEventsStatus = "Publishing needs an admin sign-in.";
            return;
        }

        ActiveEventsStatus = "Publishing...";

        try
        {
            ActiveEvents saved = await EventsSyncService.SaveActiveEventsAsync(
                wanted.Select(slot => slot?.Trim() ?? string.Empty)
                      .Where(slot => slot.Length > 0));

            // The machine that published it should not wait for its own
            // heartbeat to start using it.
            ActiveEventService.Apply(saved);

            LoadSlotsFrom(saved);
            ShowPublished(saved, fromServer: true);

            ActiveEventsStatus = success;
        }
        catch (EventsSyncException ex)
        {
            if (ex.Unauthorized)
                EventsSyncService.ForgetAdminCredentials();

            ActiveEventsStatus = ex.Unauthorized
                ? "The events server rejected the sign-in - try again to sign in."
                : $"Could not publish it - {ex.Message}.";
        }
    }

    // ---- Spawns commands (§397) ----

    /// <summary>Opens the section: the map box gets the catalog's names,
    /// and the list gets whatever this tracker already holds so the tab is
    /// not blank before the first Read.</summary>
    private void PrepareSpawnsSection()
    {
        if (SpawnMapChoices.Count == 0)
        {
            foreach (CatalogLocation location in LocationCatalogService.All)
                SpawnMapChoices.Add(location.Name);
        }

        if (spawnsSeeded)
            return;

        spawnsSeeded = true;

        ShowPublishedSpawns(SpawnDataService.Current, fromServer: false);
        RefreshSpawnMapPreview();
    }

    /// <summary>The line under the map box. A catalog name also SUGGESTS
    /// the page - Route 201 to Sinnoh, Vulcan Cove to Other - which the
    /// dropdown can then be changed from; a name the catalog lacks leaves
    /// the dropdown as it is.</summary>
    private void RefreshSpawnMapPreview()
    {
        string name = SpawnMapName.Trim();

        if (name.Length == 0)
        {
            SpawnMapPreview = "Type or pick a map, choose its page, then Add.";
            return;
        }

        if (LocationCatalogService.Find(name) is CatalogLocation location)
            SpawnRegion = SpawnRegions.ForCatalogRegion(location.Region);

        SpawnMap page = SpawnDataService.BuildFromPokedex(SpawnRegion, name);
        SpawnMap? published = SpawnDataService.Find(name);

        string already = published is null
            ? string.Empty
            : $" Already published under {published.Region} with {published.PokemonCountText}.";

        if (page.Pokemon.Count == 0)
        {
            SpawnMapPreview =
                $"No Pokémon has been scanned for {name} yet - it can still be added, and republished once the " +
                "Pokedex scans name it." + already;
            return;
        }

        string names = string.Join(", ", page.Pokemon.Take(8).Select(p => p.Name));

        if (page.Pokemon.Count > 8)
            names += $", and {page.Pokemon.Count - 8} more";

        SpawnMapPreview = $"{page.PokemonCountText} scanned for {name}: {names}.{already}";
    }

    private void ShowPublishedSpawns(IReadOnlyList<SpawnMap> maps, bool fromServer)
    {
        string? selectedKey = SelectedPublishedSpawnMap?.Map.Key;

        PublishedSpawnMaps.Clear();

        foreach (SpawnMap map in maps
                     .OrderBy(m => Array.IndexOf(SpawnRegions.All.ToArray(), m.Region))
                     .ThenBy(m => m.Map, StringComparer.OrdinalIgnoreCase))
        {
            PublishedSpawnMaps.Add(new AdminSpawnMapItem(map, DescribeSpawnMap(map)));
        }

        SelectedPublishedSpawnMap = PublishedSpawnMaps.FirstOrDefault(item => item.Map.Key == selectedKey);

        if (maps.Count == 0)
        {
            SpawnsStatus = fromServer
                ? "Nothing published yet - add a map above."
                : "Nothing known here yet - press Read Published.";
            return;
        }

        int pages = maps.Select(m => m.Region).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        string mapWord = maps.Count == 1 ? "map" : "maps";
        string pageWord = pages == 1 ? "page" : "pages";

        SpawnsStatus = (fromServer ? "Published: " : "Last known here: ")
            + $"{DisplayNumber.Count(maps.Count)} {mapWord} across {pages} {pageWord}.";
    }

    /// <summary>"Kanto · Route 1 · 12 Pokémon · 2 boxes · 3 Sep 14:02 by master" -
    /// the boxes (§399, §402: any number) drawn for it on the region picture.</summary>
    private static string DescribeSpawnMap(SpawnMap map)
    {
        string when = map.UpdatedUtc is DateTime utc
            ? $" · {utc.ToLocalTime():d MMM HH:mm}" + (string.IsNullOrWhiteSpace(map.UpdatedBy) ? string.Empty : " by " + map.UpdatedBy)
            : string.Empty;

        int boxes = map.Markers.Count(m => m.IsValid);
        string box = boxes == 0 ? string.Empty : boxes == 1 ? " · 1 box" : $" · {boxes} boxes";

        return $"{map.Region} · {map.Map} · {map.PokemonCountText}{box}{when}";
    }

    /// <summary>§399. The Map Boxes editor - the region picture, zoomable,
    /// with a box drawn per map. Not a sign-in step itself; the editor asks
    /// when it publishes.</summary>
    [RelayCommand]
    private void ShowMapBoxes() => OpenMapBoxes?.Invoke();

    /// <summary>Reads the published list. No sign-in: every tracker reads
    /// this route anonymously, which is the whole point of it.</summary>
    [RelayCommand]
    private async Task ReadPublishedSpawns()
    {
        if (!EventsSyncService.IsOnline)
        {
            SpawnsStatus = "No events server configured.";
            return;
        }

        if (SpawnsBusy)
            return;

        SpawnsBusy = true;
        SpawnsStatus = "Asking the events server...";

        try
        {
            IReadOnlyList<SpawnMap> published = await EventsSyncService.FetchSpawnMapsAsync();

            SpawnDataService.Apply(published, DateTime.UtcNow);
            ShowPublishedSpawns(published, fromServer: true);
        }
        catch (EventsSyncException ex)
        {
            SpawnsStatus = DescribeQuestFailure(ex, "read the list");
        }
        finally
        {
            SpawnsBusy = false;
        }
    }

    /// <summary>Files the map in the box under the chosen page and
    /// publishes its page - Add and Republish are the same request; the
    /// server replaces whatever it held for the map.</summary>
    [RelayCommand]
    private Task AddSpawnMap() => PublishSpawnMap(SpawnRegion, SpawnMapName);

    /// <summary>Publishes the selected map again from this machine's scans
    /// - the way scans made since the map was added reach players.</summary>
    [RelayCommand]
    private Task RepublishSpawnMap() =>
        SelectedPublishedSpawnMap is AdminSpawnMapItem item
            ? PublishSpawnMap(item.Map.Region, item.Map.Map)
            : Task.CompletedTask;

    private async Task PublishSpawnMap(string region, string name)
    {
        string map = name.Trim();

        if (map.Length == 0 || SpawnMap.KeyFor(map).Length == 0)
        {
            SpawnsStatus = "Type or pick a map first.";
            return;
        }

        if (!EventsSyncService.IsOnline)
        {
            SpawnsStatus = "No events server configured.";
            return;
        }

        if (SpawnsBusy)
            return;

        if (!await EnsureAdminSignInAsync())
        {
            SpawnsStatus = "Publishing needs the master admin token.";
            return;
        }

        SpawnsBusy = true;
        SpawnsStatus = $"Publishing {map}...";

        try
        {
            SpawnMap page = SpawnDataService.BuildFromPokedex(region, map);
            SpawnMap saved = await EventsSyncService.SaveSpawnMapAsync(page);

            // The machine that published it should see the page at once.
            SpawnDataService.ApplyOne(saved);
            ShowPublishedSpawns(SpawnDataService.Current, fromServer: true);
            SelectedPublishedSpawnMap = PublishedSpawnMaps.FirstOrDefault(item => item.Map.Key == saved.Key);
            RefreshSpawnMapPreview();

            SpawnsStatus = saved.Pokemon.Count == 0
                ? $"Published {saved.Map} under {saved.Region} with no Pokémon yet - scan the Pokedex for the species that live there, then Republish."
                : $"Published {saved.Map} under {saved.Region} with {saved.PokemonCountText}. Every tracker shows it the next time a Spawns page opens.";
        }
        catch (EventsSyncException ex)
        {
            if (ex.Unauthorized)
                EventsSyncService.ForgetAdminCredentials();

            SpawnsStatus = DescribeQuestFailure(ex, $"publish {map}");
        }
        finally
        {
            SpawnsBusy = false;
        }
    }

    /// <summary>Takes the selected map off every tracker's page. The scans
    /// on this machine are untouched - Add brings it back.</summary>
    [RelayCommand]
    private async Task RemoveSpawnMap()
    {
        if (SelectedPublishedSpawnMap is not AdminSpawnMapItem item)
        {
            SpawnsStatus = "Select a published map first.";
            return;
        }

        if (!EventsSyncService.IsOnline)
        {
            SpawnsStatus = "No events server configured.";
            return;
        }

        if (SpawnsBusy)
            return;

        if (!await EnsureAdminSignInAsync())
        {
            SpawnsStatus = "Removing needs the master admin token.";
            return;
        }

        SpawnsBusy = true;
        SpawnsStatus = $"Removing {item.Map.Map}...";

        try
        {
            await EventsSyncService.DeleteSpawnMapAsync(item.Map.Key);

            SpawnDataService.RemoveOne(item.Map.Key);
            ShowPublishedSpawns(SpawnDataService.Current, fromServer: true);
            RefreshSpawnMapPreview();

            SpawnsStatus = $"Removed {item.Map.Map} from the {item.Map.Region} page.";
        }
        catch (EventsSyncException ex)
        {
            if (ex.Unauthorized)
                EventsSyncService.ForgetAdminCredentials();

            // A 404 here is the map already being gone, not an old Worker -
            // the list may be stale; a Read Published sorts it out.
            SpawnsStatus = ex.StatusCode == 404
                ? $"{item.Map.Map} was not on the server any more - press Read Published."
                : DescribeQuestFailure(ex, $"remove {item.Map.Map}");
        }
        finally
        {
            SpawnsBusy = false;
        }
    }

    /// <summary>Every published map again, from this machine's scans, in
    /// one go - after a scanning session, so every page catches up. Stops
    /// at the first failure and says where.</summary>
    [RelayCommand]
    private async Task RepublishAllSpawnMaps()
    {
        IReadOnlyList<SpawnMap> maps = SpawnDataService.Current;

        if (maps.Count == 0)
        {
            SpawnsStatus = "Nothing published to republish - press Read Published first.";
            return;
        }

        if (!EventsSyncService.IsOnline)
        {
            SpawnsStatus = "No events server configured.";
            return;
        }

        if (SpawnsBusy)
            return;

        if (!await EnsureAdminSignInAsync())
        {
            SpawnsStatus = "Publishing needs the master admin token.";
            return;
        }

        SpawnsBusy = true;

        int done = 0;
        int changed = 0;

        try
        {
            // The store is read once for the whole batch, not once per map.
            IReadOnlyList<PokedexEntry> scans = PokedexService.All();

            foreach (SpawnMap map in maps)
            {
                SpawnsStatus = $"Republishing {map.Map} ({done + 1} of {maps.Count})...";

                SpawnMap page = SpawnDataService.BuildFromPokedex(map.Region, map.Map, scans);
                SpawnMap saved = await EventsSyncService.SaveSpawnMapAsync(page);

                if (saved.Pokemon.Count != map.Pokemon.Count)
                    changed++;

                SpawnDataService.ApplyOne(saved);
                done++;
            }

            ShowPublishedSpawns(SpawnDataService.Current, fromServer: true);
            RefreshSpawnMapPreview();

            string mapWord = done == 1 ? "map" : "maps";
            string pageWord = changed == 1 ? "page" : "pages";

            SpawnsStatus = $"Republished {DisplayNumber.Count(done)} {mapWord}; "
                + $"{DisplayNumber.Count(changed)} {pageWord} changed size.";
        }
        catch (EventsSyncException ex)
        {
            if (ex.Unauthorized)
                EventsSyncService.ForgetAdminCredentials();

            ShowPublishedSpawns(SpawnDataService.Current, fromServer: true);

            SpawnsStatus = DescribeQuestFailure(ex, $"republish everything - stopped after {done} of {maps.Count}");
        }
        finally
        {
            SpawnsBusy = false;
        }
    }

    // ---- World Quest test commands (§234) ----

    /// <summary>The share of the community goal that earns the first
    /// Mysterious Ticket, and the share that earns the second. The same two
    /// numbers the Worker uses (see QUEST_FIRST_TIER_SHARE) and the same ones
    /// the World Quest window derives its second tier from. Checked against
    /// the August quest: it printed a goal of 148,800 and a per-player figure
    /// of 744, and 148800 * 0.005 is exactly 744.
    ///
    /// They are here only to PREVIEW the tiers while the goal is being typed.
    /// What the console reports after starting a quest is the Worker's own
    /// answer, so the two can never be seen to disagree.</summary>
    private const double QuestFirstTierShare = 0.005;
    private const double QuestSecondTierShare = 0.03;

    /// <summary>The goal has to fit what the Worker accepts, and a goal of
    /// zero is not a quest.</summary>
    private const int QuestTestMaxIvs = 100_000_000;

    /// <summary>§234. The tracker and the Worker ship separately, so a client
    /// built for a route the deployed Worker has never heard of gets a flat
    /// 404 whose message ("No such route.") is perfectly true and completely
    /// unhelpful - it does not say which half is behind. Every route this
    /// section added is new, so a 404 from one of them has exactly one cause,
    /// and saying so beats making someone guess.</summary>
    private const string WorkerOutOfDateHint =
        "the events server is running an older build - redeploy worker.js, then try again";

    private static string DescribeQuestFailure(EventsSyncException ex, string what)
    {
        if (ex.Unauthorized)
            return "The events server rejected the sign-in - try again to sign in.";

        return ex.StatusCode == 404
            ? $"Could not {what} - {WorkerOutOfDateHint}."
            : $"Could not {what} - {ex.Message}.";
    }

    [ObservableProperty] private string testQuestPokemon = string.Empty;
    [ObservableProperty] private string testQuestTotalIvsText = string.Empty;
    [ObservableProperty] private string testQuestTiers = string.Empty;

    [ObservableProperty] private string testQuestStatus =
        EventsSyncService.IsOnline
            ? "Fill in a Pokemon and a goal, then Start."
            : "No events server configured.";

    partial void OnTestQuestTotalIvsTextChanged(string value) => RefreshTestQuestTiers();

    /// <summary>The live preview under the goal box - ceiling, not rounding,
    /// because "at least 0.5%" means the first whole IV total AT or above the
    /// share. A goal small enough for the share to fall below one IV still
    /// needs one, or the tier reads as met before anything is submitted.</summary>
    private void RefreshTestQuestTiers()
    {
        if (!TryReadTestQuestGoal(out int goal))
        {
            TestQuestTiers = string.Empty;
            return;
        }

        int first = Math.Max(1, (int)Math.Ceiling(goal * QuestFirstTierShare));
        int second = Math.Max(1, (int)Math.Ceiling(goal * QuestSecondTierShare));

        TestQuestTiers =
            $"1st Mysterious Ticket at {first:#,0} IVs (0.5%), 2nd at {second:#,0} (3%). Runs 24 hours.";
    }

    private bool TryReadTestQuestGoal(out int goal) =>
        int.TryParse(TestQuestTotalIvsText.Replace(",", string.Empty).Trim(), out goal)
        && goal > 0
        && goal <= QuestTestMaxIvs;

    /// <summary>§234. Starts a World Quest of your own so the World Quest
    /// window can be exercised on a day PRO is not running one. It is stored
    /// exactly where a real quest goes and nothing downstream can tell the
    /// difference, so this tests the real path rather than a stand-in.</summary>
    [RelayCommand]
    private async Task StartTestQuest()
    {
        if (!EventsSyncService.IsOnline)
        {
            TestQuestStatus = "No events server configured.";
            return;
        }

        string pokemon = TestQuestPokemon.Trim();

        if (pokemon.Length == 0)
        {
            TestQuestStatus = "Enter the Pokemon the quest asks for, e.g. Rattata.";
            return;
        }

        if (!TryReadTestQuestGoal(out int goal))
        {
            TestQuestStatus = $"Enter the community goal as a plain number from 1 to {QuestTestMaxIvs:#,0}, e.g. 2000.";
            return;
        }

        if (!await EnsureAdminSignInAsync())
        {
            TestQuestStatus = "Starting a test quest needs an admin sign-in.";
            return;
        }

        TestQuestStatus = "Starting...";

        try
        {
            TestWorldQuest created = await EventsSyncService.StartTestWorldQuestAsync(pokemon, goal);

            string ends = created.EndsUtc is null
                ? "no end time"
                : $"ends {created.EndsUtc.Value.ToLocalTime():g}";

            TestQuestStatus =
                $"Started: {created.Pokemon}, goal {created.TotalIvs:#,0} IVs, " +
                $"1st ticket at {created.FirstTierIvs:#,0}, 2nd at {created.SecondTierIvs:#,0}, {ends}. " +
                "Open Menu > World Quest to see it.";
        }
        catch (EventsSyncException ex)
        {
            if (ex.Unauthorized)
                EventsSyncService.ForgetAdminCredentials();

            TestQuestStatus = DescribeQuestFailure(ex, "start it");
        }
    }

    /// <summary>§234. Removes every test quest, not only the running one - a
    /// quest lasts a day and testing makes several. A quest PRO announced can
    /// never match, so this cannot delete a real one.</summary>
    [RelayCommand]
    private async Task ClearTestQuests()
    {
        if (!EventsSyncService.IsOnline)
        {
            TestQuestStatus = "No events server configured.";
            return;
        }

        if (!await EnsureAdminSignInAsync())
        {
            TestQuestStatus = "Clearing test quests needs an admin sign-in.";
            return;
        }

        TestQuestStatus = "Clearing...";

        try
        {
            int removed = await EventsSyncService.ClearTestWorldQuestsAsync();

            TestQuestStatus = removed switch
            {
                0 => "There were no test quests to remove.",
                1 => "Removed 1 test quest. Real quests are untouched.",
                _ => $"Removed {removed} test quests. Real quests are untouched.",
            };
        }
        catch (EventsSyncException ex)
        {
            if (ex.Unauthorized)
                EventsSyncService.ForgetAdminCredentials();

            TestQuestStatus = DescribeQuestFailure(ex, "clear them");
        }
    }

    // ---- The announced quest (§298) ----

    /// <summary>§298. Set by the view: a Yes/No box, the same
    /// ConfirmDialogWindow the main window and the Hunt Log use. Ending a
    /// quest is the one thing this console does that changes what every
    /// other tracker sees, so it asks first. Null when no view wired one, in
    /// which case the command does nothing rather than acting unasked.</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>A quest started from this console rather than announced -
    /// the Worker's own QUEST_TEST_PREFIX. A real Discord message id is all
    /// digits, so the two can never be confused.</summary>
    private const string TestQuestIdPrefix = "test-";

    /// <summary>The quest the End/Reopen buttons act on: whatever Refresh
    /// last read, running or over. Not the "active" quest - an ended one has
    /// stopped being active by definition, and it is the one that has to be
    /// reachable to be put back.</summary>
    private WorldQuest? announcedQuest;

    [ObservableProperty] private string announcedQuestText =
        "Press Refresh to read the latest World Quest from the events server.";

    [ObservableProperty] private string announcedQuestStatus =
        EventsSyncService.IsOnline ? string.Empty : "No events server configured.";

    [ObservableProperty] private bool canEndAnnouncedQuest;
    [ObservableProperty] private bool canReopenAnnouncedQuest;

    /// <summary>§298. The newest quest the server knows about, whatever state
    /// it is in. The Worker answers newest first; the first PARSED row is the
    /// newest real quest, and an unparsed row is an announcement the Worker
    /// could not read a species out of, which there is nothing to end.</summary>
    [RelayCommand]
    private async Task RefreshAnnouncedQuest()
    {
        if (!EventsSyncService.IsOnline)
        {
            AnnouncedQuestStatus = "No events server configured.";
            return;
        }

        AnnouncedQuestStatus = "Reading...";

        try
        {
            IReadOnlyList<WorldQuest> quests = await EventsSyncService.FetchWorldQuestsAsync();

            ShowAnnouncedQuest(quests.FirstOrDefault(q => q.Parsed));
            AnnouncedQuestStatus = string.Empty;
        }
        catch (EventsSyncException ex)
        {
            AnnouncedQuestStatus = DescribeQuestFailure(ex, "read the quest");
        }
    }

    /// <summary>§298. Declares the quest over, for everyone.
    ///
    /// The tracker counts a quest down from a derived time - the
    /// announcement's timestamp plus its stated duration - and that time is
    /// only ever a maximum: a quest also stops the moment the community goal
    /// is met, and the goal is met per server, so it is really over once both
    /// have finished. Nothing the Worker can read says when that happened.
    /// Somebody who played it does, and this is where they say so.</summary>
    [RelayCommand]
    private async Task EndAnnouncedQuest()
    {
        if (announcedQuest is not { } quest)
        {
            AnnouncedQuestStatus = "Press Refresh first.";
            return;
        }

        if (!EventsSyncService.IsOnline)
        {
            AnnouncedQuestStatus = "No events server configured.";
            return;
        }

        if (quest.EndedUtc is not null)
        {
            AnnouncedQuestStatus = "That quest has already been ended.";
            return;
        }

        if (ConfirmAsync is null)
            return;

        bool confirmed = await ConfirmAsync(
            $"End the World Quest for {quest.Pokemon} for everyone?\n\n" +
            "Every tracker stops counting it down, the World Quest menu item goes plain, and anyone " +
            "hunting it is told the goal was met. Counts already submitted are kept.\n\n" +
            "Only do this once BOTH servers have finished it. It can be put back with Reopen.");

        if (!confirmed)
        {
            AnnouncedQuestStatus = "Left running.";
            return;
        }

        if (!await EnsureAdminSignInAsync())
        {
            AnnouncedQuestStatus = "Ending a quest needs an admin sign-in.";
            return;
        }

        AnnouncedQuestStatus = "Ending...";

        try
        {
            ShowAnnouncedQuest(await EventsSyncService.SetWorldQuestEndedAsync(quest.MessageId, ended: true));

            AnnouncedQuestStatus =
                $"The {quest.Pokemon} quest is over. Every tracker sees that within five minutes, or at once if it is opened after now.";

            // The admin who ends the quest they are hunting comes out of the
            // mode - the quest session is saved under the client it was hunted
            // on and the normal session is back, exactly as the menu item does
            // it. A DIFFERENT quest's session is left alone: ending one quest
            // is not a reason to end someone's hunt of another.
            if (WorldQuestMode.Current is { } on &&
                string.Equals(on.MessageId, quest.MessageId, StringComparison.Ordinal))
            {
                await WorldQuestMode.LeaveThroughOwnerAsync();

                AnnouncedQuestStatus += " World Quest hunting was left here too - your normal session is back, paused where it was.";
            }
        }
        catch (EventsSyncException ex)
        {
            if (ex.Unauthorized)
                EventsSyncService.ForgetAdminCredentials();

            AnnouncedQuestStatus = DescribeQuestFailure(ex, "end it");
        }
    }

    /// <summary>§298. Puts an ended quest back. A quest ended by mistake
    /// would otherwise read as over on every tracker until its own clock ran
    /// out, which is up to a day of a quest nobody is counting.</summary>
    [RelayCommand]
    private async Task ReopenAnnouncedQuest()
    {
        if (announcedQuest is not { } quest)
        {
            AnnouncedQuestStatus = "Press Refresh first.";
            return;
        }

        if (!EventsSyncService.IsOnline)
        {
            AnnouncedQuestStatus = "No events server configured.";
            return;
        }

        if (!await EnsureAdminSignInAsync())
        {
            AnnouncedQuestStatus = "Reopening a quest needs an admin sign-in.";
            return;
        }

        AnnouncedQuestStatus = "Reopening...";

        try
        {
            WorldQuest updated = await EventsSyncService.SetWorldQuestEndedAsync(quest.MessageId, ended: false);

            ShowAnnouncedQuest(updated);

            // Reopening cannot make a quest run again once its own 24 hours
            // are up, and saying otherwise would be a lie the console tells
            // about the thing it just did.
            AnnouncedQuestStatus = updated.EndsUtc is { } ends && ends <= DateTime.UtcNow
                ? $"The mark is off the {updated.Pokemon} quest, but its 24 hours are up, so it is still not running."
                : $"The {updated.Pokemon} quest is running again.";
        }
        catch (EventsSyncException ex)
        {
            if (ex.Unauthorized)
                EventsSyncService.ForgetAdminCredentials();

            AnnouncedQuestStatus = DescribeQuestFailure(ex, "reopen it");
        }
    }

    /// <summary>§298. The one line that says what state the quest is in, and
    /// which of the two buttons that state allows. Both are driven from the
    /// quest itself rather than from which command was pressed last, so a
    /// Refresh, an End and a Reopen all leave the panel agreeing with the
    /// server.</summary>
    private void ShowAnnouncedQuest(WorldQuest? quest)
    {
        announcedQuest = quest;

        if (quest is null)
        {
            AnnouncedQuestText = "The events server lists no World Quest it could read.";
            CanEndAnnouncedQuest = false;
            CanReopenAnnouncedQuest = false;
            return;
        }

        DateTime now = DateTime.UtcNow;

        string kind = quest.MessageId.StartsWith(TestQuestIdPrefix, StringComparison.Ordinal)
            ? " (a test quest from this console)"
            : string.Empty;

        string started = $"started {DescribeSpan(now - quest.StartedUtc)} ago";

        if (quest.EndedUtc is { } ended)
        {
            AnnouncedQuestText =
                $"{quest.Pokemon}{kind} - {started}, ended {DescribeSpan(now - ended)} ago because both servers had finished it.";
            CanEndAnnouncedQuest = false;
            CanReopenAnnouncedQuest = true;
            return;
        }

        if (quest.EndsUtc is { } endsUtc && endsUtc <= now)
        {
            AnnouncedQuestText =
                $"{quest.Pokemon}{kind} - {started}, and its 24 hours are up. It has stopped on its own; there is nothing to end.";
            CanEndAnnouncedQuest = false;
            CanReopenAnnouncedQuest = false;
            return;
        }

        string left = quest.EndsUtc is { } ends
            ? $"up to {DescribeSpan(ends - now)} left"
            : "no end time could be read from the announcement";

        AnnouncedQuestText = $"{quest.Pokemon}{kind} - {started}, {left}. Running.";
        CanEndAnnouncedQuest = true;
        CanReopenAnnouncedQuest = false;
    }

    /// <summary>"3 hours", "12 minutes", "2 days" - the coarsest unit that
    /// still says something, for a panel measuring a quest that lasts a day
    /// and a declaration made minutes ago.</summary>
    private static string DescribeSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        if (span.TotalMinutes < 1)
            return "under a minute";

        if (span.TotalHours < 1)
            return Plural((int)span.TotalMinutes, "minute");

        return span.TotalHours < 48
            ? Plural((int)span.TotalHours, "hour")
            : Plural((int)span.TotalDays, "day");
    }

    private static string Plural(int count, string unit) =>
        count == 1 ? $"1 {unit}" : $"{count} {unit}s";

    // ---- Inspector commands ----

    // §103 per the reference image: one Upload button (single or multiple
    // files in one picker pass), plus the two live-capture buttons below.
    [RelayCommand]
    private Task Upload() => RunInspector(multiple: true);

    [RelayCommand]
    private async Task ScreenshotClient()
    {
        IsBusy = true;

        try
        {
            bool overlay = ShowRegionOverlay;

            DiagnosticReplayService.ReplayResult result =
                await Task.Run(() => DiagnosticReplayService.ReplayLiveClient(overlay));

            AddReplayItem(result);
            SelectedReplay = ReplayResults.LastOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Live client capture failed");
            ReplayResults.Add(new AdminReplayItem { Title = "Live capture error", Details = ex.Message });
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScreenshotTracker()
    {
        if (RequestTrackerScreenshot is null)
            return;

        try
        {
            (string Path, Bitmap Image)? shot = await RequestTrackerScreenshot();

            if (shot is null)
            {
                ReplayResults.Add(new AdminReplayItem
                {
                    Title = "Tracker capture failed",
                    Details = "The tracker's main window could not be rendered."
                });
                return;
            }

            ReplayResults.Add(new AdminReplayItem
            {
                Title = $"Tracker window {DateTime.Now:HH:mm:ss}",
                Details = $"Saved to {shot.Value.Path}.\nThe tracker's own window is not OCR material - this capture is kept for visual reference (what the app showed at this moment), the same second half Report a Problem saves.",
                Overlay = shot.Value.Image
            });

            SelectedReplay = ReplayResults.LastOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Tracker window capture failed");
        }
    }

    private async Task RunInspector(bool multiple)
    {
        if (RequestScreenshotPaths is null)
            return;

        string[] paths = await RequestScreenshotPaths(multiple);

        if (paths.Length == 0)
            return;

        IsBusy = true;

        try
        {
            ReplayResults.Clear();
            bool overlay = ShowRegionOverlay;

            // The OCR runs off the UI thread; results marshal back per file.
            foreach (string path in paths)
            {
                DiagnosticReplayService.ReplayResult result =
                    await Task.Run(() => DiagnosticReplayService.Replay(path, overlay));

                AddReplayItem(result);
            }

            SelectedReplay = ReplayResults.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OCR inspector replay failed");
            ReplayResults.Add(new AdminReplayItem { Title = "Error", Details = ex.Message });
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddReplayItem(DiagnosticReplayService.ReplayResult result)
    {
        Bitmap? overlayBitmap = null;

        if (result.OverlayPng is { Length: > 0 })
        {
            using var stream = new MemoryStream(result.OverlayPng);
            overlayBitmap = new Bitmap(stream);
        }

        ReplayResults.Add(new AdminReplayItem
        {
            Title = result.FileName,
            Details = result.Summary,
            Overlay = overlayBitmap
        });
    }

    // ---- Diagnostics commands ----

    [RelayCommand]
    private async Task SaveRecording()
    {
        try
        {
            string folder = await TrackerDiagnostics.SaveRecordingAsync();
            RecordingStatus = $"Saved to {folder}";
        }
        catch (Exception ex)
        {
            RecordingStatus = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearRecording()
    {
        TrackerDiagnostics.ClearRecording();
        RecordingStatus = "0 entries retained (cap 120)";
    }

    // ---- Data health / bundle commands ----

    [RelayCommand]
    private async Task RefreshDataHealth()
    {
        IsBusy = true;

        try
        {
            DataHealthReport = await Task.Run(DataHealthService.BuildReport);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateBundle()
    {
        IsBusy = true;

        try
        {
            string path = await SupportBundleService.CreateAsync(new SupportBundleService.BundleOptions
            {
                IncludeEnvironment = BundleEnvironment,
                IncludeLogs = BundleLogs,
                IncludeDiagnostics = BundleDiagnostics,
                IncludeDataHealth = BundleDataHealth,
                IncludeSettings = BundleSettings,
                IncludeHuntingData = BundleHuntingData
            });

            BundleStatus = $"Created {Path.GetFileName(path)} in Downloads.";
        }
        catch (Exception ex)
        {
            BundleStatus = $"Bundle failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- §168 Battle Lab ----
    // The bulk observation runner: thousands of headless battles, both of
    // the Simulator's AIs against fresh random teams, every battle
    // recorded by a §156 observer into the SimulatorObservations folder.
    // Admin-only by construction (this whole console sits behind Admin
    // Login), heavy by design - it is meant to run while the machine is
    // otherwise idle.

    [ObservableProperty] private string labBattleCountText = "1000";
    [ObservableProperty] private bool labRunning;
    [ObservableProperty] private string labProgress = "";
    [ObservableProperty] private string labStatus =
        "Idle. Battles record as Kind \"Lab\" into the Simulator's observation folder.";
    [ObservableProperty] private string labStoredText = "";
    [ObservableProperty] private string labArchiveStatus = "";

    // §171: who the deep-learning model faces. Left alone it plays nobody
    // and simply watches the other two, which is the §168 behaviour.
    [ObservableProperty] private bool labObserveOnly = true;
    [ObservableProperty] private bool labVersusMonteCarlo;
    [ObservableProperty] private bool labVersusBaseline;

    // §182: DAgger. The model plays and the Monte Carlo brain marks every
    // decision, so the corpus is finally made of the positions the MODEL
    // reaches rather than the ones its teacher reaches.
    [ObservableProperty] private bool labTaught;
    [ObservableProperty] private string labModelStatus = "";

    /// <summary>§182: how much the brain prefers upside to expectation
    /// while it teaches, 0 to 1. Ignored on Observe only, which walks the
    /// whole dial by itself so one run covers every difficulty.</summary>
    [ObservableProperty] private string labRiskText = "0";

    /// <summary>§327. Which tier pool this corpus is collected from.
    /// Defaults, like everything else, to OU and UU - a corpus where a
    /// third of the battles were decided by a box legendary teaches the
    /// matchup rather than the play.</summary>
    [ObservableProperty] private int labTierIndex;

    // §180: the matchup selector was added in §171 for MEASURING a trained
    // model, but it sits directly above the Run button that COLLECTS. Left on
    // a model matchup, a run records a corpus with the model on one side and
    // no teacher anywhere in it - which is how nine thousand battles once got
    // spent teaching a model to imitate itself. Say so beside the radios,
    // before the hours go by rather than after.
    [ObservableProperty] private string labMatchupNote = "";
    [ObservableProperty] private bool labMatchupNoteVisible;

    partial void OnLabObserveOnlyChanged(bool value) => RefreshMatchupNote();
    partial void OnLabVersusMonteCarloChanged(bool value) => RefreshMatchupNote();
    partial void OnLabVersusBaselineChanged(bool value) => RefreshMatchupNote();
    partial void OnLabTaughtChanged(bool value) => RefreshMatchupNote();

    /// <summary>§180: warn while the choice is still cheap to change. Only
    /// the Monte Carlo brain ranks its options, so a matchup it does not play
    /// in yields a corpus the trainer's --soft-targets silently ignores.</summary>
    private void RefreshMatchupNote()
    {
        LabMatchup matchup = SelectedMatchup;

        // §182: the warning is about a matchup with no TEACHER in it, not
        // about the model being on the field. DAgger puts it on the field
        // and keeps the teacher, which is the whole point of it.
        LabMatchupNote = matchup switch
        {
            LabMatchup.DeepLearningTaught =>
                "Collects the positions the model steers itself into, labelled with what the "
                + "brain would have played there. This is the corpus to train the next model "
                + "on; the risk setting below picks which teacher it learns from.",
            LabMatchup.ObserveOnly => "",
            _ =>
                "For measuring a trained model, not for collecting: the Monte Carlo brain "
                + "does not play in this matchup, so nothing recorded carries a ranking and "
                + "the corpus teaches the model its own answers. Use Observe only, or "
                + "Model taught by brain, to collect."
        };

        LabMatchupNoteVisible = LabMatchupNote.Length > 0;
    }

    private LabMatchup SelectedMatchup =>
        LabVersusMonteCarlo ? LabMatchup.DeepLearningVsMonteCarlo :
        LabVersusBaseline ? LabMatchup.DeepLearningVsBaseline :
        LabTaught ? LabMatchup.DeepLearningTaught :
        LabMatchup.ObserveOnly;

    private CancellationTokenSource? labCts;

    /// <summary>§169: the window owns the file dialogs. Save takes a
    /// suggested file name and returns the chosen path (null = cancelled);
    /// the two open pickers return a path or null.</summary>
    public Func<string, Task<string?>>? RequestArchiveSavePath { get; set; }
    public Func<Task<string?>>? RequestArchiveOpenPath { get; set; }
    public Func<Task<string?>>? RequestModelPath { get; set; }

    [RelayCommand]
    private async Task RunLab()
    {
        DisarmDestructiveButtons();

        if (LabRunning)
            return;

        if (!int.TryParse(LabBattleCountText.Trim(), out int battles) ||
            battles < LabBattleRunner.MinBattles || battles > LabBattleRunner.MaxBattles)
        {
            LabStatus = $"Enter a battle count between {LabBattleRunner.MinBattles} and {LabBattleRunner.MaxBattles}.";
            return;
        }

        LabMatchup matchup = SelectedMatchup;

        // §182: the teaching brain's risk appetite. Observe only ignores it
        // and walks the whole dial itself; every other matchup holds it
        // still for the run.
        if (!double.TryParse(LabRiskText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                             out double risk) || risk < 0 || risk > 1)
        {
            LabStatus = "Enter a risk setting between 0 (plays the odds) and 1 (plays for the win).";
            return;
        }

        IShadowEvaluator? evaluator = null;

        if (LabBattleRunner.NeedsModel(matchup))
        {
            // §171: check the model BEFORE committing to a long run, so a
            // broken one costs a message rather than hours of battles the
            // network never actually played.
            evaluator = await Task.Run(() => BuildEvaluator());

            if (!evaluator.Status.Available)
            {
                LabStatus = "The deep-learning model cannot play: " + evaluator.Status.Description +
                            ". Install a trained model (Training data, below), or choose Observe only.";
                (evaluator as IDisposable)?.Dispose();
                return;
            }
        }

        LabRunning = true;
        labCts = new CancellationTokenSource();
        LabProgress = $"0 / {battles} battles";
        LabStatus = "Running - " + LabBattleRunner.Describe(matchup) + ", all on fresh random teams."
                  + (LabBattleRunner.HasTeacher(matchup)
                        ? string.Empty
                        : " This matchup records no rankings; see the note above.");

        DateTime started = DateTime.UtcNow;

        try
        {
            LabRunReport report = await LabBattleRunner.RunAsync(
                new TrackerSpeciesSource(),
                battles,
                progress: (done, total) =>
                {
                    if (done % 10 != 0 && done != total)
                        return;

                    TimeSpan elapsed = DateTime.UtcNow - started;
                    double perBattle = elapsed.TotalSeconds / done;
                    var eta = TimeSpan.FromSeconds(perBattle * (total - done));
                    string etaText = $"{(int)eta.TotalHours}h {eta.Minutes:00}m left";

                    Dispatcher.UIThread.Post(() =>
                        LabProgress = $"{done} / {total} battles - about {etaText}");
                },
                labCts.Token,
                matchup,
                evaluator,
                risk,
                TierFilter.At(LabTierIndex));

            TimeSpan took = report.Elapsed;
            SimulationResult r = report.Result;

            LabProgress = $"{r.Simulations - r.Cancelled} / {r.Simulations} battles completed";

            string sides = matchup == LabMatchup.ObserveOnly
                ? $"P1 {r.Player1Wins} / P2 {r.Player2Wins}"
                : $"model {r.Player1Wins} / opponent {r.Player2Wins}";

            // §323. This sentence is why §322 went unnoticed for three
            // sections.
            //
            // It was written in §171, when a model genuinely had no switch
            // output and "the rest were switches" was true. §176 gave the
            // model a switch head and this line was not revisited, so when
            // §322 left the network unable to score ANY turn, the panel
            // reported "answered 0 of 3063 decisions" and then explained
            // that away in the same breath - with a reason that had stopped
            // being true two sections earlier.
            //
            // A summary line that can explain its own worst reading as
            // normal is worse than no summary line. So: zero is now stated
            // as the impossibility it is, the excuse is only offered when it
            // actually applies, and a low share says so rather than passing.
            string played;

            if (report.NeuralDecisions == 0)
            {
                played = string.Empty;
            }
            else if (report.NeuralModelChoices == 0)
            {
                played = $" THE NETWORK ANSWERED NONE of the {report.NeuralDecisions} decisions it was " +
                         "asked for - every one of them fell through to the baseline, so this run was " +
                         "random play recorded under the model's name. It is not a weak model; it is a " +
                         "model that never played. The usual cause is a model that reads a different " +
                         "number of features than the observation it is shown (MIGRATION_GUIDE.md " +
                         "section 322). Do not train on this corpus.";
            }
            else
            {
                int share = (int)Math.Round(
                    100.0 * report.NeuralModelChoices / report.NeuralDecisions);

                played = $" The network answered {report.NeuralModelChoices} of " +
                         $"{report.NeuralDecisions} decisions ({share}%)";

                // The old excuse, offered only when it is the truth.
                played += evaluator.Status.CanChooseSwitches
                    ? " - the rest were turns it had nothing legal to score."
                    : " - the rest were switches, which this model has no output for, " +
                      "and turns it had nothing legal to score.";

                // Every fall-through is an exceptional case by construction,
                // so most of a run being exceptional is a finding, not a
                // statistic. Deliberately phrased as something to look at
                // rather than a verdict: there is no measurement behind the
                // threshold, only the shape of the code.
                if (share < 50)
                {
                    played += $" Fewer than half is worth a look - the fall-through cases are " +
                              "meant to be rare.";
                }
            }

            // §180: say how much of what was just collected can actually
            // teach anything. Only the Monte Carlo brain ranks its options,
            // so a run that never put it on the field produces a corpus the
            // trainer's --soft-targets silently ignores.
            string ranked = report.RankedRecords > 0
                ? $" {report.RankedRecords} carry the brain's ranking."
                : " NONE carry a ranking - the Monte Carlo brain did not play, "
                  + "so this corpus cannot use --soft-targets. Collect with "
                  + "Observe only.";

            LabStatus =
                $"Done in {(int)took.TotalHours}h {took.Minutes:00}m: {report.ObservationFiles} battle file(s), " +
                $"{report.DecisionRecords} decision record(s), {report.DroppedRecords} dropped." + ranked + " " +
                $"{sides} / draws {r.Draws} / cancelled {r.Cancelled}. " +
                $"Teams from {TierFilter.At(LabTierIndex).Name}." + played;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Admin Battle Lab: the run failed.");
            LabStatus = $"The lab run failed: {ex.Message}";
        }
        finally
        {
            LabRunning = false;
            labCts?.Dispose();
            labCts = null;
            (evaluator as IDisposable)?.Dispose();
            RefreshLabStored();
        }
    }

    /// <summary>§171: the model as the lab would load it - the same path
    /// the observer resolves, so what this line says is what a run gets.</summary>
    private static IShadowEvaluator BuildEvaluator()
    {
        try
        {
            return new OnnxShadowEvaluator(ObservationStore.ResolveModelPath());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Battle Lab: the shadow evaluator failed to construct.");
            return new NullShadowEvaluator("the evaluator failed to start: " + ex.Message);
        }
    }

    private void RefreshModelStatus()
    {
        _ = Task.Run(() =>
        {
            IShadowEvaluator evaluator = BuildEvaluator();
            string description = evaluator.Status.Description;
            bool available = evaluator.Status.Available;
            (evaluator as IDisposable)?.Dispose();

            Dispatcher.UIThread.Post(() =>
                LabModelStatus = (available ? "Model: " : "Model unavailable - ") + description);
        });
    }

    [RelayCommand]
    private void CancelLab()
    {
        DisarmDestructiveButtons();

        labCts?.Cancel();
        LabStatus = "Cancelling - running battles stop at their next turn; finished files stay.";
    }

    private void RefreshLabStored()
    {
        _ = Task.Run(() =>
        {
            (int files, long records) = ObservationStore.CountStored();
            (_, long bytes) = ObservationArchive.Measure(ObservationStore.Root);

            Dispatcher.UIThread.Post(() =>
                LabStoredText =
                    $"Stored: {records} observation(s) in {files} battle file(s), {Megabytes(bytes)} on disk.");
        });
    }

    static string Megabytes(long bytes) => $"{bytes / 1024.0 / 1024.0:F1} MB";

    // ---- §169: the training corpus, in and out as one file ----
    // Observation data is the INPUT the ai-lab trainer turns into
    // pokemon_ai.onnx; it is not something a published build carries. These
    // three commands cover the whole loop before a release: bundle what the
    // lab produced, merge a bundle from another machine, and drop a freshly
    // trained model in where the shadow evaluator will prefer it.

    // §179: clearing the corpus from here rather than from Explorer. A
    // lab run at ten thousand battles leaves ten thousand files behind,
    // and File.Delete does not use the recycle bin, so this is the whole
    // job in one pass rather than two slow ones through the shell.
    [ObservableProperty] private string clearLabDataLabel = "Clear Observation Data";

    private bool clearLabDataArmed;

    /// <summary>Two clicks, deliberately: this is the only irreversible
    /// button on the panel, and a mis-click would cost a run that took
    /// hours. The same idiom the Simulator's own Clear uses (§156).</summary>
    [RelayCommand]
    private async Task ClearLabData()
    {
        // §311a: pressing this cancels an armed FORGET - it is another
        // action as far as that button is concerned - but must not cancel
        // its own arming, which is the bug that made the other one useless.
        DisarmClearMemory();

        if (LabRunning)
        {
            LabArchiveStatus = "Let the run finish (or cancel it) before clearing - it is still writing.";
            return;
        }

        if (!clearLabDataArmed)
        {
            clearLabDataArmed = true;
            ClearLabDataLabel = "Really delete? Click again";
            LabArchiveStatus = "This deletes every recorded battle file. Your trained model and settings stay. "
                             + "Export first if you have not. Click again to confirm.";
            return;
        }

        DisarmClearLabData();

        var started = DateTime.UtcNow;

        (int files, long bytes) = await Task.Run(() => ObservationStore.ClearAll());

        LabArchiveStatus = files == 0
            ? "Nothing to clear - there are no recorded battles."
            : $"Cleared {files} battle file(s), {Megabytes(bytes)} reclaimed in "
              + $"{(DateTime.UtcNow - started).TotalSeconds:F1}s. Nothing went to the recycle bin.";

        RefreshLabStored();
    }

    /// <summary>
    /// §311a. Any other action on the panel cancels an armed delete, so
    /// neither button can sit primed while attention has moved on.
    ///
    /// This used to be DisarmClearLabData, and it disarmed BOTH buttons -
    /// which is right for every caller that means "something else happened"
    /// and wrong for the two commands themselves. ClearMemory called it as
    /// its first statement, so every click of Clear Opponent Memory cleared
    /// clearMemoryArmed before testing it, the arm check was always true,
    /// and the confirm branch below it could never run. The button armed,
    /// re-armed, and never forgot anything.
    ///
    /// The three now say exactly which button they mean, so a command can
    /// cancel the OTHER one without cancelling itself.
    /// </summary>
    private void DisarmDestructiveButtons()
    {
        DisarmClearLabData();
        DisarmClearMemory();
    }

    private void DisarmClearLabData()
    {
        clearLabDataArmed = false;
        ClearLabDataLabel = "Clear Observation Data";
    }

    private void DisarmClearMemory()
    {
        clearMemoryArmed = false;
        ClearMemoryLabel = "Clear Opponent Memory";
    }

    // §183: the opponent's book on how battles against you have gone. It
    // is small and it is not training data, but it is the one file here
    // that is a record of PLAY rather than of the simulator, so it gets
    // its own line, its own button and the same two-click arming.
    [ObservableProperty] private string opponentMemoryText = "";
    [ObservableProperty] private string clearMemoryLabel = "Clear Opponent Memory";

    private bool clearMemoryArmed;

    [RelayCommand]
    private void ClearMemory()
    {
        // §311a: the OTHER button's arming, not this one's. Disarming this
        // one here is what made the second click do nothing.
        DisarmClearLabData();

        if (!clearMemoryArmed)
        {
            clearMemoryArmed = true;
            ClearMemoryLabel = "Really forget? Click again";
            LabArchiveStatus = "This makes the Simulator opponent forget every matchup it has learned, "
                             + "so it plays you like the first time again. Click again to confirm.";
            return;
        }

        DisarmClearMemory();

        int forgotten = OpponentMemoryStore.Clear();

        LabArchiveStatus = forgotten == 0
            ? "Nothing to forget - the opponent has no book yet."
            : $"Forgot {forgotten} matchup(s). The opponent starts fresh against every team.";

        RefreshOpponentMemory();
    }

    /// <summary>§183: reads the book's size for the panel line. Cheap - it
    /// counts what is already in memory and stats one file.</summary>
    private void RefreshOpponentMemory() =>
        OpponentMemoryText = OpponentMemoryStore.Describe();

    [RelayCommand]
    private async Task ExportLabData()
    {
        DisarmDestructiveButtons();

        if (LabRunning)
        {
            LabArchiveStatus = "Let the run finish (or cancel it) before exporting - open files are skipped.";
            return;
        }

        if (RequestArchiveSavePath == null)
            return;

        string suggested = $"simulator-observations-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        string? destination = await RequestArchiveSavePath(suggested);

        if (string.IsNullOrWhiteSpace(destination))
            return;

        IsBusy = true;
        LabArchiveStatus = "Bundling observation data...";

        try
        {
            ObservationExportResult result = await Task.Run(() =>
                ObservationArchive.Export(ObservationStore.Root, destination));

            LabArchiveStatus =
                $"Exported {result.BattleFiles} battle file(s), {result.DecisionRecords} record(s) " +
                $"to {Path.GetFileName(result.ArchivePath)} ({Megabytes(result.ArchiveBytes)}).";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Battle Lab: the observation export failed.");
            LabArchiveStatus = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportLabData()
    {
        DisarmDestructiveButtons();

        if (LabRunning)
        {
            LabArchiveStatus = "Let the run finish (or cancel it) before importing.";
            return;
        }

        if (RequestArchiveOpenPath == null)
            return;

        string? source = await RequestArchiveOpenPath();

        if (string.IsNullOrWhiteSpace(source))
            return;

        IsBusy = true;
        LabArchiveStatus = "Merging observation data...";

        try
        {
            ObservationImportResult result = await Task.Run(() =>
                ObservationArchive.Import(source, ObservationStore.Root));

            LabArchiveStatus =
                $"Imported {result.Added} new battle file(s), {result.DecisionRecords} record(s). " +
                $"{result.SkippedExisting} already present" +
                (result.Rejected > 0 ? $", {result.Rejected} entry/entries rejected." : ".");

            RefreshLabStored();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Battle Lab: the observation import failed.");
            LabArchiveStatus = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallTrainedModel()
    {
        DisarmDestructiveButtons();

        if (RequestModelPath == null)
            return;

        string? source = await RequestModelPath();

        if (string.IsNullOrWhiteSpace(source))
            return;

        try
        {
            Directory.CreateDirectory(ObservationStore.Root);

            string destination = Path.Combine(ObservationStore.Root, "pokemon_ai.onnx");

            await Task.Run(() => File.Copy(source, destination, overwrite: true));

            LabArchiveStatus =
                "Trained model installed for this machine - the observer's shadow evaluator prefers it over " +
                "the shipped copy. To PUBLISH it, replace PokemonSim/DataFiles/pokemon_ai.onnx in the repo and rebuild.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Battle Lab: installing the trained model failed.");
            LabArchiveStatus = $"Installing the model failed: {ex.Message}";
        }
    }

    // ---- §325 AI Evaluation ------------------------------------
    //
    // §324 built the harness; this is the panel that starts one.
    //
    // It is deliberately a different section from the Battle Lab even
    // though the two look alike, because they do opposite things. The lab
    // writes every battle into the training corpus. This writes nothing at
    // all - a measurement that changes what it measures is not a
    // measurement - and putting them side by side under one Run button is
    // exactly how an evaluation run ends up contributing 60 MB of its own
    // play to the data it was judging.

    /// <summary>§326. Two seats, each taking any brain.
    ///
    /// §325 fixed the installed model into seat one and offered a list of
    /// opponents. The first evening of results showed what that cost: the
    /// model lost to the baseline and to the brain, and the question that
    /// would have said how much of the gap was the MODEL - what does the
    /// brain itself score against the baseline - could not be asked.</summary>
    public static IReadOnlyList<string> EvalBrainNames => EvaluationRunner.BrainNames;

    /// <summary>§327. The tier pools, shared by both panels because both
    /// generate random teams and both need to say which pool they used -
    /// the Battle Lab because the corpus is made of them, this panel
    /// because a win rate measured on one pool is not comparable to one
    /// measured on another.</summary>
    public static IReadOnlyList<string> TierPoolNames =>
        TierFilter.Choices.Select(t => t.Name).ToList();

    [ObservableProperty] private int evalTierIndex;

    [ObservableProperty] private int evalContenderIndex;
    [ObservableProperty] private int evalOpponentIndex = 2;

    [ObservableProperty] private string evalContenderModelPath = "";

    /// <summary>§324's own arithmetic: 384 battles resolve a five-point
    /// difference at 95%. The default is that, rounded up to something
    /// people type.</summary>
    [ObservableProperty] private string evalBattlesText = "400";

    /// <summary>Blank draws a fresh one and the report says which. A number
    /// reproduces a run, which is what you want the moment a result
    /// surprises you.</summary>
    [ObservableProperty] private string evalSeedText = "";

    [ObservableProperty] private string evalOpponentModelPath = "";
    [ObservableProperty] private string evalModelStatus = "";
    [ObservableProperty] private string evalProgress = "";
    [ObservableProperty] private string evalStatus = "";
    [ObservableProperty] private string evalSavedTo = "";
    [ObservableProperty] private bool evalRunning;

    private CancellationTokenSource? evalCts;

    partial void OnEvalContenderIndexChanged(int value) => RefreshEvaluationModel();
    partial void OnEvalOpponentIndexChanged(int value) => RefreshEvaluationModel();

    public bool EvalContenderNeedsFile => EvaluationRunner.NeedsFile(EvalContenderIndex);
    public bool EvalOpponentNeedsFile => EvaluationRunner.NeedsFile(EvalOpponentIndex);

    /// <summary>§325. What is installed, said before a run rather than
    /// after - a model that cannot read the observation is the §322 failure
    /// and it makes every number a measurement would produce meaningless.</summary>
    private void RefreshEvaluationModel()
    {
        OnPropertyChanged(nameof(EvalContenderNeedsFile));
        OnPropertyChanged(nameof(EvalOpponentNeedsFile));

        try
        {
            using var evaluator = new OnnxShadowEvaluator(ObservationStore.ResolveModelPath());

            string? problem = NeuralStrategy.Incompatibility(evaluator);

            EvalModelStatus = problem == null
                ? "Installed model: " + evaluator.Status.Description
                : "THIS MODEL CANNOT BE MEASURED: " + problem;
        }
        catch (Exception ex)
        {
            EvalModelStatus = "The model could not be loaded: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task PickContenderModel()
    {
        string? chosen = await AskForModelFile();

        if (chosen != null)
            EvalContenderModelPath = chosen;
    }

    [RelayCommand]
    private async Task PickOpponentModel()
    {
        string? chosen = await AskForModelFile();

        if (chosen != null)
            EvalOpponentModelPath = chosen;
    }

    private async Task<string?> AskForModelFile()
    {
        if (RequestModelPath == null)
            return null;

        string? chosen = await RequestModelPath();

        return string.IsNullOrWhiteSpace(chosen) ? null : chosen;
    }

    [RelayCommand]
    private void CancelEvaluation() => evalCts?.Cancel();

    [RelayCommand]
    private async Task RunEvaluation()
    {
        if (EvalRunning)
            return;

        if (!int.TryParse(EvalBattlesText, out int battles))
        {
            EvalStatus = "Battles must be a whole number between " +
                         EvaluationRunner.MinBattles + " and " + EvaluationRunner.MaxBattles + ".";
            return;
        }

        int? seed = int.TryParse(EvalSeedText, out int typed) ? typed : null;

        if (EvalContenderNeedsFile && string.IsNullOrWhiteSpace(EvalContenderModelPath))
        {
            EvalStatus = "Choose the contender's model file first.";
            return;
        }

        if (EvalOpponentNeedsFile && string.IsNullOrWhiteSpace(EvalOpponentModelPath))
        {
            EvalStatus = "Choose the opponent's model file first.";
            return;
        }

        MatchConfiguration match = EvaluationRunner.BuildMatch(
            EvaluationRunner.ChoiceAt(EvalContenderIndex),
            EvaluationRunner.ChoiceAt(EvalOpponentIndex),
            battles, seed,
            EvalContenderModelPath, EvalOpponentModelPath, 0f,
            TierFilter.At(EvalTierIndex));

        // §322's lesson as a precondition. Everything after this point costs
        // minutes to hours, and none of it is worth anything if a side
        // cannot read the observation it is shown.
        string? refusal = EvaluationRunner.Refuse(match);

        if (refusal != null)
        {
            EvalStatus = "This match cannot run: " + refusal;
            return;
        }

        EvalRunning = true;
        EvalSavedTo = "";
        EvalStatus = "";
        EvalProgress = "Starting...";

        evalCts = new CancellationTokenSource();

        var started = DateTime.UtcNow;

        try
        {
            MatchReport report = await EvaluationRunner.RunAsync(
                match,
                progress: (done, total) =>
                {
                    if (done % 10 != 0 && done != total)
                        return;

                    TimeSpan elapsed = DateTime.UtcNow - started;
                    double each = elapsed.TotalSeconds / Math.Max(1, done);
                    var eta = TimeSpan.FromSeconds(each * (total - done));

                    Dispatcher.UIThread.Post(() =>
                        EvalProgress = $"{done} / {total} battles - about " +
                                       $"{(int)eta.TotalHours}h {eta.Minutes:00}m left");
                },
                evalCts.Token);

            EvalProgress = $"{report.Battles} battles played in " +
                           $"{(int)report.Elapsed.TotalMinutes}m {report.Elapsed.Seconds:00}s";

            EvalStatus = EvaluationRunner.Summarise(report);

            string? saved = EvaluationRunner.Save(report);

            EvalSavedTo = saved == null
                ? "The result could not be saved to disk - it is above, and the log has the reason."
                : "Saved to " + saved;
        }
        catch (OperationCanceledException)
        {
            EvalProgress = "Cancelled.";
            EvalStatus = "The run was cancelled, so there is no result. Nothing was written - " +
                         "this panel never records battles.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AI Evaluation: the match failed.");
            EvalStatus = "The match failed: " + ex.Message;
        }
        finally
        {
            EvalRunning = false;
            evalCts?.Dispose();
            evalCts = null;
        }
    }

    public void Dispose()
    {
        AdminModeService.AuthenticationChanged -= OnAuthenticationChanged;
        refreshTimer.Stop();
        spawnPreviewTimer.Stop();
    }
}
