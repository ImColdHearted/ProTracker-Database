using Foot_Tracker.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Persists the "Hunting Log" - a granular, per-catch record (species,
    /// level, map, timestamp) distinct from HuntSession.EncounterCounts (a
    /// running per-species tally with no per-event detail). MainWindowViewModel
    /// calls RegisterEncounter only when EncounterTracker fires
    /// CatchResultDetected with CatchResult.Success, while a hunt is running -
    /// see MIGRATION_GUIDE.md §76 for why this only logs catches now rather
    /// than every encounter. Follows the same in-memory-list-plus-JSON-file,
    /// per-client pattern as PvpOpponentService.cs.
    ///
    /// This is a LOG, not a per-species summary: every encounter gets its own
    /// entry, even for a species already logged many times before - the
    /// existing Session Encounters table (HuntSession.EncounterCounts) already
    /// covers "how many of each species this session"; this log exists to
    /// answer "which one, when, what level, where" for each individual
    /// encounter instead. Keeps only the MaxSavedEntries most recent entries -
    /// see that constant's remarks for why it's set much higher than
    /// PvpOpponentService's MaxSavedBattles. Raises LogChanged whenever the
    /// saved list changes so an open Hunting Log/species-detail window can stay
    /// live without a manual refresh.
    ///
    /// Deliberately NOT touched by HuntSession.Reset()/Restore()/MergeFrom() -
    /// same reasoning PvpOpponentService's battle log is left alone by a hunt
    /// reset (see MainWindowViewModel.Reset/AssignTrackerClient): this is an
    /// independent, persistent log with its own Clear All button, not part of
    /// "the current hunt's numbers."
    /// </summary>
    public static class HuntLogService
    {
        // Encounter volume is much higher than PVP battles (a single efficient
        // hunting session can log hundreds of entries), so this is set far above
        // PvpOpponentService.MaxSavedBattles (250) - high enough that the
        // Export button (there specifically so data isn't lost before this cap
        // is ever reached) stays the normal way to archive old data, not this
        // cap.
        private const int MaxSavedEntries = 10000;

        /// <summary>Raised after the saved log changes - a new encounter
        /// registered, an entry removed/cleared, or the MaxSavedEntries cap
        /// trimmed an old entry. HuntLogViewModel/HuntLogSpeciesDetailViewModel
        /// subscribe to this to keep their lists current while their window is
        /// open, same as PreviouslyBattledUsersViewModel does for
        /// PvpOpponentService's identically-shaped event.</summary>
        public static event Action? LogChanged;

        // Same shared folder LifetimeStatsService/SessionPersistenceService/
        // BossCooldownService/PvpOpponentService save to
        // (%LocalAppData%\ProTracker\Database) rather than a folder next to the
        // built executable - survives a rebuild/republish the same way those do.
        private static readonly string SaveFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "ProTracker",
                "Database"
            );

        // Per-client, same reasoning as PvpOpponentService.GetSavePath - each
        // PRO account tracked by this app instance gets its own hunting log,
        // not one shared/commingled list across every client.
        private static string GetSavePath()
        {
            int clientNumber = SessionPersistenceService.ActiveClientNumber;

            string fileName = clientNumber >= 1
                ? $"hunt-log-client{clientNumber}.json"
                : "hunt-log.json";

            return Path.Combine(SaveFolder, fileName);
        }

        private static readonly List<HuntLogEntry> entries = new();

        private static bool loaded;

        /// <summary>Loads from disk on first access - callers don't need to call
        /// Load() explicitly first, same lazy-loading reasoning as
        /// PvpOpponentService.Opponents (read from more than one place - the
        /// tracker and any open display window).</summary>
        public static IReadOnlyList<HuntLogEntry> Entries
        {
            get
            {
                EnsureLoaded();
                return entries;
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
            entries.Clear();

            string savePath = GetSavePath();

            if (!File.Exists(savePath))
                return;

            try
            {
                string json = File.ReadAllText(savePath);

                List<HuntLogEntry>? deserialized =
                    JsonSerializer.Deserialize<List<HuntLogEntry>>(json);

                if (deserialized != null)
                {
                    entries.AddRange(deserialized);
                }

                // Defensive - covers a manually edited file exceeding the cap.
                TrimToMostRecent();
            }
            catch
            {
                // Keep an empty list if the file is damaged, same as
                // PvpOpponentService/other JSON-backed services in this app.
            }
        }

        /// <summary>Forces a fresh reload from the now-active client's own save
        /// file - called by MainWindowViewModel.AssignTrackerClient right after
        /// SessionPersistenceService.SetActiveClient, so switching which PRO
        /// client this app instance is tracking swaps in that client's own
        /// hunting log instead of continuing to show/append to whichever
        /// client's list was already loaded. Raises LogChanged so an open
        /// Hunting Log window updates immediately.</summary>
        public static void ReloadForActiveClient()
        {
            loaded = true;

            LoadFromDisk();

            LogChanged?.Invoke();
        }

        // Drops the oldest entries once the log exceeds MaxSavedEntries - see
        // that constant's comment for why.
        private static void TrimToMostRecent()
        {
            if (entries.Count <= MaxSavedEntries)
                return;

            List<HuntLogEntry> toRemove = entries
                .OrderBy(x => x.EncounteredAtUtc)
                .Take(entries.Count - MaxSavedEntries)
                .ToList();

            foreach (HuntLogEntry entry in toRemove)
            {
                entries.Remove(entry);
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
                entries,
                new JsonSerializerOptions { WriteIndented = true });

            DurableFile.WriteAllText(savePath, json);
        }

        /// <summary>
        /// Records one caught Pokemon - ALWAYS adds a new entry, even if this
        /// species already has other entries in the list (this is a catch
        /// log, not a per-species summary - see the class doc comment). Called
        /// by MainWindowViewModel.OnCatchResultDetected (only for
        /// CatchResult.Success - see MIGRATION_GUIDE.md §76) with the species
        /// name huntSession.CurrentEncounter already holds, the level
        /// LevelDetector read for this specific catch (or null - see
        /// HuntLogEntry.Level), the gender GenderDetector read (or null - see
        /// HuntLogEntry.Gender), whatever MainWindowViewModel.CurrentRouteText
        /// currently holds, and - added by MIGRATION_GUIDE.md §77 - "Shiny",
        /// "Form", or null (see HuntLogEntry.RareType).
        /// </summary>
        public static void RegisterEncounter(string pokemonName, int? level, string? gender, string map, string? rareType)
        {
            // Admin Client isolation (§101) - the Catch Logs are normal-client
            // data; admin-mode activity must never add to or remove from them.
            if (AdminModeService.IsActive)
                return;

            if (string.IsNullOrWhiteSpace(pokemonName))
                return;

            EnsureLoaded();

            entries.Add(new HuntLogEntry
            {
                PokemonName = pokemonName,
                Level = level,
                Gender = gender,
                Map = string.IsNullOrWhiteSpace(map) ? "Unknown" : map,
                RareType = rareType,
                EncounteredAtUtc = DateTime.UtcNow
            });

            TrimToMostRecent();
            Save();

            LogChanged?.Invoke();
        }

        /// <summary>
        /// Removes the single most recently logged encounter, of any species -
        /// backs the "Remove Previous" button in HuntLogWindow (the big,
        /// all-species table). That table's DataGrid selection isn't wired to a
        /// command, so "most recent" - already how the list is displayed - is
        /// what "previous" refers to here, same as
        /// PvpOpponentService.RemoveMostRecent. Does nothing if the log is
        /// already empty.
        /// </summary>
        public static void RemoveMostRecent()
        {
            // Admin Client isolation (§101) - the Catch Logs are normal-client
            // data; admin-mode activity must never add to or remove from them.
            if (AdminModeService.IsActive)
                return;

            EnsureLoaded();

            if (entries.Count == 0)
                return;

            HuntLogEntry mostRecent = entries
                .OrderByDescending(x => x.EncounteredAtUtc)
                .First();

            entries.Remove(mostRecent);

            Save();

            LogChanged?.Invoke();
        }

        /// <summary>
        /// Wipes the entire saved log, every species - backs the "Clear All"
        /// button in HuntLogWindow, always shown behind a confirmation prompt
        /// since this can't be undone.
        /// </summary>
        public static void ClearAll()
        {
            // Admin Client isolation (§101) - the Catch Logs are normal-client
            // data; admin-mode activity must never add to or remove from them.
            if (AdminModeService.IsActive)
                return;

            EnsureLoaded();

            if (entries.Count == 0)
                return;

            entries.Clear();

            Save();

            LogChanged?.Invoke();
        }

        /// <summary>All logged encounters for one species, most recent first -
        /// backs HuntLogSpeciesDetailWindow. Matches case-insensitively since
        /// every species name that reaches this log already went through
        /// PokemonSpriteService.ResolveEncounterName, but this stays tolerant of
        /// a stray casing difference rather than silently showing an empty
        /// list.</summary>
        public static IReadOnlyList<HuntLogEntry> GetEntriesForSpecies(string pokemonName)
        {
            EnsureLoaded();

            return entries
                .Where(e => string.Equals(e.PokemonName, pokemonName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.EncounteredAtUtc)
                .ToList();
        }

        /// <summary>Removes the single most recently logged encounter FOR THIS
        /// SPECIES ONLY - backs HuntLogSpeciesDetailWindow's own "Remove
        /// Previous" button, scoped the same way GetEntriesForSpecies is. Every
        /// other species' entries are untouched.</summary>
        public static void RemoveMostRecentForSpecies(string pokemonName)
        {
            // Admin Client isolation (§101) - the Catch Logs are normal-client
            // data; admin-mode activity must never add to or remove from them.
            if (AdminModeService.IsActive)
                return;

            EnsureLoaded();

            HuntLogEntry? mostRecent = entries
                .Where(e => string.Equals(e.PokemonName, pokemonName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.EncounteredAtUtc)
                .FirstOrDefault();

            if (mostRecent is null)
                return;

            entries.Remove(mostRecent);

            Save();

            LogChanged?.Invoke();
        }

        /// <summary>Wipes every logged encounter FOR THIS SPECIES ONLY - backs
        /// HuntLogSpeciesDetailWindow's own "Clear All" button. Every other
        /// species' entries are untouched.</summary>
        public static void ClearAllForSpecies(string pokemonName)
        {
            // Admin Client isolation (§101) - the Catch Logs are normal-client
            // data; admin-mode activity must never add to or remove from them.
            if (AdminModeService.IsActive)
                return;

            EnsureLoaded();

            int removed = entries.RemoveAll(
                e => string.Equals(e.PokemonName, pokemonName, StringComparison.OrdinalIgnoreCase));

            if (removed == 0)
                return;

            Save();

            LogChanged?.Invoke();
        }
    }
}
