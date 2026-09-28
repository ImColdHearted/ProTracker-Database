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

    /// <summary>§272. For a card in the forms/event-skins column: the
    /// counterpart image this card would draw the target with, as the catalog
    /// spells it. Null for every other card - the species itself, its
    /// regional forms, Mega and G-Max, and every card in the search grid -
    /// which is exactly the test for "is this a skin or another Pokemon".
    /// See TargetSkinPicker.</summary>
    public string? SkinImagePath { get; }

    /// <summary>Backs a small type-icon row next to this card's name.</summary>
    public IReadOnlyList<string> Types => PokemonSpriteService.GetTypes(Name);

    [ObservableProperty] private bool isSelected;

    public PokemonCardItem(string name, Bitmap? sprite, string? tag = null, string? skinImagePath = null)
    {
        Name = name;
        Sprite = sprite;
        Tag = tag;
        SkinImagePath = skinImagePath;
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

    /// <summary>§219. Whether a card has been clicked yet. The forms panel
    /// moved from a strip under the grid to a column beside it, so it is on
    /// screen from the moment the window opens rather than appearing when
    /// there is something to put in it - which makes an empty column
    /// something the player sees, and something that should say why it is
    /// empty. LoadForms always adds at least one entry, so this is false
    /// only before the first click.</summary>
    [ObservableProperty] private bool hasForms;

    /// <summary>§272. The species the forms column is currently showing - the
    /// last card clicked in the search grid. A click in that column applies to
    /// THIS, not to the clicked card's own name, because a counterpart card is
    /// a picture of this species rather than another one.</summary>
    [ObservableProperty] private string formsSpecies = string.Empty;

    /// <summary>§272. What the last click in the forms column did. Its own line
    /// rather than StatusMessage's, which is drawn in the error brush - "drawn
    /// with its Summer sprite" is not an error, and a confirmation in red
    /// reads as one.</summary>
    [ObservableProperty] private string skinMessage = string.Empty;

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
        // §362: the outline means "this species is being hunted", so a card
        // matching an existing target wears it - but it is NOT a slot, and
        // nothing is added to SelectedCards here. Re-opening Set Target and
        // pressing Select without touching the column has always needed a
        // pick; that has not changed.
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
        // §362: a click here OPENS a species, it does not add it.
        //
        // §361 made every card in the right-hand column a target slot, but
        // left this one adding a slot too - so picking Charmander to see its
        // forms put a plain Charmander in the list, and the plain slot had to
        // be clicked off again before Halloween, Halloween and Shiny would
        // fit inside the three-per-species cap. Getting rid of something you
        // never asked for, before you can have what you did, is the wrong way
        // round.
        //
        // Every slot now comes from one place: the column on the right. The
        // grid says WHICH Pokemon that column is about. Hunting one with its
        // ordinary sprite is the Normal card there, one click further and
        // consistent with the other three.
        StatusMessage = null;

        LoadForms(card.Name);
        MarkSearchResults();
    }

    /// <summary>§362. A grid card is outlined when that species holds at
    /// least one slot - which is what the highlight can honestly mean now
    /// that the card itself is not a slot. Re-run after every change on
    /// either side, so the two columns never disagree about what is being
    /// hunted.</summary>
    private void MarkSearchResults()
    {
        foreach (PokemonCardItem card in SearchResults)
        {
            card.IsSelected = SelectedCards.Any(
                s => string.Equals(s.Name, card.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [RelayCommand]
    private void ConfirmCard(PokemonCardItem card)
    {
        // Multi-select: double-click just opens the same as a single click now -
        // immediately closing the dialog on double-click no longer makes sense
        // once more than one target can be picked, and since §362 a grid click
        // adds nothing to close the dialog ABOUT.
        SelectCard(card);
    }

    private void LoadForms(string pokemonName)
    {
        // §272: the column itself is built by TargetSkinPicker now, because
        // SwapPokemonWindow shows the same one. What stays here is only which
        // species it is showing and that it is no longer empty.
        AvailableForms.Clear();

        foreach (PokemonCardItem card in TargetSkinPicker.BuildForms(pokemonName))
        {
            AvailableForms.Add(card);
        }

        FormsSpecies = pokemonName;
        HasForms = AvailableForms.Count > 0;
    }

    /// <summary>§272. A click in the forms column. Before this section those
    /// cards were display only - the event skins were listed right there and
    /// clicking one did nothing. Now a counterpart card draws this target with
    /// that sprite, the Normal card puts it back, and a regional form says it
    /// is a Pokemon of its own rather than quietly doing nothing. The rule is
    /// TargetSkinPicker's, shared with the swap window.</summary>
    [RelayCommand]
    private void SelectSkin(PokemonCardItem card)
    {
        SkinMessage = TargetSkinPicker.Apply(
            FormsSpecies, card, AvailableForms, SelectedCards, MaxTargets);

        StatusMessage = null;
        MarkSearchResults();
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
}