using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// §159. The Simulator's held-item picker - the SimulatorPokemonPicker
/// dialog pattern: one click on a card (or the No Item button) closes with
/// true and the caller reads SelectedName; Cancel closes with false and
/// the slot keeps what it had. §375: a click on a shelf card opens the
/// shelf instead - the same PointerPressed shape, a different command.
/// </summary>
public partial class SimulatorItemPickerWindow : Window
{
    public SimulatorItemPickerWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is SimulatorItemPickerViewModel vm)
            {
                vm.Confirmed += () => Close(true);
            }
        };
    }

    private SimulatorItemPickerViewModel? ViewModel => DataContext as SimulatorItemPickerViewModel;

    private void Category_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: SimulatorItemCategory shelf })
        {
            ViewModel?.OpenCategoryCommand.Execute(shelf);
        }
    }

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: SimulatorItemCard card })
        {
            ViewModel?.PickCardCommand.Execute(card);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
