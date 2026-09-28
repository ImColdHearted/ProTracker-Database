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
    /// §397. The published spawn pages, as this tracker last saw them.
    ///
    /// The admin files maps under regions in the Admin Console and the
    /// events server keeps the list; every tracker reads it from there when
    /// a Spawns page opens, and keeps a copy on disk so the pages still open
    /// - with what they showed last time - when the server cannot be
    /// reached. Same shape as ActiveEventService (§207) and for the same
    /// reason: one person sets it, every tracker follows, nobody ships
    /// anything.
    ///
    /// The copy is deliberately never expired. A stale page that says what
    /// it said last week beats an empty one that says nothing, and the
    /// window says when its copy is from.
    ///
    /// The admin side lives here too (BuildFromPokedex): a map's page is
    /// composed from §281's per-species Pokedex scans on the machine that
    /// publishes it, turned round from "where does Pidgey spawn" into "what
    /// spawns on Route 1".
    /// </summary>
    public static class SpawnDataService
    {
        private static readonly object Gate = new();

        private static readonly string CachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker",
            "Database",
            "spawns.json");

        private static List<SpawnMap>? current;

        /// <summary>When the copy in hand was fetched from the server - null
        /// for a copy read from disk whose fetch time was not recorded, and
        /// for no copy at all.</summary>
        public static DateTime? FetchedUtc { get; private set; }

        /// <summary>Raised after the list changes - a fetch that brought
        /// something different, or a publish from the console. Raised on
        /// whatever thread did it; windows post to the UI thread.</summary>
        public static event Action? Changed;

        /// <summary>Every published map, whatever region, as last seen.
        /// Reads the disk copy on first use.</summary>
        public static IReadOnlyList<SpawnMap> Current
        {
            get
            {
                lock (Gate)
                {
                    return (current ??= LoadCache()).ToList();
                }
            }
        }

        /// <summary>§399. The maps on one page that have a box on the
        /// region's picture, alphabetical.</summary>
        public static IReadOnlyList<SpawnMap> BoxedInRegion(string region) =>
            InRegion(region).Where(m => m.HasBoxes).ToList();

        /// <summary>The maps filed under one page, alphabetical - "simple
        /// links of the routes as they are added".</summary>
        public static IReadOnlyList<SpawnMap> InRegion(string region)
        {
            string? canonical = SpawnRegions.Canonical(region);

            if (canonical is null)
                return Array.Empty<SpawnMap>();

            return Current
                .Where(m => string.Equals(m.Region, canonical, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Map, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>One map by name, through the same key the server files
        /// it under; null when nothing is published for it.</summary>
        public static SpawnMap? Find(string map)
        {
            string key = SpawnMap.KeyFor(map);

            return key.Length == 0 ? null : Current.FirstOrDefault(m => m.Key == key);
        }

        /// <summary>Asks the server for the whole list and remembers it.
        /// Quiet: a Spawns page opening on a machine with no network is not
        /// a situation worth a dialog. False when nothing could be fetched,
        /// leaving the copy in hand in place.</summary>
        public static async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (!EventsSyncService.IsOnline)
                return false;

            try
            {
                IReadOnlyList<SpawnMap> fetched = await EventsSyncService.FetchSpawnMapsAsync(cancellationToken);

                Apply(fetched, DateTime.UtcNow);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Spawns: the published list could not be refreshed - the copy in hand stays in use.");
                return false;
            }
        }

        /// <summary>Takes a whole list - from a fetch, or from the console's
        /// Read Published - and says so if it differs from the one in hand.</summary>
        public static void Apply(IReadOnlyList<SpawnMap> maps, DateTime? fetchedUtc)
        {
            bool changed;

            lock (Gate)
            {
                List<SpawnMap> replacement = maps
                    .OrderBy(m => m.Region, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(m => m.Map, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                changed = current is null || !SameList(current, replacement);
                current = replacement;
                FetchedUtc = fetchedUtc;

                SaveCache(replacement, fetchedUtc);
            }

            if (changed)
            {
                Log.Information("Spawns: {Count} published maps in hand.", maps.Count);
                Changed?.Invoke();
            }
        }

        /// <summary>One map the console just published, so the machine that
        /// published it sees the page without a round trip.</summary>
        public static void ApplyOne(SpawnMap saved)
        {
            List<SpawnMap> list;

            lock (Gate)
            {
                list = (current ??= LoadCache())
                    .Where(m => m.Key != saved.Key)
                    .Append(saved)
                    .ToList();
            }

            Apply(list, FetchedUtc);
        }

        /// <summary>One map the console just removed.</summary>
        public static void RemoveOne(string key)
        {
            List<SpawnMap> list;

            lock (Gate)
            {
                list = (current ??= LoadCache())
                    .Where(m => m.Key != key)
                    .ToList();
            }

            Apply(list, FetchedUtc);
        }

        // ------------------------------------------------------- admin side

        /// <summary>The page for one map, composed from this machine's
        /// Pokedex scans (§281): every species whose scanned Area list names
        /// the map, with that row's facts, alphabetical. A map nobody has
        /// scanned a species for comes back with an empty list - a true
        /// answer the console says out loud rather than refuses.</summary>
        public static SpawnMap BuildFromPokedex(string region, string map) =>
            BuildFromPokedex(region, map, PokedexService.All());

        /// <summary>§412. Where a map is on the picture: itself when it has
        /// boxes of its own, else the map it is linked to when that one has
        /// boxes, else nowhere (null). One step only - a link to a map that
        /// is itself only linked leads nowhere.</summary>
        public static SpawnMap? SpotFor(SpawnMap map)
        {
            if (map.HasBoxes)
                return map;

            if (map.IsLinked && Find(map.LinkedTo!) is SpawnMap spot && spot.HasBoxes)
                return spot;

            return null;
        }

        /// <summary>§412. The maps that share a spot's box - linked to it and
        /// with no boxes of their own - by name.</summary>
        public static IReadOnlyList<SpawnMap> LinkedTo(SpawnMap spot)
        {
            string key = spot.Key;

            return Current
                .Where(m => !m.HasBoxes && m.IsLinked && SpawnMap.KeyFor(m.LinkedTo) == key && m.Key != key)
                .OrderBy(m => m.Map, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>§412. What a spot is called on the picture: its own name
        /// with nothing linked; with maps linked, the words every name in the
        /// group begins with ("Hoenn Safari Zone" for Area 1 to Area 6), or
        /// the spot's own name when they share none.</summary>
        public static string SpotName(SpawnMap spot, IReadOnlyList<SpawnMap> linked)
        {
            if (linked.Count == 0)
                return spot.Map;

            string[] first = spot.Map.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int shared = first.Length;

            foreach (SpawnMap other in linked)
            {
                string[] words = other.Map.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int n = 0;

                while (n < shared && n < words.Length && string.Equals(first[n], words[n], StringComparison.OrdinalIgnoreCase))
                    n++;

                shared = n;
            }

            return shared == 0 ? spot.Map : string.Join(" ", first.Take(shared));
        }

        /// <summary>The same, over scans already read - Republish All reads
        /// the store once for every map rather than once per map.</summary>
        public static SpawnMap BuildFromPokedex(string region, string map, IReadOnlyList<PokedexEntry> scans)
        {
            var page = new SpawnMap
            {
                Region = SpawnRegions.Canonical(region) ?? SpawnRegions.Other,
                Map = map.Trim(),
                // §399: a rebuild is about the species; the boxes the admin
                // drew for the map stay as published (§402: a copy of the
                // list, so the editor can add to or take from it freely).
                Markers = Find(map)?.Markers.ToList() ?? new List<MapMarker>(),
                // §412: the spot it shares stays too.
                LinkedTo = Find(map)?.LinkedTo,
            };

            // The store's own map key (§281), so a page matches exactly the
            // rows the Pokedex Scraper section would show for the map.
            string key = PokedexService.NormaliseMap(page.Map);

            if (key.Length == 0)
                return page;

            foreach (PokedexEntry entry in scans)
            {
                PokedexSpawn? spawn = entry.Spawns.FirstOrDefault(s => PokedexService.NormaliseMap(s.MapName) == key);

                if (spawn is null)
                    continue;

                // A species scanned twice under two spellings would list
                // twice; the first spelling seen keeps the row, as §281's
                // merge keeps the first spelling of a map.
                if (page.Pokemon.Any(p => string.Equals(p.Name, entry.Species, StringComparison.OrdinalIgnoreCase)))
                    continue;

                page.Pokemon.Add(SpawnPokemon.From(entry.Species, spawn));
            }

            page.Pokemon.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            return page;
        }

        // ------------------------------------------------------------ cache

        private static bool SameList(List<SpawnMap> a, List<SpawnMap> b)
        {
            if (a.Count != b.Count)
                return false;

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Key != b[i].Key
                    || a[i].Region != b[i].Region
                    || a[i].UpdatedUtc != b[i].UpdatedUtc
                    || a[i].Pokemon.Count != b[i].Pokemon.Count
                    || a[i].Markers.Count != b[i].Markers.Count
                    || !string.Equals(a[i].LinkedTo, b[i].LinkedTo, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<SpawnMap> LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath))
                    return new List<SpawnMap>();

                CacheDto? dto = JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(CachePath));

                if (dto is null)
                    return new List<SpawnMap>();

                FetchedUtc = dto.FetchedUtc;

                List<SpawnMap> maps = dto.Maps
                    .Where(m => !string.IsNullOrWhiteSpace(m.Map))
                    .ToList();

                // §405: the old spelling of the two Nidoran becomes the new.
                foreach (SpawnPokemon pokemon in maps.SelectMany(m => m.Pokemon))
                    pokemon.Name = PokemonNames.Modern(pokemon.Name);

                // §406: boxes drawn on the old region pictures move onto
                // the world picture.
                foreach (SpawnMap map in maps)
                    RegionMaps.ToWorld(map);

                return maps;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Spawns: the copy on disk could not be read - treating it as nothing published.");
                return new List<SpawnMap>();
            }
        }

        private static void SaveCache(List<SpawnMap> maps, DateTime? fetchedUtc)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);

                string json = JsonSerializer.Serialize(new CacheDto { Maps = maps, FetchedUtc = fetchedUtc });

                // §193's durable write - a power cut must not leave a
                // zero-filled file where the pages used to be.
                DurableFile.WriteAllText(CachePath, json);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Spawns: the published list could not be cached.");
            }
        }

        private sealed class CacheDto
        {
            public List<SpawnMap> Maps { get; set; } = new();
            public DateTime? FetchedUtc { get; set; }
        }
    }
}
