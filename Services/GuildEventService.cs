using Foot_Tracker.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Local-only prototype storage for the Events board (see EventsWindow/
    /// EventsViewModel) - a small test to see what composing and reading a
    /// guild post actually looks like, before building the real thing.
    ///
    /// This does NOT talk to any other machine yet. Every event posted here
    /// only ever shows up in this same local list, saved to a JSON file next
    /// to the other local databases this app already keeps
    /// (%LocalAppData%\ProTracker\Database). A real "guild hunts, giveaways,
    /// and more" feature needs this replaced with something that actually
    /// reaches other players' trackers - a shared backend of some kind -
    /// which is a separate, bigger piece of work than this prototype covers.
    /// Two people running this build side by side will each only ever see
    /// their own posts.
    ///
    /// Deliberately NOT per-client (unlike BossCooldownService/
    /// PvpOpponentService's GetSavePath) - the guild board isn't tied to
    /// which PRO account this app instance happens to be tracking right now,
    /// so switching clients should not swap out or hide any of these posts.
    /// </summary>
    public static class GuildEventService
    {
        private static readonly string SaveFolder =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProTracker",
                "Database"
            );

        private static readonly string SavePath =
            Path.Combine(SaveFolder, "guild-events.json");

        private static readonly List<GuildEvent> events = new();
        private static bool loaded;

        /// <summary>Every posted event, newest first.</summary>
        public static IReadOnlyList<GuildEvent> Events
        {
            get
            {
                EnsureLoaded();
                return events.OrderByDescending(e => e.PostedAtUtc).ToList();
            }
        }

        private static void EnsureLoaded()
        {
            if (loaded)
                return;

            loaded = true;

            if (!File.Exists(SavePath))
                return;

            try
            {
                string json = File.ReadAllText(SavePath);
                List<GuildEvent>? saved = JsonSerializer.Deserialize<List<GuildEvent>>(json);

                if (saved != null)
                    events.AddRange(saved);
            }
            catch
            {
                // Keep an empty board if the file is damaged, same as every
                // other local save this app reads.
            }
        }

        public static GuildEvent Post(
            GuildEventType type,
            string title,
            string message,
            string postedBy,
            string pokemonName = "",
            string itemReward = "",
            string pokemonReward = "",
            long pokeDollars = 0,
            bool allowPokemonSubmissions = true,
            bool allowViewEntries = true)
        {
            EnsureLoaded();

            var newEvent = new GuildEvent
            {
                Type = type,
                Title = title.Trim(),
                // §103: the board's card design shows the message inside a
                // fixed box labeled "max 75 characters" - enforced here as
                // well as in the compose window, so the card can never
                // overflow whatever a caller sends.
                Message = Truncate(message.Trim(), 75),
                PostedBy = string.IsNullOrWhiteSpace(postedBy) ? "Unknown" : postedBy.Trim(),
                PostedAtUtc = DateTime.UtcNow,
                PokemonName = string.IsNullOrWhiteSpace(pokemonName) ? string.Empty : pokemonName.Trim(),
                ItemReward = string.IsNullOrWhiteSpace(itemReward) ? string.Empty : itemReward.Trim(),
                PokemonReward = string.IsNullOrWhiteSpace(pokemonReward) ? string.Empty : pokemonReward.Trim(),
                PokeDollars = Math.Max(0, pokeDollars),
                // §107: default true on BOTH the parameter and the model
                // property, so an older caller that does not pass them still
                // posts an event with both buttons - the behaviour every post
                // had before the markers existed.
                AllowPokemonSubmissions = allowPokemonSubmissions,
                AllowViewEntries = allowViewEntries
            };

            events.Add(newEvent);
            Save();

            return newEvent;
        }

        /// <summary>Removes one posted event by Id. Returns false (a no-op, not
        /// an error) if nothing with that Id was found - e.g. two Remove Event
        /// windows open at once and it was already deleted from the other one.</summary>
        public static bool Delete(string id)
        {
            EnsureLoaded();

            int removed = events.RemoveAll(e => e.Id == id);

            if (removed > 0)
            {
                // §106: a post's entries belong to it - they go with it,
                // screenshots included, rather than being orphaned in the
                // entries file forever.
                GuildEventEntryService.DeleteForEvent(id);
                Save();
            }

            return removed > 0;
        }

        /// <summary>§143. Makes the local copy the server's copy: every
        /// post the server returned, nothing else. Entries cached for posts
        /// the server no longer has go too (an admin deleted them there),
        /// screenshots included - the same rule Delete applies locally.</summary>
        public static void ReplaceAll(IEnumerable<GuildEvent> serverEvents)
        {
            EnsureLoaded();

            List<GuildEvent> fresh = serverEvents.ToList();
            var keep = new HashSet<string>(fresh.Select(e => e.Id), StringComparer.Ordinal);

            foreach (GuildEvent gone in events.Where(e => !keep.Contains(e.Id)).ToList())
                GuildEventEntryService.DeleteForEvent(gone.Id);

            events.Clear();
            events.AddRange(fresh);
            Save();
        }

        /// <summary>§143. Caches a post the server just accepted - the
        /// server's id and clock, not a local guess - so the board shows it
        /// before the next full refresh.</summary>
        public static void Upsert(GuildEvent serverEvent)
        {
            EnsureLoaded();

            events.RemoveAll(e => e.Id == serverEvent.Id);
            events.Add(serverEvent);
            Save();
        }

        private static string Truncate(string text, int maxLength) =>
            text.Length <= maxLength ? text : text[..maxLength];

        private static void Save()
        {
            Directory.CreateDirectory(SaveFolder);

            string json = JsonSerializer.Serialize(
                events,
                new JsonSerializerOptions { WriteIndented = true });

            DurableFile.WriteAllText(SavePath, json);
        }
    }
}
