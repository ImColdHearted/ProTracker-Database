using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.Services;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>§237. Asks for a Trade Zone address and reads the listing behind
/// it, so the card is built from what the forum says rather than from what the
/// player retypes. Returns null when cancelled, or when the listing could not
/// be read - a card with nothing in it would be worse than no card.
///
/// The file name is from §236, when this composed an auction post; see
/// CreateAuctionPostViewModel for why that went away.</summary>
public partial class CreateAuctionPostWindow : Window
{
    private readonly CreateAuctionPostViewModel model = new();

    private TrackedAuction? result;

    public CreateAuctionPostWindow()
    {
        InitializeComponent();
        DataContext = model;
    }

    public static async Task<TrackedAuction?> AskAsync(Window owner)
    {
        var window = new CreateAuctionPostWindow();
        await window.ShowDialog(owner);
        return window.result;
    }

    private async void AddButton_Click(object? sender, RoutedEventArgs e)
    {
        if (model.Busy || !model.TryReadLink(out _, out string url))
            return;

        model.Busy = true;
        model.StatusMessage = "Reading the listing...";

        try
        {
            TradeListing? listing = await TradeListingService.ReadAsync(url);

            if (listing is null)
            {
                model.StatusMessage =
                    "That listing could not be read. Check the address is right and that the listing is still there.";
                return;
            }

            result = CreateAuctionPostViewModel.From(listing);
            Close();
        }
        finally
        {
            model.Busy = false;
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
