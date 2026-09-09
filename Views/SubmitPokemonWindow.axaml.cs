using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// The §106 entry-submission dialog - see SubmitPokemonViewModel. Wires the
/// two things a ViewModel shouldn't own: the shared Pokémon picker (the exact
/// same dialog the Events compose form uses) and the OS image picker.
/// </summary>
public partial class SubmitPokemonWindow : Window
{
    public SubmitPokemonWindow(string eventId, string eventTitle)
    {
        InitializeComponent();

        var vm = new SubmitPokemonViewModel(eventId, eventTitle);

        vm.RequestPokemonPick = async () =>
        {
            var dialogVm = new EventPokemonPickerViewModel();
            var dialog = new EventPokemonPickerWindow { DataContext = dialogVm };
            bool confirmed = await dialog.ShowDialog<bool?>(this) == true;
            return confirmed ? dialogVm.SelectedPokemon : null;
        };

        vm.RequestImagePath = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a screenshot",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Image Files")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" }
                    }
                }
            });

            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };

        vm.Submitted += () => Close(true);

        DataContext = vm;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
