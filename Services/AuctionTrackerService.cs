using System.Globalization;
using System.Text.Json;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>§237. One auction this player is running, as the tracker holds
    /// it: the listing it points at, the last thing the Trade Zone said about
    /// it, and - once it is over - what it made.
    ///
    /// The forum's own listing id is the key. §236 kept a separate local id
    /// because a card could be composed before the auction was posted; a card
    /// is now made FROM a listing and cannot exist without one, so the second
    /// id was a key with nothing left to distinguish.
    ///
    /// The bid fields are a SNAPSHOT of the last successful read, not a live
    /// value. CheckedUtc says when that read happened, and the window shows it,
    /// because a figure with no time against it is indistinguishable from a
    /// figure that stopped updating an hour ago.</summary>
    /// <summary>
    /// §310. Which side of an auction this card is. It is a role rather than
    /// two stores because everything else about the two is identical - the
    /// same listing, the same read, the same two-minute poll, the same
    /// history and the same export - and the only things that differ are
    /// which tab draws it and which four figures it counts toward.
    ///
    /// Selling is ZERO on purpose. Every row already in somebody's
    /// auction-tracker.json was written before this existed, has no Role
    /// property at all, and deserializes to the default - which is the right
    /// answer for every one of them, because until now the window could only
    /// track an auction you were running.
    /// </summary>
    public enum AuctionRole
    {
        Selling = 0,
        Bidding = 1,
    }

    public sealed record TrackedAuction(
        string ListingId,
        string Url,
        string Title,
        string Server,
        string CurrentBidder,
        long CurrentBid,
        string LastBidder,
        long LastBid,
        DateTime AddedUtc,
        DateTime? EndsOnForumUtc,
        DateTime? CheckedUtc,
        DateTime? EndedUtc,
        long FinalPrice,
        string FinalBidder,

        /// <summary>§310. Selling by default, which is what every row written
        /// before §310 is. Last, and with a default, so that every place
        /// that builds one of these keeps compiling.</summary>
        AuctionRole Role = AuctionRole.Selling)
    {
        /// <summary>Ended auctions are history; the rest are cards.</summary>
        public bool IsEnded => EndedUtc is not null;

        /// <summary>When the Trade Zone's own countdown runs out. Read from
        /// the listing rather than worked out from a duration - the page
        /// carries it as a Unix timestamp, which is exact. Null until the card
        /// has been linked and read once.</summary>
        public bool EndedOnForum => EndsOnForumUtc is not null && EndsOnForumUtc <= DateTime.UtcNow;

        /// <summary>An auction that ended with nobody bidding made nothing.
        /// It still counts as conducted - it was run - which is why the two
        /// summary figures are counted separately rather than one being
        /// derived from the other.</summary>
        public bool Sold => IsEnded && FinalPrice > 0;

        /// <summary>§310. An auction this player was bidding on rather than
        /// running.</summary>
        public bool IsBid => Role == AuctionRole.Bidding;
    }

    /// <summary>§236. The two figures under the cards.</summary>
    public sealed record AuctionTotals(long TotalMade, int Conducted);

    /// <summary>
    /// §310. The three under the Auction Bids tab.
    ///
    /// Won and Lost are counted rather than one being derived from the other,
    /// for §236's reason applied the other way round: a bid auction that
    /// ended while the player had no name set cannot be judged either way,
    /// so the two do not have to add up to the number of finished bids and
    /// must not be made to.
    ///
    /// Spent is what the WON ones cost. Losing an auction costs nothing.
    /// </summary>
    public sealed record BidTotals(long TotalSpent, int Won, int Lost, int Unjudged);

    /// <summary>
    /// §236. The Auction Tracker's store: which Trade Zone listings this
    /// player is running, what the last read of each said, and what the
    /// finished ones made.
    ///
    /// §310 gave every row a ROLE. The same file now holds the auctions this
    /// player is selling and the ones they are bidding on, told apart by one
    /// enum that defaults to Selling - so a store written before §310 reads
    /// back exactly as it did, with every row on the side of the window it
    /// has always been on. It also holds the forum name this player bids
    /// under, which is the only thing that can tell a won auction from a lost
    /// one.
    ///
    /// LOCAL ONLY, for the same reason §233's World Quest progress is. What
    /// someone sells and what they were paid for it is theirs; the events
    /// server has no business holding it, and the observer is forbidden from
    /// keeping hunting or personal history in any case. Export and Import
    /// exist so the player can move it between their own machines themselves,
    /// which is a different thing from the tracker doing it for them.
    ///
    /// Nothing here fetches anything. TradeListingService reads the forum;
    /// this only remembers what it found.
    /// </summary>
    public static class AuctionTrackerService
    {
        /// <summary>Beside the World Quest progress and the install token, in
        /// the tracker's own data folder.</summary>
        private static string StorePath =>
            Path.Combine(EventsSyncService.LocalDataFolder, "auction-tracker.json");

        /// <summary>How many finished auctions are kept. History is the point
        /// of Total Made, so this is generous - a few hundred rows of six
        /// short fields is a file measured in tens of kilobytes.</summary>
        public const int KeepEnded = 500;

        /// <summary>How many can be tracked at once PER ROLE. Each one is a
        /// request against someone else's forum every couple of minutes, so
        /// this is a courtesy limit as much as a screen-space one.
        ///
        /// §310 made it per role rather than shared, which doubles the worst
        /// case from twelve requests every two minutes to twenty-four. That is
        /// a deliberate choice and the class remark above says so: a shared
        /// twelve would have meant a player running a full dozen auctions
        /// could not watch a single bid, which is the same as not having the
        /// tab.</summary>
        public const int MaxActive = 12;

        private static readonly JsonSerializerOptions Json =
            new(JsonSerializerDefaults.Web) { WriteIndented = true };

        private static readonly object Gate = new();

        /// <summary>§310. The forum name this player bids under, kept beside
        /// the auctions because it is auction data and because it should
        /// travel with an export.
        ///
        /// It is what turns "Wegan is the top bidder" into "you have been
        /// outbid", and it is the only thing that can tell a won auction from
        /// a lost one. Without it the Bids tab still works - the cards read
        /// exactly as the Auctions tab's do - and the totals honestly say
        /// they cannot judge rather than guessing.
        /// </summary>
        private static string biddingAs = string.Empty;

        private static bool loadedName;

        // ------------------------------------------------------------ reads

        /// <summary>§310. The name this player bids under, or empty.</summary>
        public static string BiddingAs
        {
            get
            {
                lock (Gate)
                {
                    EnsureNameLoaded();
                    return biddingAs;
                }
            }
        }

        /// <summary>§310. Sets it and rewrites the store. Trimmed, because a
        /// trailing space typed into a text box must not be the reason a
        /// player's own auctions stop counting as theirs.</summary>
        public static void SetBiddingAs(string? name)
        {
            lock (Gate)
            {
                List<TrackedAuction> all = ReadFile();   // also loads the name
                biddingAs = (name ?? string.Empty).Trim();
                WriteFile(all);
            }
        }

        /// <summary>§310. Whether this finished BID was won by the named
        /// player. Derived rather than stamped on the row when it ended: a
        /// player who fixes a typo in their name gets their history corrected
        /// instead of keeping a verdict that was wrong when it was made.
        /// </summary>
        public static bool WonBy(TrackedAuction auction, string? me) =>
            auction.IsBid && auction.IsEnded &&
            !string.IsNullOrWhiteSpace(me) &&
            string.Equals(auction.FinalBidder?.Trim(), me!.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Everything, newest first: the running auctions and the
        /// finished ones together, both roles. The window splits them.</summary>
        public static IReadOnlyList<TrackedAuction> Load()
        {
            lock (Gate)
                return ReadFile();
        }

        /// <summary>Every running auction, BOTH roles. This is what the poll
        /// reads, and it stays role-blind on purpose: an auction you are
        /// bidding on goes stale exactly as fast as one you are running, and
        /// which tab happens to be showing is not a reason to stop reading the
        /// other one.</summary>
        public static IReadOnlyList<TrackedAuction> Active()
        {
            var active = new List<TrackedAuction>();

            foreach (TrackedAuction auction in Load())
            {
                if (!auction.IsEnded)
                    active.Add(auction);
            }

            return active;
        }

        /// <summary>§310. The running ones on one side of the window.</summary>
        public static IReadOnlyList<TrackedAuction> Active(AuctionRole role)
        {
            var active = new List<TrackedAuction>();

            foreach (TrackedAuction auction in Load())
            {
                if (!auction.IsEnded && auction.Role == role)
                    active.Add(auction);
            }

            return active;
        }

        public static IReadOnlyList<TrackedAuction> History()
        {
            var ended = new List<TrackedAuction>();

            foreach (TrackedAuction auction in Load())
            {
                if (auction.IsEnded)
                    ended.Add(auction);
            }

            return ended;
        }

        /// <summary>§310. The finished ones on one side of the window.</summary>
        public static IReadOnlyList<TrackedAuction> History(AuctionRole role)
        {
            var ended = new List<TrackedAuction>();

            foreach (TrackedAuction auction in Load())
            {
                if (auction.IsEnded && auction.Role == role)
                    ended.Add(auction);
            }

            return ended;
        }

        /// <summary>Total Made is what the SOLD ones fetched; Auctions
        /// Conducted counts every finished one, sold or not. Two counts rather
        /// than one derived from the other, because an auction that ended with
        /// no bids was still an auction that was run.</summary>
        public static AuctionTotals Totals()
        {
            long made = 0;
            int conducted = 0;

            foreach (TrackedAuction auction in Load())
            {
                // §310: bids are not auctions this player conducted, and what
                // somebody else's auction fetched is not money this player
                // made. Before §310 there was nothing in the store but their
                // own auctions, so there was nothing to exclude.
                if (!auction.IsEnded || auction.IsBid)
                    continue;

                conducted++;
                made += auction.FinalPrice;
            }

            return new AuctionTotals(made, conducted);
        }

        /// <summary>
        /// §310. Total Spent is what the WON bids cost - losing an auction
        /// costs nothing. Won and Lost are counted separately and neither is
        /// derived from the other, because a finished bid can be NEITHER: one
        /// that ended while no name was set cannot be judged, and guessing
        /// which way it went would be inventing a number.
        /// </summary>
        public static BidTotals Bids()
        {
            string me = BiddingAs;

            long spent = 0;
            int won = 0;
            int lost = 0;
            int unjudged = 0;

            foreach (TrackedAuction auction in Load())
            {
                if (!auction.IsEnded || !auction.IsBid)
                    continue;

                if (string.IsNullOrWhiteSpace(me))
                {
                    unjudged++;
                    continue;
                }

                if (WonBy(auction, me))
                {
                    won++;
                    spent += auction.FinalPrice;
                }
                else
                {
                    lost++;
                }
            }

            return new BidTotals(spent, won, lost, unjudged);
        }

        // ----------------------------------------------------------- writes

        /// <summary>
        /// Starts tracking a listing that has already been read. Returns false
        /// when that listing is already on a running card - tracking one
        /// auction twice would poll the forum twice for one answer and count
        /// it twice in the totals.
        /// </summary>
        public static bool Add(TrackedAuction auction)
        {
            if (string.IsNullOrWhiteSpace(auction.ListingId))
                throw new ArgumentException("A listing id is required.", nameof(auction));

            lock (Gate)
            {
                List<TrackedAuction> all = ReadFile();

                int active = 0;

                foreach (TrackedAuction existing in all)
                {
                    if (existing.IsEnded)
                        continue;

                    // Still keyed by listing id across BOTH roles: one listing
                    // is one thread on the forum, and tracking it twice would
                    // poll it twice for one answer however the two cards were
                    // labelled.
                    if (Same(existing, auction.ListingId))
                        return false;

                    // §310: the cap is per role, so a full dozen auctions does
                    // not stop a player watching a bid.
                    if (existing.Role == auction.Role)
                        active++;
                }

                if (active >= MaxActive)
                {
                    throw new InvalidOperationException(auction.IsBid
                        ? $"Only {MaxActive} auction bids can be watched at once."
                        : $"Only {MaxActive} auctions can be tracked at once.");
                }

                all.Insert(0, auction);
                WriteFile(all);
                return true;
            }
        }

        /// <summary>Replaces the snapshot after a read. Leaves everything the
        /// forum does not own - when it was added, whether it has been ended
        /// here - exactly as it was.</summary>
        public static void UpdateSnapshot(
            string listingId,
            string title,
            string currentBidder,
            long currentBid,
            string lastBidder,
            long lastBid,
            string server,
            DateTime? endsOnForumUtc)
        {
            lock (Gate)
            {
                List<TrackedAuction> all = ReadFile();

                for (int i = 0; i < all.Count; i++)
                {
                    if (!Same(all[i], listingId) || all[i].IsEnded)
                        continue;

                    all[i] = all[i] with
                    {
                        Title = string.IsNullOrWhiteSpace(title) ? all[i].Title : title,
                        CurrentBidder = currentBidder,
                        CurrentBid = currentBid,
                        LastBidder = lastBidder,
                        LastBid = lastBid,
                        Server = string.IsNullOrWhiteSpace(server) ? all[i].Server : server,
                        EndsOnForumUtc = endsOnForumUtc,
                        CheckedUtc = DateTime.UtcNow,
                    };

                    WriteFile(all);
                    return;
                }
            }
        }

        /// <summary>Moves an auction into history at whatever the last read
        /// said it was worth. The tracker cannot end an auction on the forum
        /// and does not pretend to - this records that it is over.</summary>
        /// <summary>
        /// Records an auction as finished.
        ///
        /// §269 gave it three optional arguments so the LISTING can end it
        /// with the figures the forum printed - who won, for how much and
        /// when - instead of the tracker's last snapshot. Called with none of
        /// them, as the End Auction button still does, the behaviour is
        /// exactly §236's: the last read stands, stamped now.
        ///
        /// Each argument falls back on its own, rather than the three being
        /// all-or-nothing: an ended auction with no bidder at all prints no
        /// winner and no amount, and its end time is still worth keeping.
        /// </summary>
        public static void End(
            string listingId,
            long? finalPrice = null,
            string? finalBidder = null,
            DateTime? endedUtc = null)
        {
            lock (Gate)
            {
                List<TrackedAuction> all = ReadFile();

                for (int i = 0; i < all.Count; i++)
                {
                    if (!Same(all[i], listingId) || all[i].IsEnded)
                        continue;

                    all[i] = all[i] with
                    {
                        EndedUtc = endedUtc ?? DateTime.UtcNow,
                        FinalPrice = finalPrice ?? all[i].CurrentBid,
                        FinalBidder = string.IsNullOrWhiteSpace(finalBidder) ? all[i].CurrentBidder : finalBidder,
                    };

                    WriteFile(all);
                    return;
                }
            }
        }

        /// <summary>Removes it outright, history included. Delete is not End:
        /// a deleted auction never happened as far as the totals are
        /// concerned, which is what makes it the right button for one linked
        /// by mistake.</summary>
        public static void Delete(string listingId)
        {
            lock (Gate)
            {
                List<TrackedAuction> all = ReadFile();

                if (all.RemoveAll(auction => Same(auction, listingId)) > 0)
                    WriteFile(all);
            }
        }

        // ------------------------------------------------- export and import

        /// <summary>Writes the whole store to a file the player chose. Not
        /// durable-written: DurableFile is for the small files this app
        /// rewrites on a timer, not for somewhere the user picked.</summary>
        public static void Export(string path)
        {
            lock (Gate)
            {
                List<TrackedAuction> all = ReadFile();   // also loads the name

                File.WriteAllText(path, JsonSerializer.Serialize(
                    new StoredFile { Auctions = all, BiddingAs = biddingAs }, Json));
            }
        }

        /// <summary>Merges a previously exported file in, keyed by listing id -
        /// so importing the same file twice adds nothing the second time, and
        /// importing one machine's file into another's does not wipe what is
        /// already there. Returns how many rows were new.</summary>
        public static int Import(string path)
        {
            lock (Gate)
            {
                StoredFile? incoming = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(path), Json);

                if (incoming?.Auctions is null || incoming.Auctions.Count == 0)
                    return 0;

                List<TrackedAuction> all = ReadFile();
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (TrackedAuction existing in all)
                    known.Add(existing.ListingId);

                int added = 0;

                foreach (TrackedAuction row in incoming.Auctions)
                {
                    if (string.IsNullOrWhiteSpace(row.ListingId) || !known.Add(row.ListingId))
                        continue;

                    all.Add(row);
                    added++;
                }

                // §310: an export carries the name, and a machine that has
                // not been told one yet takes it. A machine that HAS keeps its
                // own - importing a file must not rename the player.
                if (string.IsNullOrWhiteSpace(biddingAs) && !string.IsNullOrWhiteSpace(incoming.BiddingAs))
                {
                    biddingAs = incoming.BiddingAs!.Trim();
                    WriteFile(all);
                    return added;
                }

                if (added > 0)
                    WriteFile(all);

                return added;
            }
        }

        // -------------------------------------------------------------- file

        private static bool Same(TrackedAuction auction, string listingId) =>
            string.Equals(auction.ListingId, listingId, StringComparison.OrdinalIgnoreCase);

        private static List<TrackedAuction> ReadFile()
        {
            try
            {
                if (!File.Exists(StorePath))
                    return new List<TrackedAuction>();

                StoredFile? file = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(StorePath), Json);

                // §310: the name rides in the same file, so reading the rows
                // is also what loads it. Anything written before §310 has no
                // such property and leaves it empty, which is correct - a
                // player who has never opened the Bids tab has never said who
                // they are.
                biddingAs = (file?.BiddingAs ?? string.Empty).Trim();
                loadedName = true;

                return file?.Auctions is null ? new List<TrackedAuction>() : new List<TrackedAuction>(file.Auctions);
            }
            catch (Exception ex)
            {
                // A store that cannot be read must not stop the window opening.
                // The next save replaces the unreadable file.
                Log.Warning(ex, "Auction Tracker: the local store could not be read - starting empty.");
                loadedName = true;
                return new List<TrackedAuction>();
            }
        }

        private static void WriteFile(List<TrackedAuction> all)
        {
            try
            {
                // Newest first, and the finished ones trimmed from the bottom.
                // Running auctions are never trimmed - MaxActive already caps
                // them, and dropping one the player is still selling would be
                // the tracker losing their auction for them.
                all.Sort(static (a, b) => (b.EndedUtc ?? b.AddedUtc).CompareTo(a.EndedUtc ?? a.AddedUtc));

                var kept = new List<TrackedAuction>();
                int ended = 0;

                foreach (TrackedAuction auction in all)
                {
                    if (auction.IsEnded && ++ended > KeepEnded)
                        continue;

                    kept.Add(auction);
                }

                DurableFile.WriteAllText(StorePath, JsonSerializer.Serialize(
                    new StoredFile { Auctions = kept, BiddingAs = biddingAs }, Json));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Auction Tracker: the local store could not be saved.");
            }
        }

        /// <summary>Pokedollars as the forum prints them - "1.100.000" - which
        /// is also how the game shows them, so it is what a player will
        /// recognise.</summary>
        public static string Money(long amount) =>
            amount.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', '.');

        /// <summary>§310. Loads the name without caring about the rows, for
        /// the getter. ReadFile is what actually parses it.</summary>
        private static void EnsureNameLoaded()
        {
            if (!loadedName)
                ReadFile();
        }

        private sealed class StoredFile
        {
            public List<TrackedAuction>? Auctions { get; set; }

            /// <summary>§310. Absent in every file written before §310, which
            /// leaves it null and the name empty.</summary>
            public string? BiddingAs { get; set; }
        }
    }
}
