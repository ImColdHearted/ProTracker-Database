using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Backs EventPokemonPickerWindow - lets the Events-board composer optionally
/// attach a Pokemon to a post (see CreateEventViewModel.RequestPokemonPick).
/// Deliberately its own small file rather than reusing SwapPokemonViewModel:
/// same "single pick, sprite grid, no forms panel" shape (right down to the
/// search-result loading, copied verbatim - a known duplication also already
/// present between PokemonSelectorViewModel and SwapPokemonViewModel, so this
/// follows existing precedent rather than starting a new one), but this picker
/// also needs an explicit "Clear" affordance that SwapPokemonWindow has no use
/// for (a hunt target can't be "no target"; an event's Pokemon can be blank).
/// </summary>
public sealed partial class EventPokemonPickerViewModel : ViewModelBase
{
    private readonly DispatcherTimer _searchDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ObservableCollection<PokemonCardItem> SearchResults { get; } = new();

    [ObservableProperty] private string? searchText;

    /// <summary>Set once a card is picked, or to "" by Clear - see below. Null
    /// (the pre-close default) is never actually read: CreateEventViewModel
    /// only reads this after Confirmed has fired.</summary>
    public string? SelectedPokemon { get; private set; }

    /// <summary>Raised once a choice is made (a card, or Clear) - the View closes itself.</summary>
    public event Action? Confirmed;

    public EventPokemonPickerViewModel()
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

        var speciesMatches = PokemonSpriteService.AllPokemon
            .Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Take(40);

        foreach (var pokemon in speciesMatches)
        {
            SearchResults.Add(new PokemonCardItem(pokemon.Name, PokemonSpriteService.GetSprite(pokemon.Name)));
        }

        var regionalMatches = PokemonSpriteService.GetHuntableRegionalForms()
            .Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Take(20);

        foreach (var pokemon in regionalMatches)
        {
            SearchResults.Add(new PokemonCardItem(pokemon.Name, PokemonSpriteService.GetEncounterSprite(pokemon.Name)));
        }
    }

    // Single click picks and closes immediately - same reasoning as
    // SwapPokemonViewModel.SelectCard: nothing to accumulate, so no separate
    // Select button to wait on.
    [RelayCommand]
    private void SelectCard(PokemonCardItem card)
    {
        SelectedPokemon = card.Name;
        Confirmed?.Invoke();
    }

    [RelayCommand]
    private void Clear()
    {
        SelectedPokemon = string.Empty;
        Confirmed?.Invoke();
    }
}
