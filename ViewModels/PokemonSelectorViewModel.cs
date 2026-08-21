using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

public sealed record PokemonCardItem(string Name, Bitmap? Sprite, string? Tag = null);

/// <summary>
/// Ported from PokemonSelectorForm.cs + PokemonFormsPopup.cs. The original showed
/// alternate forms/counterparts in a second floating Form positioned next to the
/// selected card; here that's an inline "Forms" panel in the same window
/// (AvailableForms) instead of a second popup window - simpler and avoids
/// screen-edge positioning logic, same information.
/// </summary>
public sealed partial class PokemonSelectorViewModel : ViewModelBase
{
    private readonly DispatcherTimer _searchDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ObservableCollection<PokemonCardItem> SearchResults { get; } = new();
    public ObservableCollection<PokemonCardItem> AvailableForms { get; } = new();

    [ObservableProperty] private string? searchText;
    [ObservableProperty] private PokemonCardItem? selectedCard;
    [ObservableProperty] private string? pendingSelection;
    [ObservableProperty] private string? statusMessage;

    public string? SelectedPokemon { get; private set; }

    /// <summary>Raised when a selection is confirmed (double-click or Select button) - the View closes itself.</summary>
    public event Action? Confirmed;

    public PokemonSelectorViewModel()
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

    [RelayCommand]
    private void SelectCard(PokemonCardItem card)
    {
        // Clicking the already-selected card again deselects it.
        if (SelectedCard == card)
        {
            SelectedCard = null;
            PendingSelection = null;
            AvailableForms.Clear();
            return;
        }

        SelectedCard = card;
        PendingSelection = card.Name;
        LoadForms(card.Name);
    }

    [RelayCommand]
    private void ConfirmCard(PokemonCardItem card)
    {
        // Double-click: immediate confirm, same as the original's DoubleClick handler.
        SelectedPokemon = card.Name;
        Confirmed?.Invoke();
    }

    private void LoadForms(string pokemonName)
    {
        AvailableForms.Clear();

        var species = PokemonSpriteService.AllPokemon
            .FirstOrDefault(p => string.Equals(p.Name, pokemonName, StringComparison.OrdinalIgnoreCase));

        if (species is not null)
        {
            AvailableForms.Add(new PokemonCardItem(species.Name, PokemonSpriteService.GetSprite(species.Name), "Normal"));

            foreach (var form in PokemonSpriteService.GetFormsForSpecies(species.Name).Take(30))
            {
                AvailableForms.Add(new PokemonCardItem(form.Name, PokemonSpriteService.GetEncounterSprite(form.Name), GetFormTag(form.Name)));
            }

            foreach (var counterpart in CounterpartSpriteService.GetForPokemon(species.Name).Take(50))
            {
                AvailableForms.Add(new PokemonCardItem(counterpart.Name, CounterpartSpriteService.GetImage(counterpart), counterpart.Event));
            }
        }
        else
        {
            // Regional form selected directly - show its own counterparts.
            AvailableForms.Add(new PokemonCardItem(pokemonName, PokemonSpriteService.GetEncounterSprite(pokemonName), GetFormTag(pokemonName)));

            foreach (var counterpart in CounterpartSpriteService.GetForPokemon(pokemonName))
            {
                AvailableForms.Add(new PokemonCardItem(counterpart.Name, CounterpartSpriteService.GetImage(counterpart), counterpart.Event));
            }
        }
    }

    [RelayCommand]
    private void Select()
    {
        if (string.IsNullOrWhiteSpace(PendingSelection))
        {
            StatusMessage = "Select a Pokémon first.";
            return;
        }

        SelectedPokemon = PendingSelection;
        Confirmed?.Invoke();
    }

    private static string GetFormTag(string name)
    {
        if (name.Contains("Alolan", StringComparison.OrdinalIgnoreCase) || name.Contains("Alola", StringComparison.OrdinalIgnoreCase))
            return "Alolan";
        if (name.Contains("Galarian", StringComparison.OrdinalIgnoreCase) || name.Contains("Galar", StringComparison.OrdinalIgnoreCase))
            return "Galarian";
        if (name.Contains("Hisuian", StringComparison.OrdinalIgnoreCase) || name.Contains("Hisui", StringComparison.OrdinalIgnoreCase))
            return "Hisuian";
        if (name.Contains("Mega", StringComparison.OrdinalIgnoreCase))
            return "Mega";
        if (name.Contains("Gmax", StringComparison.OrdinalIgnoreCase))
            return "G-Max";
        return "Form";
    }
}
