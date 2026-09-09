using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// §158. The Simulator's compact Pokemon picker - one click on a card
/// closes the dialog with that pick (Close(true); the caller reads
/// SelectedName). Follows the PokemonSelectorWindow dialog pattern:
/// the view model raises Confirmed, the window turns it into a dialog
/// result and closes itself.
/// </summary>
public partial class SimulatorPokemonPickerWindow : Window
{
    public SimulatorPokemonPickerWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is SimulatorPokemonPickerViewModel vm)
            {
                vm.Confirmed += () => Close(true);
            }
        };
    }

    private SimulatorPokemonPickerViewModel? ViewModel => DataContext as SimulatorPokemonPickerViewModel;

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PokemonCardItem card })
        {
            ViewModel?.PickCardCommand.Execute(card);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
