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

        public static GuildEvent Post(GuildEventType type, string title, string message, string postedBy, string pokemonName = "")
        {
            EnsureLoaded();

            var newEvent = new GuildEvent
            {
                Type = type,
                Title = title.Trim(),
                Message = message.Trim(),
                PostedBy = string.IsNullOrWhiteSpace(postedBy) ? "Unknown" : postedBy.Trim(),
                PostedAtUtc = DateTime.UtcNow,
                PokemonName = string.IsNullOrWhiteSpace(pokemonName) ? string.Empty : pokemonName.Trim()
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
                Save();

            return removed > 0;
        }

        private static void Save()
        {
            Directory.CreateDirectory(SaveFolder);

            string json = JsonSerializer.Serialize(
                events,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(SavePath, json);
        }
    }
}
