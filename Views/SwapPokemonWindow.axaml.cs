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

    // §272: the skins column. Separate from Card_PointerPressed above because
    // the two columns do different things with the same card type - that one
    // replaces the target and closes, this one restyles it and stays open.
    private void FormCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ViewModels.PokemonCardItem card })
        {
            ViewModel?.SelectSkinCommand.Execute(card);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
