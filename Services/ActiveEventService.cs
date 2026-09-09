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
    /// §207. What the tracker believes is currently running, and where that
    /// belief comes from.
    ///
    /// The admin publishes a list of up to three counterpart events from the
    /// admin console; every tracker reads it from the same server, on the
    /// heartbeat it already sends, and caches it to disk. That is the whole
    /// point of putting it on the server rather than in the build: an event
    /// starts, one person sets it, and every tracker follows within minutes
    /// with nobody shipping anything.
    ///
    /// Three states, and the difference between the last two matters:
    ///
    ///   - Nothing published. Every event skin is considered, exactly as
    ///     before this existed. This is what an untouched install sees.
    ///   - Published, and this tracker has read it. Only those events are
    ///     considered - see CounterpartMatcher.
    ///   - Published, but this tracker cannot reach the server right now.
    ///     The cached copy from last time is used, because a hunt that loses
    ///     its network should not silently change how it identifies forms.
    ///
    /// The cache is deliberately not given an expiry. A stale list narrows
    /// the search to the wrong event, which is bad; an EMPTY list because
    /// the cache expired mid-hunt widens it back to everything, which is
    /// the behaviour the admin was trying to move away from. Between two
    /// wrongs, the one the admin last asked for wins.
    /// </summary>
    public static class ActiveEventService
    {
        private static readonly object Gate = new();

        private static readonly string CachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker",
            "Database",
            "active-events.json");

        private static ActiveEvents? current;

        /// <summary>What is running, as far as this tracker knows. Reads the
        /// cache on first use so a hunt that starts offline still narrows the
        /// way the last online run did.</summary>
        public static ActiveEvents Current
        {
            get
            {
                lock (Gate)
                {
                    return current ??= LoadCache();
                }
            }
        }

        /// <summary>Asks the server and remembers the answer. Quiet by
        /// design: this runs on a timer next to the presence heartbeat, and
        /// a tracker with no network is not a situation worth a dialog.
        /// Returns false when nothing could be fetched, leaving whatever was
        /// cached in place.</summary>
        public static async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (!EventsSyncService.IsOnline)
                return false;

            try
            {
                ActiveEvents fetched = await EventsSyncService.FetchActiveEventsAsync(cancellationToken);

                Apply(fetched);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Active events: could not be refreshed - the cached list stays in use.");
                return false;
            }
        }

        /// <summary>Takes a list the admin console just published, so the
        /// machine that set it does not have to wait for its own heartbeat
        /// to see it.</summary>
        public static void Apply(ActiveEvents events)
        {
            lock (Gate)
            {
                bool changed = current == null ||
                               !current.Events.SequenceEqual(events.Events, StringComparer.OrdinalIgnoreCase);

                current = events;

                SaveCache(events);

                if (changed)
                {
                    Log.Information(
                        "Active events: now {Events}",
                        events.Any ? string.Join(", ", events.Events) : "(none published - every event skin is considered)");
                }
            }
        }

        private static ActiveEvents LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath))
                    return ActiveEvents.None;

                CacheDto? dto = JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(CachePath));

                if (dto == null)
                    return ActiveEvents.None;

                return new ActiveEvents(dto.Events, dto.UpdatedUtc, dto.UpdatedBy ?? string.Empty);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Active events: the cached list could not be read - treating it as nothing published.");
                return ActiveEvents.None;
            }
        }

        private static void SaveCache(ActiveEvents events)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);

                string json = JsonSerializer.Serialize(new CacheDto
                {
                    Events = events.Events.ToList(),
                    UpdatedUtc = events.UpdatedUtc,
                    UpdatedBy = events.UpdatedBy
                });

                // §193's durable write - a power cut must not leave a
                // zero-filled file where the list used to be.
                DurableFile.WriteAllText(CachePath, json);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Active events: the list could not be cached.");
            }
        }

        private sealed class CacheDto
        {
            public List<string> Events { get; set; } = new();
            public string? UpdatedUtc { get; set; }
            public string? UpdatedBy { get; set; }
        }
    }
}
