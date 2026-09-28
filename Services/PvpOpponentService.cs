using Foot_Tracker.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Persists the "Previously Battled Users" battle log -
    /// Tracking/PvpTracker.cs calls RegisterBattle whenever it automatically
    /// detects a PVP battle, and ViewModels/PreviouslyBattledUsersViewModel.cs
    /// reads Opponents to display it. Follows the same in-memory-list-plus-
    /// JSON-file pattern as BossCooldownService.cs.
    ///
    /// This is a LOG, not a per-opponent summary: rebattling the same person
    /// adds a new entry rather than updating an existing one, so the same name
    /// can legitimately appear more than once. Keeps only the MaxSavedBattles
    /// most recent entries - the lifetime total per opponent (used for each
    /// entry's TimesBattled, and for a future "who have you battled most" PVP
    /// stats view) lives separately in
    /// LifetimeStats.PvpOpponentBattleCounts, which is never trimmed. Raises
    /// OpponentsChanged whenever the saved list changes so a currently-open
    /// "Previously Battled Users" window can stay live without a manual
    /// refresh.
    /// </summary>
    public static class PvpOpponentService
    {
        // Caps how many individual battles are kept - without this the save
        // file (and the "Previously Battled Users" window's list) would grow
        // forever over months of play. 250 comfortably covers a very active PVP
        // player's recent history; TrimToMostRecent drops whichever battles
        // happened longest ago once this is exceeded. The lifetime per-opponent
        // count (LifetimeStats.PvpOpponentBattleCounts) is unaffected by this -
        // only this rolling log is capped.
        private const int MaxSavedBattles = 250;

        /// <summary>§276. How many distinct message-box lines one battle
        /// keeps. A long PVP match prints a great many, and this log holds 250
        /// battles - the cap is what stops a rolling log from becoming a
        /// transcript archive. The lines kept are the FIRST ones seen, not the
        /// last: a match's opening moves are what identify the opponent's
        /// team, which is what these are being collected for.</summary>
        public const int MaxLogLinesPerBattle = 120;

        /// <summary>Raised after the saved battle log changes - a new battle
        /// registered, or the MaxSavedBattles cap trimmed an old entry.
        /// PreviouslyBattledUsersViewModel subscribes to this to keep its list
        /// current while its window is open, instead of needing a manual refresh
        /// button. Raised from whatever thread called RegisterBattle - that's
        /// PvpTracker's background tracking loop, not the UI thread, so
        /// subscribers must marshal back to the UI thread themselves (same
        /// requirement as PvpTracker's own events).</summary>
        public static event Action? OpponentsChanged;

        // Same shared folder LifetimeStatsService/SessionPersistenceService/
        // BossCooldownService save to (%LocalAppData%\ProTracker\Database)
        // rather than a "Data" folder next to the built executable - that way
        // the battled-users list survives a rebuild/republish (which wipes the
        // bin output folder) the same way lifetime stats and session saves do.
        private static readonly string SaveFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "ProTracker",
                "Database"
            );

        // Each PVP battle log is specific to whichever PRO account is logged
        // into the client this app instance is tracking (see
        // SessionPersistenceService.ActiveClientNumber, the same number
        // current-session-client{N}.json is already keyed by) - two accounts
        // tracked by two instances shouldn't have their battle histories
        // commingled into one shared list. Falls back to a client-less shared
        // file only if somehow no client number is set yet - shouldn't
        // normally happen, since SessionPersistenceService defaults to client 1.
        private static string GetSavePath()
        {
            int clientNumber = SessionPersistenceService.ActiveClientNumber;

            string fileName = clientNumber >= 1
                ? $"pvp-opponents-client{clientNumber}.json"
                : "pvp-opponents.json";

            return Path.Combine(SaveFolder, fileName);
        }

        // Two prior save locations, oldest first - both from before per-client
        // files existed, so there's no "which client" to preserve; see
        // MigrateLegacySaveIfNeeded for why only client 1 inherits either one.
        private static readonly string LegacyNextToExePath =
            Path.Combine(
                AppContext.BaseDirectory,
                "Data",
                "pvp-opponents.json"
            );

        private static readonly string LegacySharedPath =
            Path.Combine(
                SaveFolder,
                "pvp-opponents.json"
            );

        private static readonly List<PvpOpponentEntry>
            opponents = new();

        private static bool loaded;

        /// <summary>Loads from disk on first access - callers don't need to call
        /// Load() explicitly first (mirrors how BossCooldownService.Cooldowns is
        /// normally used after an explicit Load() call at startup, but this list is
        /// read from more places - the tracker and the display window both - so
        /// lazy-loading here avoids needing a second startup wiring point).</summary>
        public static IReadOnlyList<PvpOpponentEntry> Opponents
        {
            get
            {
                EnsureLoaded();
                return opponents;
            }
        }

        private static void EnsureLoaded()
        {
            if (loaded)
                return;

            loaded = true;

            LoadFromDisk();
        }

        // Shared by EnsureLoaded (first access) and ReloadForActiveClient
        // (switching which client this instance is tracking).
        private static void LoadFromDisk()
        {
            opponents.Clear();

            string savePath = GetSavePath();

            MigrateLegacySaveIfNeeded(savePath);

            if (!File.Exists(savePath))
                return;

            try
            {
                string json = File.ReadAllText(savePath);

                List<PvpOpponentEntry>? deserialized =
                    JsonSerializer.Deserialize<List<PvpOpponentEntry>>(json);

                if (deserialized != null)
                {
                    opponents.AddRange(deserialized);
                }

                // Defensive - covers a legacy save from before MaxSavedBattles
                // existed, or a manually edited file, exceeding the cap.
                TrimToMostRecent();
            }
            catch
            {
                // Keep an empty list if the file is damaged, same as
                // BossCooldownService/other JSON-backed services in this app.
            }
        }

        /// <summary>Forces a fresh reload from the now-active client's own save
        /// file - called by MainWindowViewModel.AssignTrackerClient right after
        /// SessionPersistenceService.SetActiveClient, so switching which PRO
        /// client this app instance is tracking swaps in that client's own
        /// battle log instead of continuing to show/append to whichever
        /// client's list was already loaded. Raises OpponentsChanged so an
        /// open "Previously Battled Users" window updates immediately rather
        /// than showing the previous client's list until the next battle.
        /// </summary>
        public static void ReloadForActiveClient()
        {
            loaded = true;

            LoadFromDisk();

            OpponentsChanged?.Invoke();
        }

        // Drops the oldest battles once the log exceeds MaxSavedBattles - see
        // that constant's comment for why.
        private static void TrimToMostRecent()
        {
            if (opponents.Count <= MaxSavedBattles)
                return;

            List<PvpOpponentEntry> toRemove = opponents
                .OrderBy(x => x.BattledAtUtc)
                .Take(opponents.Count - MaxSavedBattles)
                .ToList();

            foreach (PvpOpponentEntry entry in toRemove)
            {
                opponents.Remove(entry);
            }
        }

        // One-time copy from a prior save location into the current client's
        // per-client file - covers both the original next-to-the-exe location
        // and the shared (not-yet-per-client) ProTracker/Database file it later
        // moved to, so a rebuild/republish or this per-client upgrade doesn't
        // silently reset everyone's battled-users list back to empty. Only
        // migrates into client 1: before per-client files existed there was
        // only ever one shared save, so it's treated as belonging to the
        // default/first client rather than guessing which client it "really"
        // tracked - clients 2+ simply start with an empty list, same as any
        // other newly-tracked client would.
        private static void MigrateLegacySaveIfNeeded(string savePath)
        {
            try
            {
                if (File.Exists(savePath))
                    return;

                if (SessionPersistenceService.ActiveClientNumber != 1)
                    return;

                string? legacySource =
                    File.Exists(LegacySharedPath) ? LegacySharedPath :
                    File.Exists(LegacyNextToExePath) ? LegacyNextToExePath :
                    null;

                if (legacySource is null)
                    return;

                Directory.CreateDirectory(SaveFolder);

                File.Copy(
                    legacySource,
                    savePath,
                    overwrite: false
                );
            }
            catch
            {
                // Migration failure must never stop the application - worst
                // case, the battled-users list starts fresh in the new location.
            }
        }

        private static void Save()
        {
            string savePath = GetSavePath();

            string? folder = Path.GetDirectoryName(savePath);

            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            string json = JsonSerializer.Serialize(
                opponents,
                new JsonSerializerOptions { WriteIndented = true });

            DurableFile.WriteAllText(savePath, json);
        }

        /// <summary>
        /// Records one PVP battle against <paramref name="opponentName"/> - ALWAYS
        /// adds a new entry, even if this opponent already has other entries in
        /// the list (this is a battle log, not a per-opponent summary - see the
        /// class doc comment). The lifetime count used for the new entry's
        /// TimesBattled comes from LifetimeStatsService.AddPvpBattle, which
        /// tracks it forever, independent of this list's MaxSavedBattles cap.
        /// Matching against that lifetime count is case-insensitive since OCR
        /// can't reliably preserve exact capitalization every time.
        ///
        /// Phase 1 only tracks the opponent's name. The planned follow-up (reading
        /// their team's Pokemon) will need its own per-entry Pokemon list plus a
        /// "confirmed roster slot" concept - PRO lets a player recall an active
        /// Pokemon and send the same one back out later in the same match, and
        /// without de-duplicating against Pokemon already seen fainted/recalled in
        /// this same battle, a naive "log whatever's on screen" approach would
        /// double-count that Pokemon as two separate team members instead of
        /// recognizing it as a re-send. That will need to live either as a
        /// per-battle "already seen this match" set (reset when a new battle title
        /// is detected, mirroring EncounterTracker's per-battle lock) or matched
        /// against the roster already saved on this PvpOpponentEntry.
        /// </summary>
        /// <returns>§276. The entry just created, so the caller can hand back
        /// the battle's result and its message lines as they arrive - see
        /// RecordOutcome and AddLogLine. Null when nothing was recorded
        /// (Admin Client, or a blank name), which the caller must treat as
        /// "there is no battle to update" rather than as a failure.</returns>
        public static PvpOpponentEntry? RegisterBattle(string opponentName)
        {
            // Admin Client isolation (§101) - see AdminModeService.
            if (AdminModeService.IsActive)
                return null;

            if (string.IsNullOrWhiteSpace(opponentName))
                return null;

            EnsureLoaded();

            LifetimeStats stats = LifetimeStatsService.AddPvpBattle(opponentName);

            long lifetimeCount = stats.PvpOpponentBattleCounts.TryGetValue(
                opponentName,
                out long count)
                    ? count
                    : 1;

            var entry = new PvpOpponentEntry
            {
                Name = opponentName,
                TimesBattled = (int)Math.Min(lifetimeCount, int.MaxValue),
                BattledAtUtc = DateTime.UtcNow
            };

            opponents.Add(entry);

            TrimToMostRecent();
            Save();

            OpponentsChanged?.Invoke();

            return entry;
        }

        /// <summary>
        /// §276. Writes the result PRO reported onto the battle it belonged
        /// to. The outcome has been read since §91 - it fed the lifetime PVP
        /// tallies - and was then thrown away; this is where it lands.
        ///
        /// Only ever called with a result that was actually READ. A battle the
        /// tracker gave up on (window unreadable, a disconnect) is left with
        /// an empty Outcome on purpose, because "we never saw" and "you lost"
        /// are different facts.
        /// </summary>
        public static void RecordOutcome(PvpOpponentEntry? entry, bool won)
        {
            if (!Owns(entry))
                return;

            entry!.Outcome = won ? "Won" : "Lost";

            Save();

            OpponentsChanged?.Invoke();
        }

        /// <summary>
        /// §276. Appends one message-box line to a battle in progress, if it
        /// is new to that battle and there is still room under
        /// <see cref="MaxLogLinesPerBattle"/>. Returns true when the line was
        /// actually kept, so the caller can save on a change rather than on
        /// every scan - PRO's message box holds the same sentence for several
        /// seconds, which at the tracker's cadence is a dozen identical reads.
        ///
        /// Distinctness is by exact text within THIS battle only. The same
        /// move used twice prints the same line twice and is kept once, which
        /// is the right trade here: this is a record of what was seen, not a
        /// turn-by-turn transcript, and OCR noise would make counting
        /// repetitions meaningless anyway.
        /// </summary>
        public static bool AddLogLine(PvpOpponentEntry? entry, string line)
        {
            if (!Owns(entry))
                return false;

            string cleaned = (line ?? string.Empty).Trim();

            if (cleaned.Length == 0)
                return false;

            if (entry!.BattleLog.Count >= MaxLogLinesPerBattle)
                return false;

            if (entry.BattleLog.Contains(cleaned, StringComparer.Ordinal))
                return false;

            entry.BattleLog.Add(cleaned);

            return true;
        }

        /// <summary>§276. Persists whatever AddLogLine has accumulated. Split
        /// from AddLogLine so a battle's lines cost one write rather than one
        /// write each.</summary>
        public static void SaveLog(PvpOpponentEntry? entry)
        {
            if (!Owns(entry))
                return;

            Save();

            OpponentsChanged?.Invoke();
        }

        /// <summary>§276. Whether this battle is still in the log at all. A
        /// tracked battle can be trimmed out from under its tracker
        /// (MaxSavedBattles) or cleared by hand while it is still being
        /// fought, and writing to an entry the log no longer holds would
        /// silently update nothing and save the file for no reason.</summary>
        private static bool Owns(PvpOpponentEntry? entry)
        {
            if (entry is null)
                return false;

            EnsureLoaded();

            return opponents.Contains(entry);
        }

        /// <summary>
        /// Removes the single most recently registered battle from the log -
        /// backs the "Remove Previous" button in PreviouslyBattledUsersWindow.
        /// There's no per-row selection in that window's list (it's a plain
        /// read-only ItemsControl), so "most recent first" - already how the
        /// list is displayed - is what "previous" refers to here. Does
        /// nothing if the log is already empty. Deliberately does NOT touch
        /// LifetimeStats.PvpOpponentBattleCounts, same reasoning as
        /// TrimToMostRecent: removing a battle from this rolling log doesn't
        /// undo that it happened, so the lifetime "battled most" count stays
        /// intact.
        /// </summary>
        public static void RemoveMostRecent()
        {
            EnsureLoaded();

            if (opponents.Count == 0)
                return;

            PvpOpponentEntry mostRecent = opponents
                .OrderByDescending(x => x.BattledAtUtc)
                .First();

            opponents.Remove(mostRecent);

            Save();

            OpponentsChanged?.Invoke();
        }

        /// <summary>
        /// Wipes the entire saved battle log - backs the "Clear All" button in
        /// PreviouslyBattledUsersWindow, always shown behind a confirmation
        /// prompt since this can't be undone. Same as RemoveMostRecent, this
        /// only clears the rolling log; LifetimeStats.PvpOpponentBattleCounts
        /// is untouched, so lifetime "battled most" totals survive a clear.
        /// </summary>
        public static void ClearAll()
        {
            EnsureLoaded();

            if (opponents.Count == 0)
                return;

            opponents.Clear();

            Save();

            OpponentsChanged?.Invoke();
        }
    }
}
