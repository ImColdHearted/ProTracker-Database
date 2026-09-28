using Foot_Tracker.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    /// <summary>§281. What one merge changed, so a scan can report itself.</summary>
    public sealed record PokedexMergeResult(int Added, int Updated, int Unchanged)
    {
        public int Total => Added + Updated + Unchanged;

        public string Summary =>
            Total == 0
                ? "No areas were read."
                : $"{DisplayNumber.Count(Added)} new, {DisplayNumber.Count(Updated)} updated, "
                  + $"{DisplayNumber.Count(Unchanged)} already known.";
    }

    /// <summary>
    /// §281. Where a species spawns, built up one scan at a time.
    ///
    /// ONE FILE PER SPECIES, the shape §278 settled on and for the same
    /// reasons: a scan writes one species' couple of kilobytes however many
    /// have been scanned, reading one is one file, and listing them is a
    /// directory scan when something asks. NOT per client - where a Pokemon
    /// spawns is a fact about the game, not about the account, so every
    /// profile on this machine shares it.
    ///
    /// MERGED, NOT APPENDED. PRO's Area list scrolls, so a species that spawns
    /// in sixty places takes several scans - and those scans overlap, because
    /// a player scrolling a few rows at a time will capture the same row
    /// repeatedly. So a scan UPSERTS by map name: a row already known with the
    /// same facts is counted and nothing is written, a row whose facts changed
    /// is corrected, and only a genuinely new map is added. Running the same
    /// scan twice therefore changes nothing, which is what makes "scan, scroll
    /// a little, scan again" a safe way to work.
    ///
    /// The map name is matched through one normaliser, so "Mt. Moon 1F" and
    /// "Mt Moon 1F" - the difference one OCR pass can make - are one map
    /// rather than two. The name KEPT is the first one seen; a later scan
    /// corrects the facts but does not rewrite the spelling, because there is
    /// no reason to think the newer read is the better one.
    /// </summary>
    public static class PokedexService
    {
        private const string FilePrefix = "pokedex-";

        private static readonly string DataFolder =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProTracker",
                "Database",
                "Pokedex");

        /// <summary>Raised after a species' spawns change, so an open viewer
        /// can re-read. Raised from whatever thread scanned.</summary>
        public static event Action? EntriesChanged;

        /// <summary>
        /// §284. Where all of this ends up, as a path the user can paste into
        /// Explorer. Asked for directly - and fairly: a scraper that writes
        /// somewhere it will not name is a scraper whose output cannot be
        /// checked, backed up or deleted. Shown in the panel rather than
        /// buried in a log.
        /// </summary>
        public static string StoreLocation => DataFolder;

        private static string DiagnosticsFolder => Path.Combine(DataFolder, "Diagnostics");

        /// <summary>
        /// §284. Writes the frame a scan could not read and returns its path.
        ///
        /// The point of the whole section. A reader measured on pasted
        /// screenshots failed on the app's own capture, and every attempt to
        /// work out why from another pasted screenshot asked the same wrong
        /// question - the pasted frame was never the frame that failed. So the
        /// tool now hands over the evidence instead of being reasoned about.
        ///
        /// Only written when a scan finds nothing. A working scan leaves no
        /// files behind, so this cannot quietly fill a disk with screenshots
        /// of a panel that is being read perfectly well.
        ///
        /// Timestamped to the second and never overwritten: two failed scans
        /// in a row are usually two DIFFERENT failures, and keeping both is
        /// what makes them comparable.
        /// </summary>
        public static string? SaveDiagnosticFrame(byte[] png)
        {
            if (png is null || png.Length == 0)
                return null;

            try
            {
                Directory.CreateDirectory(DiagnosticsFolder);

                string path = Path.Combine(
                    DiagnosticsFolder,
                    "scan-"
                    + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                    + ".png");

                File.WriteAllBytes(path, png);

                Log.Information("Pokedex scan found no rows; frame written to {Path}.", path);

                return path;
            }
            catch (Exception ex)
            {
                // A diagnostic that throws is worse than no diagnostic: the
                // scan's own message is the thing the user needs, and losing
                // it to a failed write would hide the fault being diagnosed.
                Log.Warning(ex, "Could not write the Pokedex scan diagnostic frame.");
                return null;
            }
        }

        /// <summary>
        /// §281. Folds one scan's rows into what is already known.
        ///
        /// Rows whose map name did not read are dropped here rather than
        /// stored as a blank map - a row that OCR could not name is a row that
        /// cannot be merged against, and keeping it would make every later
        /// scan add another one.
        /// </summary>
        public static PokedexMergeResult Merge(string species, IEnumerable<PokedexSpawn> scanned)
        {
            if (string.IsNullOrWhiteSpace(species))
                return new PokedexMergeResult(0, 0, 0);

            // §421: filed under the library's spelling of the name typed.
            PokedexEntry entry = Load(species) ?? new PokedexEntry { Species = Canonical(species) };

            int added = 0, updated = 0, unchanged = 0;

            foreach (PokedexSpawn row in scanned)
            {
                if (string.IsNullOrWhiteSpace(row.MapName))
                    continue;

                string key = NormaliseMap(row.MapName);

                PokedexSpawn? existing = entry.Spawns
                    .FirstOrDefault(s => NormaliseMap(s.MapName) == key);

                if (existing is null)
                {
                    entry.Spawns.Add(row);
                    added++;
                    continue;
                }

                existing.LastSeenUtc = row.LastSeenUtc;

                if (existing.SameFactsAs(row))
                {
                    // §420. A scan that agrees with a hand-typed row has
                    // confirmed it: the row stops being "manual" and is
                    // counted as an update, because the file changes. A
                    // hand-typed row that agrees with a scanned one changes
                    // nothing - the scan already knew.
                    if (existing.Manual && !row.Manual)
                    {
                        existing.Manual = false;
                        updated++;
                        continue;
                    }

                    unchanged++;
                    continue;
                }

                existing.Land = row.Land;
                existing.Water = row.Water;
                existing.Morning = row.Morning;
                existing.Day = row.Day;
                existing.Night = row.Night;
                existing.MembersOnly = row.MembersOnly;
                // §420. Whichever wrote last says whether the facts were
                // typed or read - a scan correcting a typed row makes it a
                // scanned row, and a hand correction of a scanned row is
                // marked as the hand's work.
                existing.Manual = row.Manual;

                updated++;
            }

            if (added + updated == 0 && unchanged == 0)
                return new PokedexMergeResult(0, 0, 0);

            entry.LastScannedUtc = DateTime.UtcNow;

            entry.Spawns = entry.Spawns
                .OrderBy(s => s.MapName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Save(entry);

            EntriesChanged?.Invoke();

            return new PokedexMergeResult(added, updated, unchanged);
        }

        /// <summary>
        /// §420. Takes one area off a species' record - the undo for a map
        /// typed wrong in the console. Matched through the same normaliser
        /// the merge uses, so the row goes whichever spelling it was kept
        /// under. A species left with no areas loses its file rather than
        /// keeping an empty one, so <see cref="All"/> does not list a Pokemon
        /// nothing is known about. Returns whether anything was removed.
        /// </summary>
        public static bool Remove(string species, string mapName)
        {
            if (string.IsNullOrWhiteSpace(species) || string.IsNullOrWhiteSpace(mapName))
                return false;

            PokedexEntry? entry = Load(species);

            if (entry is null)
                return false;

            string key = NormaliseMap(mapName);
            int removed = entry.Spawns.RemoveAll(s => NormaliseMap(s.MapName) == key);

            if (removed == 0)
                return false;

            if (entry.Spawns.Count == 0)
            {
                string? path = PathFor(entry.Species);

                try
                {
                    if (path is not null && File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Pokedex entry could not be removed: {Path}", path);
                    return false;
                }
            }
            else
            {
                Save(entry);
            }

            EntriesChanged?.Invoke();

            return true;
        }

        /// <summary>
        /// §421. The name a record is filed under: §405's modern spelling,
        /// then the sprite library's own name for it when the library knows
        /// it - "Galarian Linoone" is filed as "Linoone-Galarian", the name
        /// the hunt targets and the Spawns pages use, so the Map Explorer
        /// can find what the console filed. A name the library does not know
        /// is kept as given rather than refused: the announcement is usually
        /// ahead of the library, and a record under the typed name is worth
        /// more than no record. When the library is not loaded (a test, a
        /// tool) this is the modern spelling and nothing else.
        /// </summary>
        internal static string Canonical(string species)
        {
            string modern = PokemonNames.Modern(species).Trim();

            return PokemonSpriteService.ResolveLibraryName(modern) ?? modern;
        }

        public static PokedexEntry? Load(string species)
        {
            string canonical = Canonical(species);
            string? path = PathFor(canonical);

            // §421: a file saved under the spelling typed, before the name
            // was resolved through the library; read that, and move it.
            if ((path is null || !File.Exists(path)) && !string.Equals(canonical, species.Trim(), StringComparison.Ordinal))
                path = PathFor(species);

            // §405: a scan saved under the old spelling of the two Nidoran
            // sits in a file named for it; read that, and move it.
            if ((path is null || !File.Exists(path)) && PokemonNames.Legacy(canonical) is string legacy)
                path = PathFor(legacy);

            if (path is null || !File.Exists(path))
                return null;

            try
            {
                PokedexEntry? entry = JsonSerializer.Deserialize<PokedexEntry>(File.ReadAllText(path));

                return entry is null ? null : Modernize(entry, path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Pokedex entry could not be read: {Path}", path);
                return null;
            }
        }

        /// <summary>§405. An entry read from disk under the old spelling of
        /// the two Nidoran - or, §421, under a hand spelling the library has
        /// its own name for - is renamed, saved under its new file and the
        /// old file removed, once; an entry already under its name comes
        /// back untouched. When a file under the new name already exists
        /// (areas typed as "Galarian Linoone" and scanned as
        /// "Linoone-Galarian" on the same machine) the two are merged, map
        /// by map, the file already under the name keeping any map both
        /// list.</summary>
        private static PokedexEntry Modernize(PokedexEntry entry, string readFrom)
        {
            string old = entry.Species;
            string canonical = Canonical(old);

            if (string.Equals(canonical, old, StringComparison.Ordinal))
                return entry;

            string? modernPath = PathFor(canonical);

            if (modernPath is null)
                return entry;

            PokedexEntry kept = entry;

            if (!string.Equals(modernPath, readFrom, StringComparison.OrdinalIgnoreCase) && File.Exists(modernPath))
            {
                try
                {
                    PokedexEntry? already = JsonSerializer.Deserialize<PokedexEntry>(File.ReadAllText(modernPath));

                    if (already is not null)
                    {
                        foreach (PokedexSpawn spawn in entry.Spawns)
                        {
                            string key = NormaliseMap(spawn.MapName);

                            if (key.Length > 0 && !already.Spawns.Any(s => NormaliseMap(s.MapName) == key))
                                already.Spawns.Add(spawn);
                        }

                        already.Spawns = already.Spawns
                            .OrderBy(s => s.MapName, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        kept = already;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Pokedex entry could not be read: {Path}", modernPath);
                }
            }

            kept.Species = canonical;
            Save(kept);

            try
            {
                if (!string.Equals(modernPath, readFrom, StringComparison.OrdinalIgnoreCase) && File.Exists(modernPath))
                    File.Delete(readFrom);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Pokedex entry under the old spelling could not be removed: {Path}", readFrom);
            }

            Log.Information("Pokedex entry {Old} is now filed as {New}.", old, canonical);

            return kept;
        }

        /// <summary>§281. Every species scanned so far. A directory scan, done
        /// when something asks.</summary>
        public static IReadOnlyList<PokedexEntry> All()
        {
            var results = new List<PokedexEntry>();

            if (!Directory.Exists(DataFolder))
                return results;

            foreach (string path in Directory.GetFiles(DataFolder, FilePrefix + "*.json"))
            {
                try
                {
                    PokedexEntry? entry =
                        JsonSerializer.Deserialize<PokedexEntry>(File.ReadAllText(path));

                    if (entry is not null && entry.Species.Length > 0)
                        results.Add(Modernize(entry, path));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Pokedex entry could not be read: {Path}", path);
                }
            }

            // §421: a file Modernize merged into its twin during this very
            // scan (typed "Galarian Linoone" beside scanned
            // "Linoone-Galarian") would list the species twice - once as the
            // merged record, once as the twin read on its own. One per
            // species, and the fuller record is the merged one.
            return results
                .GroupBy(e => e.Species, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(e => e.Spawns.Count).First())
                .OrderBy(e => e.Species, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>§281. Which species are known to spawn on this map - the
        /// question the Pokedex panel cannot answer and a hunter asks all the
        /// time. Built from the per-species files rather than a second index,
        /// because an index is a thing that can disagree with them.</summary>
        public static IReadOnlyList<(PokedexEntry Entry, PokedexSpawn Spawn)> SpawnsOn(string mapName)
        {
            if (string.IsNullOrWhiteSpace(mapName))
                return Array.Empty<(PokedexEntry, PokedexSpawn)>();

            string key = NormaliseMap(mapName);

            return All()
                .SelectMany(e => e.Spawns.Select(s => (Entry: e, Spawn: s)))
                .Where(p => NormaliseMap(p.Spawn.MapName) == key)
                .OrderBy(p => p.Entry.Species, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void Save(PokedexEntry entry)
        {
            string? path = PathFor(entry.Species);

            if (path is null)
                return;

            try
            {
                Directory.CreateDirectory(DataFolder);

                // §193: durable - see DurableFile.
                DurableFile.WriteAllText(
                    path,
                    JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Pokedex entry could not be written: {Path}", path);
            }
        }

        private static string? PathFor(string species)
        {
            string safe = SafeName(species);

            return safe.Length == 0 ? null : Path.Combine(DataFolder, FilePrefix + safe + ".json");
        }

        /// <summary>§278's file-name rule, and for the same reason: dropping
        /// awkward characters lets two different names collide, so runs of
        /// them become one underscore and a stable hash of the original makes
        /// a collision impossible. FNV-1a written out rather than
        /// string.GetHashCode, which .NET randomises per process - the same
        /// species would land in a different file on every launch.</summary>
        internal static string SafeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string trimmed = value.Trim();

            var kept = new StringBuilder(trimmed.Length + 10);
            bool lastWasSeparator = false;

            foreach (char c in trimmed)
            {
                if (char.IsAsciiLetterOrDigit(c))
                {
                    kept.Append(char.ToLowerInvariant(c));
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

            string body = kept.ToString().Trim('_');

            return $"{body}-{StableHash(trimmed)}";
        }

        /// <summary>§281. What makes two readings of a map name the same map.
        /// Case, spacing and the punctuation OCR is least reliable about are
        /// all dropped, so "Mt. Moon 1F", "Mt Moon 1F" and "mt moon 1f" are
        /// one key. The stored SPELLING is untouched by this - only the
        /// matching is.</summary>
        internal static string NormaliseMap(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var kept = new StringBuilder(value.Length);

            foreach (char c in value)
            {
                if (char.IsAsciiLetterOrDigit(c))
                    kept.Append(char.ToLowerInvariant(c));
            }

            return kept.ToString();
        }

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
