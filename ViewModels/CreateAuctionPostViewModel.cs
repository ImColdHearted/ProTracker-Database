using CommunityToolkit.Mvvm.ComponentModel;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// §237. Adds an auction by its Trade Zone address, and nothing else.
///
/// §236 asked the player to compose the auction here - title, start price,
/// minimum bid, instant price, server, duration, accepted payment - and
/// produced post text to paste into the forum. Every one of those fields is
/// on the listing once it is posted, and the parser reads them all, so
/// composing them by hand was asking for the same information twice and
/// leaving two versions of it to disagree. The listing is the source now.
///
/// The file is still called CreateAuctionPost because this session cannot
/// delete files on the machine it commits to; the name is from §236 and only
/// the name.
/// </summary>
public sealed partial class CreateAuctionPostViewModel : ViewModelBase
{
    [ObservableProperty] private string linkText = string.Empty;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool busy;

    /// <summary>The address as the tracker will use it, or null with a reason
    /// on the status line. Checked before anything is fetched, so a typo costs
    /// nothing and says so immediately.</summary>
    public bool TryReadLink(out string listingId, out string url)
    {
        if (TradeListingService.TryReadListingId(LinkText, out listingId, out url))
        {
            StatusMessage = string.Empty;
            return true;
        }

        StatusMessage =
            "Paste the address of the Trade Zone listing, for example https://pokemonrevolution.net/forum/trade/9557/ - the number on its own works too.";
        return false;
    }

    /// <summary>Builds the card from what the listing actually said. Nothing
    /// here is typed by the player, which is the point of the change.</summary>
    public static TrackedAuction From(TradeListing listing) =>
        new(
            ListingId: listing.ListingId,
            Url: listing.Url,
            Title: listing.Title,
            Server: listing.Server,
            CurrentBidder: listing.CurrentBidder,
            CurrentBid: listing.CurrentBid,
            LastBidder: listing.LastBidder,
            LastBid: listing.LastBid,
            AddedUtc: DateTime.UtcNow,
            EndsOnForumUtc: listing.EndsUtc,
            CheckedUtc: DateTime.UtcNow,
            EndedUtc: null,
            FinalPrice: 0,
            FinalBidder: string.Empty);
}
