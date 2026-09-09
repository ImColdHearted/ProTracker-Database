using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>§236. The Auction Tracker window. It holds the view model and
/// supplies the two things a view model has no business knowing about: the
/// add-auction dialog and the file pickers. §237 dropped the browser handoff
/// with Link Forum, which was the only thing that used it.</summary>
public partial class AuctionTrackerWindow : Window
{
    private readonly AuctionTrackerViewModel model = new();

    public AuctionTrackerWindow()
    {
        InitializeComponent();
        DataContext = model;

        model.RequestNewAuction = () => CreateAuctionPostWindow.AskAsync(this);

        model.RequestSaveFile = async suggestedName =>
        {
            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Auction Tracker",
                SuggestedFileName = suggestedName,
                DefaultExtension = "json",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Auction Tracker Export") { Patterns = new[] { "*.json" } }
                }
            });

            return file?.TryGetLocalPath();
        };

        model.RequestOpenFile = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Auction Tracker",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Auction Tracker Export") { Patterns = new[] { "*.json" } }
                }
            });

            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };
    }

    /// <summary>The poll starts once the window is on screen, not in the
    /// constructor, so it never runs for a window that failed to open.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        model.Start();
    }

    /// <summary>Stops the poll. Without this it would keep reading the Trade
    /// Zone every two minutes for the rest of the session.</summary>
    protected override void OnClosed(EventArgs e)
    {
        model.Dispose();
        base.OnClosed(e);
    }
}
