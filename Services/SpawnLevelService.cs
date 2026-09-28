using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Foot_Tracker.Models;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// §429. The level ranges every tracker reads: what level each species
    /// has been met at on each map, from everyone who opted in to sharing.
    /// The same shape as SpawnDataService and BossPinService, and for the
    /// same reasons - fetched from the events server, a copy kept on disk so
    /// the Map Explorer has an answer with no network, never expired.
    ///
    /// What this machine's own tracker sees is folded in here at once
    /// (Observe), so a hunter's own sightings show on their map before the
    /// server has been told, and stay shown if it never is. The server's
    /// copy replaces the local one on the next fetch; a range can only widen
    /// on either side, so nothing is ever lost by that.
    /// </summary>
    public static class SpawnLevelService
    {
        private static readonly object Gate = new();

        private static readonly string CachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker",
            "Database",
            "spawn-levels.json");

        private static Dictionary<string, SpawnLevelRange>? current;

        public static DateTime? FetchedUtc { get; private set; }

        /// <summary>Raised after the ranges change - a fetch that brought
        /// something new, or a sighting of this tracker's own. On whatever
        /// thread did it; windows post to the UI thread.</summary>
        public static event Action? Changed;

        /// <summary>Every range in hand.</summary>
        public static IReadOnlyList<SpawnLevelRange> Current
        {
            get
            {
                lock (Gate)
                {
                    return (current ??= LoadCache()).Values.ToList();
                }
            }
        }

        /// <summary>The range for one species on one map, by the map's name
        /// or key and the library's name for the species; null when nobody
        /// has reported one.</summary>
        public static SpawnLevelRange? Find(string? map, string? species)
        {
            if (string.IsNullOrWhiteSpace(map) || string.IsNullOrWhiteSpace(species))
                return null;

            string key = SpawnLevelRange.KeyFor(SpawnMap.KeyFor(map), species);

            lock (Gate)
            {
                return (current ??= LoadCache()).TryGetValue(key, out SpawnLevelRange? range) ? range : null;
            }
        }

        /// <summary>Asks the server for the whole list and remembers it.
        /// Quiet, like the spawn pages: false when nothing could be fetched,
        /// leaving the copy in hand in place.</summary>
        public static async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (!EventsSyncService.IsOnline)
                return false;

            try
            {
                IReadOnlyList<SpawnLevelRange> fetched = await EventsSyncService.FetchSpawnLevelsAsync(cancellationToken);

                Apply(fetched, DateTime.UtcNow);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Spawn levels: the published ranges could not be refreshed - the copy in hand stays in use.");
                return false;
            }
        }

        /// <summary>Takes the server's whole list. Anything this tracker saw
        /// itself that the server does not yet know (a sighting not yet
        /// posted, or one that never will be because sharing was turned off
        /// after) is kept by widening the server's range with it.</summary>
        public static void Apply(IReadOnlyList<SpawnLevelRange> ranges, DateTime? fetchedUtc)
        {
            bool changed;

            lock (Gate)
            {
                Dictionary<string, SpawnLevelRange> replacement = new(StringComparer.Ordinal);

                foreach (SpawnLevelRange range in ranges)
                {
                    if (string.IsNullOrWhiteSpace(range.MapKey) || string.IsNullOrWhiteSpace(range.Species) || range.Min < 1 || range.Max < range.Min)
                        continue;

                    replacement[range.Key] = range;
                }

                Dictionary<string, SpawnLevelRange> before = current ??= LoadCache();

                foreach (SpawnLevelRange mine in before.Values)
                {
                    if (replacement.TryGetValue(mine.Key, out SpawnLevelRange? theirs))
                    {
                        if (mine.Min < theirs.Min) theirs.Min = mine.Min;
                        if (mine.Max > theirs.Max) theirs.Max = mine.Max;
                    }
                    else if (mine.Samples > 0 && mine.UpdatedUtc is null)
                    {
                        // A local-only sighting (never stamped by the server):
                        // kept, so the hunter's own map does not forget what
                        // they saw because the server has not heard yet.
                        replacement[mine.Key] = mine;
                    }
                }

                changed = !Same(before, replacement);
                current = replacement;
                FetchedUtc = fetchedUtc;

                SaveCache(replacement, fetchedUtc);
            }

            if (changed)
            {
                Log.Information("Spawn levels: {Count} ranges in hand.", ranges.Count);
                Changed?.Invoke();
            }
        }

        /// <summary>§429. One sighting of this tracker's own, folded into the
        /// copy in hand at once - whether or not it is ever shared. Returns
        /// true when it widened (or created) the range, which is when the
        /// Map Explorer has something new to draw.</summary>
        public static bool Observe(string map, string species, int level)
        {
            if (string.IsNullOrWhiteSpace(map) || string.IsNullOrWhiteSpace(species) || level < 1 || level > 100)
                return false;

            string mapKey = SpawnMap.KeyFor(map);

            if (mapKey.Length == 0)
                return false;

            bool changed;

            lock (Gate)
            {
                Dictionary<string, SpawnLevelRange> ranges = current ??= LoadCache();
                string key = SpawnLevelRange.KeyFor(mapKey, species);

                if (ranges.TryGetValue(key, out SpawnLevelRange? range))
                {
                    changed = level < range.Min || level > range.Max;

                    if (level < range.Min) range.Min = level;
                    if (level > range.Max) range.Max = level;
                    range.Samples++;
                }
                else
                {
                    ranges[key] = new SpawnLevelRange
                    {
                        MapKey = mapKey,
                        Species = species.Trim(),
                        Min = level,
                        Max = level,
                        Samples = 1,
                    };
                    changed = true;
                }

                SaveCache(ranges, FetchedUtc);
            }

            if (changed)
                Changed?.Invoke();

            return changed;
        }

        // ------------------------------------------------------------ cache

        private static bool Same(Dictionary<string, SpawnLevelRange> a, Dictionary<string, SpawnLevelRange> b)
        {
            if (a.Count != b.Count)
                return false;

            foreach (KeyValuePair<string, SpawnLevelRange> pair in a)
            {
                if (!b.TryGetValue(pair.Key, out SpawnLevelRange? other)
                    || other.Min != pair.Value.Min
                    || other.Max != pair.Value.Max
                    || other.Samples != pair.Value.Samples)
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class CacheFile
        {
            public DateTime? FetchedUtc { get; set; }

            public List<SpawnLevelRange> Ranges { get; set; } = new();
        }

        private static Dictionary<string, SpawnLevelRange> LoadCache()
        {
            var ranges = new Dictionary<string, SpawnLevelRange>(StringComparer.Ordinal);

            try
            {
                if (!File.Exists(CachePath))
                    return ranges;

                CacheFile? file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));

                if (file is null)
                    return ranges;

                FetchedUtc = file.FetchedUtc;

                foreach (SpawnLevelRange range in file.Ranges)
                {
                    if (!string.IsNullOrWhiteSpace(range.MapKey) && !string.IsNullOrWhiteSpace(range.Species) && range.Min >= 1 && range.Max >= range.Min)
                        ranges[range.Key] = range;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Spawn levels: the cached ranges could not be read: {Path}", CachePath);
            }

            return ranges;
        }

        private static void SaveCache(Dictionary<string, SpawnLevelRange> ranges, DateTime? fetchedUtc)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);

                var file = new CacheFile
                {
                    FetchedUtc = fetchedUtc,
                    Ranges = ranges.Values
                        .OrderBy(r => r.MapKey, StringComparer.Ordinal)
                        .ThenBy(r => r.Species, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                };

                DurableFile.WriteAllText(CachePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Spawn levels: the cached ranges could not be written: {Path}", CachePath);
            }
        }
    }
}
