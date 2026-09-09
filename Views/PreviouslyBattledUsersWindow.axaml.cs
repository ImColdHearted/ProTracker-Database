using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Foot_Tracker.Views;

public partial class PreviouslyBattledUsersWindow : Window
{
    public PreviouslyBattledUsersWindow()
    {
        InitializeComponent();

        var vm = new ViewModels.PreviouslyBattledUsersViewModel();
        DataContext = vm;

        // this = PreviouslyBattledUsersWindow itself, not whatever window
        // opened it - both popups should be owned by (and appear over) this
        // window, same reasoning as RemoveEventWindow's ConfirmAsync wiring.
        vm.ConfirmAsync = message => ConfirmDialogWindow.ShowAsync(this, message);
        vm.RequestExportFormat = () => ExportFormatDialogWindow.ShowAsync(this);

        // Same delegate-set-by-the-View pattern MainWindow.axaml.cs uses for
        // RequestSaveFilePath, just narrowed to whichever single format
        // RequestExportFormat already returned instead of offering both up
        // front the way this used to (that's ExportFormatDialogWindow's job
        // now).
        vm.RequestExportFilePath = async (suggestedFileName, format) =>
        {
            bool isJson = format == "json";

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Previously Battled Users",
                SuggestedFileName = suggestedFileName,
                DefaultExtension = isJson ? "json" : "csv",
                FileTypeChoices = isJson
                    ? new[] { new FilePickerFileType("JSON File") { Patterns = new[] { "*.json" } } }
                    : new[] { new FilePickerFileType("CSV File") { Patterns = new[] { "*.csv" } } }
            });

            return file?.TryGetLocalPath();
        };

        // vm subscribes to PvpOpponentService.OpponentsChanged (a static event)
        // in its constructor to keep the list live while this window is open -
        // Dispose() unsubscribes so closing the window doesn't leak this
        // instance for the rest of the app's lifetime.
        Closed += (_, _) => vm.Dispose();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
