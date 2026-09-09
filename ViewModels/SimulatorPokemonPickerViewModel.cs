using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// §158. The Simulator's own Pokemon picker - a smaller, single-select
/// cousin of PokemonSelectorViewModel. The team builder needs exactly one
/// battler per slot, so the shared Set Target dialog was the wrong shape:
/// its multi-select flow, its forms panel and above all its counterpart
/// cards (event recolors like Christmas or Halloween variants) have no
/// meaning in a battle where only the species matters. This one searches
/// the same species catalog plus the huntable regional forms - both are
/// distinct battlers in the Simulator's pokedex - and nothing else.
/// Clicking a card picks it immediately; there is no toggle state.
/// </summary>
public sealed partial class SimulatorPokemonPickerViewModel : ViewModelBase
{
    private readonly DispatcherTimer _searchDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ObservableCollection<PokemonCardItem> SearchResults { get; } = new();

    [ObservableProperty] private string? searchText;

    /// <summary>The picked name once <see cref="Confirmed"/> has fired.</summary>
    public string? SelectedName { get; private set; }

    /// <summary>Raised when a card is picked - the View closes itself.</summary>
    public event Action? Confirmed;

    public SimulatorPokemonPickerViewModel()
    {
        _searchDelayTimer.Tick += (_, _) =>
        {
            _searchDelayTimer.Stop();
            LoadSearchResults(SearchText?.Trim() ?? string.Empty);
        };
    }

    partial void OnSearchTextChanged(string? value)
    {
        _searchDelayTimer.Stop();

        if ((value?.Trim().Length ?? 0) < 2)
        {
            SearchResults.Clear();
            return;
        }

        _searchDelayTimer.Start();
    }

    private void LoadSearchResults(string search)
    {
        SearchResults.Clear();

        foreach (var pokemon in PokemonSpriteService.AllPokemon
                     .Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                     .Take(40))
        {
            SearchResults.Add(new PokemonCardItem(pokemon.Name, PokemonSpriteService.GetSprite(pokemon.Name)));
        }

        // Alolan/Galarian/Hisuian forms are their own species to the battle
        // engine (own stats, types and learnsets), so they belong here.
        // Counterparts deliberately do not - they are sprite skins.
        foreach (var form in PokemonSpriteService.GetHuntableRegionalForms()
                     .Where(f => f.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                     .Take(20))
        {
            SearchResults.Add(new PokemonCardItem(form.Name, PokemonSpriteService.GetEncounterSprite(form.Name)));
        }
    }

    /// <summary>Single click on a card: that is the pick, dialog done.</summary>
    [RelayCommand]
    private void PickCard(PokemonCardItem? card)
    {
        if (card == null)
            return;

        SelectedName = card.Name;
        Confirmed?.Invoke();
    }
}
