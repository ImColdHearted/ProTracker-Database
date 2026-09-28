using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;
using Serilog;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// §236. One auction card. Updated in place rather than rebuilt, so the list
/// does not jump under the player every two minutes while it refreshes.
/// </summary>
public sealed partial class AuctionCard : ObservableObject
{
    public AuctionCard(TrackedAuction auction)
        : this(auction, AuctionTrackerService.BiddingAs)
    {
    }

    public AuctionCard(TrackedAuction auction, string? me)
    {
        ListingId = auction.ListingId;
        Apply(auction, me);
    }

    /// <summary>The forum's listing id, which is also the store's key.</summary>
    public string ListingId { get; }

    /// <summary>The listing address, for the button that opens it.</summary>
    public string Url { get; private set; } = string.Empty;

    [ObservableProperty] private string title = string.Empty;
    [ObservableProperty] private string currentBidder = "-";
    [ObservableProperty] private string currentBid = "-";
    [ObservableProperty] private string lastBidder = "-";
    [ObservableProperty] private string lastBid = "-";
    [ObservableProperty] private string server = "-";
    [ObservableProperty] private string checkedText = string.Empty;
    [ObservableProperty] private Bitmap? sprite;

    /// <summary>§310. Which side of the auction this card is, so the template
    /// can label its middle button for what it actually does.</summary>
    [ObservableProperty] private bool isBid;

    /// <summary>§310. "You are the top bidder", "Outbid by Wegan", "You won
    /// this one" - or empty on a card that is not a bid, and on a bid the
    /// tracker has not been told a name for. The whole reason to watch an
    /// auction somebody else is running is to find out you have been outbid,
    /// and a current-bidder line does not say that; it makes you read it.
    /// </summary>
    [ObservableProperty] private string standingText = string.Empty;

    /// <summary>§310. True while this player is losing - the template shows
    /// the standing in the alert colour when it is.</summary>
    [ObservableProperty] private bool losing;

    public void Apply(TrackedAuction auction) => Apply(auction, AuctionTrackerService.BiddingAs);

    public void Apply(TrackedAuction auction, string? me)
    {
        Url = auction.Url;
        Title = auction.Title;
        Server = string.IsNullOrWhiteSpace(auction.Server) ? "-" : auction.Server;

        CurrentBidder = Name(auction.CurrentBidder);
        CurrentBid = auction.CurrentBid > 0 ? AuctionTrackerService.Money(auction.CurrentBid) : "-";
        LastBidder = Name(auction.LastBidder);
        LastBid = auction.LastBid > 0 ? AuctionTrackerService.Money(auction.LastBid) : "-";

        // §236. A figure with no time against it cannot be told apart from a
        // figure that stopped updating an hour ago, so the card always says
        // when it last managed to read the listing.
        // §236. The middle state is the one worth having: the tracker cannot
        // close an auction on the forum, so when the forum's own countdown has
        // run out the card says so rather than going quietly stale at a figure
        // that will never change again.
        CheckedText = auction.IsEnded
            ? $"ended {Local(auction.EndedUtc)}"
            : auction.EndedOnForum
                ? "finished on the forum - press End Auction"
                : auction.CheckedUtc is null
                    ? "not read yet"
                    : $"checked {Ago(auction.CheckedUtc.Value)}";

        Sprite = SpriteFor(auction.Title);

        IsBid = auction.IsBid;
        ApplyStanding(auction, me);
    }

    /// <summary>
    /// §310. Where this player stands on a bid, in one line.
    ///
    /// Nothing is said when the tracker has not been told a name, because the
    /// alternative is guessing which of the bidders is the person reading the
    /// window - and a card that guesses wrong about whether you are winning
    /// is worse than a card that does not say.
    /// </summary>
    private void ApplyStanding(TrackedAuction auction, string? me)
    {
        if (!auction.IsBid || string.IsNullOrWhiteSpace(me))
        {
            StandingText = string.Empty;
            Losing = false;
            return;
        }

        if (auction.IsEnded)
        {
            bool won = AuctionTrackerService.WonBy(auction, me);

            StandingText = won
                ? $"You won this one for {AuctionTrackerService.Money(auction.FinalPrice)}"
                : "You lost this one";

            Losing = !won;
            return;
        }

        bool leading = string.Equals(
            auction.CurrentBidder?.Trim(), me!.Trim(), StringComparison.OrdinalIgnoreCase);

        StandingText = leading
            ? "You are the top bidder"
            : string.IsNullOrWhiteSpace(auction.CurrentBidder)
                ? "No bids yet"
                : $"Outbid by {auction.CurrentBidder.Trim()}";

        Losing = !leading && !string.IsNullOrWhiteSpace(auction.CurrentBidder);
    }

    private static string Name(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string Local(DateTime? utc) =>
        utc is null ? "-" : utc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static string Ago(DateTime utc)
    {
        TimeSpan since = DateTime.UtcNow - utc;

        if (since < TimeSpan.FromMinutes(1))
            return "just now";

        if (since < TimeSpan.FromHours(1))
            return $"{(int)since.TotalMinutes} min ago";

        return $"{(int)since.TotalHours} h ago";
    }

    /// <summary>
    /// The sprite for whatever species the title names.
    ///
    /// The Trade Zone has no species field - "Oshawott Jolly H.A. 29/30" is
    /// one freeform title - so the words are tried one at a time against the
    /// sprite library and the first that resolves wins. §224's relaxed
    /// matching does the work, which is why "oshawott" and "Sirfetchd" both
    /// land. A title that names nothing recognisable leaves the box empty
    /// rather than showing the wrong Pokemon.
    ///
    /// The listing's own uploaded screenshot is deliberately NOT downloaded.
    /// Every sprite is already on disk, so fetching pictures off someone
    /// else's forum to show a thumbnail would be traffic spent on something
    /// the tracker can already draw - the same reasoning as the guide
    /// scraper's, which drops remote images too.
    /// </summary>
    internal static Bitmap? SpriteFor(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        foreach (string word in Words(title))
        {
            if (PokemonSpriteService.TryGetSpritePath(word, out _, allowRelaxedMatch: true))
            {
                Bitmap? sprite = PokemonSpriteService.GetSprite(word)
                                 ?? PokemonSpriteService.GetEncounterSprite(word);

                if (sprite is not null)
                    return sprite;
            }
        }

        return null;
    }

    /// <summary>Runs of letters and apostrophes, longest first - so a title
    /// like "Shiny Farfetch'd" tries the longer word before the shorter one
    /// and cannot match a two-letter fragment of it.</summary>
    internal static IReadOnlyList<string> Words(string title)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (char c in title)
        {
            if (char.IsLetter(c) || c == '\'')
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            words.Add(current.ToString());

        words.RemoveAll(word => word.Length < 3);
        words.Sort(static (a, b) => b.Length.CompareTo(a.Length));

        return words;
    }
}

/// <summary>
/// §236. The Auction Tracker window.
///
/// WHAT IT DOES AND DOES NOT DO. It reads Trade Zone listings the player links
/// to it and remembers what they said. It does not post, bid, or end anything
/// on the forum - all of that needs a logged-in session, and an app acting on
/// someone's account is a different thing from one reading a public page. End
/// Auction records here that an auction is over; it does not close it there.
///
/// COURTESY. One request per linked, running auction every two minutes, only
/// while this window is open, and only for listings the player linked by hand.
/// Twelve is the cap PER TAB since §310, so the busiest this can ever be is
/// twenty-four requests a couple of times a minute against a forum whose
/// robots.txt sets no crawl delay at all. That doubling is deliberate: a
/// shared twelve would have meant a player running a full dozen auctions
/// could not watch a single bid, which is the same as not having the tab. A
/// read that fails is left alone until the next tick rather than retried, and
/// the card keeps saying when it last succeeded.
///
/// §310. The window has two halves and ONE poll. Which tab is showing decides
/// which cards are drawn and nothing else - an auction you are bidding on
/// goes stale exactly as fast as one you are running, and a tracker that
/// stopped reading half its cards because they were behind a tab would be
/// showing figures it knew were old.
/// </summary>
public sealed partial class AuctionTrackerViewModel : ViewModelBase, IDisposable
{
    /// <summary>See the class remark on courtesy.</summary>
    private const int PollIntervalMs = 120_000;

    private readonly DispatcherTimer poll;
    private readonly SemaphoreSlim pollGate = new(1, 1);

    private bool disposed;

    public AuctionTrackerViewModel()
    {
        poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        poll.Tick += async (_, _) => await PollAsync();

        Refresh();
    }

    public ObservableCollection<AuctionCard> Cards { get; } = new();

    [ObservableProperty] private string totalMadeText = "0";
    [ObservableProperty] private string conductedText = "0";
    [ObservableProperty] private string statusMessage = string.Empty;

    // ---- §310: the two tabs ----

    /// <summary>§310. Which half of the window is showing. It is one list
    /// filtered, exactly as History is one list filtered - the cards are the
    /// same cards and the poll reads both sides whichever is on screen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowingAuctions))]
    [NotifyPropertyChangedFor(nameof(AddButtonText))]
    private bool showingBids;

    public bool ShowingAuctions => !ShowingBids;

    [ObservableProperty] private string totalSpentText = "0";
    [ObservableProperty] private string wonText = "0";
    [ObservableProperty] private string lostText = "0";

    /// <summary>§310. The forum name this player bids under. Two-way from the
    /// box on the Bids tab; written through to the store as it is typed, so
    /// there is no Save button to forget to press.</summary>
    [ObservableProperty] private string biddingAsText = AuctionTrackerService.BiddingAs;

    /// <summary>§310. Said under the three figures when no name has been
    /// given - the totals are not wrong, they are unanswerable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBidNotice))]
    private string bidNoticeText = string.Empty;

    public bool HasBidNotice => !string.IsNullOrWhiteSpace(BidNoticeText);

    /// <summary>History is the same list filtered, not a second window: the
    /// cards are identical and one of them is just finished.</summary>
    [ObservableProperty] private bool showingHistory;

    [ObservableProperty] private string historyButtonText = "History";

    /// <summary>§310. "Add Auction" on one tab and "Watch a Bid" on the
    /// other: the button does the same thing either way, and calling it Add
    /// Auction on the Bids tab would read as though it posted one.</summary>
    public string AddButtonText => ShowingBids ? "Watch a Bid" : "Add Auction";

    /// <summary>Set by the window - the composer and the file pickers need an
    /// owner, and a view model has no business knowing about windows.</summary>
    public Func<Task<TrackedAuction?>>? RequestNewAuction { get; set; }

    public Func<string, Task<string?>>? RequestSaveFile { get; set; }

    public Func<Task<string?>>? RequestOpenFile { get; set; }

    /// <summary>§268. How stale a read has to be before OPENING the window
    /// re-reads it. The two-minute poll only runs while the window is open, so
    /// anything older than this went stale with the window closed - and a
    /// player who closes and reopens the tracker twice in a minute should not
    /// send the forum a second round of requests for it.</summary>
    private static readonly TimeSpan OpenRefreshCooldown = TimeSpan.FromHours(1);

    /// <summary>§268. Starts the poll and, at the same time, catches up on any
    /// auction the closed window let go stale. The first poll tick is two
    /// minutes away, which used to be two minutes of a card saying "checked 6
    /// h ago" with nothing happening.</summary>
    public void Start()
    {
        poll.Start();

        _ = RefreshStaleAsync();
    }

    /// <summary>§268. Reads only the auctions whose last successful read is
    /// older than the cooldown, or that have never been read at all.</summary>
    private async Task RefreshStaleAsync()
    {
        DateTime now = DateTime.UtcNow;

        List<TrackedAuction> stale = AuctionTrackerService.Active()
            .Where(a => a.CheckedUtc is not { } checkedUtc || now - checkedUtc >= OpenRefreshCooldown)
            .ToList();

        if (stale.Count == 0)
            return;

        StatusMessage = stale.Count == 1
            ? "Catching up on one auction that went stale while the tracker was closed..."
            : $"Catching up on {stale.Count} auctions that went stale while the tracker was closed...";

        await ReadAsync(stale);
    }

    // -------------------------------------------------------------- display

    private void Refresh()
    {
        AuctionRole role = ShowingBids ? AuctionRole.Bidding : AuctionRole.Selling;

        // §310: the list on screen is one role's half. The POLL is not - it
        // reads both, because an auction you are bidding on goes stale exactly
        // as fast as one you are running.
        IReadOnlyList<TrackedAuction> wanted = ShowingHistory
            ? AuctionTrackerService.History(role)
            : AuctionTrackerService.Active(role);

        string me = AuctionTrackerService.BiddingAs;

        // Updated in place where the card is already on screen, so a refresh
        // does not scroll the list out from under whoever is reading it.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (TrackedAuction auction in wanted)
        {
            seen.Add(auction.ListingId);

            AuctionCard? existing = null;

            foreach (AuctionCard card in Cards)
            {
                if (string.Equals(card.ListingId, auction.ListingId, StringComparison.OrdinalIgnoreCase))
                {
                    existing = card;
                    break;
                }
            }

            if (existing is null)
                Cards.Add(new AuctionCard(auction, me));
            else
                existing.Apply(auction, me);
        }

        for (int i = Cards.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Cards[i].ListingId))
                Cards.RemoveAt(i);
        }

        AuctionTotals totals = AuctionTrackerService.Totals();

        TotalMadeText = AuctionTrackerService.Money(totals.TotalMade);
        ConductedText = totals.Conducted.ToString(CultureInfo.CurrentCulture);

        BidTotals bids = AuctionTrackerService.Bids();

        TotalSpentText = AuctionTrackerService.Money(bids.TotalSpent);
        WonText = bids.Won.ToString(CultureInfo.CurrentCulture);
        LostText = bids.Lost.ToString(CultureInfo.CurrentCulture);

        // §310. An unjudged bid is not a lost one. Saying so is the whole
        // difference between three figures that are incomplete and three that
        // are wrong.
        BidNoticeText = bids.Unjudged == 0
            ? string.Empty
            : bids.Unjudged == 1
                ? "One finished bid is not counted - fill in the name you bid under."
                : $"{bids.Unjudged} finished bids are not counted - fill in the name you bid under.";
    }

    // ------------------------------------------------------------- commands

    /// <summary>§310. Switches tab. Pressing the tab already showing does
    /// nothing rather than reloading it, and switching always lands on the
    /// RUNNING half - arriving on the other tab's history because that was
    /// where you left this one is a small surprise with no upside.</summary>
    [RelayCommand]
    private void ShowRole(string? which)
    {
        bool bids = string.Equals(which, "Bids", StringComparison.OrdinalIgnoreCase);

        if (bids == ShowingBids && !ShowingHistory)
            return;

        ShowingBids = bids;
        ShowingHistory = false;
        HistoryButtonText = "History";

        // Nothing carries over between the two lists, so the cards are cleared
        // rather than diffed - every one of them is about to be replaced.
        Cards.Clear();
        Refresh();

        StatusMessage = string.Empty;
    }

    /// <summary>§310. Written through as it is typed - there is no Save
    /// button to forget, and the standings on screen follow the box.</summary>
    partial void OnBiddingAsTextChanged(string value)
    {
        AuctionTrackerService.SetBiddingAs(value);
        Refresh();
    }

    [RelayCommand]
    private void ToggleHistory()
    {
        ShowingHistory = !ShowingHistory;
        HistoryButtonText = ShowingHistory ? "Running" : "History";

        // Nothing carries over between the two lists, so the cards are cleared
        // rather than diffed - every one of them is about to be replaced.
        Cards.Clear();
        Refresh();
    }

    /// <summary>§237. Asks for a listing address and builds the card from what
    /// the listing says. Nothing is typed in twice.</summary>
    [RelayCommand]
    private async Task AddAuction()
    {
        if (RequestNewAuction is null)
            return;

        TrackedAuction? added = await RequestNewAuction();

        if (added is null)
            return;

        // §310: the composer reads a listing and knows nothing about which
        // tab asked for it, so the role is stamped here - the one place that
        // does know.
        added = added with
        {
            Role = ShowingBids ? AuctionRole.Bidding : AuctionRole.Selling,
        };

        try
        {
            if (!AuctionTrackerService.Add(added))
            {
                StatusMessage = "That auction is already being tracked.";
                return;
            }
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
            return;
        }

        if (ShowingHistory)
            ToggleHistory();
        else
            Refresh();

        StatusMessage = added.IsBid
            ? $"Watching {added.Title}."
            : $"Tracking {added.Title}.";
    }

    /// <summary>§237. Reads one listing now, rather than waiting for the next
    /// two-minute tick. Replaces §236's Link Forum, which had nothing left to
    /// do once a card could only be made from a listing in the first place.</summary>
    [RelayCommand]
    private async Task RefreshCard(AuctionCard? card)
    {
        if (card is null)
            return;

        StatusMessage = "Reading the listing...";

        TradeListing? listing = await TradeListingService.ReadAsync(card.Url);

        if (listing is null)
        {
            StatusMessage = "That listing could not be read this time. The card still shows when it was last read.";
            return;
        }

        bool justEnded = Store(listing);
        Refresh();

        StatusMessage = justEnded
            ? $"{listing.Title} has ended on the forum - moved to History."
            : $"Read {listing.Title}.";
    }

    /// <summary>§268. Opens the listing on the forum. The card is a summary
    /// of a page the player linked; bidding, asking a question and closing the
    /// auction all happen there, and until now the only way back was to find
    /// the thread again by hand.</summary>
    [RelayCommand]
    private void OpenListing(AuctionCard? card)
    {
        if (card is null || string.IsNullOrWhiteSpace(card.Url))
            return;

        try
        {
            // UseShellExecute is required - without it .NET tries to run the
            // URL as a process instead of handing it to the default browser.
            // Same as GuideViewModel.OpenInBrowser.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = card.Url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auction Tracker: the listing could not be opened in a browser.");
            StatusMessage = $"That listing could not be opened in a browser - {ex.Message}";
        }
    }

    [RelayCommand]
    private void EndAuction(AuctionCard? card)
    {
        if (card is null)
            return;

        AuctionTrackerService.End(card.ListingId);
        Refresh();

        // §310: a bid is somebody else's auction, so there is no "do that
        // there" to offer - the sentence about not closing it on the forum
        // would be answering a question nobody asked.
        StatusMessage = card.IsBid
            ? "Recorded as finished, at whatever the last read said. Whether you won it is read off who the last read said was leading."
            : "Recorded as ended, at whatever the last read said. This does not close it on the forum - do that there.";
    }

    [RelayCommand]
    private void Delete(AuctionCard? card)
    {
        if (card is null)
            return;

        AuctionTrackerService.Delete(card.ListingId);
        Refresh();
        StatusMessage = "Removed. Delete is not End - a deleted auction counts toward nothing.";
    }

    [RelayCommand]
    private async Task Export()
    {
        if (RequestSaveFile is null)
            return;

        string? path = await RequestSaveFile("auction-tracker.json");

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            AuctionTrackerService.Export(path);
            StatusMessage = "Exported.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auction Tracker: export failed.");
            StatusMessage = "That file could not be written - see today's log.";
        }
    }

    [RelayCommand]
    private async Task Import()
    {
        if (RequestOpenFile is null)
            return;

        string? path = await RequestOpenFile();

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            int added = AuctionTrackerService.Import(path);

            Refresh();

            StatusMessage = added switch
            {
                0 => "Nothing new in that file - everything in it was already here.",
                1 => "Imported 1 auction.",
                _ => $"Imported {added} auctions.",
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auction Tracker: import failed.");
            StatusMessage = "That file could not be read as an auction export.";
        }
    }

    // --------------------------------------------------------------- polling

    /// <summary>Reads every linked, running auction once. A tick arriving
    /// while the last is still working is dropped rather than queued.</summary>
    private async Task PollAsync()
    {
        if (!pollGate.Wait(0))
            return;

        try
        {
            await ReadInsideGateAsync(AuctionTrackerService.Active());
        }
        finally
        {
            pollGate.Release();
        }
    }

    /// <summary>§268. Reads a given set of auctions, under the same gate the
    /// two-minute poll uses so the two can never overlap. A tick or an open
    /// arriving while the other is working is dropped rather than queued -
    /// which is the whole point of the gate.</summary>
    private async Task ReadAsync(IReadOnlyList<TrackedAuction> auctions)
    {
        if (!pollGate.Wait(0))
            return;

        try
        {
            await ReadInsideGateAsync(auctions);
        }
        finally
        {
            pollGate.Release();
        }
    }

    /// <summary>The read itself. One body, so the poll and the catch-up cannot
    /// come to disagree about what a failed read does - which is nothing: the
    /// card goes on saying when it last succeeded, and that is the honest
    /// thing to show.</summary>
    private async Task ReadInsideGateAsync(IReadOnlyList<TrackedAuction> auctions)
    {
        try
        {
            var failures = 0;
            var ended = new List<string>();

            foreach (TrackedAuction auction in auctions)
            {
                TradeListing? listing = await TradeListingService.ReadAsync(auction.Url);

                if (listing is null)
                {
                    failures++;
                    continue;
                }

                if (Store(listing))
                    ended.Add(listing.Title);
            }

            Refresh();

            // §269: an auction closing itself is the news, so it outranks the
            // read failures in the one status line there is.
            if (ended.Count > 0)
            {
                StatusMessage = ended.Count == 1
                    ? $"{ended[0]} has ended on the forum - moved to History."
                    : $"{ended.Count} auctions have ended on the forum - moved to History.";
            }
            else if (failures > 0)
            {
                StatusMessage = failures == 1
                    ? "One listing could not be read this time. The card shows when it was last read."
                    : $"{failures} listings could not be read this time. The cards show when they were last read.";
            }
            else if (auctions.Count > 0)
            {
                StatusMessage = string.Empty;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auction Tracker: a refresh failed.");
            StatusMessage = "The Trade Zone could not be read this time - see today's log.";
        }
    }

    /// <summary>One place that writes a read into the store, shared by the
    /// two-minute poll and the per-card Refresh - so the two can never come to
    /// disagree about which fields a read updates.</summary>
    private static bool Store(TradeListing listing)
    {
        AuctionTrackerService.UpdateSnapshot(
            listing.ListingId,
            listing.Title,
            listing.CurrentBidder,
            listing.CurrentBid,
            listing.LastBidder,
            listing.LastBid,
            listing.Server,
            listing.EndsUtc);

        // §269: the snapshot is written FIRST either way, so an auction that
        // ended is recorded with the last state the page showed before it is
        // closed off - and End refuses an auction already ended, so a second
        // read of the same finished listing changes nothing.
        if (!listing.IsEnded)
            return false;

        AuctionTrackerService.End(
            listing.ListingId,
            listing.WinningBid > 0 ? listing.WinningBid : null,
            listing.WinningBidder,
            listing.EndedUtc);

        return true;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        poll.Stop();
        pollGate.Dispose();

        // The sprites are NOT disposed: PokemonSpriteService hands them out
        // from a shared cache, so disposing one here would blank it everywhere
        // else in the app. Same reasoning as the World Quest window's.
    }
}
