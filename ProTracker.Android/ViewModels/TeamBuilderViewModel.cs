using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;
using PokemonSim.Data;
using PokemonSim.Models;
using PokemonSim.Simulation;

namespace ProTracker.Companion.ViewModels;

/// <summary>§314. One move on the species' learnset, and whether it is one of
/// the four. The cap is enforced by the builder rather than here, because a
/// checkbox that silently unticks itself needs somebody to say why.</summary>
public sealed partial class BuilderMoveChoice : ObservableObject
{
    public BuilderMoveChoice(string name, Action<BuilderMoveChoice> chosen)
    {
        Name = name;
        this.chosen = chosen;
    }

    private readonly Action<BuilderMoveChoice> chosen;

    public string Name { get; }

    [ObservableProperty] private bool isChosen;

    partial void OnIsChosenChanged(bool value)
    {
        if (value)
            chosen(this);
    }

    /// <summary>Unticks without telling the builder, which is what stops the
    /// cap check from recursing when it undoes a fifth pick.</summary>
    public void Untick() => SetProperty(ref isChosen, false, nameof(IsChosen));
}

/// <summary>
/// §314. Build a Pokemon out of the Pokedex, on the phone.
///
/// WHY THIS IS ANDROID-ONLY. The desktop builds a team from the player's own
/// game: §162 reads their cards with OCR and §198 keeps them in storage, and
/// both of those are a screen capture away from something the phone does not
/// have. §313 put the Simulator on a phone with the team read-only for
/// exactly that reason - it played whatever the desktop last saved. This is
/// the phone's own way in, and it lives in the companion project so the
/// desktop keeps its one source of truth about what a player actually owns.
///
/// IVS ARE THIRTY-ONE, ALWAYS. There is no control for them. A Pokedex entry
/// is a species, not a creature that was caught, so there is no IV to read -
/// and a simulator whose point is comparing builds is better served by the
/// spread everybody assumes than by six more boxes to fill in. ImportedPokemon
/// already defaults its six to 31, so this does not even set them; the
/// battery asserts that it does not, which is the honest way to keep a
/// default a default.
///
/// EVERYTHING ELSE IS THE PLAYER'S: four moves off the real learnset, any of
/// the twenty-five natures, any ability §307 recorded for that species, and
/// the EV spread, capped the way the game caps it.
/// </summary>
public sealed partial class TeamBuilderViewModel : ObservableObject
{
    /// <summary>The game's own two caps.</summary>
    public const int MaxEvPerStat = 252;
    public const int MaxEvTotal = 510;

    public const int MoveCount = 4;

    public TeamBuilderViewModel()
    {
        try
        {
            PokemonDex.EnsureLoaded();
        }
        catch (Exception ex)
        {
            // §316: SAID, not swallowed. This used to be an empty catch, and
            // the way it failed on a phone was every Pokemon having no
            // abilities and nothing anywhere saying why. The message is kept
            // and shown the moment a species is picked.
            dexError = ex.Message;
        }

        allSpecies = CalculatorDataService.AllSpeciesNames;

        // §316: ALL of them. There was a cap of 60 here, so the list stopped
        // dead at Beheeyem - the sixtieth name - and no amount of scrolling
        // went further, though searching past it worked. A ListBox virtualises
        // its rows, so eight hundred costs no more to show than sixty.
        foreach (string name in allSpecies)
            SpeciesMatches.Add(name);
    }

    private readonly IReadOnlyList<string> allSpecies;

    private readonly string? dexError;

    public ObservableCollection<string> SpeciesMatches { get; } = new();

    public ObservableCollection<BuilderMoveChoice> MoveChoices { get; } = new();

    public IReadOnlyList<string> NatureOptions { get; } = Enum.GetNames<Nature>();

    [ObservableProperty] private IReadOnlyList<string> abilityOptions = Array.Empty<string>();

    [ObservableProperty] private string speciesSearch = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string? selectedSpecies;

    [ObservableProperty] private string? selectedAbility;

    [ObservableProperty] private string? selectedNature = "Hardy";

    [ObservableProperty] private string moveSearch = string.Empty;

    [ObservableProperty] private string speciesLine = "Pick a Pokemon.";

    [ObservableProperty] private string status = string.Empty;

    /// <summary>§316. What the primary button says. Building calls it Add;
    /// editing one card calls it Save, because there is no team to add to -
    /// the card is already on it.</summary>
    [ObservableProperty] private string addLabel = "Add to the team";

    /// <summary>§316. True when this builder was opened on a card that
    /// already exists, by the team's Update button.</summary>
    public bool IsEditing { get; private set; }

    /// <summary>How many of these the Simulator can take: the team's free
    /// slots for Build from Pokedex, and exactly one for a card's Replace -
    /// §275's rule arriving here rather than being re-derived. Zero is a
    /// legal answer, and the page says so rather than refusing in silence.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(BasketLine))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private int capacity = 6;

    // The six, in ImportedPokemon's own order: HP, Attack, Defense,
    // Sp. Attack, Sp. Defense, Speed. Getting that order wrong would put a
    // Speed spread on Defense and nothing would ever say so.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EvSummary), nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string evHp = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EvSummary), nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string evAttack = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EvSummary), nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string evDefense = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EvSummary), nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string evSpAttack = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EvSummary), nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string evSpDefense = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EvSummary), nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string evSpeed = "0";

    /// <summary>What has been built so far and is waiting to be handed back.
    /// The page never holds this - it only pushes and pops - so a builder the
    /// hardware back button leaves still hands over what it had.</summary>
    private readonly List<ImportedPokemon> basket = new();

    public IReadOnlyList<ImportedPokemon> Basket => basket;

    public ObservableCollection<string> BasketNames { get; } = new();

    public string BasketLine =>
        IsEditing
            ? (basket.Count == 0
                ? "Change what you like, then Save. Back leaves the card alone."
                : "Saved. Go back to put it on the card.")
            : Capacity == 0
                ? "The team is full. Go back, remove a card, and come again."
                : basket.Count == 0
                    ? $"Room for {Capacity}. Build one and it waits here."
                    : $"{basket.Count} of {Capacity} built. Go back to put them on the team.";

    public int[] Evs => new[]
    {
        Ev(EvHp), Ev(EvAttack), Ev(EvDefense), Ev(EvSpAttack), Ev(EvSpDefense), Ev(EvSpeed),
    };

    public int EvTotal => Evs.Sum();

    public string EvSummary =>
        EvTotal > MaxEvTotal
            ? $"{EvTotal} of {MaxEvTotal} - {EvTotal - MaxEvTotal} too many"
            : $"{EvTotal} of {MaxEvTotal} spent, {MaxEvTotal - EvTotal} left";

    public int ChosenMoveCount => MoveChoices.Count(m => m.IsChosen);

    public bool CanAdd =>
        !string.IsNullOrWhiteSpace(SelectedSpecies)
        && EvTotal <= MaxEvTotal
        && basket.Count < Capacity;

    static int Ev(string text) =>
        int.TryParse(text, out int value) ? Math.Clamp(value, 0, MaxEvPerStat) : 0;

    partial void OnSpeciesSearchChanged(string value) => RebuildSpecies();

    partial void OnMoveSearchChanged(string value) => RebuildMoves();

    partial void OnSelectedSpeciesChanged(string? value)
    {
        // §316: clear FIRST. RebuildAbilities has something to say when the
        // list comes back empty, and clearing after it would wipe the one
        // message that explains the thing the player is looking at.
        Status = string.Empty;

        // §316: a new species means a new learnset, so the four moves picked
        // for the old one are dropped - they are almost never legal on it.
        MoveChoices.Clear();

        RebuildMoves();
        RebuildAbilities();

        CalculatorDataService.CalcSpecies? species = CalculatorDataService.Find(value);

        SpeciesLine = species is null
            ? "Pick a Pokemon."
            : $"{string.Join(" / ", species.Types)}  -  "
              + $"{species.BaseHp}/{species.BaseAttack}/{species.BaseDefense}/"
              + $"{species.BaseSpAttack}/{species.BaseSpDefense}/{species.BaseSpeed}";
    }

    private void RebuildSpecies()
    {
        string needle = (SpeciesSearch ?? string.Empty).Trim();

        IEnumerable<string> hits = needle.Length == 0
            ? allSpecies
            : allSpecies.Where(n => n.Contains(needle, StringComparison.OrdinalIgnoreCase));

        SpeciesMatches.Clear();

        foreach (string name in hits)
            SpeciesMatches.Add(name);
    }

    /// <summary>§307's abilities, off the ENGINE's dex. calc-pokedex.json has
    /// an abilities field of its own and 743 of its 804 species leave it
    /// empty; pokedex.json is the one §307 filled, and it is the one the
    /// battle actually reads.</summary>
    private void RebuildAbilities()
    {
        IReadOnlyList<string> abilities;

        try
        {
            abilities = PokemonDex.AbilitiesOf(SelectedSpecies);
        }
        catch
        {
            abilities = Array.Empty<string>();
        }

        // §316: an ability the dex does not list - one carried by a card
        // built before the data landed - is kept rather than dropped, so
        // editing a Pokemon never silently strips it.
        string? keep = SelectedAbility;

        if (!string.IsNullOrWhiteSpace(keep)
            && !abilities.Contains(keep, StringComparer.OrdinalIgnoreCase))
        {
            var widened = new List<string>(abilities) { keep! };
            abilities = widened;
        }

        AbilityOptions = abilities;
        SelectedAbility = keep is not null && abilities.Contains(keep, StringComparer.OrdinalIgnoreCase)
            ? keep
            : abilities.Count > 0 ? abilities[0] : null;

        // §316: an empty list is a fact about the DATA, not about the
        // Pokemon, and it now says so where the player is looking.
        if (abilities.Count == 0)
        {
            Status = dexError is null
                ? "No abilities listed for this Pokemon - the Pokedex data has not been unpacked on this device."
                : "The Pokedex did not load, so abilities are unavailable: " + dexError;
        }
    }

    /// <summary>The learnset, filtered. Ticks survive the filter because the
    /// choices are rebuilt from the ones already chosen - a move that scrolls
    /// out of sight has not been unpicked.</summary>
    private void RebuildMoves()
    {
        var chosen = MoveChoices.Where(m => m.IsChosen)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        CalculatorDataService.CalcSpecies? species = CalculatorDataService.Find(SelectedSpecies);

        IEnumerable<string> learnset = species?.LearnsetMoveNames ?? Array.Empty<string>();

        string needle = (MoveSearch ?? string.Empty).Trim();

        if (needle.Length > 0)
            learnset = learnset.Where(n => n.Contains(needle, StringComparison.OrdinalIgnoreCase));

        MoveChoices.Clear();

        foreach (string name in learnset)
        {
            var choice = new BuilderMoveChoice(name, OnMoveChosen);

            if (chosen.Contains(name))
                choice.IsChosen = true;

            MoveChoices.Add(choice);
        }
    }

    /// <summary>Four is four. A fifth pick is undone rather than swapped,
    /// because guessing which of the four to drop is worse than saying no.
    /// </summary>
    private void OnMoveChosen(BuilderMoveChoice choice)
    {
        if (ChosenMoveCount <= MoveCount)
        {
            Status = string.Empty;
            return;
        }

        choice.Untick();
        Status = $"Four moves. Untick one before picking {choice.Name}.";
    }

    /// <summary>
    /// §314. The card, in the shape the Simulator's own import path takes -
    /// so the view model that receives it cannot tell a phone-built Pokemon
    /// from an OCR'd one, and does not have to.
    ///
    /// The IVs are NOT set: ImportedPokemon's own default is six thirty-ones,
    /// and re-stating it here would be a second place for it to be wrong.
    /// </summary>
    public ImportedPokemon? Build()
    {
        if (!CanAdd)
            return null;

        var card = new ImportedPokemon
        {
            SpeciesName = SelectedSpecies!,
            Level = 100,
            NatureName = string.IsNullOrWhiteSpace(SelectedNature) ? "Hardy" : SelectedNature!,
            AbilityName = SelectedAbility,
            MoveNames = MoveChoices.Where(m => m.IsChosen).Select(m => m.Name).ToList(),
        };

        int[] evs = Evs;

        for (int i = 0; i < card.Evs.Length && i < evs.Length; i++)
            card.Evs[i] = evs[i];

        return card;
    }

    /// <summary>Put the current build in the basket. The form is left exactly
    /// as it is afterwards, because the next Pokemon is usually the same one
    /// with two numbers moved.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        ImportedPokemon? card = Build();

        if (card is null)
            return;

        basket.Add(card);
        BasketNames.Add($"{card.SpeciesName} - {card.NatureName}, {card.MoveNames.Count} moves");

        Status = $"{card.SpeciesName} built.";

        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(BasketLine));
        AddCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// §316. Open on a Pokemon that already exists, for the team's Update
    /// button: every control starts where that card is, so an edit is a
    /// change to one thing rather than a rebuild from nothing.
    ///
    /// Capacity is one and the button says Save - there is no team to add to,
    /// because the card is already on it.
    /// </summary>
    public void LoadFrom(ImportedPokemon card)
    {
        IsEditing = true;
        Capacity = 1;
        AddLabel = "Save changes";

        // The ability is set BEFORE the species, so RebuildAbilities finds it
        // already there and keeps it even if the dex does not list it.
        SelectedAbility = card.AbilityName;

        // Setting the species builds this card's learnset; its own four are
        // ticked below, once there is something to tick.
        SelectedSpecies = card.SpeciesName;

        SelectedNature = string.IsNullOrWhiteSpace(card.NatureName) ? "Hardy" : card.NatureName;

        var wanted = new HashSet<string>(card.MoveNames, StringComparer.OrdinalIgnoreCase);

        foreach (BuilderMoveChoice choice in MoveChoices)
        {
            if (wanted.Contains(choice.Name))
                choice.IsChosen = true;
        }

        int[] evs = card.Evs;

        EvHp = Box(evs, 0);
        EvAttack = Box(evs, 1);
        EvDefense = Box(evs, 2);
        EvSpAttack = Box(evs, 3);
        EvSpDefense = Box(evs, 4);
        EvSpeed = Box(evs, 5);

        OnPropertyChanged(nameof(BasketLine));

        static string Box(int[] source, int index) =>
            index < source.Length ? source[index].ToString() : "0";
    }

}
