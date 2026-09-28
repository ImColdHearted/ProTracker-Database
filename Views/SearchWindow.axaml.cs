using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.Services;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// §280. The Search item on the menu bar. One box over the tracker's own
/// records; clicking a hit opens the window that already shows that kind of
/// thing - see SearchViewModel.
///
/// Every window it opens is opened UNOWNED, so closing the search does not
/// take the thing you searched for with it - the same correction
/// BossListWindow records for its detail windows.
/// </summary>
public partial class SearchWindow : Window
{
    public SearchWindow()
    {
        InitializeComponent();

        var vm = new SearchViewModel();
        DataContext = vm;

        vm.OpenResult = item =>
        {
            switch (item.Kind)
            {
                case SearchResultKind.Pokemon:
                    new SessionEncounterHistoryWindow(item.Key).Show();
                    break;

                case SearchResultKind.Map:
                    new MapDetailWindow(item.Key).Show();
                    break;

                case SearchResultKind.Player:
                    new PvpOpponentDetailWindow(item.Key).Show();
                    break;
            }
        };

        // §403: a Pokémon's spawn locations on the picture - §407: the
        // Maps window, opened on the species' card. One Maps window,
        // through the registry, unowned like the rest; a second Locations
        // press while it is open brings it forward with the new species.
        vm.OpenLocations = item =>
        {
            if (WindowRegistry.TryGet<MapsWindow>() is MapsWindow open)
            {
                open.ShowPokemon(item.Key);
                open.Activate();
                return;
            }

            WindowRegistry.ShowOrActivate(this, () => new MapsWindow(item.Key));
        };

        // The box is the only thing in this window worth focusing, and a
        // search window that needs a click before it can be typed into is a
        // search window that was opened for nothing.
        Opened += (_, _) => this.FindControl<TextBox>("QueryBox")?.Focus();
    }

    private void Result_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: SearchResultItem item } &&
            DataContext is SearchViewModel vm)
        {
            vm.OpenCommand.Execute(item);
        }
    }

    private void LocationsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SearchResultItem item } && DataContext is SearchViewModel vm)
            vm.OpenLocations?.Invoke(item);
    }

    private void EncountersButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SearchResultItem item } && DataContext is SearchViewModel vm)
            vm.OpenCommand.Execute(item);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
