using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Foot_Tracker.Services;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class HuntLogWindow : Window
{
    public HuntLogWindow()
    {
        InitializeComponent();

        var vm = new HuntLogViewModel();
        DataContext = vm;

        // this = HuntLogWindow itself, not whatever window opened it - both
        // popups should be owned by (and appear over) this window, same
        // reasoning as PreviouslyBattledUsersWindow's ConfirmAsync wiring.
        vm.ConfirmAsync = message => ConfirmDialogWindow.ShowAsync(this, message);
        vm.RequestExportFormat = () => ExportFormatDialogWindow.ShowAsync(this);

        vm.RequestExportFilePath = async (suggestedFileName, format) =>
        {
            bool isJson = format == "json";

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Hunting Log",
                SuggestedFileName = suggestedFileName,
                DefaultExtension = isJson ? "json" : "csv",
                FileTypeChoices = isJson
                    ? new[] { new FilePickerFileType("JSON File") { Patterns = new[] { "*.json" } } }
                    : new[] { new FilePickerFileType("CSV File") { Patterns = new[] { "*.csv" } } }
            });

            return file?.TryGetLocalPath();
        };

        // vm subscribes to HuntLogService.LogChanged (a static event) in its
        // constructor to keep the list live while this window is open -
        // Dispose() unsubscribes so closing the window doesn't leak this
        // instance for the rest of the app's lifetime, same as
        // PreviouslyBattledUsersWindow's Closed handler.
        Closed += (_, _) => vm.Dispose();
    }

    // Opens the per-species detail view for whichever row was clicked -
    // "similar to previously battled opponents for PVP," just scoped to one
    // species instead of every logged encounter. See HuntLogSpeciesDetailWindow.
    private void EntryRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: HuntLogDisplayItem item })
            return;

        // §189: unowned - see WindowRegistry.ShowUnowned.
        WindowRegistry.ShowUnowned(new HuntLogSpeciesDetailWindow(item.PokemonName), this);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
