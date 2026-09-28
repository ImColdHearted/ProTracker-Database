using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class SessionEncounterHistoryWindow : Window
{
    // Parameterless constructor so InitializeComponent (called by Avalonia's
    // generated XAML loader) always has a constructor to hang off of - not
    // intended to be used directly, always call the (string pokemonName)
    // overload below. Same two-constructor shape as
    // HuntLogSpeciesDetailWindow/ConfirmDialogWindow.
    public SessionEncounterHistoryWindow()
    {
        InitializeComponent();
    }

    public SessionEncounterHistoryWindow(string pokemonName) : this()
    {
        var vm = new SessionEncounterHistoryViewModel(pokemonName);
        DataContext = vm;

        // vm subscribes to its (service-owned) records collection in its
        // constructor to keep the empty-state flags live while this window is
        // open - Dispose() unsubscribes so closing the window doesn't leak
        // this instance for the rest of the app's lifetime, same pattern as
        // PreviouslyBattledUsersWindow/HuntLogSpeciesDetailWindow.
        Closed += (_, _) => vm.Dispose();
    }

    // §273: the Shiny/Form header sorts. One handler for both lists' headers
    // - they are two views of one question, and the view model holds one flag
    // for exactly that reason.
    private void RareHeader_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is SessionEncounterHistoryViewModel vm)
        {
            vm.ToggleRareFirstCommand.Execute(null);
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
