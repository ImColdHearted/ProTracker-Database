using System;
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

/// <summary>§153. One row of the Event Logins list - top-level for the
/// same compiled-DataTemplate reason as AdminReplayItem. Text is the whole
/// line, precomputed (AdminConsoleViewModel.DescribeAdminLogin).</summary>
public sealed record AdminLoginListItem(AdminLoginInfo Login, string Text);

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
        ["Status", "Recovery", "OCR Inspector", "Diagnostics", "Data Health", "Support Bundle", "Event Logins", "Active Events", "World Quest", "Sprite Scraper", "Battle Lab"];

    [ObservableProperty] private string activeSection = "Status";

    public bool IsStatusSection => ActiveSection == "Status";
    public bool IsRecoverySection => ActiveSection == "Recovery";
    public bool IsInspectorSection => ActiveSection == "OCR Inspector";
    public bool IsDiagnosticsSection => ActiveSection == "Diagnostics";
    public bool IsDataHealthSection => ActiveSection == "Data Health";
    public bool IsSupportBundleSection => ActiveSection == "Support Bundle";
    public bool IsEventLoginsSection => ActiveSection == "Event Logins";
    public bool IsActiveEventsSection => ActiveSection == "Active Events";
    public bool IsWorldQuestSection => ActiveSection == "World Quest";
    public bool IsSpriteScraperSection => ActiveSection == "Sprite Scraper";
    public bool IsBattleLabSection => ActiveSection == "Battle Lab";

    partial void OnActiveSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsStatusSection));
        OnPropertyChanged(nameof(IsRecoverySection));
        OnPropertyChanged(nameof(IsInspectorSection));
        OnPropertyChanged(nameof(IsDiagnosticsSection));
        OnPropertyChanged(nameof(IsDataHealthSection));
        OnPropertyChanged(nameof(IsSupportBundleSection));
        OnPropertyChanged(nameof(IsEventLoginsSection));
        OnPropertyChanged(nameof(IsActiveEventsSection));
        OnPropertyChanged(nameof(IsWorldQuestSection));
        OnPropertyChanged(nameof(IsSpriteScraperSection));
        OnPropertyChanged(nameof(IsBattleLabSection));

        if (IsActiveEventsSection)
            PrepareActiveEventsSection();

        if (IsSpriteScraperSection)
            PrepareSpriteScraperSection();

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

    /// <summary>Set by the window - AdminTokenWindow.ShowAsync, the same hook
    /// CreateEventViewModel and RemoveEventViewModel carry (§153: a bool -
    /// the window hands the credential to EventsSyncService itself).</summary>
    public Func<Task<bool>>? RequestAdminSignIn { get; set; }

    // ---- Event Logins tab (§153) ----

    // The delegated sign-ins for the shared Events board. Everything here
    // needs the MASTER token server-side; a delegated login gets the
    // server's own 403 sentence instead, and "Sign In Differently" is the
    // way back to the master prompt.
    public ObservableCollection<AdminLoginListItem> AdminLogins { get; } = new();

    [ObservableProperty] private AdminLoginListItem? selectedAdminLogin;
    [ObservableProperty] private string newLoginUsername = string.Empty;
    [ObservableProperty] private string newLoginPassword = string.Empty;
    [ObservableProperty] private bool newLoginCanViewStatus;
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

    partial void OnScrapedSpriteChanged(Bitmap? value) => HasScrapedSprite = value is not null;

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

        RefreshStatus();
        refreshTimer.Start();
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

    // ---- Event Logins commands (§153) ----

    private static readonly Regex AdminLoginNamePattern = new("^[A-Za-z0-9._-]{3,24}$");

    /// <summary>§153. "bob  (events + status; created 1 Sep 2026; last used
    /// 1 Sep 18:40)" - the whole row as one line, local time.</summary>
    internal static string DescribeAdminLogin(AdminLoginInfo login)
    {
        string permission = login.CanViewStatus ? "events + status" : "events only";
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
            AdminLoginInfo saved = await EventsSyncService.SaveAdminLoginAsync(username, verifier, NewLoginCanViewStatus);

            NewLoginPassword = string.Empty;
            await FillAdminLoginsAsync();

            AdminLoginsStatus =
                $"\"{saved.Username}\" is ready ({(saved.CanViewStatus ? "events + status" : "events only")}) - " +
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

        // An ordinary admin login is enough here, unlike the Event Logins
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
        DisarmClearLabData();

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
                risk);

            TimeSpan took = report.Elapsed;
            SimulationResult r = report.Result;

            LabProgress = $"{r.Simulations - r.Cancelled} / {r.Simulations} battles completed";

            string sides = matchup == LabMatchup.ObserveOnly
                ? $"P1 {r.Player1Wins} / P2 {r.Player2Wins}"
                : $"model {r.Player1Wins} / opponent {r.Player2Wins}";

            string played = report.NeuralDecisions == 0
                ? string.Empty
                : $" The network answered {report.NeuralModelChoices} of {report.NeuralDecisions} decisions " +
                  "(the rest were switches or Struggle turns it has no output for).";

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
                $"{sides} / draws {r.Draws} / cancelled {r.Cancelled}." + played;
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
        DisarmClearLabData();

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

    /// <summary>Any other action on the panel cancels an armed delete, so
    /// the button cannot sit primed while attention has moved on.</summary>
    private void DisarmClearLabData()
    {
        clearLabDataArmed = false;
        ClearLabDataLabel = "Clear Observation Data";
        ClearMemoryLabel = "Clear Opponent Memory";
        clearMemoryArmed = false;
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
        DisarmClearLabData();

        if (!clearMemoryArmed)
        {
            clearMemoryArmed = true;
            ClearMemoryLabel = "Really forget? Click again";
            LabArchiveStatus = "This makes the Simulator opponent forget every matchup it has learned, "
                             + "so it plays you like the first time again. Click again to confirm.";
            return;
        }

        clearMemoryArmed = false;
        ClearMemoryLabel = "Clear Opponent Memory";

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
        DisarmClearLabData();

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
        DisarmClearLabData();

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
        DisarmClearLabData();

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

    public void Dispose()
    {
        AdminModeService.AuthenticationChanged -= OnAuthenticationChanged;
        refreshTimer.Stop();
    }
}
