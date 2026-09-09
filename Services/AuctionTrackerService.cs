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
        string FinalBidder)
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
    }

    /// <summary>§236. The two figures under the cards.</summary>
    public sealed record AuctionTotals(long TotalMade, int Conducted);

    /// <summary>
    /// §236. The Auction Tracker's store: which Trade Zone listings this
    /// player is running, what the last read of each said, and what the
    /// finished ones made.
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

        /// <summary>How many can be tracked at once. Each one is a request
        /// against someone else's forum every couple of minutes, so this is a
        /// courtesy limit as much as a screen-space one.</summary>
        public const int MaxActive = 12;

        private static readonly JsonSerializerOptions Json =
            new(JsonSerializerDefaults.Web) { WriteIndented = true };

        private static readonly object Gate = new();

        // ------------------------------------------------------------ reads

        /// <summary>Everything, newest first: the running auctions and the
        /// finished ones together. The window splits them.</summary>
        public static IReadOnlyList<TrackedAuction> Load()
        {
            lock (Gate)
                return ReadFile();
        }

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
                if (!auction.IsEnded)
                    continue;

                conducted++;
                made += auction.FinalPrice;
            }

            return new AuctionTotals(made, conducted);
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

                    if (Same(existing, auction.ListingId))
                        return false;

                    active++;
                }

                if (active >= MaxActive)
                    throw new InvalidOperationException($"Only {MaxActive} auctions can be tracked at once.");

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
        public static void End(string listingId)
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
                        EndedUtc = DateTime.UtcNow,
                        FinalPrice = all[i].CurrentBid,
                        FinalBidder = all[i].CurrentBidder,
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
                File.WriteAllText(path, JsonSerializer.Serialize(new StoredFile { Auctions = ReadFile() }, Json));
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

                return file?.Auctions is null ? new List<TrackedAuction>() : new List<TrackedAuction>(file.Auctions);
            }
            catch (Exception ex)
            {
                // A store that cannot be read must not stop the window opening.
                // The next save replaces the unreadable file.
                Log.Warning(ex, "Auction Tracker: the local store could not be read - starting empty.");
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

                DurableFile.WriteAllText(StorePath, JsonSerializer.Serialize(new StoredFile { Auctions = kept }, Json));
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

        private sealed class StoredFile
        {
            public List<TrackedAuction>? Auctions { get; set; }
        }
    }
}
