using Foot_Tracker.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Local-only storage for Events-board submissions (MIGRATION_GUIDE.md
    /// §106) - the entries behind each post's "View Entries" list. Same
    /// prototype scope and the same honesty as GuildEventService itself: this
    /// does NOT reach any other machine, so entries only ever appear on the
    /// tracker they were submitted on. A real guild board needs a shared
    /// backend, which is a separate, bigger piece of work than either of
    /// these two files covers.
    ///
    /// Kept in its own file rather than folded into GuildEventService: posts
    /// and submissions have different shapes, different lifetimes (deleting a
    /// post takes its entries with it, never the other way round) and, once a
    /// backend does exist, almost certainly different sync rules.
    ///
    /// Screenshots are COPIED into the app's own folder at submit time rather
    /// than referenced where they sit. Three reasons: an entry stays valid
    /// when the player later moves or deletes the original, the saved file
    /// holds a bare file name instead of somebody's personal Downloads path,
    /// and there is exactly one place to look when the planned auto-capture
    /// button starts writing these images directly.
    /// </summary>
    public static class GuildEventEntryService
    {
        private static readonly string SaveFolder =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProTracker",
                "Database"
            );

        private static readonly string SavePath =
            Path.Combine(SaveFolder, "guild-event-entries.json");

        /// <summary>Where submitted screenshots live - one folder, flat, file
        /// names generated per entry so two submissions of the same source
        /// image can never collide.</summary>
        private static readonly string ScreenshotFolder =
            Path.Combine(SaveFolder, "EventEntries");

        private static readonly List<EventEntry> entries = new();
        private static bool loaded;

        private static void EnsureLoaded()
        {
            if (loaded)
                return;

            loaded = true;

            if (!File.Exists(SavePath))
                return;

            try
            {
                List<EventEntry>? saved =
                    JsonSerializer.Deserialize<List<EventEntry>>(File.ReadAllText(SavePath));

                if (saved != null)
                    entries.AddRange(saved);
            }
            catch (Exception ex)
            {
                // Keep an empty list if the file is damaged, same as every
                // other local save this app reads.
                Log.Warning(ex, "Event entries could not be loaded from {Path}", SavePath);
            }
        }

        /// <summary>Every entry for one post, newest first.</summary>
        public static IReadOnlyList<EventEntry> EntriesFor(string eventId)
        {
            EnsureLoaded();

            return entries
                .Where(e => string.Equals(e.EventId, eventId, StringComparison.Ordinal))
                .OrderByDescending(e => e.SubmittedAtUtc)
                .ToList();
        }

        public static int CountFor(string eventId)
        {
            EnsureLoaded();

            return entries.Count(e => string.Equals(e.EventId, eventId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Records one submission. <paramref name="screenshotSourcePath"/> is
        /// copied into this app's own screenshot folder (see the class doc);
        /// null/empty/missing simply means "no screenshot attached", which is
        /// a valid entry rather than an error - the detail window says so in
        /// words instead of showing a broken image.
        /// </summary>
        public static EventEntry Submit(
            string eventId,
            string username,
            string pokemonName,
            string? screenshotSourcePath)
        {
            EnsureLoaded();

            var entry = new EventEntry
            {
                EventId = eventId,
                Username = string.IsNullOrWhiteSpace(username) ? "Unknown" : username.Trim(),
                PokemonName = string.IsNullOrWhiteSpace(pokemonName) ? string.Empty : pokemonName.Trim(),
                SubmittedAtUtc = DateTime.UtcNow,
                ScreenshotFileName = CopyScreenshot(screenshotSourcePath)
            };

            entries.Add(entry);
            Save();

            return entry;
        }

        /// <summary>§143. Brings this event's local entries in line with the
        /// server's list. An entry this machine submitted keeps its
        /// screenshot (the server never sees screenshots); entries submitted
        /// elsewhere arrive without one; entries the server no longer has -
        /// removed by the admin or by the tracker that submitted them - are
        /// dropped here too, screenshot and all.</summary>
        public static void MergeServerEntries(string eventId, IEnumerable<EventEntry> serverEntries)
        {
            EnsureLoaded();

            List<EventEntry> fresh = serverEntries.ToList();
            var keep = new HashSet<string>(fresh.Select(e => e.Id), StringComparer.Ordinal);

            foreach (EventEntry gone in entries
                         .Where(e => string.Equals(e.EventId, eventId, StringComparison.Ordinal) && !keep.Contains(e.Id))
                         .ToList())
            {
                TryDeleteScreenshot(gone);
                entries.Remove(gone);
            }

            foreach (EventEntry incoming in fresh)
            {
                EventEntry? local = entries.FirstOrDefault(e => string.Equals(e.Id, incoming.Id, StringComparison.Ordinal));

                if (local is null)
                {
                    entries.Add(incoming);
                    continue;
                }

                local.Username = incoming.Username;
                local.PokemonName = incoming.PokemonName;
                local.SubmittedAtUtc = incoming.SubmittedAtUtc;
            }

            Save();
        }

        /// <summary>§143. Records an entry the server accepted - its id, its
        /// clock - together with this machine's screenshot, which stays
        /// local. The online counterpart of Submit.</summary>
        public static EventEntry Record(EventEntry accepted, string? screenshotSourcePath)
        {
            EnsureLoaded();

            accepted.ScreenshotFileName = CopyScreenshot(screenshotSourcePath);

            entries.RemoveAll(e => string.Equals(e.Id, accepted.Id, StringComparison.Ordinal));
            entries.Add(accepted);
            Save();

            return accepted;
        }

        /// <summary>Removes every entry belonging to a post (and the
        /// screenshots they own) - called when the post itself is deleted, so
        /// entries can never outlive the thing they were submitted to. Returns
        /// how many were removed.</summary>
        public static int DeleteForEvent(string eventId)
        {
            EnsureLoaded();

            List<EventEntry> doomed = entries
                .Where(e => string.Equals(e.EventId, eventId, StringComparison.Ordinal))
                .ToList();

            if (doomed.Count == 0)
                return 0;

            foreach (EventEntry entry in doomed)
            {
                TryDeleteScreenshot(entry);
                entries.Remove(entry);
            }

            Save();
            return doomed.Count;
        }

        /// <summary>The full path of an entry's screenshot, or null when it
        /// has none (or the file has since gone missing) - callers show the
        /// "no screenshot" state rather than a broken image.</summary>
        public static string? GetScreenshotPath(EventEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.ScreenshotFileName))
                return null;

            string path = Path.Combine(ScreenshotFolder, entry.ScreenshotFileName);

            return File.Exists(path) ? path : null;
        }

        /// <summary>Writes raw PNG bytes (the live-capture path in the submit
        /// window, and the future auto-capture button) into the screenshot
        /// folder, returning the full path for Submit to take as its source.
        /// Returns null on any failure - a failed capture must never block a
        /// submission.</summary>
        public static string? SaveCapturedScreenshot(byte[] pngBytes)
        {
            if (pngBytes.Length == 0)
                return null;

            try
            {
                Directory.CreateDirectory(ScreenshotFolder);

                string path = Path.Combine(
                    ScreenshotFolder,
                    $"capture-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");

                File.WriteAllBytes(path, pngBytes);
                return path;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Captured entry screenshot could not be written");
                return null;
            }
        }

        private static string CopyScreenshot(string? sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return string.Empty;

            try
            {
                Directory.CreateDirectory(ScreenshotFolder);

                string extension = Path.GetExtension(sourcePath);

                if (string.IsNullOrWhiteSpace(extension))
                    extension = ".png";

                string fileName = $"{Guid.NewGuid():N}{extension}";
                string destination = Path.Combine(ScreenshotFolder, fileName);

                // A capture this app just wrote into the same folder is moved
                // rather than copied - otherwise every live capture would
                // leave a stray twin behind.
                if (string.Equals(
                        Path.GetDirectoryName(sourcePath),
                        ScreenshotFolder,
                        StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(sourcePath, destination);
                }
                else
                {
                    File.Copy(sourcePath, destination, overwrite: false);
                }

                return fileName;
            }
            catch (Exception ex)
            {
                // An entry without its screenshot is still a real entry -
                // never lose the submission over the image.
                Log.Warning(ex, "Entry screenshot could not be stored from {Path}", sourcePath);
                return string.Empty;
            }
        }

        private static void TryDeleteScreenshot(EventEntry entry)
        {
            try
            {
                string? path = GetScreenshotPath(entry);

                if (path != null)
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                // A leftover image is harmless - nothing reads the folder
                // except through an entry that names a file in it.
                Log.Warning(ex, "Entry screenshot could not be deleted for entry {Id}", entry.Id);
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(SaveFolder);

                File.WriteAllText(
                    SavePath,
                    JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Event entries could not be saved to {Path}", SavePath);
            }
        }
    }
}
