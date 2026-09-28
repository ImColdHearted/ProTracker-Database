using System;
using System.Globalization;
using System.Text.RegularExpressions;

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

        // ============================================================
        // §343. Discord writes some things as markup, not as text
        // ============================================================
        //
        // PRO posted "Ribbit. <t:1789740000:F>". Discord turns that into a
        // date in the READER's own timezone - the reporter saw "Friday,
        // September 18, 2026 7:00" because he is on US Pacific, and someone
        // in Berlin saw 16:00 on the same line. That is the entire point of
        // the syntax: one post, one instant, every reader's clock.
        //
        // The relay hands the text over exactly as Discord stored it, which
        // §232 was right to do - the raw post is the thing worth keeping.
        // What was missing is that nothing ever turned it back into words,
        // so the window showed the markup.
        //
        // Rendering happens HERE rather than on the Worker, and that is not
        // an implementation detail. The Worker has no idea what timezone the
        // reader is in; the client is the only place that does.

        // Discord's own range: <t:seconds> with an optional one-letter style.
        static readonly Regex TimestampMarkup =
            new(@"<t:(-?\d{1,19})(?::([tTdDfFR]))?>", RegexOptions.Compiled);

        // <:name:id> and the animated <a:name:id>. There is no picture to
        // show here, so the name is the best that can be done with it.
        static readonly Regex EmojiMarkup =
            new(@"<a?:([A-Za-z0-9_]{2,32}):\d{1,20}>", RegexOptions.Compiled);

        // DateTimeOffset.FromUnixTimeSeconds throws outside these, and a
        // malformed post must not take the whole announcements list down.
        const long EarliestSecond = -62135596800L;
        const long LatestSecond = 253402300799L;

        /// <summary>
        /// §343. Discord markup turned into something readable, with every
        /// instant in the reader's own local time.
        ///
        /// Anything it cannot make sense of is LEFT EXACTLY AS IT WAS. A
        /// timestamp out of range, a number too long to be one, a style
        /// letter Discord has not invented yet: the original text survives,
        /// because a post that reads oddly is better than a post with a hole
        /// in it.
        /// </summary>
        public static string RenderDiscordMarkup(string? body)
        {
            string text = body ?? string.Empty;

            // The common case - no markup at all - costs one scan and no
            // allocation. Every construct handled here starts with '<'.
            if (text.Length == 0 || !text.Contains('<'))
                return text;

            text = TimestampMarkup.Replace(text, RenderTimestamp);
            text = EmojiMarkup.Replace(text, match => ":" + match.Groups[1].Value + ":");

            return text;
        }

        static string RenderTimestamp(Match match)
        {
            if (!long.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long seconds))
            {
                return match.Value;
            }

            if (seconds < EarliestSecond || seconds > LatestSecond)
                return match.Value;

            DateTimeOffset moment = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
            DateTime local = moment.DateTime;
            CultureInfo culture = CultureInfo.CurrentCulture;

            // The date shapes match PublishedDisplay below, so two lines in
            // the same row do not disagree about how a date looks.
            return match.Groups[2].Success
                ? match.Groups[2].Value[0] switch
                {
                    't' => local.ToString("HH:mm", culture),
                    'T' => local.ToString("HH:mm:ss", culture),
                    'd' => local.ToString("d", culture),
                    'D' => local.ToString("MMMM d, yyyy", culture),
                    'F' => local.ToString("dddd, MMMM d, yyyy HH:mm", culture),
                    'R' => Relative(moment),
                    _ => local.ToString("MMMM d, yyyy HH:mm", culture)
                }
                : local.ToString("MMMM d, yyyy HH:mm", culture);
        }

        /// <summary>§343. Discord's R style: "in 2 days", "3 hours ago".
        /// Computed when the row is read rather than when it arrives, so it
        /// is still true an hour later.</summary>
        static string Relative(DateTimeOffset moment)
        {
            TimeSpan difference = moment - DateTimeOffset.Now;
            bool ahead = difference > TimeSpan.Zero;
            TimeSpan size = ahead ? difference : difference.Negate();

            if (size.TotalSeconds < 1)
                return "now";

            string amount =
                size.TotalDays >= 365 ? Count((int)(size.TotalDays / 365), "year")
                : size.TotalDays >= 30 ? Count((int)(size.TotalDays / 30), "month")
                : size.TotalDays >= 1 ? Count((int)size.TotalDays, "day")
                : size.TotalHours >= 1 ? Count((int)size.TotalHours, "hour")
                : size.TotalMinutes >= 1 ? Count((int)size.TotalMinutes, "minute")
                : Count((int)size.TotalSeconds, "second");

            return ahead ? "in " + amount : amount + " ago";
        }

        static string Count(int many, string unit) =>
            many == 1 ? "1 " + unit : many + " " + unit + "s";

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

        /// <summary>§343. The post as the reader should see it: Discord's
        /// markup rendered, timestamps in this machine's own timezone. Body
        /// stays exactly as the relay delivered it.</summary>
        public string Display => AnnouncementsService.RenderDiscordMarkup(Body);

        public string Summary => AnnouncementsService.Summarise(Display);

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
