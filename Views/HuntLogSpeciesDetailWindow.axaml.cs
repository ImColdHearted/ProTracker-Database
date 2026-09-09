using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class HuntLogSpeciesDetailWindow : Window
{
    // Parameterless constructor so InitializeComponent (called by Avalonia's
    // generated XAML loader) always has a constructor to hang off of - not
    // intended to be used directly, always call the (string pokemonName)
    // overload below. Same two-constructor shape as ConfirmDialogWindow.
    public HuntLogSpeciesDetailWindow()
    {
        InitializeComponent();
    }

    public HuntLogSpeciesDetailWindow(string pokemonName) : this()
    {
        var vm = new HuntLogSpeciesDetailViewModel(pokemonName);
        DataContext = vm;

        // this = HuntLogSpeciesDetailWindow itself, not whatever window opened
        // it - both popups should be owned by (and appear over) this window,
        // same reasoning as HuntLogWindow/PreviouslyBattledUsersWindow.
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

        Closed += (_, _) => vm.Dispose();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
