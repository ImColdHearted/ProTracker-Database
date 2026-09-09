using System;

namespace Foot_Tracker.Models
{
    /// <summary>
    /// One player's submission to an Events-board post (MIGRATION_GUIDE.md
    /// §106) - who submitted, which Pokemon, when, and the screenshot they
    /// attached as proof. Deliberately flat and additive: a "Community
    /// Hunting" post collects these, the entries window lists them
    /// (username/Pokemon/timestamp) and clicking one shows the screenshot.
    ///
    /// Every field except Username is optional in practice, which is what
    /// makes this shape survive the next round of additions - the planned
    /// auto-capture button on the main window (which will fill
    /// ScreenshotFileName without the player picking a file), and whatever
    /// extra detail the entry-detail window grows underneath the image, can
    /// both land here as new properties without migrating anything: an older
    /// entries file simply loads them at their defaults.
    /// </summary>
    public class EventEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>The GuildEvent.Id this entry belongs to. Entries are
        /// stored in one flat list and filtered by this, the same way the
        /// board itself keeps one flat list of posts.</summary>
        public string EventId { get; set; } = string.Empty;

        public string Username { get; set; } = string.Empty;

        /// <summary>The submitted Pokemon's resolved name, or empty if the
        /// submitter attached only a screenshot.</summary>
        public string PokemonName { get; set; } = string.Empty;

        public DateTime SubmittedAtUtc { get; set; }

        /// <summary>File NAME only (not a full path) inside the entry
        /// screenshot folder - see GuildEventEntryService.GetScreenshotPath.
        /// Storing the name rather than an absolute path keeps entries valid
        /// if the app's data folder ever moves, and keeps a personal
        /// filesystem path out of the saved file. Empty means no screenshot
        /// was attached.</summary>
        public string ScreenshotFileName { get; set; } = string.Empty;
    }
}
