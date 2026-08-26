using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

// Was a plain record - now an ObservableObject so IsSelected can update the UI
// live (a checkmark/highlight) as cards are toggled for multi-target selection,
// without rebuilding the whole SearchResults list on every click.
public sealed partial class PokemonCardItem : ObservableObject
{
    public string Name { get; }
    public Bitmap? Sprite { get; }
    public string? Tag { get; }

    /// <summary>Backs a small type-icon row next to this card's name.</summary>
    public IReadOnlyList<string> Types => PokemonSpriteService.GetTypes(Name);

    [ObservableProperty] private bool isSelected;

    public PokemonCardItem(string name, Bitmap? sprite, string? tag = null)
    {
        Name = name;
        Sprite = sprite;
        Tag = tag;
    }
}

/// <summary>
/// Ported from PokemonSelectorForm.cs + PokemonFormsPopup.cs. The original showed
/// alternate forms/counterparts in a second floating Form positioned next to the
/// selected card; here that's an inline "Forms" panel in the same window
/// (AvailableForms) instead of a second popup window - simpler and avoids
/// screen-edge positioning logic, same information.
/// </summary>
public sealed partial class PokemonSelectorViewModel : ViewModelBase
{
    private const int MaxTargets = 4;

    private readonly DispatcherTimer _searchDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ObservableCollection<PokemonCardItem> SearchResults { get; } = new();
    public ObservableCollection<PokemonCardItem> AvailableForms { get; } = new();

    // Up to MaxTargets cards, in the order they were picked.
    public ObservableCollection<PokemonCardItem> SelectedCards { get; } = new();

    [ObservableProperty] private string? searchText;
    [ObservableProperty] private string? statusMessage;

    public List<string> SelectedPokemons { get; private set; } = new();

    /// <summary>Raised when a selection is confirmed (Select button) - the View closes itself.</summary>
    public event Action? Confirmed;

    public PokemonSelectorViewModel()
    {
        _searchDelayTimer.Tick += (_, _) =>
        {
            _searchDelayTimer.Stop();
            LoadSearchResults(SearchText?.Trim() ?? string.Empty);
        };
    }

    /// <summary>Pre-selects cards matching these names (case-insensitive) once the
    /// initial search results/forms include them - used when re-opening the
    /// dialog to edit an already-set list of targets.</summary>
    public void PreselectExisting(IEnumerable<string> existingTargets)
    {
        _preselectNames = new HashSet<string>(existingTargets, StringComparer.OrdinalIgnoreCase);
    }

    private HashSet<string>? _preselectNames;

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
            AddSearchResult(pokemon.Name, PokemonSpriteService.GetSprite(pokemon.Name));
        }

        var regionalMatches = PokemonSpriteService.GetHuntableRegionalForms()
            .Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Take(20);

        foreach (var pokemon in regionalMatches)
        {
            AddSearchResult(pokemon.Name, PokemonSpriteService.GetEncounterSprite(pokemon.Name));
        }
    }

    private void AddSearchResult(string name, Bitmap? sprite)
    {
        var card = new PokemonCardItem(name, sprite);

        // Keep newly-loaded cards in sync with anything already picked (e.g. the
        // user searched, picked a card, then searched again - that card should
        // still show as selected if it reappears in the new results).
        if (SelectedCards.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) ||
            (_preselectNames?.Contains(name) ?? false))
        {
            card.IsSelected = true;
        }

        SearchResults.Add(card);
    }

    [RelayCommand]
    private void SelectCard(PokemonCardItem card)
    {
        bool alreadySelected = SelectedCards.Contains(card);

        if (alreadySelected)
        {
            SelectedCards.Remove(card);
            card.IsSelected = false;
        }
        else
        {
            if (SelectedCards.Count >= MaxTargets)
            {
                StatusMessage = $"You can hunt up to {MaxTargets} Pokémon at once - deselect one first.";
                return;
            }

            SelectedCards.Add(card);
            card.IsSelected = true;
            StatusMessage = null;
        }

        LoadForms(card.Name);
    }

    [RelayCommand]
    private void ConfirmCard(PokemonCardItem card)
    {
        // Multi-select: double-click just toggles the same as a single click now -
        // immediately closing the dialog on double-click no longer makes sense
        // once more than one target can be picked.
        SelectCard(card);
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
        if (SelectedCards.Count == 0)
        {
            StatusMessage = "Select at least one Pokémon first.";
            return;
        }

        SelectedPokemons = SelectedCards.Select(c => c.Name).ToList();
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