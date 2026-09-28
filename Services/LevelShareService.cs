using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foot_Tracker.Models;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// §429. The sharing half of the level ranges: what THIS tracker tells
    /// the events server, and when.
    ///
    /// OPT-IN, AND NOTHING WITHOUT IT. Every method below reads
    /// TrackerSettings.ShareLevelData first and does nothing while it is
    /// false - which it is until the player ticks the box. A sighting from
    /// before the box was ticked is never sent later; turning sharing on
    /// starts from the next encounter. Turning it off drops whatever was
    /// waiting to go.
    ///
    /// WHAT IS SENT. Species, map and level, folded to one (map, species)
    /// row with its lowest and highest level and a count - the shape the
    /// server keeps. Never a username, never a time, never the install
    /// token (the request is anonymous, as the presence heartbeat is), and
    /// never an encounter whose level was not read or whose map was not
    /// known: a range built on "Lv. ?" or "Unknown" would be a wrong range.
    ///
    /// WHEN. Not at the moment of the encounter. The level the tracker reads
    /// when a battle opens is a first reading that the §96 vote can correct
    /// a second later, and the map can be corrected too (§102) - and a range
    /// the server keeps can only ever widen, so a first reading sent early
    /// and corrected late would widen it for good. So an encounter is
    /// observed when its record is FINAL: when the next encounter's record
    /// replaces it, or when the hunt stops or the app closes. The
    /// observations queue here and go in one post every couple of minutes,
    /// or at once when the app is closing.
    ///
    /// Each observation is also folded into SpawnLevelService straight away,
    /// so the hunter's own map shows what they saw whether or not the server
    /// ever hears of it.
    /// </summary>
    public static class LevelShareService
    {
        private const int FlushSeconds = 120;

        private const int MaxPerPost = 200;

        private static readonly object Gate = new();

        /// <summary>Folded, waiting to go: (map key | species) to the range
        /// seen since the last post.</summary>
        private static readonly Dictionary<string, PendingRange> pending = new(StringComparer.Ordinal);

        /// <summary>Records already observed, by reference - the same record
        /// can reach Observe twice (once when the next encounter replaces
        /// it, once at shutdown) and must count once.</summary>
        private static readonly HashSet<SessionEncounterRecord> seen = new(ReferenceEqualityComparer.Instance);

        private static Timer? timer;

        private static int posting;

        private sealed class PendingRange
        {
            public required string Map { get; init; }
            public required string Species { get; init; }
            public int Min { get; set; }
            public int Max { get; set; }
            public int Count { get; set; }
        }

        /// <summary>
        /// One encounter whose record will not change again. Folded into the
        /// local ranges at once and queued for the server when sharing is
        /// on. Safe to call with the same record more than once, with null,
        /// or with a record that has no level or no map - all are no-ops.
        /// </summary>
        public static void Observe(SessionEncounterRecord? record)
        {
            if (record is null || record.Level is not int level)
                return;

            if (level < 1 || level > 100)
                return;

            string map = record.Location;

            if (string.IsNullOrWhiteSpace(map) || string.Equals(map, "Unknown", StringComparison.OrdinalIgnoreCase)
                || string.Equals(map, "Unknown Location", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string species = record.PokemonName.Trim();

            if (species.Length == 0)
                return;

            lock (Gate)
            {
                if (!seen.Add(record))
                    return;
            }

            // The hunter's own map first, shared or not.
            SpawnLevelService.Observe(map, species, level);

            if (!TrackerSettings.ShareLevelData)
                return;

            string mapKey = SpawnMap.KeyFor(map);

            if (mapKey.Length == 0)
                return;

            lock (Gate)
            {
                string key = SpawnLevelRange.KeyFor(mapKey, species);

                if (pending.TryGetValue(key, out PendingRange? range))
                {
                    if (level < range.Min) range.Min = level;
                    if (level > range.Max) range.Max = level;
                    range.Count++;
                }
                else
                {
                    pending[key] = new PendingRange { Map = map, Species = species, Min = level, Max = level, Count = 1 };
                }

                timer ??= new Timer(_ => _ = FlushAsync(), null, TimeSpan.FromSeconds(FlushSeconds), TimeSpan.FromSeconds(FlushSeconds));
            }
        }

        /// <summary>§429. Sharing turned off: whatever was waiting is dropped,
        /// because the player just said not to send it.</summary>
        public static void Discard()
        {
            lock (Gate)
            {
                pending.Clear();
            }
        }

        /// <summary>Posts what is waiting, if anything and if sharing is
        /// still on. One post at a time; a post that fails leaves its rows
        /// waiting for the next try rather than losing them.</summary>
        public static async Task FlushAsync()
        {
            if (!TrackerSettings.ShareLevelData)
            {
                Discard();
                return;
            }

            if (!EventsSyncService.IsOnline)
                return;

            if (Interlocked.Exchange(ref posting, 1) == 1)
                return;

            try
            {
                List<PendingRange> batch;

                lock (Gate)
                {
                    if (pending.Count == 0)
                        return;

                    batch = pending.Values.Take(MaxPerPost).ToList();

                    foreach (PendingRange range in batch)
                        pending.Remove(SpawnLevelRange.KeyFor(SpawnMap.KeyFor(range.Map), range.Species));
                }

                List<EventsSyncService.LevelSighting> sightings = batch
                    .Select(r => new EventsSyncService.LevelSighting(r.Map, r.Species, r.Min, r.Max, r.Count))
                    .ToList();

                try
                {
                    int accepted = await EventsSyncService.PostSpawnLevelsAsync(sightings);

                    Log.Information("Spawn levels: shared {Rows} range(s), {Accepted} accepted.", sightings.Count, accepted);
                }
                catch (Exception ex)
                {
                    // Back in the queue, folded with anything that arrived
                    // meanwhile, for the next try.
                    Log.Debug(ex, "Spawn levels: the post did not go; keeping {Rows} range(s) for the next try.", batch.Count);

                    lock (Gate)
                    {
                        foreach (PendingRange range in batch)
                        {
                            string key = SpawnLevelRange.KeyFor(SpawnMap.KeyFor(range.Map), range.Species);

                            if (pending.TryGetValue(key, out PendingRange? later))
                            {
                                if (range.Min < later.Min) later.Min = range.Min;
                                if (range.Max > later.Max) later.Max = range.Max;
                                later.Count += range.Count;
                            }
                            else
                            {
                                pending[key] = range;
                            }
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref posting, 0);
            }
        }

        /// <summary>The app is closing: one last try, waited for briefly, so
        /// a hunt's last few encounters are not the ones that never make it.</summary>
        public static void FlushOnExit()
        {
            try
            {
                FlushAsync().Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Spawn levels: the closing post did not finish in time.");
            }
        }
    }
}
