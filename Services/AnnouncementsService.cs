using System.Globalization;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// §239. What PRO announced, read from the events server rather than from
    /// PRO's forum.
    ///
    /// WHAT THIS REPLACED, AND WHY. Until now this class fetched the "Update
    /// Logs" forum topic and parsed its rendered HTML: it found the current
    /// last page, read the posts out of it, and pulled the page before it in
    /// too. Its own remarks called that "a strictly less stable foundation
    /// than the RSS feed was - if the forum's theme changes, this can break in
    /// a way the RSS feed never could have." It also only ever saw client
    /// changelogs, because that is all that topic holds.
    ///
    /// PRO's own #announcements channel carries the rest: maintenance windows,
    /// PvP bans, Trade Zone changes, when the World Quest starts, when the
    /// servers come back. §232 already had to learn how to read a followed
    /// Discord channel for World Quests, and the bot token lives on the Worker
    /// where a decompilable client cannot reach it - so the same machinery
    /// reads this channel and this asks the Worker for the result.
    ///
    /// No HTML is parsed here any more, and no request goes to PRO at all.
    /// </summary>
    public static class AnnouncementsService
    {
        /// <summary>How much of a post the list shows before it is cut. The
        /// full text is kept on the server and the window can show it; this is
        /// only the collapsed line.</summary>
        public const int SummaryMaxLength = 400;

        public static Task<IReadOnlyList<Announcement>> FetchAsync(CancellationToken cancellationToken = default) =>
            EventsSyncService.FetchAnnouncementsAsync(cancellationToken);

        /// <summary>First line, or the first SummaryMaxLength characters,
        /// whichever comes first. Discord posts run long and the list wants a
        /// line, not an essay.</summary>
        public static string Summarise(string body)
        {
            string text = (body ?? string.Empty).Replace("\r", string.Empty).Trim();

            if (text.Length == 0)
                return string.Empty;

            return text.Length <= SummaryMaxLength
                ? text
                : text[..SummaryMaxLength].TrimEnd() + "...";
        }
    }

    /// <summary>§239. One announcement. Body is the whole post; Summary is
    /// what the collapsed row shows. ImageUrl is empty unless the post carried
    /// a picture the Worker was willing to pass on - see its own
    /// ANNOUNCE_IMAGE_HOSTS.</summary>
    public sealed class Announcement
    {
        public required string Id { get; init; }
        public required string Author { get; init; }
        public required string Body { get; init; }
        public required string ImageUrl { get; init; }
        public DateTimeOffset? Published { get; init; }

        public string Summary => AnnouncementsService.Summarise(Body);

        public bool HasImage => !string.IsNullOrWhiteSpace(ImageUrl);

        /// <summary>The heading for the row: who posted it, or the date when
        /// the relay did not carry a name.</summary>
        public string Title =>
            string.IsNullOrWhiteSpace(Author) ? "Announcement" : Author;

        // Formatted here, not in XAML, so the view never needs a StringFormat
        // binding against a nullable DateTimeOffset.
        public string PublishedDisplay =>
            Published?.ToLocalTime().ToString("MMMM d, yyyy HH:mm", CultureInfo.CurrentCulture)
            ?? "Date unknown";
    }
}
