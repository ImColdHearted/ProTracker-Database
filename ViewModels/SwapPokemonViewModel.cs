using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Backs SwapPokemonWindow - the "click a Currently Hunting sprite to swap it"
/// quality-of-life feature. See MainWindow.axaml.cs's TargetSprite_PointerPressed
/// and MainWindowViewModel.SwapTargetAsync. Deliberately undiscoverable in the
/// UI itself (no tooltip, no hover affordance, no hint text anywhere) - meant
/// to be a "didn't know we could do that" find, not an advertised feature.
///
/// Much simpler than the full PokemonSelectorViewModel (Set Target): single
/// pick only, no multi-select, no forms/counterparts panel - picking a card
/// closes this window immediately. The actual Yes/No confirmation the user
/// asked for happens afterward, via MainWindowViewModel's existing ConfirmAsync
/// hook, before anything is actually changed.
/// </summary>
public sealed partial class SwapPokemonViewModel : ViewModelBase
{
    private readonly DispatcherTimer _searchDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>The target currently occupying this slot - shown in the window
    /// title (bound in SwapPokemonWindow.axaml) so it's clear what's being
    /// replaced.</summary>
    public string CurrentTargetName { get; }

    public ObservableCollection<PokemonCardItem> SearchResults { get; } = new();

    [ObservableProperty] private string? searchText;

    /// <summary>Set once a card is picked - see SelectCard below.</summary>
    public string? SelectedPokemon { get; private set; }

    /// <summary>Raised as soon as a card is picked - the View closes itself.</summary>
    public event Action? Confirmed;

    public SwapPokemonViewModel(string currentTargetName)
    {
        CurrentTargetName = currentTargetName;

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

    // Single click picks and closes immediately - unlike PokemonSelectorViewModel
    // there's no multi-select to accumulate, so there's nothing to wait on a
    // separate "Select" button for.
    [RelayCommand]
    private void SelectCard(PokemonCardItem card)
    {
        SelectedPokemon = card.Name;
        Confirmed?.Invoke();
    }
}
