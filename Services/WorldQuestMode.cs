using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace Foot_Tracker.Services;

/// <summary>
/// §250. World Quest mode: a second isolated hunting session (see
/// IsolatedSession), entered from the World Quest menu, in which the tracker
/// hunts the running quest's species into a session of its own and touches
/// none of the normal client's records - not lifetime stats, not the Catch
/// Logs, not the encounter history, not the normal session file.
///
/// It is the Admin Client override's shape (§101) with three differences:
///
///   - No authentication. Anyone can toggle it; it protects data, it does
///     not grant anything.
///   - The quest species is FORCED as the sole target on entry, every time,
///     and Set Target is disabled while the mode is on. A quest session that
///     hunts something else is not a quest session.
///   - The session PERSISTS, per client and per quest, so closing the app
///     mid-quest and coming back finds the figures intact. The admin session
///     is in-memory by design; this one is not.
///
/// What this class owns is small on purpose: whether the mode is on, which
/// quest it is on for, and the marker that carries both across a restart.
/// The session swap, the forced target and the start/stop live in
/// MainWindowViewModel, which already owns every other session transition.
///
/// Like the admin marker, this one survives a restart deliberately.
/// Isolation resuming without being asked is the safe direction: the
/// alternative is an app that comes back in normal mode after a crash
/// mid-quest and quietly sends the next hour of quest catches into the
/// normal client's records. The quest id and species travel in the marker
/// so the right session can be reloaded before the quest is fetched again -
/// or without fetching it at all, if the events server is unreachable.
///
/// Admin Client takes precedence if both are somehow active, because it is
/// the more restrictive context, and Enter refuses while it is on rather
/// than layering one isolation over another.
/// </summary>
public static class WorldQuestMode
{
    /// <summary>What the marker carries: enough to reload the quest session
    /// and force the target without reaching the events server.</summary>
    public sealed record ActiveQuest(string MessageId, string Pokemon, DateTime EnteredUtc);

    private static readonly string MarkerPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker",
            "Database",
            "world-quest-mode.json");

    /// <summary>Raised on every Enter/Leave. Raised on the caller's thread;
    /// the only caller today is MainWindowViewModel, on the UI thread.</summary>
    public static event Action? ActiveChanged;

    /// <summary>The quest the mode is on for, or null when it is off.
    /// Restored from the marker at startup.</summary>
    public static ActiveQuest? Current { get; private set; } = ReadMarker();

    /// <summary>True while World Quest mode is the active data context.</summary>
    public static bool IsActive => Current is not null;

    /// <summary>§187's lesson applied here from the start: true when the
    /// mode was restored from the marker rather than entered in this
    /// process. Declared AFTER Current so the static initialiser order
    /// captures the restored value.</summary>
    public static bool RestoredFromMarker { get; } = IsActive;

    private static bool startupStateLogged;

    /// <summary>One log line at startup saying which data context the app
    /// woke up in. Called from MainWindowViewModel's constructor, after
    /// Serilog exists - see AdminModeService.LogStartupState for why not
    /// from a type initialiser.</summary>
    public static void LogStartupState()
    {
        if (startupStateLogged)
            return;

        startupStateLogged = true;

        if (RestoredFromMarker && Current is { } q)
        {
            Log.Information(
                "World Quest mode restored from its marker at startup - hunting {Pokemon} for quest {QuestId} " +
                "into the isolated quest session; the normal client's records are untouched. Marker: {Marker}",
                q.Pokemon, q.MessageId, MarkerPath);
        }
        else
        {
            Log.Information("World Quest mode not active at startup");
        }
    }

    /// <summary>Turns the mode on for <paramref name="messageId"/>. Returns
    /// false, and changes nothing, while Admin Client is active. Entering the
    /// same quest twice is a no-op; entering a different quest while one is
    /// on replaces it - the caller has already saved the outgoing session.</summary>
    public static bool Enter(string messageId, string pokemon)
    {
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(pokemon))
        {
            Log.Warning("World Quest mode requested without a quest id or species - ignored");
            return false;
        }

        if (AdminModeService.IsActive)
        {
            Log.Warning("World Quest mode requested while Admin Client is active - refused");
            return false;
        }

        if (Current is { } current && current.MessageId == messageId)
            return true;

        // §267: entered once is entered for good. The menu item colours for a
        // quest the player has not been into; leaving the mode must not put
        // the colour back, so the fact is recorded here - the one place every
        // entry passes through - rather than at any one call site.
        FirstRunNoticeService.MarkSeen(FirstRunNoticeService.WorldQuestEntered(messageId));

        Current = new ActiveQuest(messageId, pokemon, DateTime.UtcNow);
        TryWriteMarker();
        Log.Information("World Quest mode entered - hunting {Pokemon} for quest {QuestId} into the isolated quest session",
            pokemon, messageId);
        ActiveChanged?.Invoke();
        return true;
    }

    /// <summary>§298. Registered by MainWindowViewModel: leaves the mode the
    /// way the menu item does - the quest session stopped and saved under the
    /// client it was hunted on, the normal session back in its place, the
    /// stats panel switched over. <see cref="Leave"/> alone does none of
    /// that; it only clears the flag and the marker.
    ///
    /// It exists because the Admin Console can now end a quest for everyone,
    /// and the admin who ends the one they are hunting should come out of the
    /// mode rather than go on counting into a session for a quest that is
    /// over. The console has no reference to the main window and should not
    /// grow one, so the main window leaves a way to be asked. Same narrow
    /// bridge as TrackerRecoveryService's delegates, and the same reason.</summary>
    public static Func<Task>? LeaveHandler { get; set; }

    /// <summary>§298. Leaves through <see cref="LeaveHandler"/> when a main
    /// window has registered one, and falls back to the bare <see
    /// cref="Leave"/> when none has - which is the right answer when there is
    /// no session on screen to save. Call it from the UI thread: the handler
    /// it invokes is view-model work.</summary>
    public static Task LeaveThroughOwnerAsync()
    {
        if (LeaveHandler is { } handler)
            return handler();

        Leave();
        return Task.CompletedTask;
    }

    public static void Leave()
    {
        if (Current is null)
            return;

        ActiveQuest was = Current;
        Current = null;
        TryWriteMarker();
        Log.Information("World Quest mode left - quest {QuestId}; the normal client session is the data context again",
            was.MessageId);
        ActiveChanged?.Invoke();
    }

    private static ActiveQuest? ReadMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath))
                return null;

            ActiveQuest? read = JsonSerializer.Deserialize<ActiveQuest>(File.ReadAllText(MarkerPath));

            // A marker with no quest in it is a broken marker, not a mode.
            return read is { MessageId.Length: > 0, Pokemon.Length: > 0 } ? read : null;
        }
        catch
        {
            // Unreadable marker: start in normal mode. That is the pre-§250
            // behaviour, and the failure is logged by LogStartupState's
            // "not active" line rather than by a throw from a type
            // initialiser, which would take the whole class down.
            return null;
        }
    }

    private static void TryWriteMarker()
    {
        try
        {
            if (Current is { } q)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                File.WriteAllText(MarkerPath, JsonSerializer.Serialize(q, new JsonSerializerOptions { WriteIndented = true }));
            }
            else if (File.Exists(MarkerPath))
            {
                File.Delete(MarkerPath);
            }
        }
        catch (Exception ex)
        {
            // Worst case the next launch starts in the wrong MODE. Wrongly
            // in World Quest mode is the write-protected direction; wrongly
            // normal is exactly the pre-§250 behaviour.
            Log.Warning(ex, "World Quest mode marker could not be updated");
        }
    }
}
