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
    /// §409. The boss pins every tracker holds - the copy of the server's
    /// list, kept on disk and refreshed quietly, the way SpawnDataService
    /// keeps the spawn pages (§397). A pin placed on one of §399's region
    /// pictures never existed, so unlike the boxes nothing is carried
    /// across here; a pin is placed on the world picture or not at all.
    /// </summary>
    public static class BossPinService
    {
        private static readonly object Gate = new();

        private static readonly string CachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker",
            "Database",
            "bosspins.json");

        private static List<BossPin>? current;

        public static DateTime? FetchedUtc { get; private set; }

        public static event Action? Changed;

        public static IReadOnlyList<BossPin> Current
        {
            get
            {
                lock (Gate)
                {
                    return (current ??= LoadCache()).ToList();
                }
            }
        }

        public static BossPin? Find(string bossId)
        {
            string key = SpawnMap.KeyFor(bossId);

            return key.Length == 0 ? null : Current.FirstOrDefault(p => p.Key == key);
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
                IReadOnlyList<BossPin> fetched = await EventsSyncService.FetchBossPinsAsync(cancellationToken);

                Apply(fetched, DateTime.UtcNow);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Boss pins: the published list could not be refreshed - the copy in hand stays in use.");
                return false;
            }
        }

        public static void Apply(IReadOnlyList<BossPin> pins, DateTime? fetchedUtc)
        {
            bool changed;

            lock (Gate)
            {
                List<BossPin> replacement = pins
                    .Where(p => p.IsValid)
                    .OrderBy(p => p.Boss, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                changed = current is null || !SameList(current, replacement);
                current = replacement;
                FetchedUtc = fetchedUtc;

                SaveCache(replacement, fetchedUtc);
            }

            if (changed)
                Changed?.Invoke();
        }

        /// <summary>One pin the editor just published.</summary>
        public static void ApplyOne(BossPin saved)
        {
            List<BossPin> list;

            lock (Gate)
            {
                list = (current ??= LoadCache())
                    .Where(p => p.Key != saved.Key)
                    .Append(saved)
                    .ToList();
            }

            Apply(list, FetchedUtc);
        }

        /// <summary>One pin the editor just took down.</summary>
        public static void RemoveOne(string bossId)
        {
            string key = SpawnMap.KeyFor(bossId);
            List<BossPin> list;

            lock (Gate)
            {
                list = (current ??= LoadCache())
                    .Where(p => p.Key != key)
                    .ToList();
            }

            Apply(list, FetchedUtc);
        }

        private static bool SameList(List<BossPin> a, List<BossPin> b)
        {
            if (a.Count != b.Count)
                return false;

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Key != b[i].Key || a[i].X != b[i].X || a[i].Y != b[i].Y
                    || a[i].ImageWidth != b[i].ImageWidth || a[i].ImageHeight != b[i].ImageHeight)
                {
                    return false;
                }
            }

            return true;
        }

        private static List<BossPin> LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath))
                    return new List<BossPin>();

                CacheDto? dto = JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(CachePath));

                if (dto is null)
                    return new List<BossPin>();

                FetchedUtc = dto.FetchedUtc;

                return dto.Pins.Where(p => p.IsValid).ToList();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Boss pins: the copy on disk could not be read - treating it as nothing placed.");
                return new List<BossPin>();
            }
        }

        private static void SaveCache(List<BossPin> pins, DateTime? fetchedUtc)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                DurableFile.WriteAllText(CachePath, JsonSerializer.Serialize(new CacheDto { Pins = pins, FetchedUtc = fetchedUtc }));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Boss pins: the published list could not be cached.");
            }
        }

        private sealed class CacheDto
        {
            public List<BossPin> Pins { get; set; } = new();
            public DateTime? FetchedUtc { get; set; }
        }
    }
}
