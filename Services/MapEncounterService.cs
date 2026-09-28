using Foot_Tracker.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// §278. The encounter table, kept per map.
    ///
    /// WHAT THIS IS AND IS NOT. Only the encounter TABLE partitions - which
    /// species turned up here, how many were caught, how many ran, when each
    /// was last seen. The stats panel does not: time hunting, Since Shiny,
    /// Since Form, the catch totals and the rate are about the hunt, not about
    /// the ground you are standing on, and a Since Shiny that reset because
    /// you stepped onto a different route would be worse than useless.
    /// <see cref="HuntSession"/> is therefore untouched by this section - it
    /// keeps counting everything exactly as it did, which is also why every
    /// stat, the CSV/JSON export and the import all still behave the same.
    ///
    /// OFF BY DEFAULT. The tracker works as it always has until someone turns
    /// this on in File - Tracker Settings, because per-map tracking writes a
    /// file per map visited and that is a cost not everyone wants to pay.
    ///
    /// ONE FILE PER MAP, not one growing document and not a folder tree by
    /// region. A save costs the current map's couple of kilobytes however many
    /// maps have ever been hunted; loading a map is one file read; listing
    /// them is one directory scan when something asks. A region tree would
    /// need a map-to-region lookup this app does not reliably have, and a map
    /// that resolved to the wrong region would write its data somewhere it
    /// could not be found again - grouping by region stays a display choice
    /// that can be made later without moving a single file.
    ///
    /// Per client, in the same folder and by the same naming shape §250 gave
    /// the World Quest sessions, because it is the same problem: a per-client,
    /// per-key session file.
    /// </summary>
    public static class MapEncounterService
    {
        private const string FilePrefix = "map-encounters-client";

        private static readonly string SessionFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "ProTracker",
                "Database"
            );

        private static MapEncounterTally? current;

        /// <summary>Raised after the current map changes or its tally is
        /// written to, so the front table can rebuild. Raised from whatever
        /// thread called in - the tracking loop posts to the UI thread before
        /// touching anything bound, the same contract PvpOpponentService
        /// carries.</summary>
        public static event Action? CurrentMapChanged;

        /// <summary>The map being hunted, or null before any map has been
        /// confirmed this run. Null is a real state and the front table shows
        /// its own message for it rather than an empty table that reads as
        /// "nothing here".</summary>
        public static MapEncounterTally? Current => current;

        public static string CurrentMapName => current?.MapName ?? string.Empty;

        /// <summary>
        /// §278. Switches to <paramref name="mapName"/>, loading that map's
        /// own tally from disk (or starting one). Does nothing when it is
        /// already the current map, which matters because RouteDetector
        /// confirms the same map over and over while the player stands on it.
        /// </summary>
        public static void SetCurrentMap(string? mapName)
        {
            if (string.IsNullOrWhiteSpace(mapName))
                return;

            if (IsolatedSession.IsActive)
                return;

            string trimmed = mapName.Trim();

            if (current is not null &&
                string.Equals(current.MapName, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = Load(trimmed) ?? new MapEncounterTally
            {
                MapName = trimmed,
                FirstSeenUtc = DateTime.UtcNow,
            };

            // A tally restored from disk keeps the name it was SAVED with,
            // not the one just read - the file is the record, and a one-off
            // OCR wobble in capitalisation should not rewrite it.
            CurrentMapChanged?.Invoke();
        }

        /// <summary>Forgets which map is current without touching any file -
        /// for a client switch, where the next confirmed map belongs to a
        /// different profile's folder of files.</summary>
        public static void ClearCurrentMap()
        {
            current = null;

            CurrentMapChanged?.Invoke();
        }

        /// <summary>§278. The three registrations, mirroring the ones
        /// HuntSession already takes. Each saves, because the tally is the
        /// only copy of this map's table and an encounter that is not on disk
        /// is lost to a crash - the same trade SessionPersistenceService makes
        /// for the session itself.</summary>
        public static void RegisterEncounter(string pokemonName) =>
            Mutate(tally => tally.RegisterPokemonEncounter(pokemonName));

        public static void RegisterCatch(string pokemonName) =>
            Mutate(tally => tally.RegisterCatch(pokemonName));

        public static void RegisterRunAway(string pokemonName) =>
            Mutate(tally => tally.RegisterRunAway(pokemonName));

        private static void Mutate(Action<MapEncounterTally> change)
        {
            if (IsolatedSession.IsActive)
                return;

            if (current is null)
                return;

            change(current);

            current.LastSeenUtc = DateTime.UtcNow;

            Save(current);

            CurrentMapChanged?.Invoke();
        }

        /// <summary>§278. Every map this profile has a file for, newest first -
        /// one directory scan, done when something asks rather than kept in
        /// memory, because nothing needs the list while hunting.</summary>
        public static IReadOnlyList<MapEncounterTally> AllMaps()
        {
            var results = new List<MapEncounterTally>();

            int clientNumber = SessionPersistenceService.ActiveClientNumber;

            if (clientNumber <= 0 || !Directory.Exists(SessionFolder))
                return results;

            string pattern = $"{FilePrefix}{clientNumber}-*.json";

            foreach (string path in Directory.GetFiles(SessionFolder, pattern))
            {
                MapEncounterTally? tally = ReadFile(path);

                if (tally is not null && tally.MapName.Length > 0)
                    results.Add(tally);
            }

            return results
                .OrderByDescending(t => t.LastSeenUtc ?? t.FirstSeenUtc ?? DateTime.MinValue)
                .ToList();
        }

        private static MapEncounterTally? Load(string mapName)
        {
            string? path = PathFor(mapName);

            return path is null ? null : ReadFile(path);
        }

        private static MapEncounterTally? ReadFile(string path)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                MapEncounterTally? tally = JsonSerializer.Deserialize<MapEncounterTally>(File.ReadAllText(path));

                if (tally is not null)
                {
                    // §405: the old spelling of the two Nidoran becomes the
                    // new, the counts under each merged.
                    ModernizeKeys(tally.EncounterCounts);
                    ModernizeKeys(tally.CaughtCounts);
                    ModernizeKeys(tally.RanFromCounts);
                    ModernizeKeys(tally.LastEncounteredUtc);
                }

                return tally;
            }
            catch (Exception ex)
            {
                // An unreadable file starts that map over rather than stopping
                // the app - the same failure direction every JSON store here
                // takes.
                Log.Warning(ex, "Map encounter file could not be read: {Path}", path);
                return null;
            }
        }

        /// <summary>§405. Re-keys a species dictionary under the library's
        /// current spelling - counts under the old and the new spelling of
        /// one species are added together.</summary>
        private static void ModernizeKeys(Dictionary<string, int>? counts)
        {
            if (counts is null)
                return;

            foreach (string key in counts.Keys.Where(PokemonNames.IsLegacy).ToList())
            {
                string modern = PokemonNames.Modern(key);
                int add = counts[key];
                counts.Remove(key);
                counts[modern] = counts.TryGetValue(modern, out int have) ? have + add : add;
            }
        }

        /// <summary>The same for a time per species: the later one is kept.</summary>
        private static void ModernizeKeys(Dictionary<string, DateTime>? times)
        {
            if (times is null)
                return;

            foreach (string key in times.Keys.Where(PokemonNames.IsLegacy).ToList())
            {
                string modern = PokemonNames.Modern(key);
                DateTime when = times[key];
                times.Remove(key);

                if (!times.TryGetValue(modern, out DateTime already) || when > already)
                    times[modern] = when;
            }
        }

        private static void Save(MapEncounterTally tally)
        {
            string? path = PathFor(tally.MapName);

            if (path is null)
                return;

            try
            {
                Directory.CreateDirectory(SessionFolder);

                // §193: durable - see DurableFile.
                DurableFile.WriteAllText(
                    path,
                    JsonSerializer.Serialize(tally, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Map encounter file could not be written: {Path}", path);
            }
        }

        private static string? PathFor(string mapName)
        {
            int clientNumber = SessionPersistenceService.ActiveClientNumber;

            if (clientNumber <= 0)
                return null;

            string safe = SafeMapName(mapName);

            if (safe.Length == 0)
                return null;

            return Path.Combine(SessionFolder, $"{FilePrefix}{clientNumber}-{safe}.json");
        }

        /// <summary>
        /// §278. A map name as a file name.
        ///
        /// §250's SafeQuestId is not enough here and the difference matters: it
        /// DROPS every character it does not like, and map names are full of
        /// them. "Route 11" and "Route1 1" both collapse to "Route11", and two
        /// maps sharing one file is data loss that looks like a bug in the
        /// counting. So every run of unwanted characters becomes one
        /// underscore, and a short stable hash of the ORIGINAL name is
        /// appended, which makes a collision impossible rather than unlikely.
        ///
        /// The hash is FNV-1a, written out here, NOT string.GetHashCode():
        /// .NET randomises that per process, so the same map would land in a
        /// different file on every launch and every map's table would appear
        /// empty the next time the app started.
        /// </summary>
        internal static string SafeMapName(string mapName)
        {
            if (string.IsNullOrWhiteSpace(mapName))
                return string.Empty;

            string trimmed = mapName.Trim();

            var kept = new StringBuilder(trimmed.Length + 10);
            bool lastWasSeparator = false;

            foreach (char c in trimmed)
            {
                if (char.IsAsciiLetterOrDigit(c))
                {
                    kept.Append(c);
                    lastWasSeparator = false;
                }
                else if (!lastWasSeparator && kept.Length > 0)
                {
                    kept.Append('_');
                    lastWasSeparator = true;
                }

                if (kept.Length >= 48)
                    break;
            }

            // Lower-cased, like the hash below. Map identity is
            // case-insensitive everywhere else in this service, and a body
            // that kept its capitals would put "Route 11" and "route 11" in
            // two different files on a case-sensitive filesystem - which is
            // every Linux build (§187), even though Windows would quietly
            // treat them as one. One OCR wobble in a capital would split a
            // map's table in half.
            string body = kept.ToString().Trim('_').ToLowerInvariant();

            return $"{body}-{StableHash(trimmed)}";
        }

        /// <summary>FNV-1a over the lower-cased name, as eight hex digits.
        /// Lower-cased because map identity is case-insensitive everywhere
        /// else in this service, so "route 11" and "Route 11" must reach the
        /// same file.</summary>
        private static string StableHash(string value)
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;

            uint hash = offset;

            foreach (char c in value.ToLowerInvariant())
            {
                hash ^= c;
                hash *= prime;
            }

            return hash.ToString("x8");
        }
    }
}
