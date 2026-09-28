using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class PokemonSelectorWindow : Window
{
    public PokemonSelectorWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is PokemonSelectorViewModel vm)
            {
                vm.Confirmed += () => Close(true);
            }
        };
    }

    private PokemonSelectorViewModel? ViewModel => DataContext as PokemonSelectorViewModel;

    // Single click: select the card and load its forms/counterparts (replaces
    // PokemonSelectorForm's Click handler on card/spriteBox/nameLabel).
    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ViewModels.PokemonCardItem card })
        {
            ViewModel?.SelectCardCommand.Execute(card);
        }
    }

    // §272: a click in the forms/event-skins column. Separate handler from
    // Card_PointerPressed above because the two columns do different things
    // with the same card type - the grid picks a TARGET, this picks the
    // PICTURE that target is drawn with. See TargetSkinPicker.
    private void FormCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ViewModels.PokemonCardItem card })
        {
            ViewModel?.SelectSkinCommand.Execute(card);
        }
    }

    // Double click: confirm immediately (replaces the DoubleClick handler).
    private void Card_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ViewModels.PokemonCardItem card })
        {
            ViewModel?.ConfirmCardCommand.Execute(card);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
