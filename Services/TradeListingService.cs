using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>§236. One Trade Zone listing as its page describes it. Every
    /// money field is whole pokedollars; the forum prints them grouped with
    /// dots ("1.100.000") and that grouping is stripped on the way in.</summary>
    public sealed record TradeListing(
        string ListingId,
        string Url,
        string Title,
        string Seller,
        string Server,
        bool IsAuction,
        bool IsActive,
        long StartPrice,
        long MinimumBid,
        long InstantPrice,
        long CurrentBid,
        string CurrentBidder,
        long LastBid,
        string LastBidder,
        DateTime? EndsUtc,
        int BidCount,

        /// <summary>§269. The listing's own ENDED badge. Read by content like
        /// every other badge, not inferred from "not Active": an instant-price
        /// listing carries neither badge, so absence of Active is not the same
        /// fact.</summary>
        bool IsEnded,

        /// <summary>§269. From the notice an ended auction prints - "Auction
        /// Ended: X won this auction with N Pokedollars on DATE UTC." Empty,
        /// zero and null when the auction ended with nobody bidding, or when
        /// the notice did not parse: the badge is the fact that it ENDED, and
        /// these are only the detail.</summary>
        string WinningBidder,
        long WinningBid,
        DateTime? EndedUtc);

    /// <summary>
    /// §236. Reads one PRO Trade Zone listing.
    ///
    /// WHY THIS IS POSSIBLE AT ALL. Auctions used to be ordinary forum topics
    /// where a bid was whatever the bidder typed ("c.o 800k", "1.1m", "1.2M"),
    /// and reading those reliably would have been guesswork. The Trade Zone is
    /// a structured module instead: the seller, the current offer, the highest
    /// bidder, the start price, the minimum bid, the instant price and a full
    /// bid history with names, amounts and timestamps are all their own
    /// elements with their own class names.
    ///
    /// That bid history is why the card can show a LAST bidder as well as a
    /// current one without the tracker having to remember its own previous
    /// read. The page says who was outbid, so the card is right even if the
    /// tracker was closed while three bids came in.
    ///
    /// COURTESY AND SCOPE. It refuses any host but the PRO forum, sends a
    /// User-Agent that says what it is, and fetches exactly one page per call.
    /// It reads; it never posts, bids or ends anything - all of that needs a
    /// logged-in session, and an app acting on someone's account would be a
    /// very different thing from one reading a public page. The forum's
    /// robots.txt allows everything and sets no crawl delay; the caller polls
    /// every two minutes anyway, and only while its window is open.
    ///
    /// TWO THINGS THE MARKUP WILL CATCH YOU WITH, both found the hard way
    /// against saved copies of real listings:
    ///
    /// The page carries an inline stylesheet BELOW the content that defines
    /// every proAuction class it uses. Searching the whole document for a
    /// class name therefore matches its own CSS rule - an instant-price
    /// listing with no bids at all appeared to have five bid rows that way.
    /// Everything here is read from the content region above that stylesheet.
    ///
    /// And a value element cannot be found by scanning a fixed number of
    /// characters ahead. The instant-price block carries one button on an
    /// auction and two on an instant-price listing, and a read capped at a few
    /// hundred characters silently reported no price for the longer one.
    /// Containers are closed by counting nesting instead - the same method,
    /// and the same reason, as ForumGuideScraperService's post bodies.
    /// </summary>
    public static class TradeListingService
    {
        /// <summary>The only host this fetches from.</summary>
        public const string AllowedHost = "pokemonrevolution.net";

        /// <summary>A listing address, as pasted. The id is the number in it.</summary>
        private static readonly Regex ListingUrlRegex = new(
            @"pokemonrevolution\.net/forum/trade/(\d{1,9})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BareIdRegex = new(@"^\s*(\d{1,9})\s*$", RegexOptions.Compiled);

        /// <summary>Where the content ends and the inline stylesheet that
        /// names every one of these classes begins.</summary>
        private const string StyleMarker = ".proAuctionHeader{";

        private static readonly Regex DivRegex = new(@"<(/?)div\b[^>]*>", RegexOptions.Compiled);
        private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex TitleRegex = new(@"<h1[^>]*>([\s\S]{0,400}?)</h1>", RegexOptions.Compiled);
        private static readonly Regex BadgeRegex = new(
            @"class=""ipsBadge[^""]*""[^>]*>(?:<[^>]*>)*([^<]*)<", RegexOptions.Compiled);
        private static readonly Regex SellerRegex = new(
            @"title=""Seller""[\s\S]{0,500}?class=""[^""]*proAuctionUsername[^""]*""[^>]*>([^<]*)</a>",
            RegexOptions.Compiled);
        private static readonly Regex CountdownRegex = new(
            @"data-proauction-countdown=""(\d+)""", RegexOptions.Compiled);
        private static readonly Regex PanelLabelRegex = new(
            @"proAuctionPanel__label"">([^<]*)</div>", RegexOptions.Compiled);
        private static readonly Regex PanelValueRegex = new(
            @"<div class=""proAuctionPanel__value[^""]*"">", RegexOptions.Compiled);
        private static readonly Regex HistoryOpenRegex = new(
            @"<div class=""proAuctionHistory"">", RegexOptions.Compiled);
        private static readonly Regex HistoryRowRegex = new(
            @"<div class=""proAuctionHistory__row"">([\s\S]*?)proAuctionHistory__tools", RegexOptions.Compiled);
        private static readonly Regex HistoryNameRegex = new(
            @"class=""[^""]*proAuctionUsername[^""]*""[^>]*>([^<]*)</a>", RegexOptions.Compiled);
        private static readonly Regex HistoryAmountRegex = new(
            @"proAuctionHistory__amount"">([^<]*)<", RegexOptions.Compiled);
        private static readonly Regex SpanRegex = new(@"<span>([^<]*)</span>", RegexOptions.Compiled);

        /// <summary>§269. The notice box an ended auction carries:
        /// "Auction Ended: Lykabaws won this auction with 325.000 Pokedollars
        /// on 2026-09-09 12:20 PM UTC." The winner is a profile link, so tags
        /// are allowed either side of the name rather than assumed away, and
        /// the amount keeps its dot grouping for Money to strip.</summary>
        private static readonly Regex AuctionEndedRegex = new(
            @"Auction Ended:\s*(?:<[^>]*>\s*)*([^<]+?)\s*(?:<[^>]*>\s*)*won this auction with\s*([0-9.,]+)\s*Pokedollars\s*on\s*(\d{4}-\d{2}-\d{2}[^<.]*?)\s*\.",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>§269. The shapes the ended notice prints its moment in.
        /// Parsed as UTC because the notice says UTC; a date that will not
        /// parse leaves the time null rather than guessing at a local
        /// one.</summary>
        private static readonly string[] EndedStampFormats =
        {
            "yyyy-MM-dd hh:mm tt UTC",
            "yyyy-MM-dd HH:mm UTC",
            "yyyy-MM-dd hh:mm:ss tt UTC",
            "yyyy-MM-dd HH:mm:ss UTC",
        };
        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

        /// <summary>The badges that name a server. Cross-Server is a real one,
        /// not a placeholder - a listing can be open to both.</summary>
        private static readonly string[] ServerBadges = { "Silver", "Gold", "Cross-Server" };

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

            // Says what it is and where to complain, the same courtesy the
            // guide scraper extends to the same forum.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"ProTracker/{AppVersion.Current} (PRO Tracker and Database; auction tracker)");

            return client;
        }

        /// <summary>
        /// Reads a listing address, however the player pasted it - the full
        /// address, one with a title slug or query on the end, or just the
        /// number. Returns the id and a canonical address to fetch, so the
        /// tracker never stores whatever tracking parameters came along.
        /// </summary>
        public static bool TryReadListingId(string? text, out string listingId, out string url)
        {
            listingId = string.Empty;
            url = string.Empty;

            string input = (text ?? string.Empty).Trim();

            if (input.Length == 0)
                return false;

            Match match = ListingUrlRegex.Match(input);

            if (!match.Success)
            {
                Match bare = BareIdRegex.Match(input);

                if (!bare.Success)
                    return false;

                match = bare;
            }

            listingId = match.Groups[1].Value.TrimStart('0');

            if (listingId.Length == 0)
                return false;

            url = $"https://{AllowedHost}/forum/trade/{listingId}/";
            return true;
        }

        /// <summary>
        /// Fetches and reads one listing. Returns null for every failure - an
        /// address that is not a listing, a host that is not the forum, a
        /// request that did not answer, a page whose markup this no longer
        /// recognises - because they all mean the same thing to the caller:
        /// nothing new to show, keep what was last read.
        /// </summary>
        public static async Task<TradeListing?> ReadAsync(string url, CancellationToken cancellationToken = default)
        {
            if (!TryReadListingId(url, out string listingId, out string canonical))
            {
                Log.Warning("Auction Tracker: {Url} is not a Trade Zone listing address.", url);
                return null;
            }

            try
            {
                using HttpResponseMessage response = await Http.GetAsync(canonical, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning(
                        "Auction Tracker: listing {ListingId} answered HTTP {Status}.",
                        listingId, (int)response.StatusCode);
                    return null;
                }

                string html = await response.Content.ReadAsStringAsync(cancellationToken);

                return Parse(html, listingId, canonical);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warning("Auction Tracker: listing {ListingId} did not answer in time.", listingId);
                return null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Auction Tracker: listing {ListingId} could not be read.", listingId);
                return null;
            }
        }

        /// <summary>The parse, separated from the fetch so it can be replayed
        /// against saved copies of real listings.</summary>
        internal static TradeListing? Parse(string html, string listingId, string url)
        {
            string content = ContentRegion(html);

            Match title = TitleRegex.Match(content);

            if (!title.Success)
            {
                Log.Warning(
                    "Auction Tracker: listing {ListingId} has no title where one was expected - the Trade Zone's markup may have changed.",
                    listingId);
                return null;
            }

            var badges = new List<string>();

            foreach (Match badge in BadgeRegex.Matches(content))
                badges.Add(badge.Groups[1].Value.Trim());

            // Read by CONTENT, not by position: an instant-price listing
            // repeats its type where an auction shows "Active", so counting
            // badges off would put the server in the wrong field.
            string server = string.Empty;

            foreach (string candidate in ServerBadges)
            {
                if (badges.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    server = candidate;
                    break;
                }
            }

            Dictionary<string, string> panel = ReadPanel(content);
            (string currentBidder, long currentBid, string lastBidder, long lastBid, int bidCount) = ReadHistory(content);

            Match seller = SellerRegex.Match(content);
            Match countdown = CountdownRegex.Match(content);

            DateTime? endsUtc = null;

            if (countdown.Success
                && long.TryParse(countdown.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long unix)
                && unix > 0)
            {
                endsUtc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            }

            // §269: who won, for how much and when - from the notice, not
            // from the last snapshot. An auction that ended between two polls
            // is recorded at the page's own figures rather than at whatever
            // the tracker last happened to see.
            string winningBidder = string.Empty;
            long winningBid = 0;
            DateTime? endedUtc = null;

            Match ended = AuctionEndedRegex.Match(content);

            if (ended.Success)
            {
                winningBidder = Text(ended.Groups[1].Value);
                winningBid = Money(ended.Groups[2].Value);

                string stamp = WhitespaceRegex.Replace(ended.Groups[3].Value.Trim(), " ");

                if (DateTime.TryParseExact(stamp, EndedStampFormats, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
                {
                    endedUtc = parsed;
                }
                else
                {
                    Log.Debug(
                        "Auction Tracker: listing {ListingId} has ended but its timestamp did not parse - {Stamp}",
                        listingId, stamp);
                }
            }

            return new TradeListing(
                listingId,
                url,
                Text(title.Groups[1].Value),
                seller.Success ? seller.Groups[1].Value.Trim() : string.Empty,
                server,
                badges.Contains("Auction", StringComparer.OrdinalIgnoreCase),
                badges.Contains("Active", StringComparer.OrdinalIgnoreCase),
                Money(panel, "Start price"),
                Money(panel, "Minimum bid"),
                Money(panel, "Instant price"),
                // The panel's own figure is preferred and the history's is the
                // fallback: an instant-price listing has no "Current offer"
                // panel at all, and an auction whose panel this failed to read
                // still has its top bid in the history.
                Money(panel, "Current offer") is var offer && offer > 0 ? offer : currentBid,
                currentBidder,
                lastBid,
                lastBidder,
                endsUtc,
                bidCount,
                badges.Contains("Ended", StringComparer.OrdinalIgnoreCase),
                winningBidder,
                winningBid,
                endedUtc);
        }

        /// <summary>Everything above the inline stylesheet - see the class
        /// remark on why searching the whole document does not work.</summary>
        internal static string ContentRegion(string html)
        {
            int marker = html.IndexOf(StyleMarker, StringComparison.Ordinal);

            return marker > 0 ? html[..marker] : html;
        }

        private static Dictionary<string, string> ReadPanel(string content)
        {
            var panel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int pos = 0;

            while (true)
            {
                Match label = PanelLabelRegex.Match(content, pos);

                if (!label.Success)
                    break;

                pos = label.Index + label.Length;

                string? value = Inner(content, PanelValueRegex, pos);

                if (value is null)
                    continue;

                Match span = SpanRegex.Match(value);

                panel[label.Groups[1].Value.Trim()] = span.Success
                    ? span.Groups[1].Value.Trim()
                    : Text(value);
            }

            return panel;
        }

        /// <summary>
        /// The two most recent bids. The history is newest first, so the top
        /// row is the current bid and the one under it is the bid it beat -
        /// which is where the card's "Last Bidder" and "Last Bid" come from.
        /// </summary>
        private static (string CurrentBidder, long CurrentBid, string LastBidder, long LastBid, int Count)
            ReadHistory(string content)
        {
            string? block = Inner(content, HistoryOpenRegex, 0);

            if (block is null)
                return (string.Empty, 0, string.Empty, 0, 0);

            var names = new List<string>();
            var amounts = new List<long>();

            foreach (Match row in HistoryRowRegex.Matches(block))
            {
                Match name = HistoryNameRegex.Match(row.Groups[1].Value);
                Match amount = HistoryAmountRegex.Match(row.Groups[1].Value);

                names.Add(name.Success ? name.Groups[1].Value.Trim() : string.Empty);
                amounts.Add(amount.Success ? Money(amount.Groups[1].Value) : 0);
            }

            return (
                names.Count > 0 ? names[0] : string.Empty,
                amounts.Count > 0 ? amounts[0] : 0,
                names.Count > 1 ? names[1] : string.Empty,
                amounts.Count > 1 ? amounts[1] : 0,
                names.Count);
        }

        /// <summary>
        /// The inner HTML of the first element matching
        /// <paramref name="opening"/> at or after <paramref name="from"/>,
        /// closed by COUNTING div nesting rather than by taking the next
        /// closing tag. See the class remark: a fixed look-ahead reads an
        /// instant-price block correctly on an auction and truncates it on an
        /// instant-price listing, which carries an extra button.
        /// </summary>
        internal static string? Inner(string html, Regex opening, int from)
        {
            Match start = opening.Match(html, from);

            if (!start.Success)
                return null;

            int contentStart = start.Index + start.Length;
            int depth = 1;
            int pos = contentStart;

            while (true)
            {
                Match tag = DivRegex.Match(html, pos);

                if (!tag.Success)
                    return null;

                depth += tag.Groups[1].Value.Length == 0 ? 1 : -1;
                pos = tag.Index + tag.Length;

                if (depth == 0)
                    return html[contentStart..tag.Index];
            }
        }

        private static long Money(Dictionary<string, string> panel, string label) =>
            panel.TryGetValue(label, out string? value) ? Money(value) : 0;

        /// <summary>
        /// Pokedollars as the forum prints them. Every separator is stripped
        /// rather than parsed: the grouping is dots ("1.100.000"), so treating
        /// a dot as a decimal point would read a million as one.
        /// </summary>
        internal static long Money(string? text)
        {
            long value = 0;

            foreach (char c in text ?? string.Empty)
            {
                if (!char.IsDigit(c))
                    continue;

                // A figure long enough to overflow is not a price, it is a
                // parse that has gone wrong - refuse it rather than wrap.
                if (value > (long.MaxValue - 9) / 10)
                    return 0;

                value = (value * 10) + (c - '0');
            }

            return value;
        }

        private static string Text(string html) =>
            WhitespaceRegex.Replace(TagRegex.Replace(html, " "), " ").Trim();
    }
}
