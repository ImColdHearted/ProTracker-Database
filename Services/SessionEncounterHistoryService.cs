using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using Foot_Tracker.Models;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// The per-Pokemon SESSION encounter history behind
    /// SessionEncounterHistoryWindow - one SessionEncounterRecord for every
    /// detected encounter of the current hunt (caught, defeated, escaped,
    /// ignored - all of them), grouped by the same resolved species/form name
    /// the Session Encounters table counts by. This is the third of three
    /// related-but-separate concepts, and it must stay separate from the
    /// other two: HuntSession.EncounterCounts is the aggregated per-species
    /// tally on the main window, and HuntLogService is the persistent Catch
    /// Logs feature (successful catches only, never cleared by a hunt reset).
    /// This history's lifetime instead matches the session counts exactly:
    /// cleared by the main Reset action, saved/restored across restarts the
    /// same way the session itself is (SessionPersistenceService), and
    /// per-client the same way every other per-client store is.
    ///
    /// PERFORMANCE CONTRACT (see MIGRATION_GUIDE.md §99): Append runs on the
    /// UI thread inside the same handler that shows the encounter sprite, so
    /// it must stay O(small): it allocates one three-field record, adds it to
    /// two in-memory collections, and schedules a DEBOUNCED save - it never
    /// touches a file, a sprite, a bitmap, or the Pokedex, and it never
    /// rebuilds any collection. All disk writes happen at least
    /// SaveDebounceSeconds after the burst that caused them, serialize a
    /// snapshot on a background thread, and go through the same
    /// temp-file-then-move pattern SessionPersistenceService uses. The only
    /// synchronous writes are the explicit flushes at Stop/window close,
    /// mirroring exactly where the session itself already saves
    /// synchronously.
    ///
    /// THREADING: every public member must be called from the UI thread (all
    /// call sites are MainWindowViewModel handlers that are already
    /// Dispatcher.Post'ed there, plus view code-behind). The per-Pokemon
    /// ObservableCollections are bound directly by open history windows, so
    /// mutating them anywhere else would be a cross-thread collection change.
    /// </summary>
    public static class SessionEncounterHistoryService
    {
        // Rather than saving on every encounter (the way the much smaller
        // session file does), history writes wait for a quiet moment: one
        // encounter every few seconds keeps pushing the timer back at most
        // this far. Chosen so a crash can lose at most a few seconds of
        // HISTORY detail while the authoritative counts (saved per encounter
        // by SessionPersistenceService) lose nothing.
        private const int SaveDebounceSeconds = 5;

        // Same shared folder every other per-client store uses.
        private static readonly string SaveFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "ProTracker",
                "Database"
            );

        // Every record of the session in chronological order (oldest first) -
        // the persistence snapshot source. The per-Pokemon collections below
        // hold the same record INSTANCES newest-first for display.
        private static readonly List<SessionEncounterRecord> allRecords = new();

        // One live collection per resolved species/form name, created lazily
        // (first encounter of that species, or first time its window opens) so
        // no UI rows or collections ever exist for species that never come up.
        // Instances are kept for the whole app run once created - an open
        // window binds one directly, and Reset/client switches Clear() and
        // refill the SAME instance so that window updates live instead of
        // pointing at a dead list.
        private static readonly Dictionary<string, ObservableCollection<SessionEncounterRecord>> perPokemon =
            new(StringComparer.OrdinalIgnoreCase);

        // The record for the encounter currently on screen - the one late
        // level/location refinements are allowed to update. Replaced by every
        // Append, cleared by Clear/LoadForActiveClient, so a refinement can
        // never land on a previous hunt's (or previous client's) record.
        private static SessionEncounterRecord? currentRecord;

        private static long nextId = 1;

        // ---- debounced persistence state ----

        // A plain DispatcherTimer (the same class MainWindowViewModel already
        // uses for its own timers) armed only while a save is owed - one
        // tick, then stopped and discarded.
        private static DispatcherTimer? pendingSaveTimer;

        private static readonly object saveLock = new();

        // Bumped on the UI thread each time a snapshot is taken; a background
        // writer only writes its snapshot if no newer snapshot already won -
        // two overlapping writes can finish out of order, and without this an
        // older snapshot could land last and quietly roll the file back.
        private static long snapshotVersion;

        private static long lastWrittenVersion;

        private static string? GetSavePath()
        {
            int clientNumber = SessionPersistenceService.ActiveClientNumber;

            // No client bound yet - same "don't write a shared file two
            // processes could fight over" rule as SessionPersistenceService.
            if (clientNumber < 1)
                return null;

            return Path.Combine(SaveFolder, $"session-encounters-client{clientNumber}.json");
        }

        /// <summary>
        /// The live, newest-first history for one Pokemon - the collection a
        /// SessionEncounterHistoryWindow binds. Created empty on first request
        /// so a window opened for a species with no encounters yet still gets
        /// a real collection that starts filling the moment one is detected.
        /// </summary>
        public static ObservableCollection<SessionEncounterRecord> GetHistoryFor(string pokemonName)
        {
            if (!perPokemon.TryGetValue(pokemonName, out var collection))
            {
                collection = new ObservableCollection<SessionEncounterRecord>();
                perPokemon[pokemonName] = collection;
            }

            return collection;
        }

        /// <summary>
        /// Records one detected encounter - called exactly once per encounter
        /// from MainWindowViewModel.RegisterEncounter (EncounterTracker fires
        /// EncounterDetected once per battle, no matter how many frames looked
        /// at the same Pokemon, so frame repeats can never duplicate records).
        /// Deliberately runs AFTER the sprite/display updates in that method
        /// and does no I/O of its own - see the class doc's performance
        /// contract.
        /// </summary>
        public static void Append(string pokemonName, int? level, string? location)
        {
            // Admin Client isolation (§101) - session history is normal-client
            // data; in admin mode nothing is recorded, refined, cleared, or
            // flushed (the admin session is purely in-memory by design).
            if (AdminModeService.IsActive)
                return;

            if (string.IsNullOrWhiteSpace(pokemonName))
                return;

            var record = new SessionEncounterRecord
            {
                Id = nextId++,
                PokemonName = pokemonName,
                Level = level,
                Location = location ?? "Unknown",

                // §128. Stamped here rather than passed in, so it is the
                // moment the encounter was registered and cannot be a caller's
                // stale value. EncounterDatabaseService.Append stamps its own
                // for the permanent row a line below, from the same tick.
                WhenUtc = DateTime.UtcNow
            };

            allRecords.Add(record);
            GetHistoryFor(pokemonName).Insert(0, record);

            currentRecord = record;

            // §112: the same encounter, mirrored into the permanent log.
            // Deliberately below the admin gate above, so an Admin Client
            // session never reaches the database either. This call does no
            // I/O - it stashes the encounter and writes the PREVIOUS one on a
            // background task - so the class doc's "no I/O of its own"
            // contract for Append still holds. See EncounterDatabaseService.
            EncounterDatabaseService.Append(pokemonName, level, location);

            ScheduleSave();
        }

        /// <summary>
        /// Late level refinement for the CURRENT encounter - fired when the
        /// §96 level-vote consensus settles on (or corrects to) a value while
        /// the battle is still on screen (see
        /// EncounterTracker.EncounterLevelRefined). Updates the existing
        /// record in place - never appends - and the record's own property
        /// notification updates any open history window automatically.
        /// </summary>
        public static void RefineCurrentLevel(int level)
        {
            // Admin Client isolation (§101) - session history is normal-client
            // data; in admin mode nothing is recorded, refined, cleared, or
            // flushed (the admin session is purely in-memory by design).
            if (AdminModeService.IsActive)
                return;

            if (currentRecord is null || currentRecord.Level == level)
                return;

            currentRecord.Level = level;

            // §112: free - the permanent row for this encounter has not been
            // written yet, so this corrects it before it ever reaches disk.
            EncounterDatabaseService.RefineCurrentLevel(level);

            ScheduleSave();
        }

        /// <summary>
        /// §124. Gender for the encounter on screen, from
        /// EncounterTracker.EncounterGenderDetected. Exactly the shape
        /// RefineCurrentLevel above already has, and for the same reason:
        /// the reading arrives after the record does, the in-memory record
        /// notifies any open history window on its own, and the permanent
        /// row has not been written yet so correcting it is free.
        /// </summary>
        public static void RefineCurrentGender(string? gender)
        {
            if (AdminModeService.IsActive)
                return;

            if (string.IsNullOrWhiteSpace(gender))
                return;

            if (currentRecord is null || currentRecord.Gender == gender)
                return;

            currentRecord.Gender = gender;

            EncounterDatabaseService.RefineCurrentGender(gender);

            ScheduleSave();
        }

        /// <summary>
        /// §124. Shiny/Form for the encounter on screen, from
        /// MainWindowViewModel's rare-encounter handler. "None" is filtered
        /// here as well as inside the record and the database service - a
        /// value this uninteresting should not need three places to agree,
        /// but each of the three is reachable on its own.
        /// </summary>
        public static void RefineCurrentRareType(string? rareType)
        {
            if (AdminModeService.IsActive)
                return;

            if (string.IsNullOrWhiteSpace(rareType) || rareType == "None")
                return;

            if (currentRecord is null || currentRecord.RareType == rareType)
                return;

            currentRecord.RareType = rareType;

            EncounterDatabaseService.RefineCurrentRareType(rareType);

            ScheduleSave();
        }

        /// <summary>
        /// Late location fill-in: if the current encounter registered before
        /// the corner OCR had ever read a route, the first route reading that
        /// arrives afterwards belongs to it. Only fills UNKNOWN in - a record
        /// that already has a location keeps it, so a route change after the
        /// battle can't relabel where the encounter actually happened.
        /// </summary>
        public static void RefineCurrentLocationIfUnknown(string location)
        {
            // Admin Client isolation (§101) - session history is normal-client
            // data; in admin mode nothing is recorded, refined, cleared, or
            // flushed (the admin session is purely in-memory by design).
            if (AdminModeService.IsActive)
                return;

            if (currentRecord is null || string.IsNullOrWhiteSpace(location))
                return;

            if (currentRecord.Location != "Unknown")
                return;

            currentRecord.Location = location;

            EncounterDatabaseService.RefineCurrentLocation(location);   // §112

            ScheduleSave();
        }

        /// <summary>
        /// Battle-time location correction (§102): a map name the corner OCR
        /// confirms WHILE the encounter's battle is still running is where
        /// that battle is happening - the player cannot move mid-battle - so
        /// this overwrites even a non-Unknown location. It repairs the record
        /// that registered during a map transition with the previous map
        /// still current (the evidence log's Seadra: recorded "Route 11" at
        /// 17:48:26, "Vermilion City" confirmed at 17:48:30, four seconds
        /// too late for Append). Only MainWindowViewModel.OnCornerInfoDetected
        /// calls this, and only for corner readings the tracker flagged as
        /// battle-time; every other reading goes through
        /// RefineCurrentLocationIfUnknown above.
        /// </summary>
        public static void RefineCurrentLocation(string location)
        {
            // Admin Client isolation (§101) - same front door as every other
            // mutator here.
            if (AdminModeService.IsActive)
                return;

            if (currentRecord is null || string.IsNullOrWhiteSpace(location))
                return;

            if (currentRecord.Location == location)
                return;

            currentRecord.Location = location;

            EncounterDatabaseService.RefineCurrentLocation(location);   // §112

            ScheduleSave();
        }

        /// <summary>
        /// Wipes the whole session history - the main Reset action's counterpart
        /// to HuntSession.Reset()/SessionPersistenceService.Delete(). Clears
        /// every per-Pokemon collection IN PLACE so any open history window
        /// immediately shows its empty state, and deletes the saved file so a
        /// restart can't resurrect a hunt the user reset. Touches nothing that
        /// belongs to Catch Logs (HuntLogService), the PVP log, boss data, or
        /// settings.
        /// </summary>
        public static void Clear()
        {
            // Admin Client isolation (§101) - session history is normal-client
            // data; in admin mode nothing is recorded, refined, cleared, or
            // flushed (the admin session is purely in-memory by design).
            if (AdminModeService.IsActive)
                return;

            // §112: Reset ends a hunt, it does not un-happen it. The
            // encounter still held in memory is written to the permanent log
            // before the session is wiped; the log itself is never cleared
            // here, which is the whole point of it being permanent.
            EncounterDatabaseService.FlushPending();

            allRecords.Clear();

            foreach (var collection in perPokemon.Values)
            {
                collection.Clear();
            }

            currentRecord = null;

            CancelPendingSave();
            DeleteSaveFile();
        }

        /// <summary>
        /// Loads (or clears) the history for whichever client is now active -
        /// called from MainWindowViewModel.LoadPreviousSession, right where
        /// the session counts themselves are restored, with the same
        /// nothing-saved-means-start-empty behavior: when there is no saved
        /// session, sessionExists is false and any stale history file is
        /// removed instead of loaded, so history lifetime always matches the
        /// counts exactly. Existing collection instances are refilled in
        /// place, so a history window left open across a client switch shows
        /// the new client's data rather than a dead list.
        /// </summary>
        public static void LoadForActiveClient(bool sessionExists)
        {
            // §112: written under the profile it belonged to, before the
            // active client number changes underneath it.
            EncounterDatabaseService.FlushPending();

            allRecords.Clear();

            foreach (var collection in perPokemon.Values)
            {
                collection.Clear();
            }

            currentRecord = null;
            CancelPendingSave();

            if (!sessionExists)
            {
                DeleteSaveFile();
                return;
            }

            string? savePath = GetSavePath();

            if (savePath is null || !File.Exists(savePath))
                return;

            List<SessionEncounterRecordData>? saved = null;

            try
            {
                saved = JsonSerializer.Deserialize<List<SessionEncounterRecordData>>(
                    File.ReadAllText(savePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                // A damaged file just means the restored session starts with
                // an empty history - same tolerance every other JSON-backed
                // store in this app has. The counts themselves are unaffected.
            }

            if (saved is null || saved.Count == 0)
                return;

            foreach (SessionEncounterRecordData data in saved)
            {
                if (string.IsNullOrWhiteSpace(data.PokemonName))
                    continue;

                allRecords.Add(new SessionEncounterRecord
                {
                    Id = nextId++,
                    PokemonName = data.PokemonName,
                    Level = data.Level,
                    Location = data.Location,

                    // §128. Null on a file written before these existed,
                    // which the display renders as a blank cell.
                    WhenUtc = data.WhenUtc,
                    Gender = data.Gender,
                    RareType = data.RareType
                });
            }

            // Refill the display collections newest-first WITHOUT the O(n^2)
            // of inserting each record at index 0 - group per species in
            // chronological order, then add each group reversed.
            foreach (var group in allRecords.GroupBy(r => r.PokemonName, StringComparer.OrdinalIgnoreCase))
            {
                var collection = GetHistoryFor(group.Key);

                foreach (SessionEncounterRecord record in group.Reverse())
                {
                    collection.Add(record);
                }
            }
        }

        /// <summary>
        /// Synchronous save for the moments the session itself already saves
        /// synchronously (Stop, app close) - guarantees the history on disk is
        /// current before the process may go away, without waiting out the
        /// debounce. Small file, one-time call sites, same trade
        /// SessionPersistenceService.Save already makes there.
        /// </summary>
        public static void FlushToDisk()
        {
            // Deliberately NOT gated on Admin Client mode, unlike the four
            // mutators above: with Append/Refine*/Clear all no-oping while
            // admin mode is active, allRecords can only ever hold the normal
            // client's own records - so this write is always safe, and
            // skipping it would LOSE a normal-mode append whose debounced
            // save (ScheduleSave) had not fired yet when the app closed
            // during an admin session (§101).
            CancelPendingSave();

            // §112: the same flush points the session file uses - stop,
            // reset, client switch, app exit - so the last encounter of a
            // hunt is never left unwritten. Safe here despite this method not
            // being admin-gated: a pending encounter can only ever have come
            // from a normal-mode Append.
            EncounterDatabaseService.FlushPending();

            string? savePath = GetSavePath();

            if (savePath is null)
                return;

            List<SessionEncounterRecordData> snapshot = TakeSnapshot(out long version);

            WriteSnapshot(snapshot, savePath, version);
        }

        // ---- internals ----

        private static void ScheduleSave()
        {
            // One armed timer at a time - a burst of encounters collapses into
            // a single write once the burst has been quiet for the debounce
            // window... or at latest SaveDebounceSeconds after the burst
            // started, since the timer is NOT pushed back by later calls.
            if (pendingSaveTimer is not null)
                return;

            if (GetSavePath() is null)
                return;

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(SaveDebounceSeconds)
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                pendingSaveTimer = null;
                SaveInBackground();
            };

            pendingSaveTimer = timer;
            timer.Start();
        }

        private static void CancelPendingSave()
        {
            pendingSaveTimer?.Stop();
            pendingSaveTimer = null;
        }

        private static void SaveInBackground()
        {
            string? savePath = GetSavePath();

            if (savePath is null)
                return;

            // Snapshot on the UI thread (cheap - one DTO per record), write on
            // a background thread, so the UI thread never serializes or
            // touches the disk for history.
            List<SessionEncounterRecordData> snapshot = TakeSnapshot(out long version);

            Task.Run(() => WriteSnapshot(snapshot, savePath, version));
        }

        private static List<SessionEncounterRecordData> TakeSnapshot(out long version)
        {
            version = ++snapshotVersion;

            var snapshot = new List<SessionEncounterRecordData>(allRecords.Count);

            foreach (SessionEncounterRecord record in allRecords)
            {
                snapshot.Add(new SessionEncounterRecordData
                {
                    PokemonName = record.PokemonName,
                    Level = record.Level,
                    Location = record.Location,
                    WhenUtc = record.WhenUtc,
                    Gender = record.Gender,
                    RareType = record.RareType
                });
            }

            return snapshot;
        }

        private static void WriteSnapshot(
            List<SessionEncounterRecordData> snapshot, string savePath, long version)
        {
            lock (saveLock)
            {
                // A newer snapshot already reached the file - writing this
                // older one would roll it back.
                if (version <= lastWrittenVersion)
                    return;

                try
                {
                    Directory.CreateDirectory(SaveFolder);

                    string json = JsonSerializer.Serialize(
                        snapshot,
                        new JsonSerializerOptions { WriteIndented = true });

                    // §193: this wrote a temp file and renamed it, which looks
                    // atomic and is not - the rename is journalled metadata and
                    // the contents were only in the operating system's cache.
                    // DurableFile does the same thing with the flush that makes
                    // the order mean something.
                    DurableFile.WriteAllText(savePath, json);

                    lastWrittenVersion = version;
                }
                catch (Exception ex)
                {
                    // Never let a history save failure disturb tracking - the
                    // in-memory session (and the separately-saved counts) are
                    // unaffected; the next debounced save simply tries again.
                    Log.Warning(ex, "Session encounter history could not be saved");
                }
            }
        }

        private static void DeleteSaveFile()
        {
            string? savePath = GetSavePath();

            if (savePath is null)
                return;

            lock (saveLock)
            {
                try
                {
                    if (File.Exists(savePath))
                        File.Delete(savePath);

                    // Anything still in flight is older than this delete by
                    // definition - make sure it can't recreate the file.
                    lastWrittenVersion = ++snapshotVersion;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Session encounter history file could not be deleted");
                }
            }
        }
    }
}
