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
    {
        ListingId = auction.ListingId;
        Apply(auction);
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

    public void Apply(TrackedAuction auction)
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
/// Twelve is the cap on tracked auctions, so the busiest this can ever be is
/// twelve requests a couple of times a minute against a forum whose robots.txt
/// sets no crawl delay at all. A read that fails is left alone until the next
/// tick rather than retried, and the card keeps saying when it last succeeded.
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

    /// <summary>History is the same list filtered, not a second window: the
    /// cards are identical and one of them is just finished.</summary>
    [ObservableProperty] private bool showingHistory;

    [ObservableProperty] private string historyButtonText = "History";

    /// <summary>Set by the window - the composer and the file pickers need an
    /// owner, and a view model has no business knowing about windows.</summary>
    public Func<Task<TrackedAuction?>>? RequestNewAuction { get; set; }

    public Func<string, Task<string?>>? RequestSaveFile { get; set; }

    public Func<Task<string?>>? RequestOpenFile { get; set; }

    public void Start() => poll.Start();

    // -------------------------------------------------------------- display

    private void Refresh()
    {
        IReadOnlyList<TrackedAuction> wanted =
            ShowingHistory ? AuctionTrackerService.History() : AuctionTrackerService.Active();

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
                Cards.Add(new AuctionCard(auction));
            else
                existing.Apply(auction);
        }

        for (int i = Cards.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Cards[i].ListingId))
                Cards.RemoveAt(i);
        }

        AuctionTotals totals = AuctionTrackerService.Totals();

        TotalMadeText = AuctionTrackerService.Money(totals.TotalMade);
        ConductedText = totals.Conducted.ToString(CultureInfo.CurrentCulture);
    }

    // ------------------------------------------------------------- commands

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

        StatusMessage = $"Tracking {added.Title}.";
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

        Store(listing);
        Refresh();
        StatusMessage = $"Read {listing.Title}.";
    }

    [RelayCommand]
    private void EndAuction(AuctionCard? card)
    {
        if (card is null)
            return;

        AuctionTrackerService.End(card.ListingId);
        Refresh();

        StatusMessage =
            "Recorded as ended, at whatever the last read said. This does not close it on the forum - do that there.";
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
            var failures = 0;

            foreach (TrackedAuction auction in AuctionTrackerService.Active())
            {
                TradeListing? listing = await TradeListingService.ReadAsync(auction.Url);

                if (listing is null)
                {
                    // Left exactly as it was. The card goes on saying when it
                    // last succeeded, which is the honest thing to show.
                    failures++;
                    continue;
                }

                Store(listing);
            }

            Refresh();

            if (failures > 0)
            {
                StatusMessage = failures == 1
                    ? "One listing could not be read this time. The card shows when it was last read."
                    : $"{failures} listings could not be read this time. The cards show when they were last read.";
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auction Tracker: a refresh failed.");
            StatusMessage = "The Trade Zone could not be read this time - see today's log.";
        }
        finally
        {
            pollGate.Release();
        }
    }

    /// <summary>One place that writes a read into the store, shared by the
    /// two-minute poll and the per-card Refresh - so the two can never come to
    /// disagree about which fields a read updates.</summary>
    private static void Store(TradeListing listing) =>
        AuctionTrackerService.UpdateSnapshot(
            listing.ListingId,
            listing.Title,
            listing.CurrentBidder,
            listing.CurrentBid,
            listing.LastBidder,
            listing.LastBid,
            listing.Server,
            listing.EndsUtc);

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
