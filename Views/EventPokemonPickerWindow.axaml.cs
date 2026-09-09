using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// Optional single-Pokemon picker for the Events board's compose form - see
/// CreateEventViewModel.RequestPokemonPick (wired in CreateEventWindow.axaml.cs)
/// and MIGRATION_GUIDE.md. Picking a card or clicking Clear both close this
/// dialog successfully (true); only Cancel leaves the post's choice untouched.
/// </summary>
public partial class EventPokemonPickerWindow : Window
{
    public EventPokemonPickerWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is EventPokemonPickerViewModel vm)
            {
                vm.Confirmed += () => Close(true);
            }
        };
    }

    private EventPokemonPickerViewModel? ViewModel => DataContext as EventPokemonPickerViewModel;

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ViewModels.PokemonCardItem card })
        {
            ViewModel?.SelectCardCommand.Execute(card);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
