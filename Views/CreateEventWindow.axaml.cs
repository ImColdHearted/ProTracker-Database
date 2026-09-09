using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// Team Magma-only form for posting a new entry to the guild Events board -
/// see CreateEventViewModel for the actual posting logic. Only reachable via
/// AdminActionsWindow's "Create Event…" button, itself only reachable after
/// a successful AdminLoginWindow/AdminAuthService login; nothing in this
/// window checks that itself.
/// </summary>
public partial class CreateEventWindow : Window
{
    public CreateEventWindow()
    {
        InitializeComponent();

        var vm = new CreateEventViewModel
        {
            // Mirrors MainWindow.axaml.cs's RequestPokemonSelection wiring,
            // scoped to this window instead since CreateEventViewModel is
            // only ever used here.
            RequestPokemonPick = async () =>
            {
                var dialogVm = new EventPokemonPickerViewModel();
                var dialog = new EventPokemonPickerWindow { DataContext = dialogVm };
                bool confirmed = await dialog.ShowDialog<bool?>(this) == true;
                return confirmed ? dialogVm.SelectedPokemon : null;
            },

            // §143/§153: the events-server sign-in (an admin login or the
            // master token), asked for once per run.
            RequestAdminSignIn = () => AdminTokenWindow.ShowAsync(this)
        };

        DataContext = vm;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
