using System.Diagnostics;
using System.Text.Json;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services
{
    public static class SessionPersistenceService
    {
        private static readonly string SessionFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "ProTracker",
                "Database"
            );

        // Old single-client file.
        private static readonly string LegacySessionPath =
            Path.Combine(
                SessionFolder,
                "current-session.json"
            );

        private static readonly string VeryOldSessionPath =
            Path.Combine(
                AppContext.BaseDirectory,
                "DataFiles",
                "current-session.json"
            );

        // Remembers which client was last active across app restarts, so the
        // previous hunt's data shows up immediately on launch instead of only
        // after Play assigns a client. Just a plain text file with one number.
        private static readonly string LastActiveClientPath =
            Path.Combine(
                SessionFolder,
                "last-active-client.txt"
            );

        private static int activeClientNumber =
            LoadLastActiveClientNumber();

        private static int LoadLastActiveClientNumber()
        {
            try
            {
                if (File.Exists(LastActiveClientPath))
                {
                    string text =
                        File.ReadAllText(LastActiveClientPath).Trim();

                    if (int.TryParse(text, out int saved) && saved >= 1)
                        return saved;
                }
            }
            catch
            {
                // Fall through to the default below.
            }

            // Default to client 1 - the common single-client case - rather
            // than 0/"no client", so a fresh install still shows *something*
            // once a session has actually been saved once.
            return 1;
        }

        // ============================================================
        // CLIENT ASSIGNMENT
        // ============================================================

        // True only once THIS process has actually won the cross-process file
        // lock below for whatever activeClientNumber currently holds - NOT
        // just whenever activeClientNumber happens to be nonzero (which
        // defaults to 1 via LoadLastActiveClientNumber() even before any
        // client has been assigned this run). ActiveClientNumber below - and
        // therefore every per-client save (this class's own Save/Load/Delete,
        // plus BossCooldownService/PvpOpponentService/
        // AppearanceSettingsRepository/UiPreferencesService, which all key
        // their own file names off ActiveClientNumber) - is gated on this.
        //
        // This closes a real bug: opening two tracker windows while only one
        // PRO client is running used to make BOTH of them silently default to
        // "client 1" (nothing stopped them sharing that number), so whichever
        // window closed last overwrote the other's data on save - even the
        // window that was never actually used for hunting. Now a second
        // window that can't win the lock never binds to that client number at
        // all, so it never saves anything under it.
        private static bool clientLockHeld;

        /// <summary>Set by SetActiveClient/IsClientLockAvailable when another
        /// still-running Pro Tracker process already holds the requested
        /// client's lock - lets MainWindowViewModel show a specific,
        /// actionable warning instead of a generic one.</summary>
        public static int? LastLockConflictProcessId { get; private set; }

        /// <summary>
        /// Attempts to bind this process to clientNumber. Returns false without
        /// changing anything if another still-running Pro Tracker process
        /// already holds that client's lock - see clientLockHeld's remarks
        /// above. Callers (see MainWindowViewModel.AssignTrackerClient) should
        /// check IsClientLockAvailable first, before pausing/saving whatever
        /// client they're currently on, so a failed reassignment doesn't
        /// disrupt an in-progress hunt for nothing.
        ///
        /// §105: <paramref name="force"/> claims the lock even when another
        /// live tracker window holds it - the client slots double as data
        /// PROFILES now, and a profile you can never switch to is not much of
        /// a profile. This is not a way to make two windows share one slot:
        /// the claim overwrites the lock file with this process's own PID, and
        /// the window that held it before notices on its next ownership check
        /// (StillOwnsClientLock below), saves once and steps down. Only ever
        /// passed true from an explicit, confirmed user action.
        /// </summary>
        public static bool SetActiveClient(
            int clientNumber,
            bool force = false)
        {
            if (clientNumber < 1)
            {
                ReleaseCurrentClientLock();
                activeClientNumber = 0;
                return true;
            }

            if (!TryClaimClientLock(clientNumber, force, out int? heldByProcessId))
            {
                LastLockConflictProcessId = heldByProcessId;
                return false;
            }

            ReleaseCurrentClientLock();

            activeClientNumber =
                clientNumber;

            clientLockHeld = true;
            LastLockConflictProcessId = null;

            try
            {
                Directory.CreateDirectory(SessionFolder);
                DurableFile.WriteAllText(LastActiveClientPath, clientNumber.ToString());
            }
            catch
            {
                // Non-critical - worst case, the next launch falls back to
                // whatever client number was last successfully remembered.
            }

            MigrateLegacySessionIfNeeded();
            return true;
        }

        // Gated on clientLockHeld, not just "activeClientNumber is nonzero" -
        // see clientLockHeld's remarks above.
        public static int ActiveClientNumber =>
            clientLockHeld ? activeClientNumber : 0;

        /// <summary>
        /// Like ActiveClientNumber, but falls back to Client 1 instead of "no
        /// client" while this process hasn't (yet, or ever this run) won a
        /// client lock - by request, so the app's look doesn't reset to a
        /// generic default the moment it launches, before auto-detection has
        /// had a chance to run.
        ///
        /// Deliberately only for AppearanceSettingsRepository/
        /// UiPreferencesService - "which client's colors/fonts/stats-panel-
        /// side should I show right now" is read-mostly and low-stakes if
        /// briefly wrong (worst case, a one-time flash of client 1's look
        /// before this instance's real client locks and corrects it). Hunt
        /// session data, boss cooldowns, and the PVP log go through the
        /// strictly-gated ActiveClientNumber above instead, with no such
        /// fallback - those are only ever written by this process's own
        /// automatic/background saves, so guessing wrong there means actually
        /// overwriting another window's real data, not just a momentary
        /// cosmetic mismatch. This is the exact "two trackers, one PRO
        /// client" bug ActiveClientNumber's lock exists to prevent.
        /// </summary>
        public static int AppearanceClientNumber =>
            clientLockHeld ? activeClientNumber : 1;

        // ============================================================
        // CLIENT LOCK - one small lock file per client number
        // (client-lock-{N}.txt, holding the owning process's PID) so a second
        // tracker instance - a completely separate OS process with no shared
        // memory - can still tell that a client number is already spoken for.
        // A lock is treated as free again once the PID inside it no longer
        // belongs to a running copy of this app - covers a crash/force-kill
        // that skipped ReleaseActiveClient's cleanup.
        // ============================================================

        private static string GetClientLockPath(
            int clientNumber) =>
            Path.Combine(
                SessionFolder,
                $"client-lock-{clientNumber}.txt"
            );

        /// <summary>Read-only peek - does not claim anything. Lets a caller
        /// warn the user and bail out before disturbing whatever client it's
        /// currently on, rather than only finding out after already
        /// pausing/saving it (see MainWindowViewModel.AssignTrackerClient).</summary>
        public static bool IsClientLockAvailable(
            int clientNumber,
            out int? heldByProcessId)
        {
            heldByProcessId = null;

            if (clientNumber < 1)
                return true;

            try
            {
                string lockPath =
                    GetClientLockPath(clientNumber);

                if (!File.Exists(lockPath))
                    return true;

                string text =
                    File.ReadAllText(lockPath).Trim();

                if (int.TryParse(text, out int existingPid) &&
                    existingPid != Environment.ProcessId &&
                    IsProcessAlive(existingPid))
                {
                    heldByProcessId = existingPid;
                    return false;
                }

                return true;
            }
            catch
            {
                // Can't read the lock file for some reason - fail open rather
                // than permanently blocking tracking over an IO hiccup.
                return true;
            }
        }

        private static bool TryClaimClientLock(
            int clientNumber,
            bool force,
            out int? heldByProcessId)
        {
            // §105: a forced claim still READS who held it (callers log and
            // report the displaced PID), it just doesn't stop for them.
            if (!IsClientLockAvailable(clientNumber, out heldByProcessId) && !force)
                return false;

            try
            {
                Directory.CreateDirectory(SessionFolder);

                // §193: deliberately a plain write, not a durable one. This is
                // a lock file whose whole meaning is "a process is alive right
                // now"; it is rewritten constantly, it is worthless after a
                // crash by design, and paying a disk flush for it on every
                // tick would be a cost for no benefit.
                File.WriteAllText(
                    GetClientLockPath(clientNumber),
                    Environment.ProcessId.ToString());

                return true;
            }
            catch
            {
                // Same "fail open" reasoning as IsClientLockAvailable above.
                return true;
            }
        }

        private static bool IsProcessAlive(
            int pid)
        {
            try
            {
                using Process process =
                    Process.GetProcessById(pid);

                // Guards against a rare PID-reuse false positive (the process
                // that originally held this lock exited and some unrelated
                // program was later assigned the same PID) - only count it as
                // "still holding the lock" if it's actually still another
                // copy of this same app.
                return !process.HasExited && IsSameApplication(process);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>§223. Whether that PID is another copy of THIS app.
        ///
        /// ProcessName alone cannot answer that on Linux. The kernel records
        /// a process's name in /proc/&lt;pid&gt;/comm capped at fifteen
        /// characters, and this app is called ProTrackerDatabase.Avalonia -
        /// twenty-seven. Everything past "ProTrackerDatab" is gone, so both
        /// sides of the old comparison were stumps and an exact Equals on
        /// them is asking a question neither string can carry.
        ///
        /// /proc/&lt;pid&gt;/cmdline holds the full argv, untruncated, which
        /// is exactly what LinuxX11WindowCaptureService.ProcessNameMatches
        /// already reads for the same reason - that one's comment even notes
        /// the truncation is "not an issue for names like PROClient", nine
        /// characters. It is very much an issue for twenty-seven.
        ///
        /// Where cmdline can be read it decides. Where it cannot, the names
        /// are compared over the length they share, so a stump at least
        /// matches a stump. Windows and macOS keep the exact comparison they
        /// always had - neither truncates, and loosening it there would
        /// invent a false positive that never existed.</summary>
        private static bool IsSameApplication(
            Process process)
        {
            string mine =
                Process.GetCurrentProcess().ProcessName;

            string theirs = process.ProcessName;

            if (!OperatingSystem.IsLinux())
                return mine.Equals(theirs, StringComparison.OrdinalIgnoreCase);

            string? theirExe = ReadLinuxExecutableName(process.Id);
            string? myExe = ReadLinuxExecutableName(Environment.ProcessId);

            if (theirExe != null && myExe != null)
                return theirExe.Equals(myExe, StringComparison.OrdinalIgnoreCase);

            int shared = Math.Min(mine.Length, theirs.Length);

            return shared > 0 &&
                   string.Equals(
                       mine.Substring(0, shared),
                       theirs.Substring(0, shared),
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>§223. The untruncated executable name behind a PID, from
        /// the first token of its argv. Null when /proc is unreadable, which
        /// is the caller's cue to fall back rather than to guess.</summary>
        private static string? ReadLinuxExecutableName(
            int pid)
        {
            try
            {
                string cmdlinePath = $"/proc/{pid}/cmdline";

                if (!File.Exists(cmdlinePath))
                    return null;

                string first = File.ReadAllText(cmdlinePath)
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? string.Empty;

                return first.Length == 0
                    ? null
                    : Path.GetFileName(first);
            }
            catch
            {
                return null;
            }
        }

        private static void ReleaseCurrentClientLock()
        {
            if (!clientLockHeld || activeClientNumber < 1)
                return;

            try
            {
                string lockPath =
                    GetClientLockPath(activeClientNumber);

                if (File.Exists(lockPath))
                {
                    string text =
                        File.ReadAllText(lockPath).Trim();

                    if (int.TryParse(text, out int existingPid) &&
                        existingPid == Environment.ProcessId)
                    {
                        File.Delete(lockPath);
                    }
                }
            }
            catch
            {
                // Non-critical - a stale lock file left behind here is still
                // safely recovered later by IsProcessAlive's dead-PID check.
            }

            clientLockHeld = false;
        }

        /// <summary>
        /// §105: does this process still own the lock for the client it is
        /// bound to? False means another window force-claimed this slot (see
        /// SetActiveClient's force parameter) - the caller is expected to save
        /// once and then step down via StepDownFromForcedTakeover, so the two
        /// windows never write the same per-client files.
        ///
        /// Cheap enough for a periodic UI-timer check: one small file read,
        /// and only while this process actually holds a lock. Unreadable file
        /// counts as "still ours" - failing open here just leaves things
        /// exactly as they were, whereas failing closed would unbind a healthy
        /// window over a transient IO hiccup.
        /// </summary>
        public static bool StillOwnsClientLock()
        {
            if (!clientLockHeld || activeClientNumber < 1)
                return true;

            try
            {
                string lockPath = GetClientLockPath(activeClientNumber);

                if (!File.Exists(lockPath))
                    return false;

                string text = File.ReadAllText(lockPath).Trim();

                return int.TryParse(text, out int ownerPid) &&
                       ownerPid == Environment.ProcessId;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// §105: the losing half of a forced takeover. Drops this process's
        /// binding WITHOUT deleting the lock file - it belongs to the window
        /// that took the slot now, and deleting it would hand the slot to a
        /// third window. Everything per-client goes through ActiveClientNumber,
        /// which is gated on clientLockHeld, so after this call every save in
        /// the app (session, boss cooldowns, PVP log, catch logs, history)
        /// simply no-ops for that client instead of racing the new owner.
        /// Nothing in memory is cleared: the window keeps showing the hunt it
        /// was showing, it just stops writing it down.
        /// </summary>
        public static void StepDownFromForcedTakeover()
        {
            clientLockHeld = false;
            activeClientNumber = 0;
        }

        /// <summary>Call when the app is shutting down (see
        /// MainWindowViewModel.OnClosing) so the next tracker to claim this
        /// client number doesn't have to wait for the stale-PID check to
        /// notice this process is gone.</summary>
        public static void ReleaseActiveClient() =>
            ReleaseCurrentClientLock();

        // ============================================================
        // PATH
        // ============================================================

        private static string? GetSessionPath()
        {
            int clientNumber =
                ActiveClientNumber;

            if (clientNumber <= 0)
                return null;

            return Path.Combine(
                SessionFolder,
                $"current-session-client{clientNumber}.json"
            );
        }

        // ============================================================
        // LEGACY MIGRATION
        // ============================================================

        private static void MigrateLegacySessionIfNeeded()
        {
            try
            {
                Directory.CreateDirectory(
                    SessionFolder
                );

                string? newPath =
                    GetSessionPath();

                if (newPath == null)
                    return;

                // This client already has its own save.
                if (File.Exists(newPath))
                    return;

                // First try the existing AppData single-client save.
                if (File.Exists(LegacySessionPath))
                {
                    File.Copy(
                        LegacySessionPath,
                        newPath,
                        overwrite: false
                    );

                    return;
                }

                // Then try the very old application-folder save.
                if (File.Exists(VeryOldSessionPath))
                {
                    File.Copy(
                        VeryOldSessionPath,
                        newPath,
                        overwrite: false
                    );
                }
            }
            catch
            {
                // Migration failure must never stop the application.
            }
        }

        // ============================================================
        // SAVE
        // ============================================================

        public static void Save(
            HuntSession session)
        {
            string? sessionPath =
                GetSessionPath();

            // No client has been selected yet.
            //
            // Do not write a shared session file because two
            // tracker processes could overwrite one another.
            if (sessionPath == null)
                return;

            Directory.CreateDirectory(
                SessionFolder
            );

            var data =
                new HuntSessionSaveData
                {
                    TargetPokemons =
                        new List<string>(session.TargetPokemons),

                    CurrentEncounter =
                        session.CurrentEncounter,

                    PreviousEncounter =
                        session.PreviousEncounter,

                    // §139.
                    CurrentEncounterForm =
                        session.CurrentEncounterForm,

                    CurrentEncounterFormImage =
                        session.CurrentEncounterFormImage,

                    PreviousEncounterForm =
                        session.PreviousEncounterForm,

                    PreviousEncounterFormImage =
                        session.PreviousEncounterFormImage,

                    TotalEncounters =
                        session.TotalEncounters,

                    EncountersSinceShiny =
                        session.EncountersSinceShiny,

                    EncountersSinceForm =
                        session.EncountersSinceForm,

                    SinceFormPaused =
                        session.SinceFormPaused,

                    SuccessfulCatches =
                        session.SuccessfulCatches,

                    FailedCatches =
                        session.FailedCatches,

                    ElapsedTime =
                        session.GetCurrentElapsedTime(),

                    EncounterCounts =
                        new Dictionary<string, int>(
                            session.EncounterCounts,
                            StringComparer.OrdinalIgnoreCase
                        ),

                    // §123. Copied rather than referenced, same as
                    // EncounterCounts above: the session keeps mutating
                    // while this object is being serialized on the way out.
                    CaughtCounts =
                        new Dictionary<string, int>(
                            session.CaughtCounts,
                            StringComparer.OrdinalIgnoreCase
                        ),

                    RanFromCounts =
                        new Dictionary<string, int>(
                            session.RanFromCounts,
                            StringComparer.OrdinalIgnoreCase
                        ),

                    LastEncounteredUtc =
                        new Dictionary<string, DateTime>(
                            session.LastEncounteredUtc,
                            StringComparer.OrdinalIgnoreCase
                        )
                };

            string json =
                JsonSerializer.Serialize(
                    data,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                );

            // §193: this is the hunt itself, rewritten every few seconds
            // while tracking, and it wrote a temp file and renamed it - which
            // looks atomic and is not. The rename is journalled metadata; the
            // contents were only in the operating system's cache. DurableFile
            // does the same two steps with the flush in between that makes
            // their order mean anything.
            DurableFile.WriteAllText(
                sessionPath,
                json
            );
        }

        // ============================================================
        // LOAD
        // ============================================================

        public static HuntSessionSaveData? Load()
        {
            string? sessionPath =
                GetSessionPath();

            if (sessionPath == null)
                return null;

            MigrateLegacySessionIfNeeded();

            if (!File.Exists(sessionPath))
                return null;

            try
            {
                string json =
                    File.ReadAllText(
                        sessionPath
                    );

                return
                    JsonSerializer.Deserialize<HuntSessionSaveData>(
                        json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        }
                    );
            }
            catch
            {
                return null;
            }
        }

        // ============================================================
        // DELETE
        // ============================================================

        public static void Delete()
        {
            string? sessionPath =
                GetSessionPath();

            if (sessionPath == null)
                return;

            if (File.Exists(sessionPath))
            {
                File.Delete(
                    sessionPath
                );
            }
        }
    }

    public class HuntSessionSaveData
    {
        // Kept only so a save file from before multi-target support still loads
        // correctly - see HuntSession.Restore()'s migration logic. New saves
        // populate TargetPokemons instead; this stays empty for them.
        public string TargetPokemon { get; set; } =
            string.Empty;

        public List<string> TargetPokemons { get; set; } =
            new();

        public string CurrentEncounter { get; set; } =
            string.Empty;

        public string PreviousEncounter { get; set; } =
            string.Empty;

        // §139. Nullable for the same reason as the §123 dictionaries
        // below: a file written before the encounter cards showed forms has
        // none of these, and HuntSession.Restore reads null back as an
        // ordinary encounter.
        public string? CurrentEncounterForm { get; set; }

        public string? CurrentEncounterFormImage { get; set; }

        public string? PreviousEncounterForm { get; set; }

        public string? PreviousEncounterFormImage { get; set; }

        public int TotalEncounters { get; set; }

        public int EncountersSinceShiny { get; set; }

        public int EncountersSinceForm { get; set; }

        public bool SinceFormPaused { get; set; }

        public int SuccessfulCatches { get; set; }

        public int FailedCatches { get; set; }

        public TimeSpan ElapsedTime { get; set; }

        public Dictionary<string, int>
            EncounterCounts
        { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        // §123. Nullable on purpose. A save file written before this
        // change simply has no such properties, and System.Text.Json leaves
        // a missing property at its default - which for a non-nullable
        // initialised dictionary would be the initialiser, and for these is
        // null. Restore() checks for null and carries on, so an old save
        // loads exactly as it did before rather than throwing.
        public Dictionary<string, int>? CaughtCounts { get; set; }

        public Dictionary<string, int>? RanFromCounts { get; set; }

        public Dictionary<string, DateTime>? LastEncounteredUtc { get; set; }
    }
}