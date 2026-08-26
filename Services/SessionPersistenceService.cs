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
        /// </summary>
        public static bool SetActiveClient(
            int clientNumber)
        {
            if (clientNumber < 1)
            {
                ReleaseCurrentClientLock();
                activeClientNumber = 0;
                return true;
            }

            if (!TryClaimClientLock(clientNumber, out int? heldByProcessId))
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
                File.WriteAllText(LastActiveClientPath, clientNumber.ToString());
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
            out int? heldByProcessId)
        {
            if (!IsClientLockAvailable(clientNumber, out heldByProcessId))
                return false;

            try
            {
                Directory.CreateDirectory(SessionFolder);

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
                return !process.HasExited &&
                       process.ProcessName.Equals(
                           Process.GetCurrentProcess().ProcessName,
                           StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
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

            string tempPath =
                sessionPath + ".tmp";

            File.WriteAllText(
                tempPath,
                json
            );

            File.Move(
                tempPath,
                sessionPath,
                overwrite: true
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
    }
}