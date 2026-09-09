using System;
using System.Threading.Tasks;

namespace Foot_Tracker.Services;

/// <summary>
/// The narrow bridge between the Admin Console and the tracking pipeline
/// MainWindowViewModel owns (MIGRATION_GUIDE.md §101). The view model
/// registers two delegates at construction; the console invokes them and
/// never sees the tracker, the capture service, or any hunting data. Both
/// hooks are also what the §101 watchdog uses internally, so there is
/// exactly one restart implementation and it can never run twice
/// concurrently (the view model guards it with its own recovering flag) -
/// no duplicate detector loops by construction.
/// </summary>
public static class TrackerRecoveryService
{
    /// <summary>Stops the current tracking task (awaited - the old loop is
    /// genuinely gone before a new one starts) and starts a fresh one IF
    /// tracking was running. Registered by MainWindowViewModel.</summary>
    public static Func<Task<string>>? RestartTrackingAsync { get; set; }

    /// <summary>Re-runs PRO window discovery and re-binds the capture
    /// service to the found window. Registered by MainWindowViewModel.</summary>
    public static Func<string>? ReacquireGameWindow { get; set; }

    /// <summary>§103, the console's "Force client #": deliberately binds the
    /// tracker to a specific client number through the same
    /// AssignTrackerClient path the pickers use - with leaveAdminMode:false,
    /// so forcing a binding for diagnostics never silently ends Admin Client
    /// isolation. Returns a one-line outcome. Registered by
    /// MainWindowViewModel.</summary>
    public static Func<int, string>? ForceClientAssign { get; set; }
}
