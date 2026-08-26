using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class SwapPokemonWindow : Window
{
    public SwapPokemonWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is SwapPokemonViewModel vm)
            {
                vm.Confirmed += () => Close(true);
            }
        };
    }

    private SwapPokemonViewModel? ViewModel => DataContext as SwapPokemonViewModel;

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
