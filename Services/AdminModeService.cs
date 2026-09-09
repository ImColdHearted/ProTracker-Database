using System;
using System.IO;
using Serilog;

namespace Foot_Tracker.Services;

/// <summary>
/// The Admin Client override (MIGRATION_GUIDE.md §101): an explicit,
/// authenticated switch that moves ALL hunting activity into an isolated,
/// in-memory diagnostic session so nothing an admin does - live test
/// encounters, screenshot replays, recovery experiments - can touch a normal
/// client's data. While IsActive:
///
///   - MainWindowViewModel routes every counter/display mutation to its
///     separate admin HuntSession instead of the real one, and branches its
///     Reset to that admin session only.
///   - LifetimeStatsService, HuntLogService and SessionEncounterHistoryService
///     each check this flag at their own front door and no-op their writes,
///     so even paths that don't go through the view model (the boss/PVP
///     trackers' tallies) cannot leak admin activity into real stores.
///   - SessionPersistenceService keeps saving only the untouched normal
///     HuntSession object, which is byte-equivalent to what was already on
///     disk - leaving admin mode therefore finds the normal client exactly
///     as it was, with nothing to restore and nothing reset.
///
/// The override is deliberately sticky: automatic client detection never
/// clears it (TryAutoAssignClient checks it), and only the explicit Leave
/// button (or deliberately assigning a normal client from the picker) ends
/// it. Whether it was active is REMEMBERED across restarts through a tiny
/// marker file - per the §101 design decision, mirroring how the app already
/// remembers its last active client - but authentication is NOT remembered:
/// IsAuthenticated is process-lifetime only, so after a restart the admin
/// data isolation resumes immediately (the safe direction) while the Admin
/// Console and admin actions stay locked behind a fresh Admin Login.
/// </summary>
public static class AdminModeService
{
    private static readonly string MarkerPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker",
            "Database",
            "admin-client-selected.txt");

    /// <summary>Raised on every Enter/Leave so MainWindowViewModel can swap
    /// the displayed session and title immediately. Raised on the caller's
    /// thread - every caller is UI-side (login flow, AdminActionsWindow).</summary>
    public static event Action? ActiveChanged;

    /// <summary>Raised when the process's authenticated admin session begins
    /// (MarkAuthenticated) or ends (ClearAuthentication - the §103 Admin
    /// Logout). Lets an already-open Admin Console lock itself the moment
    /// the session ends instead of only on its next construction. Raised on
    /// the caller's thread - both callers are UI-side.</summary>
    public static event Action? AuthenticationChanged;

    /// <summary>True while the isolated Admin Client session is the active
    /// data context. Restored from the marker file at startup - isolation
    /// resuming without a login is safe (it PREVENTS writes); the reverse
    /// would not be.</summary>
    public static bool IsActive { get; private set; } = File.Exists(MarkerPath);

    /// <summary>§187: true when IsActive above was restored from the marker
    /// file at startup rather than set by an Enter() in this process.
    /// Declared AFTER IsActive deliberately - static field initializers run
    /// in textual order, so this captures the restored value before anything
    /// can change it.
    ///
    /// It exists because the restore used to be completely invisible. It
    /// wrote no log line, a Report a Problem bundle never mentioned it, and
    /// the mode's only visible effect outside the title bar was a single
    /// status message that appeared if - and only if - the user happened to
    /// try an import. A Linux tester hit exactly that: the app came back up
    /// in Admin Client mode from a previous session, and neither he nor the
    /// report he sent could say so.</summary>
    public static bool RestoredFromMarker { get; } = IsActive;

    /// <summary>True once Admin Login succeeded IN THIS PROCESS. Never
    /// persisted; gates the Admin Console and every admin action after a
    /// restart, exactly per the §101 requirement.</summary>
    public static bool IsAuthenticated { get; private set; }

    private static bool startupStateLogged;

    /// <summary>§187: writes the startup data context to the log, exactly
    /// once. Called from MainWindowViewModel's constructor rather than from
    /// this class's own initializer on purpose: a type initializer can run
    /// before Program.Main configures Serilog, and anything written before
    /// then goes to the silent default logger and is lost - which is the
    /// same class of bug this line is here to end.</summary>
    public static void LogStartupState()
    {
        if (startupStateLogged)
            return;

        startupStateLogged = true;

        if (RestoredFromMarker)
        {
            Log.Information(
                "Admin Client mode restored from its marker file at startup - hunting data is the isolated " +
                "diagnostic session, hunt data import is refused, and the title bar reads ADMIN MODE. " +
                "Assign Client leaves it. Marker file: {Marker}",
                MarkerPath);
        }
        else
        {
            Log.Information("Admin Client mode not active at startup - the normal client data context is in use");
        }
    }

    /// <summary>Called by MainWindow's login flow on a successful
    /// AdminAuthService.Verify - the only caller. Note no credential ever
    /// reaches this class; it records only the fact of success.</summary>
    public static void MarkAuthenticated()
    {
        IsAuthenticated = true;
        AuthenticationChanged?.Invoke();
    }

    /// <summary>The explicit Admin Logout (§103): ends this process's
    /// authenticated admin session immediately. Deliberately does NOT touch
    /// IsActive - authentication controls ACCESS to admin controls, while
    /// Admin Client mode is a DATA CONTEXT choice; logging out locks the
    /// controls but leaves the isolation exactly as it was, the same
    /// separation the §101 restart behavior already relies on. Nothing about
    /// the session was ever persisted, so there is nothing to delete.</summary>
    public static void ClearAuthentication()
    {
        if (!IsAuthenticated)
            return;

        IsAuthenticated = false;
        Log.Information("Admin session ended by explicit logout");
        AuthenticationChanged?.Invoke();
    }

    public static void Enter()
    {
        if (!IsAuthenticated)
        {
            // Defensive - the UI only offers Enter after login, but a future
            // caller must not be able to bypass the gate by accident.
            Log.Warning("Admin Client requested without an authenticated session - ignored");
            return;
        }

        if (IsActive)
            return;

        IsActive = true;
        TryWriteMarker(true);
        Log.Information("Admin Client mode entered - hunting data context is now the isolated diagnostic session");
        ActiveChanged?.Invoke();
    }

    public static void Leave()
    {
        if (!IsActive)
            return;

        IsActive = false;
        TryWriteMarker(false);
        Log.Information("Admin Client mode left - hunting data context is the normal client again");
        ActiveChanged?.Invoke();
    }

    private static void TryWriteMarker(bool active)
    {
        try
        {
            if (active)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);

                // Content is irrelevant - existence is the flag. A word goes
                // in anyway so a curious admin opening the file learns what
                // it is for.
                File.WriteAllText(MarkerPath, "Admin Client mode was active when ProTracker last ran.");
            }
            else if (File.Exists(MarkerPath))
            {
                File.Delete(MarkerPath);
            }
        }
        catch (Exception ex)
        {
            // Worst case the next launch starts in the wrong MODE - which for
            // "wrongly in admin mode" is the write-protected direction, and
            // for "wrongly normal" is exactly the pre-§101 behavior.
            Log.Warning(ex, "Admin Client marker could not be updated");
        }
    }
}
