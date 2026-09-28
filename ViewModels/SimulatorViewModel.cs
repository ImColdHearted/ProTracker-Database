using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Foot_Tracker.Services.Simulator;
using PokemonSim.Actions;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Items;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using PokemonSim.Shadow;
using PokemonSim.Simulation;
using Serilog;

namespace Foot_Tracker.ViewModels;

/// <summary>§372. One row of a team card's stat table: the stat's short
/// name, its IV and its EV, in the card's own row order. §374: and what
/// the nature does to it - Boosted for the +10% stat, Hindered for the
/// -10% one, neither for the other four and for HP, which no nature
/// touches. The card draws the name green or orange from these
/// (SimulatorWindow.axaml's boosted/hindered styles). §376: the numbers
/// themselves are coloured as PRO's card colours them - every IV orange,
/// every EV blue - so the row no longer says whether an EV is invested;
/// the colour is the column's, not the value's.</summary>
public sealed record SimulatorStatRow(string Label, int Iv, int Ev, bool Boosted, bool Hindered);

/// <summary>§374. One line of a team card's move list. A plain move is its
/// name and nothing else. Hidden Power is the exception: its type is part
/// of what the move is, and "Hidden Power (Fighting)" does not fit the
/// column at any width the card can have, so the line carries the type
/// separately - the name is drawn in that type's colour
/// (PokemonTypeColors) with the bracket dropped, and the whole thing,
/// bracket included, is the tooltip.</summary>
public sealed record SimulatorMoveRow(string Name, string? HiddenPowerType, bool TypeFromIvs = false)
{
    public bool IsHiddenPower => HiddenPowerType != null;

    /// <summary>The type's colour - only read for a Hidden Power line.</summary>
    public Color TypeColor => PokemonTypeColors.For(HiddenPowerType);

    /// <summary>The full name the colour stands in for, and where the type
    /// came from when the card did not print it.</summary>
    public string? Tip => HiddenPowerType == null
        ? null
        : TypeFromIvs
            ? $"{Name} ({HiddenPowerType}) - the type the IVs give it"
            : $"{Name} ({HiddenPowerType})";
}

/// <summary>§154/§157. One slot of the Simulator's team builder - top-level
/// (not nested) so the window's compiled DataTemplates stay plain, same as
/// AdminReplayItem. §157: the species is picked through the same
/// PokemonSelectorWindow as Set Target (the sprite box is the button), the
/// in-card search list is gone, and everything battles at level 100 - PRO
/// PvP and bosses are level 100, so the level box earned nothing.</summary>
public sealed partial class SimulatorSlotViewModel : ViewModelBase
{
    private readonly ISimulatorSpriteProvider sprites;

    /// <summary>§372. The card's row order - Atk, Def, Spe, SpA, SpD, HP -
    /// as indexes into the HP-first arrays the import carries. The same
    /// order CardOrderSpread has always used; the table just lays it out
    /// as rows instead of a line. §374: with the stat's name as
    /// PokemonBattleMath.NatureCatalog spells it, so each row can ask the
    /// nature what it does to it - null for HP, which no nature touches.</summary>
    private static readonly (string Label, int Index, string? Stat)[] CardStatOrder =
    {
        ("Atk", 1, "Attack"), ("Def", 2, "Defense"), ("Spe", 5, "Speed"),
        ("SpA", 3, "SpAttack"), ("SpD", 4, "SpDefense"), ("HP", 0, null),
    };

    /// <summary>§374. "Hidden Power", with or without the type the card
    /// prints after it - CardImport writes it as "Hidden Power (Ice)" when
    /// the card's own word was in the move data, and as plain "Hidden
    /// Power" when it was not. Square brackets as well, since that is how
    /// the game itself prints it and a storage entry could carry either.</summary>
    private static readonly Regex HiddenPowerName = new(
        @"^\s*Hidden\s+Power\s*(?:[\(\[]\s*([A-Za-z]+)\s*[\)\]])?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>§162: the Pokemon this slot carries - always one read off
    /// the player's own game (a fresh scan or a storage pick). The slot
    /// shows it read-only: teams are built from what the player actually
    /// owns, and only the held item is chosen here (the summary card does
    /// not show items).</summary>
    public ImportedPokemon? Imported { get; private set; }

    [ObservableProperty] private Bitmap? sprite;
    [ObservableProperty] private string headline = "Empty slot";
    [ObservableProperty] private string movesLine = "";
    [ObservableProperty] private string spreadLine = "";
    [ObservableProperty] private string noteLine = "";
    [ObservableProperty] private bool hasNotes;

    // §372: the card as the mockup draws it - a stat table and a move list
    // where one line of each used to be, and a record in the corner.
    // §374: the move list is rows rather than names, so Hidden Power can
    // carry its type (see SimulatorMoveRow).
    [ObservableProperty] private IReadOnlyList<SimulatorStatRow> statRows = Array.Empty<SimulatorStatRow>();
    [ObservableProperty] private IReadOnlyList<SimulatorMoveRow> moves = Array.Empty<SimulatorMoveRow>();

    /// <summary>§374. The species' types, for the icon row under the sprite
    /// - the same TypeIconConverter row every other sprite in the app has,
    /// from the same lookup (PokemonSpriteService.GetTypes).</summary>
    [ObservableProperty] private IReadOnlyList<string> types = Array.Empty<string>();

    /// <summary>§372. Finished battles this Pokemon was on the team for.
    /// Read from storage when the card is filled and again after every
    /// battle ends (RefreshRecord) - the store is the record, this is the
    /// display.</summary>
    [ObservableProperty] private int wins;
    [ObservableProperty] private int losses;

    // §159: the held item, picked through the item picker dialog.
    [ObservableProperty] private string? selectedItemName;
    [ObservableProperty] private Bitmap? itemSprite;
    [ObservableProperty] private string itemButtonText = "No Item";

    public SimulatorSlotViewModel(ISimulatorSpriteProvider sprites)
    {
        this.sprites = sprites;
    }

    public void Apply(ImportedPokemon imported)
    {
        Imported = imported;

        Headline = (imported.IsShiny ? "Shiny " : "") +
                   $"{imported.SpeciesName}  Lv. {imported.Level}  {imported.NatureName}" +
                   (string.IsNullOrWhiteSpace(imported.AbilityName) ? "" : $"  -  {imported.AbilityName}");

        MovesLine = imported.MoveNames.Count > 0
            ? string.Join("  /  ", imported.MoveNames)
            : "(no moves)";

        // §167: the card's own row order, same as the import preview.
        SpreadLine = imported.CardOrderSpread;

        NoteLine = string.Join("  ", imported.Notes);
        HasNotes = imported.Notes.Count > 0;

        // §372: the same six numbers as SpreadLine, as rows. Guarded on the
        // array lengths the way ToImported guards them, so a malformed
        // entry shows an empty table rather than throwing at the binding.
        // §374: each row also knows what the nature does to it, from the
        // same table the calculators use - 1.1 for the raised stat, 0.9 for
        // the lowered one, 1.0 for the rest, for HP, and for a nature name
        // the table does not know (a misread), which colours nothing rather
        // than guessing.
        StatRows = imported.Ivs.Length == 6 && imported.Evs.Length == 6
            ? CardStatOrder.Select(o => new SimulatorStatRow(
                    o.Label,
                    imported.Ivs[o.Index],
                    imported.Evs[o.Index],
                    Boosted: o.Stat != null && PokemonBattleMath.GetNatureMultiplier(imported.NatureName, o.Stat) > 1.0,
                    Hindered: o.Stat != null && PokemonBattleMath.GetNatureMultiplier(imported.NatureName, o.Stat) < 1.0))
                .ToList()
            : Array.Empty<SimulatorStatRow>();

        Moves = imported.MoveNames.Count > 0
            ? imported.MoveNames.Select(name => BuildMoveRow(name, imported.Ivs)).ToList()
            : new List<SimulatorMoveRow> { new("(no moves)", null) };

        Types = PokemonSpriteService.GetTypes(imported.SpeciesName);

        RefreshRecord();

        _ = LoadSpriteAsync(imported.SpeciesName, imported.IsShiny);
    }

    /// <summary>§374. One move as the card lists it. Anything but Hidden
    /// Power is its name. Hidden Power gets its type: the one the card
    /// printed in the bracket when there was one, otherwise the one the
    /// IVs give it - the same Gen 3+ formula the IV calculator shows, which
    /// is how the game decides it - so the colour is there either way.
    /// The bracket itself is dropped from the name; the colour says it,
    /// and the tooltip spells it out.</summary>
    internal static SimulatorMoveRow BuildMoveRow(string name, int[] ivs)
    {
        Match match = HiddenPowerName.Match(name ?? string.Empty);

        if (!match.Success)
            return new SimulatorMoveRow(name ?? string.Empty, null);

        if (match.Groups[1].Success)
        {
            string printed = match.Groups[1].Value;
            string typed = char.ToUpperInvariant(printed[0]) + printed.Substring(1).ToLowerInvariant();

            return new SimulatorMoveRow("Hidden Power", typed);
        }

        // GetHiddenPowerType wants HP, Atk, Def, SPEED, SpA, SpD; the import
        // carries HP, Atk, Def, SpA, SpD, Speed (see ImportedPokemon).
        if (ivs is { Length: 6 })
        {
            string fromIvs = PokemonBattleMath.GetHiddenPowerType(
                new[] { ivs[0], ivs[1], ivs[2], ivs[5], ivs[3], ivs[4] });

            if (PokemonTypeColors.TryGet(fromIvs, out _))
                return new SimulatorMoveRow("Hidden Power", fromIvs, TypeFromIvs: true);
        }

        return new SimulatorMoveRow("Hidden Power", null);
    }

    /// <summary>§372. Re-reads this card's record from storage.</summary>
    public void RefreshRecord()
    {
        if (Imported == null)
        {
            Wins = 0;
            Losses = 0;
            return;
        }

        (Wins, Losses) = SimulatorPokemonStorage.RecordFor(Imported);
    }

    private async Task LoadSpriteAsync(string species, bool shiny)
    {
        Bitmap? bitmap = await sprites.GetSpriteAsync(species, shiny);

        if (Imported?.SpeciesName == species)
            Sprite = bitmap;
    }

    partial void OnSelectedItemNameChanged(string? value)
    {
        ItemButtonText = string.IsNullOrWhiteSpace(value) ? "No Item" : value;
        ItemSprite = ItemSpriteService.GetSprite(value);
    }

    public TeamSlotPlan ToPlan()
    {
        ImportedPokemon imported = Imported ?? new ImportedPokemon();

        var plan = new TeamSlotPlan
        {
            SpeciesName = imported.SpeciesName,
            // §162: the card's real level - a lv. 74 import battles at 74.
            Level = imported.Level,
            Nature = Enum.TryParse(imported.NatureName, ignoreCase: true, out Nature nature) ? nature : Nature.Hardy,
            AbilityName = imported.AbilityName,
            // §159: the held item rides the plan into TeamBuilder.
            ItemName = SelectedItemName,
            // §162: the card is authoritative - real IVs/EVs, trusted
            // moves and ability (see TeamBuilder).
            Ivs = imported.Ivs.ToArray(),
            Evs = imported.Evs.ToArray(),
            Trusted = true,
            // §164: the golden S badge - the battle shows shiny artwork.
            IsShiny = imported.IsShiny
        };

        plan.MoveNames.AddRange(imported.MoveNames);

        return plan;
    }
}

/// <summary>§157. One slot of the battle view's team row - all of the
/// player's Pokemon as clickable sprites, Showdown style. Clicking a
/// legal teammate switches to it (or sends it in after a faint); the
/// active one is highlighted, fainted ones fade out.</summary>
public sealed partial class SimulatorTeamSlotItem : ObservableObject
{
    public required int TeamIndex { get; init; }
    public required string Species { get; init; }

    [ObservableProperty] private Bitmap? sprite;
    [ObservableProperty] private string hpTip = "";
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private double spriteOpacity = 1.0;
    [ObservableProperty] private bool canSwitch;

    // §160: the held item, for the switch-confirmation card.
    [ObservableProperty] private string itemText = "";
    [ObservableProperty] private Bitmap? itemSprite;
}

/// <summary>§167 (was §155/§157's one-row-per-fight). One row of the boss
/// opponent list: ONE boss (or one named combatant of a dual-boss file),
/// with its available difficulties as in-row picks - the list reads like
/// the Boss Database instead of tripling every name. Picking a difficulty
/// makes this row the chosen opponent; a difficulty the boss does not
/// have in PRO stays disabled. §157 still holds: portrait + name only.</summary>
public sealed class SimulatorBossGroup : ViewModelBase
{
    private readonly Action<SimulatorBossGroup> picked;
    private string selectedKey;

    public SimulatorBossGroup(
        string title, Bitmap? portrait,
        SimulatorBossFight? easy, SimulatorBossFight? medium, SimulatorBossFight? hard,
        Action<SimulatorBossGroup> picked)
    {
        this.picked = picked;
        Title = title;
        Portrait = portrait;
        EasyFight = easy;
        MediumFight = medium;
        HardFight = hard;
        selectedKey = easy != null ? "easy" : medium != null ? "medium" : "hard";
    }

    public string Title { get; }
    public Bitmap? Portrait { get; }
    public SimulatorBossFight? EasyFight { get; }
    public SimulatorBossFight? MediumFight { get; }
    public SimulatorBossFight? HardFight { get; }

    public bool HasEasy => EasyFight != null;
    public bool HasMedium => MediumFight != null;
    public bool HasHard => HardFight != null;

    /// <summary>The fight the row currently stands for - its default is
    /// the easiest difficulty the boss has.</summary>
    public SimulatorBossFight SelectedFight => selectedKey switch
    {
        "easy" => EasyFight!,
        "medium" => MediumFight!,
        _ => HardFight!
    };

    // Radio bindings. A radio group would leak across rows, so exclusivity
    // lives here: only a true write on an available difficulty moves the
    // selection, and every write re-raises all three.
    public bool EasyChecked
    {
        get => selectedKey == "easy";
        set => Pick(value, HasEasy, "easy");
    }

    public bool MediumChecked
    {
        get => selectedKey == "medium";
        set => Pick(value, HasMedium, "medium");
    }

    public bool HardChecked
    {
        get => selectedKey == "hard";
        set => Pick(value, HasHard, "hard");
    }

    private void Pick(bool value, bool available, string key)
    {
        if (!value || !available)
            return;

        selectedKey = key;

        OnPropertyChanged(nameof(EasyChecked));
        OnPropertyChanged(nameof(MediumChecked));
        OnPropertyChanged(nameof(HardChecked));

        picked(this);
    }
}

/// <summary>
/// §154/§155/§156/§157. Backs SimulatorWindow: the team-builder setup view
/// (§155: with the Boss Database as an opponent list; §156: with the
/// passive battle observer and its shadow model; §157: species picked
/// through the Set Target window, everything at level 100, the boss list
/// pared to names, the lab UI retired) and the battle view - now with a
/// clickable team row - over a PokemonSim SimulatorSession. Everything battle-heavy runs
/// through the session off the UI thread; this class only reads state
/// after a turn completes, so no binding ever races the engine. Species
/// come from the tracker's own calc-pokedex (TrackerSpeciesSource), moves
/// from the engine's MoveDex, sprites from the tracker's sprite service
/// behind ISimulatorSpriteProvider. Nothing here touches hunting data, and
/// the engine assembly could not even if this class wanted it to - it has
/// no reference to the tracker.
/// </summary>
public sealed partial class SimulatorViewModel : ViewModelBase, IDisposable
{
    private readonly ISpeciesSource speciesSource = new TrackerSpeciesSource();
    private readonly ISimulatorSpriteProvider spriteProvider = new TrackerSpriteProvider();

    private SimulatorSession? session;

    /// <summary>§183: the book is written once per battle, on the turn it
    /// ends, not on every refresh that runs after it.</summary>
    private bool memoryWritten;
    private CancellationTokenSource? battleCts;
    private List<SimulatorBossGroup> allBossGroups = new();

    /// <summary>§157: opens the Set Target Pokemon picker and returns the
    /// chosen name (null when cancelled). Supplied by SimulatorWindow,
    /// which owns the dialog.</summary>
    /// <summary>§162: the window opens the screenshot importer and hands
    /// back what it scanned. §198: the importer no longer closes on Add,
    /// so this hands back EVERY Pokemon added while it was open (an empty
    /// list = nothing was added). The argument is how many team slots are
    /// free, which the dialog uses to say where each one landed.</summary>
    public Func<int, Task<IReadOnlyList<ImportedPokemon>>>? RequestImport { get; set; }

    /// <summary>§162: the window opens the Pokemon storage picker. §203:
    /// it stays open across picks now, so this hands back everything chosen
    /// while it was up (an empty list = nothing was). The argument asks for
    /// the multi-pick window; Replace from Storage passes false and gets a
    /// window that closes on the first click.
    ///
    /// §275: the second argument is how many team slots are free, so the
    /// picker can refuse a pick that would not fit AT THE CLICK instead of
    /// banking it and dropping it on the way out. The single-pick path
    /// passes it too and the picker ignores it there - a swap needs no free
    /// slot, which is the whole point of a swap.</summary>
    public Func<bool, int, Task<IReadOnlyList<ImportedPokemon>>>? RequestStoragePick { get; set; }

    /// <summary>§173: the window opens the custom opponent editor - null
    /// starts a new one. True back means something was saved, so the
    /// list reloads.</summary>
    public Func<CustomBossEntry?, Task<bool>>? RequestCustomBossEdit { get; set; }

    /// <summary>§159: opens the held-item picker. Picked=false means the
    /// dialog was cancelled and the slot keeps its current item; a null
    /// ItemName with Picked=true is the No Item choice.</summary>
    public Func<Task<(bool Picked, string? ItemName)>>? RequestItemPick { get; set; }

    /// <summary>§160: confirms a team-row click before it becomes a switch
    /// (true = go ahead). The second argument is true when this is a
    /// send-in after a faint. Supplied by SimulatorWindow.</summary>
    public Func<SimulatorTeamSlotItem, bool, Task<bool>>? RequestSwitchConfirm { get; set; }

    // §156: the passive observer. One BattleObserver per observed battle;
    // the shadow evaluator loads once per window and is shared read-only.
    private BattleObserver? observer;
    private IShadowEvaluator? shadowEvaluator;
    private ObserverSettings observerSettings = new();

    // §155: ONE strategy family and configuration governs every boss -
    // the completed Monte Carlo brain at its conservative defaults. Each
    // battle still gets its own instance (fresh per-battle state, own
    // seed), so nothing mutable is shared between fights.
    private static readonly MonteCarloConfig BossBrainConfig = new();

    private int consumedLogLines;
    private string playerSpriteFor = string.Empty;
    private string opponentSpriteFor = string.Empty;

    // §201: the scene's own two, tracked separately because they are
    // different pictures - the player's is the back sprite, and both are
    // cropped to their feet.
    private string playerSceneFor = string.Empty;
    private string opponentSceneFor = string.Empty;

    private bool battleSceneLoaded;

    // §203: true while the saved team is being put back, so restoring it
    // does not immediately write what was just read.
    private bool restoringTeam;

    // §201. The stadium field's own pixel size. Every position below is in
    // ITS coordinates, and the view scales the whole canvas as one, so the
    // Pokemon stay on their pads at any window size.
    public const double FieldWidth = 937;
    public const double FieldHeight = 755;

    // Where each Pokemon's feet land, and how much bigger than its 96x96
    // library canvas it is drawn. The near one is larger because it is
    // nearer; these five numbers are the whole of the composition, so
    // nudging the artwork is nudging them.
    private const double OpponentFeetX = 640;
    private const double OpponentFeetY = 250;
    private const double OpponentScale = 2.4;
    private const double PlayerFeetX = 315;
    private const double PlayerFeetY = 660;
    private const double PlayerScale = 2.8;

    // ---- §217: move animations ----

    /// <summary>§217. What the effect layer should be drawing. The window
    /// binds it; the view model sets one request at a time and waits for
    /// each to finish before setting the next.</summary>
    [ObservableProperty] private BattleEffectRequest? currentEffect;

    /// <summary>§372. The animation speed dropdown is gone from the battle
    /// header and every move plays at what used to be its Fast setting.
    /// §217's four choices were Normal 1.00, Fast 0.55, Slow 1.60 and Off;
    /// this is the one that was kept. Off is not offered any more - the
    /// pictures are short at this speed and the turn resolves the same
    /// either way.</summary>
    private const double AnimationFactor = 0.55;

    /// <summary>§217. Everything one picture needs, resolved at the moment
    /// the move was announced. Deliberately NOT a reference to the
    /// PokemonState: the engine goes on mutating those, and by the time the
    /// animation plays the attacker may have fainted and been replaced.</summary>
    private sealed record PendingAnimation(
        MoveAnimationSpec Spec, EffectPalette Palette, bool ByPlayer, int Seed);

    private readonly List<PendingAnimation> pendingAnimations = new();

    private readonly object animationGate = new();

    // ---- mode ----

    [ObservableProperty] private bool inBattle;

    /// <summary>§203: the one leave button's label - it cancels a battle
    /// that is still going and simply leaves one that is over.</summary>
    [ObservableProperty] private string leaveBattleText = "Cancel Battle";

    // ---- §201: the stadium scene ----

    /// <summary>Whether the battle draws on the field or falls back to the
    /// two bordered panels. Persisted through UiPreferences.</summary>
    [ObservableProperty] private bool battleSceneEnabled = true;

    [ObservableProperty] private Bitmap? battleFieldImage;

    /// <summary>§201: whether the scene is what the battle draws. Both the
    /// switch AND the asset have to be there - Pokemon standing on nothing
    /// is worse than the panels - so the two views ask one bool each rather
    /// than the view combining conditions.</summary>
    [ObservableProperty] private bool battleSceneVisible;

    [ObservableProperty] private bool battlePanelsVisible = true;

    [ObservableProperty] private Bitmap? sceneOpponentSprite;
    [ObservableProperty] private double sceneOpponentWidth;
    [ObservableProperty] private double sceneOpponentHeight;
    [ObservableProperty] private Thickness sceneOpponentMargin;

    [ObservableProperty] private Bitmap? scenePlayerSprite;
    [ObservableProperty] private double scenePlayerWidth;
    [ObservableProperty] private double scenePlayerHeight;
    [ObservableProperty] private Thickness scenePlayerMargin;

    // ---- setup view ----

    public ObservableCollection<SimulatorSlotViewModel> TeamSlots { get; } = new();

    /// <summary>§198: how many of the six are still empty. The importer is
    /// told this when it opens so it can say whether the next Add joins the
    /// team or waits in storage.</summary>
    public int FreeTeamSlots => Math.Max(0, TeamBuilder.MaxTeamSize - TeamSlots.Count);

    /// <summary>§275. A full team's size, exposed so the View can tell the
    /// storage picker what number to put in its refusal. Read from
    /// TeamBuilder rather than written out as six, the same as every other
    /// use of it in this file.</summary>
    public int MaxTeamSize => TeamBuilder.MaxTeamSize;

    [ObservableProperty] private string setupStatus =
        "Import Pokemon from your game (1-6, screenshots of their summary cards), pick an opponent on the right, then press Start Battle.";

    [ObservableProperty] private bool dataReady;

    // ---- §155: the opponent picker ----

    [ObservableProperty] private bool useRandomOpponent = true;
    [ObservableProperty] private bool useBossOpponent;

    // §173: the player's own opponents, one .json each.
    [ObservableProperty] private bool useCustomOpponent;
    [ObservableProperty] private CustomBossEntry? selectedCustomBoss;
    [ObservableProperty] private string customBossNote = string.Empty;

    public ObservableCollection<CustomBossEntry> CustomBossChoices { get; } = new();

    [ObservableProperty] private string bossSearch = string.Empty;
    [ObservableProperty] private SimulatorBossGroup? selectedBoss;
    [ObservableProperty] private string bossListNote = "Loading the Boss Database...";

    public ObservableCollection<SimulatorBossGroup> BossChoices { get; } = new();

    // ---- §156: the observer (a passive recorder - it never chooses,
    //      never mutates, never delays; see PokemonSim.Observation) ----

    [ObservableProperty] private bool observerEnabled;
    [ObservableProperty] private bool observerActive;
    [ObservableProperty] private string observerCountText = "Recorded: counting...";
    [ObservableProperty] private string observerPathText = ObservationStore.Root;
    [ObservableProperty] private string observerStatusText = "Observer off.";
    [ObservableProperty] private string observerBattleText = string.Empty;
    [ObservableProperty] private string clearObservationsLabel = "Clear Observation Data";
    private bool clearObservationsArmed;
    private bool observerSettingsLoaded;

    // ---- battle view ----

    [ObservableProperty] private string battleTitle = string.Empty;
    [ObservableProperty] private string turnText = string.Empty;
    [ObservableProperty] private string weatherText = "Weather: none";
    [ObservableProperty] private string terrainText = "Terrain: none";

    [ObservableProperty] private string playerHeader = string.Empty;
    [ObservableProperty] private string playerHpText = string.Empty;
    [ObservableProperty] private string playerStatusText = string.Empty;
    [ObservableProperty] private int playerHp;
    [ObservableProperty] private int playerMaxHp = 1;
    [ObservableProperty] private Bitmap? playerSprite;

    [ObservableProperty] private string opponentHeader = string.Empty;
    [ObservableProperty] private string opponentHpText = string.Empty;
    [ObservableProperty] private string opponentStatusText = string.Empty;

    // §159: the actives' held items ("@ Leftovers"), sprite and text.
    [ObservableProperty] private string playerItemText = string.Empty;
    [ObservableProperty] private string opponentItemText = string.Empty;
    [ObservableProperty] private Bitmap? playerItemSprite;
    [ObservableProperty] private Bitmap? opponentItemSprite;

    // §161: the Mega Evolve / Z-Power toggles beside the move buttons.
    // "Available" shows the toggle at all; "Armed" is the player's intent
    // for the next move click. The engine re-checks legality when the
    // turn resolves, so a stale toggle can never force anything illegal.
    [ObservableProperty] private bool megaAvailable;
    [ObservableProperty] private bool megaArmed;
    [ObservableProperty] private bool zAvailable;
    [ObservableProperty] private bool zArmed;
    [ObservableProperty] private int opponentHp;
    [ObservableProperty] private int opponentMaxHp = 1;
    [ObservableProperty] private Bitmap? opponentSprite;

    public SimulatorMoveButton MoveButton1 { get; } = new();
    public SimulatorMoveButton MoveButton2 { get; } = new();
    public SimulatorMoveButton MoveButton3 { get; } = new();
    public SimulatorMoveButton MoveButton4 { get; } = new();

    // §157: the whole team as a clickable sprite row, Showdown style.
    public ObservableCollection<SimulatorTeamSlotItem> BattleTeam { get; } = new();

    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private bool isResolving;
    [ObservableProperty] private bool mustReplace;
    [ObservableProperty] private bool battleOver;
    [ObservableProperty] private string resultText = string.Empty;

    public SimulatorViewModel()
    {
        // §201: the scene, and whether the player wants it. A preferences
        // file from before §201 has no key and loads as true.
        try
        {
            BattleSceneEnabled = UiPreferencesService.Load().BattleSceneEnabled;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Simulator: the battle scene preference could not be read.");
        }

        battleSceneLoaded = true;

        BattleFieldImage = BattleSpriteService.LoadField();
        RefreshBattleSceneVisibility();

        // §203: the team survives closing the app. Every change to the
        // slots writes it back; the held item is the one thing a slot
        // carries that is chosen here rather than read off a summary card,
        // so PickItem saves too.
        RestoreTeam();

        TeamSlots.CollectionChanged += (_, _) => SaveTeam();

        // §162: slots exist only for imported Pokemon - the builder starts
        // empty and fills through Import / From Storage.

        // The engine's move data loads once, off the UI thread; species data
        // is the calculator's and lazy-loads the same way it always has.
        _ = Task.Run(() =>
        {
            string? problem = null;

            try
            {
                MoveDex.EnsureLoaded();

                if (MoveDex.Count == 0)
                    problem = "The battle engine's move data (PokemonSimData/moves.json) could not be loaded - the Simulator cannot start a battle.";
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Simulator: move data failed to load.");
                problem = "The battle engine's move data could not be loaded - see today's log.";
            }

            // Bound properties change on the UI thread only.
            Dispatcher.UIThread.Post(() =>
            {
                DataReady = problem == null;

                if (problem != null)
                    SetupStatus = problem;
            });
        });

        // §155: the Boss Database - 49 files, ~130 fights, ~50 portraits -
        // reads and decodes off the UI thread, then lands in one post.
        // §167: fights arrive per difficulty and are folded into one row
        // per boss (per named combatant for the dual-boss files), with the
        // difficulties as in-row picks.
        _ = Task.Run(() =>
        {
            var groups = new List<SimulatorBossGroup>();
            var problems = new List<string>();
            int fightCount = 0;

            try
            {
                (List<SimulatorBossFight> fights, problems) = BossOpponentSource.LoadAll();
                fightCount = fights.Count;

                var portraits = new Dictionary<string, Bitmap?>(StringComparer.OrdinalIgnoreCase);
                var order = new List<string>();
                var byBoss = new Dictionary<string, List<SimulatorBossFight>>(StringComparer.OrdinalIgnoreCase);

                foreach (SimulatorBossFight fight in fights)
                {
                    string key = fight.BossId + "|" + (fight.NpcName ?? string.Empty);

                    if (!byBoss.TryGetValue(key, out List<SimulatorBossFight>? list))
                    {
                        list = new List<SimulatorBossFight>();
                        byBoss[key] = list;
                        order.Add(key);
                    }

                    list.Add(fight);
                }

                foreach (string key in order)
                {
                    List<SimulatorBossFight> bossFights = byBoss[key];
                    SimulatorBossFight first = bossFights[0];

                    groups.Add(new SimulatorBossGroup(
                        first.NpcName == null ? first.BossName : $"{first.NpcName} ({first.BossName})",
                        LoadPortrait(first.PortraitRelativePath, portraits),
                        bossFights.FirstOrDefault(f => f.DifficultyKey.Equals("easy", StringComparison.OrdinalIgnoreCase)),
                        bossFights.FirstOrDefault(f => f.DifficultyKey.Equals("medium", StringComparison.OrdinalIgnoreCase)),
                        bossFights.FirstOrDefault(f => f.DifficultyKey.Equals("hard", StringComparison.OrdinalIgnoreCase)),
                        picked => SelectedBoss = picked));
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Simulator: the boss list failed to load.");
                problems.Add("The boss list failed to load - see today's log.");
            }

            Dispatcher.UIThread.Post(() =>
            {
                allBossGroups = groups;

                BossListNote = groups.Count == 0
                    ? "No boss fights could be loaded."
                    : $"{groups.Count} bosses ({fightCount} fights) from the Boss Database." +
                      (problems.Count > 0 ? $" {problems.Count} file problem(s): {string.Join(" ", problems)}" : string.Empty);

                RefreshBossChoices();
            });
        });

        // §156: observer settings + stored-record count, off the UI thread.
        _ = Task.Run(() =>
        {
            ObserverSettings settings = ObservationStore.LoadSettings();
            (int files, long records) = ObservationStore.CountStored();

            Dispatcher.UIThread.Post(() =>
            {
                observerSettings = settings;
                ObserverEnabled = settings.Enabled;
                observerSettingsLoaded = true;
                ObserverCountText = $"Recorded: {records} observation(s) in {files} battle file(s)";
                ObserverStatusText = settings.Enabled
                    ? "Observer on - primary battles will be recorded."
                    : "Observer off.";
            });
        });
    }

    /// <summary>§201: remembered the moment it is switched, the same way
    /// the observer's own switch is. The guard keeps the load in the
    /// constructor from writing straight back.</summary>
    /// <summary>§203: one button, and it always says which of the two
    /// things it is about to do.</summary>
    partial void OnBattleOverChanged(bool value) =>
        LeaveBattleText = value ? "Back to Team Builder" : "Cancel Battle";

    partial void OnBattleSceneEnabledChanged(bool value)
    {
        RefreshBattleSceneVisibility();

        if (!battleSceneLoaded)
            return;

        try
        {
            Models.UiPreferences preferences = UiPreferencesService.Load();
            preferences.BattleSceneEnabled = value;
            UiPreferencesService.Save(preferences);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Simulator: the battle scene preference could not be saved.");
        }
    }

    void RefreshBattleSceneVisibility()
    {
        BattleSceneVisible = BattleSceneEnabled && BattleFieldImage != null;
        BattlePanelsVisible = !BattleSceneVisible;
    }

    // ---- §156: observer plumbing ----

    partial void OnObserverEnabledChanged(bool value)
    {
        DisarmClear();

        if (!observerSettingsLoaded)
            return;

        observerSettings.Enabled = value;
        var snapshot = observerSettings;
        _ = Task.Run(() => ObservationStore.SaveSettings(snapshot));

        ObserverStatusText = value
            ? "Observer on - primary battles will be recorded."
            : "Observer off.";
    }

    // §372: OpponentBrainText and UseModelOpponent are gone. The trained
    // model (§185, opt-in since §218) is no longer offered anywhere in the
    // window: every opponent - random, Boss Database, custom - is played by
    // the Monte Carlo brain, and no screen names the brain at all. The
    // model code itself stays in the project, unreachable from here;
    // BuildModelOpponent below is kept for the day it is wanted again.

    /// <summary>
    /// §185. The deep-learning opponent, or null when there is no model to
    /// play one with.
    ///
    /// It shares the window's one evaluator rather than opening a second
    /// onnx session - the Simulator plays one battle at a time, so one
    /// session is all there is to share.
    /// </summary>
    private IBattleStrategy? BuildModelOpponent(BattleRecall recall, out string note)
    {
        IShadowEvaluator evaluator = EnsureShadowEvaluator();

        // §322: "available" was the wrong question. A model can load and
        // still read a different number of features than the observation
        // it would be shown, in which case it scores nothing and this
        // opponent plays random moves at a person who was told they were
        // facing the network. Asking about the width too turns that into
        // this same honest note and the Monte Carlo brain they already
        // trust.
        string? problem = NeuralStrategy.Incompatibility(evaluator);

        if (problem != null)
        {
            note = "Deep-learning opponent unavailable (" + problem +
                   ") - this battle uses the Monte Carlo brain instead. " +
                   "Install a trained model from Admin Console > Battle Lab.";
            return null;
        }

        note = "Deep-learning opponent: " + evaluator.Status.Description +
               $", playing at risk {ModelOpponentRisk:0.##} - it will take a gamble the boss brain would not.";

        return new NeuralStrategy(evaluator, recall) { RiskAppetite = ModelOpponentRisk };
    }

    /// <summary>
    /// §185. How much of a gambler the custom opponent is, on §182's dial
    /// where 0 ranks a move by its average outcome and 1 ranks it by how
    /// good it is when it goes well.
    ///
    /// The midpoint, and not arbitrarily: an Observe only run walks the
    /// dial through 0, 0.25, 0.5, 0.75 and 1, so this is a setting the
    /// model has actually seen training data at rather than one it has to
    /// interpolate to.
    /// </summary>
    private const float ModelOpponentRisk = 0.5f;

    /// <summary>The shadow evaluator, created once per window from
    /// whatever model ObservationStore can find. A missing or broken
    /// model just reports itself - observation carries on without it.</summary>
    private IShadowEvaluator EnsureShadowEvaluator()
    {
        if (shadowEvaluator != null)
            return shadowEvaluator;

        try
        {
            shadowEvaluator = new OnnxShadowEvaluator(ObservationStore.ResolveModelPath());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Simulator observer: shadow evaluator failed to construct.");
            shadowEvaluator = new NullShadowEvaluator("shadow evaluator failed to start: " + ex.Message);
        }

        return shadowEvaluator;
    }

    private void RefreshStoredCounts()
    {
        _ = Task.Run(() =>
        {
            (int files, long records) = ObservationStore.CountStored();

            Dispatcher.UIThread.Post(() =>
                ObserverCountText = $"Recorded: {records} observation(s) in {files} battle file(s)");
        });
    }

    [RelayCommand]
    private void ClearObservations()
    {
        if (!clearObservationsArmed)
        {
            clearObservationsArmed = true;
            ClearObservationsLabel = "Really delete? Click again";
            ObserverStatusText = "This deletes every recorded observation file. Click the button again to confirm.";
            return;
        }

        DisarmClear();

        _ = Task.Run(() =>
        {
            (int cleared, long bytes) = ObservationStore.ClearAll();

            Dispatcher.UIThread.Post(() =>
            {
                ObserverStatusText = bytes > 0
                    ? $"Cleared {cleared} observation file(s), {bytes / 1024.0 / 1024.0:F1} MB reclaimed."
                    : $"Cleared {cleared} observation file(s).";
                RefreshStoredCounts();
            });
        });
    }

    private void DisarmClear()
    {
        clearObservationsArmed = false;
        ClearObservationsLabel = "Clear Observation Data";
    }

    private static Bitmap? LoadPortrait(string relativePath, Dictionary<string, Bitmap?> cache)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        if (cache.TryGetValue(relativePath, out Bitmap? cached))
            return cached;

        Bitmap? bitmap = null;

        try
        {
            string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            string fullPath = Path.IsPathRooted(normalized) ? normalized : Path.Combine(AppContext.BaseDirectory, normalized);

            if (File.Exists(fullPath))
                bitmap = new Bitmap(fullPath);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Simulator: boss portrait {Path} failed to load.", relativePath);
        }

        cache[relativePath] = bitmap;
        return bitmap;
    }

    // ---- §155: opponent picker plumbing ----

    // §173: three modes now, so each one clears the other two.
    partial void OnUseRandomOpponentChanged(bool value)
    {
        if (!value)
            return;

        UseBossOpponent = false;
        UseCustomOpponent = false;
    }

    partial void OnUseBossOpponentChanged(bool value)
    {
        if (!value)
            return;

        UseRandomOpponent = false;
        UseCustomOpponent = false;
    }

    partial void OnUseCustomOpponentChanged(bool value)
    {
        if (!value)
            return;

        UseRandomOpponent = false;
        UseBossOpponent = false;

        RefreshCustomBosses();
    }

    /// <summary>§173: rereads both CustomBosses folders, keeping the
    /// current pick when it survived the reload.</summary>
    public void RefreshCustomBosses()
    {
        string? keepId = SelectedCustomBoss?.Id;

        _ = Task.Run(() =>
        {
            (List<CustomBossEntry> entries, List<string> problems) = CustomBossStore.LoadAll();

            Dispatcher.UIThread.Post(() =>
            {
                CustomBossChoices.Clear();

                foreach (CustomBossEntry entry in entries)
                    CustomBossChoices.Add(entry);

                SelectedCustomBoss =
                    CustomBossChoices.FirstOrDefault(e => e.Id == keepId) ??
                    CustomBossChoices.FirstOrDefault();

                CustomBossNote = entries.Count == 0
                    ? "No custom opponents yet - press New to build one."
                    : $"{entries.Count} custom opponent(s)." +
                      (problems.Count > 0 ? $" {problems.Count} file problem(s): {string.Join(" ", problems)}" : string.Empty);
            });
        });
    }

    [RelayCommand]
    private async Task NewCustomBoss()
    {
        if (RequestCustomBossEdit == null)
            return;

        if (await RequestCustomBossEdit(null))
            RefreshCustomBosses();
    }

    [RelayCommand]
    private async Task EditCustomBoss()
    {
        if (RequestCustomBossEdit == null || SelectedCustomBoss == null)
            return;

        if (await RequestCustomBossEdit(SelectedCustomBoss))
            RefreshCustomBosses();
    }

    [RelayCommand]
    private void DeleteCustomBoss()
    {
        if (SelectedCustomBoss == null)
            return;

        CustomBossEntry entry = SelectedCustomBoss;

        // A shipped file belongs to the build, not to this machine.
        if (!entry.Editable)
        {
            CustomBossNote = $"{entry.Title} ships with the app - remove its file from " +
                             "DataFiles/Bosses/CustomBosses in the repo instead.";
            return;
        }

        CustomBossNote = CustomBossStore.Delete(entry.Id)
            ? $"Deleted {entry.Title}."
            : $"{entry.Title} could not be deleted - see today's log.";

        RefreshCustomBosses();
    }

    partial void OnBossSearchChanged(string value) => RefreshBossChoices();

    // §161: arming Z-Power relabels the move buttons to their Z-Moves.
    partial void OnZArmedChanged(bool value) => RefreshMoveButtons();

    private void RefreshBossChoices()
    {
        SimulatorBossGroup? keep = SelectedBoss;

        BossChoices.Clear();

        string needle = BossSearch.Trim();

        foreach (SimulatorBossGroup group in allBossGroups)
        {
            if (needle.Length == 0 ||
                group.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                BossChoices.Add(group);
            }
        }

        if (keep != null && BossChoices.Contains(keep))
            SelectedBoss = keep;
    }

    // ---- setup commands ----

    [RelayCommand]
    private async Task ImportPokemon()
    {
        if (RequestImport == null)
            return;

        // §198: a full team no longer refuses to open the importer. Every
        // successful scan is written to storage regardless (see
        // SimulatorImportViewModel.Confirm), so scanning a seventh Pokemon
        // loses nothing - it waits in storage for Replace from Storage.
        IReadOnlyList<ImportedPokemon> imported = await RequestImport(FreeTeamSlots);

        if (imported.Count == 0)
            return;

        int joined = 0;

        foreach (ImportedPokemon one in imported)
        {
            if (TeamSlots.Count >= TeamBuilder.MaxTeamSize)
                break;

            AddImportedSlot(one);
            joined++;
        }

        int stored = imported.Count - joined;

        if (stored == 0)
        {
            SetupStatus = joined == 1
                ? $"{imported[0].SpeciesName} joined the team."
                : $"{joined} Pokemon joined the team.";
        }
        else
        {
            SetupStatus =
                $"{joined} joined the team and {stored} went to storage - a team holds " +
                $"{TeamBuilder.MaxTeamSize}. Use a card's Replace from Storage to swap one in.";
        }
    }

    [RelayCommand]
    private async Task PickFromStorage()
    {
        if (RequestStoragePick == null)
            return;

        // §198: the picker opens whatever the team size is. Nothing can be
        // lost here - what is picked is already in storage - so a full team
        // is reported rather than refused, and it names the way in.
        // §203: and it hands back everything picked, not one Pokemon.
        // §275: it is also told how many slots are free, and refuses a pick
        // beyond that with a warning of its own - so the overflow arm below
        // is now unreachable from a View that wires the picker properly, and
        // is kept as the backstop for one that does not.
        IReadOnlyList<ImportedPokemon> picked = await RequestStoragePick(true, FreeTeamSlots);

        if (picked.Count == 0)
            return;

        int joined = 0;

        foreach (ImportedPokemon one in picked)
        {
            if (TeamSlots.Count >= TeamBuilder.MaxTeamSize)
                break;

            AddImportedSlot(one);
            joined++;
        }

        int left = picked.Count - joined;

        if (left == 0)
        {
            SetupStatus = joined == 1
                ? $"{picked[0].SpeciesName} joined the team."
                : $"{joined} Pokemon joined the team.";
        }
        else
        {
            SetupStatus =
                $"{joined} joined the team; {left} stayed in storage because a team holds " +
                $"{TeamBuilder.MaxTeamSize}. Use a card's Replace from Storage to swap one in.";
        }
    }

    /// <summary>§198: swap one card's Pokemon for a stored one WITHOUT
    /// disturbing the order. The slot keeps its place, so replacing the
    /// first card replaces the lead - which before this meant removing the
    /// team and adding it back in the right order.</summary>
    [RelayCommand]
    private async Task ReplaceFromStorage(SimulatorSlotViewModel? slot)
    {
        if (slot == null || RequestStoragePick == null || !TeamSlots.Contains(slot))
            return;

        // §203: one pick only - swapping one Pokemon has nothing to do
        // with a second - so this window closes on the first click.
        IReadOnlyList<ImportedPokemon> picked = await RequestStoragePick(false, FreeTeamSlots);

        if (picked.Count == 0)
            return;

        ImportedPokemon imported = picked[0];

        string leaving = slot.Imported?.SpeciesName ?? "The empty slot";

        slot.Apply(imported);

        // The held item belonged to the Pokemon that just left, not to the
        // one arriving - it is picked again for the new occupant.
        slot.SelectedItemName = null;

        SetupStatus = $"{leaving} swapped for {imported.SpeciesName} in slot {TeamSlots.IndexOf(slot) + 1}.";

        // §203: same slot, different Pokemon - the collection did not change.
        SaveTeam();
    }

    /// <summary>§198: the drag-to-reorder drop, called by the window as the
    /// pointer crosses into another card. Moving a card to the top makes it
    /// the lead, which is the whole point of the gesture: the order the
    /// cards sit in IS the party order, and changing it used to mean
    /// removing everything and adding it back from storage in sequence.
    ///
    /// Move, not swap - the card lifts out and the rest close up behind it,
    /// so dragging the fourth card to the top gives 4,1,2,3.</summary>
    public void MoveSlot(SimulatorSlotViewModel? slot, int targetIndex)
    {
        if (slot == null)
            return;

        int from = TeamSlots.IndexOf(slot);

        if (from < 0 || TeamSlots.Count == 0)
            return;

        int to = Math.Clamp(targetIndex, 0, TeamSlots.Count - 1);

        if (to == from)
            return;

        TeamSlots.Move(from, to);

        string who = slot.Imported?.SpeciesName ?? "That Pokemon";

        SetupStatus = to == 0
            ? $"{who} leads the party now."
            : $"{who} moved to slot {to + 1}.";
    }

    /// <summary>
    /// §203/§317. The team as it was when the app last closed.
    ///
    /// STORAGE FIRST, THE SAVED COPY SECOND. §203 stored references only -
    /// the GameId-then-Fingerprint identity - on the reasoning that every
    /// Pokemon on a team is already in storage, so a copy would be a second
    /// version of the same thing, free to drift, and a Pokemon re-imported
    /// with better EVs would come back stale.
    ///
    /// That reasoning is still right, and the lookup still wins. What it
    /// stopped being is COMPLETE: §314 gave the phone a builder that makes
    /// Pokemon out of the Pokedex, and those were never in storage, so every
    /// reference missed and a team built on a phone came back empty with
    /// nothing said about it. §317 writes the Pokemon into the team file as
    /// well, and reads it only when the reference cannot be resolved - so a
    /// stored Pokemon is still the live one and a phone-built one survives.
    ///
    /// A slot that has neither is still dropped with a note, and that is the
    /// case that keeps §203 honest: a Pokemon that WAS in storage saved no
    /// copy, so deleting it from storage still drops the slot rather than
    /// resurrecting something the player threw away. A team file written
    /// before §317 has no copies at all and behaves exactly as it did.
    /// </summary>
    /// <summary>§317. The one definition of "storage already has this one",
    /// used by the save to decide whether a copy is needed and by the restore
    /// to decide whether to read one. Two spellings of this rule would mean a
    /// slot that saves no copy and then cannot find one.</summary>
    private static StoredPokemon? FindInStorage(
        IReadOnlyList<StoredPokemon> stored, string? gameId, string fingerprint) =>
        stored.FirstOrDefault(e =>
            (gameId != null && e.GameId == gameId) || e.Fingerprint == fingerprint);

    private void RestoreTeam()
    {
        List<SavedTeamSlot> saved = SimulatorTeamStore.Load();

        if (saved.Count == 0)
            return;

        IReadOnlyList<StoredPokemon> stored = SimulatorPokemonStorage.All();

        restoringTeam = true;

        int missing = 0;

        try
        {
            foreach (SavedTeamSlot slot in saved.Take(TeamBuilder.MaxTeamSize))
            {
                StoredPokemon? entry = FindInStorage(stored, slot.GameId, slot.Fingerprint);

                // §317: the copy the file carries, used only when the
                // reference found nothing - which the save arranged to be
                // exactly the slots storage never held.
                entry ??= slot.Pokemon;

                if (entry == null)
                {
                    missing++;
                    continue;
                }

                var restored = new SimulatorSlotViewModel(spriteProvider);
                restored.Apply(entry.ToImported());
                restored.SelectedItemName = slot.ItemName;

                TeamSlots.Add(restored);
            }
        }
        finally
        {
            restoringTeam = false;
        }

        if (TeamSlots.Count == 0)
            return;

        SetupStatus = missing == 0
            ? $"Your last team is back ({TeamSlots.Count} Pokemon)."
            : $"Your last team is back ({TeamSlots.Count} Pokemon); {missing} could not be found any more.";
    }

    /// <summary>
    /// §203. Written on every change to the slots. Cheap enough to do
    /// eagerly - six Pokemon and their items - and eager is what makes it
    /// survive the app being closed from the taskbar rather than through a
    /// menu.
    ///
    /// §316: public, so the Android companion's Update button can persist a
    /// card it edited in place. Every one of the desktop's own callers is
    /// still inside this class.
    ///
    /// §317: a slot storage does NOT hold also carries the Pokemon itself.
    /// Only such a slot: a Pokemon that is in storage stays a pure reference,
    /// so §203's rule still holds exactly - re-import it with better EVs and
    /// the team picks the better one up, delete it from storage on purpose
    /// and the slot is still dropped rather than resurrected from a copy.
    /// What changes is the case §203 could not have: a Pokemon built on a
    /// phone, which storage will never hold and which used to vanish.
    ///
    /// The copy costs nothing to make - the fingerprint is read off it
    /// anyway, so this is the same object kept rather than thrown away.
    /// </summary>
    public void SaveTeam()
    {
        if (restoringTeam)
            return;

        IReadOnlyList<StoredPokemon> stored = SimulatorPokemonStorage.All();

        SimulatorTeamStore.Save(TeamSlots
            .Where(s => s.Imported != null)
            .Select(s =>
            {
                StoredPokemon copy = StoredPokemon.From(s.Imported!);

                bool inStorage =
                    FindInStorage(stored, s.Imported!.GameId, copy.Fingerprint) != null;

                return new SavedTeamSlot
                {
                    GameId = s.Imported!.GameId,
                    Fingerprint = copy.Fingerprint,
                    ItemName = s.SelectedItemName,

                    // The whole point: a copy exists for, and only for, a
                    // Pokemon the lookup will not find.
                    Pokemon = inStorage ? null : copy
                };
            }));
    }

    private void AddImportedSlot(ImportedPokemon imported)
    {
        var slot = new SimulatorSlotViewModel(spriteProvider);
        slot.Apply(imported);
        TeamSlots.Add(slot);

        SetupStatus = $"{imported.SpeciesName} joined the team.";
    }

    [RelayCommand]
    private void RemoveSlot(SimulatorSlotViewModel? slot)
    {
        if (slot != null && TeamSlots.Count > 1)
            TeamSlots.Remove(slot);
    }

    /// <summary>§159: the slot's item button opens the item picker.</summary>
    [RelayCommand]
    private async Task PickItem(SimulatorSlotViewModel? slot)
    {
        if (slot == null || RequestItemPick == null)
            return;

        (bool picked, string? itemName) = await RequestItemPick();

        if (!picked)
            return;

        slot.SelectedItemName = itemName;

        // §203: the collection has not changed, so its own hook will not
        // fire - but what the team IS has.
        SaveTeam();
    }

    [RelayCommand]
    private void StartBattle()
    {
        if (!DataReady)
        {
            SetupStatus = "Still loading battle data - one moment.";
            return;
        }

        var plans = TeamSlots.Select(s => s.ToPlan()).ToList();

        TeamBuildResult playerTeam = TeamBuilder.Build(plans, speciesSource);

        if (!playerTeam.Ok)
        {
            SetupStatus = string.Join("  ", playerTeam.Errors.Take(4));
            return;
        }

        // §162: the seed box is gone - every battle rolls fresh. The seed
        // still reaches the log title below so a replay can be discussed.
        int seed = Environment.TickCount & 0x7fffffff;

        // §155: the opponent comes from the picked mode. §372: whichever
        // mode, it is played by the Monte Carlo brain - the §154 baseline
        // strategy that the random mirror team used to get is no longer
        // used, and the brain is never named on screen.
        List<PokemonState> opponentMons;
        var opponentNotes = new List<string>();
        string opponentName;
        string battleTitle;
        IBattleStrategy bossBrain;

        // §183: one battle's worth of notes for the opponent's book. Built
        // for every battle and handed to both the brain (which reads it
        // while choosing) and the session (which folds it in when the
        // battle ends and it knows who won).
        BattleRecall recall = OpponentMemoryStore.Current.Begin();

        if (UseCustomOpponent)
        {
            if (SelectedCustomBoss == null)
            {
                SetupStatus = "Pick a custom opponent first, or press New to build one.";
                return;
            }

            CustomBossEntry entry = SelectedCustomBoss;

            OpponentTeamResult customTeam = OpponentTeams.Build(
                CustomOpponents.ToPlans(entry.Team), speciesSource, new BattleRng(seed ^ 0x0C05));

            if (!customTeam.Ok)
            {
                SetupStatus = string.Join("  ", customTeam.Errors.Take(3));
                return;
            }

            opponentMons = customTeam.Team;
            opponentNotes.AddRange(customTeam.Errors);
            opponentNotes.AddRange(customTeam.Warnings);
            opponentNotes.AddRange(CustomOpponents.Validate(entry.Team));
            opponentName = entry.Title;
            // §372: no seed in the title. It was there so a replay could be
            // discussed; Restart Battle is a fresh roll by design, so there
            // is nothing to quote it for, and the header reads as the mockup
            // draws it.
            battleTitle = $"You vs {entry.Title}";

            // §372: the same brain a boss gets. §185 had put the trained
            // model here, §218 made it opt-in, and this takes it off the
            // screen entirely - see the note where UseModelOpponent used
            // to be declared.
            bossBrain = new MonteCarloStrategy(BossBrainConfig, seed: seed ^ 0x51ED, recall: recall);
        }
        else if (UseBossOpponent)
        {
            if (SelectedBoss == null)
            {
                SetupStatus = "Pick a boss from the opponent list first.";
                return;
            }

            // §167: the row's picked difficulty is the fight.
            SimulatorBossFight fight = SelectedBoss.SelectedFight;

            OpponentTeamResult bossTeam = OpponentTeams.Build(
                BossOpponentSource.ToPlans(fight), speciesSource, new BattleRng(seed ^ 0x0B055));

            if (!bossTeam.Ok)
            {
                SetupStatus = string.Join("  ", bossTeam.Errors.Take(3));
                return;
            }

            opponentMons = bossTeam.Team;
            opponentNotes.AddRange(bossTeam.Errors);      // slot-level losses still show in the log
            opponentNotes.AddRange(bossTeam.Warnings);
            opponentName = fight.NpcName ?? fight.BossName;
            battleTitle = $"You vs {fight.DisplayTitle}";

            // One brain family and configuration for every boss; a fresh
            // instance per battle so no decision state crosses fights -
            // §183's book is the one thing that deliberately does, and it
            // is held outside the brain for exactly that reason.
            bossBrain = new MonteCarloStrategy(BossBrainConfig, seed: seed ^ 0x51ED, recall: recall);
        }
        else
        {
            TeamBuildResult opponentTeam = BuildOpponentTeam(playerTeam.Team, seed);

            if (!opponentTeam.Ok)
            {
                SetupStatus = "Could not assemble a random opponent team: " + string.Join("  ", opponentTeam.Errors.Take(3));
                return;
            }

            opponentMons = opponentTeam.Team;
            opponentNotes.AddRange(opponentTeam.Warnings);

            // §372: a random team used to be played by the §154 baseline
            // strategy and titled after it. It is the Monte Carlo brain now
            // like every other opponent, and it is named for what it is
            // rather than for how it thinks - "Random Trainer sent out
            // Pidgey!" is a line a log can carry; "Baseline AI sent out"
            // was a line about the program.
            opponentName = "Random Trainer";
            battleTitle = "You vs Random Trainer";
            bossBrain = new MonteCarloStrategy(BossBrainConfig, seed: seed ^ 0x51ED, recall: recall);
        }

        battleCts = new CancellationTokenSource();

        // Rollouts in flight are cut short when the battle is left.
        if (bossBrain is MonteCarloStrategy cancellable)
            cancellable.CancellationToken = battleCts.Token;

        // §156: retire the previous battle's observer (its file is already
        // final), then attach a fresh one when observation is on.
        DisarmClear();
        RetireObserver();

        if (ObserverEnabled)
        {
            try
            {
                observer = new BattleObserver(
                    ObservationStore.Root, "Primary", seed, EnsureShadowEvaluator());
                ObserverStatusText = $"Recording battle {observer.BattleId}. {observer.ShadowStatus.Description}";
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Simulator observer: could not start recording.");
                observer = null;
                ObserverStatusText = "Observer could not start (see today's log) - the battle runs unrecorded.";
            }
        }

        ObserverActive = observer != null;
        ObserverBattleText = observer != null ? "Observer: recording" : string.Empty;

        session = new SimulatorSession(
            "You", playerTeam.Team,
            opponentName, opponentMons,
            bossBrain,
            seed: seed,
            observer: observer,
            recall: recall);

        // §217: the engine reports every move as it is used. Subscribing to
        // this battle's own EventManager and not a shared one matters -
        // BattleState.Clone builds a fresh EventManager, so the thousands of
        // cloned battles the Monte Carlo strategy plays out carry no
        // subscribers and never reach this view model.
        session.State.Events.Subscribe(OnBattleEvent);

        lock (animationGate)
            pendingAnimations.Clear();

        memoryWritten = false;

        consumedLogLines = 0;
        LogLines.Clear();
        playerSpriteFor = string.Empty;
        opponentSpriteFor = string.Empty;
        playerSceneFor = string.Empty;
        opponentSceneFor = string.Empty;
        PlayerSprite = null;
        OpponentSprite = null;
        BattleOver = false;
        ResultText = string.Empty;
        BattleTitle = battleTitle;

        if (playerTeam.Warnings.Count > 0 || opponentNotes.Count > 0)
        {
            foreach (string warning in playerTeam.Warnings.Concat(opponentNotes))
                LogLines.Add($"(setup: {warning})");
        }

        InBattle = true;

        BuildBattleTeamRow();
        RefreshBattleView();
    }

    /// <summary>§157: one clickable sprite per team member for the battle
    /// view. Sprites load off the UI thread once per battle.</summary>
    private void BuildBattleTeamRow()
    {
        BattleTeam.Clear();

        if (session == null)
            return;

        for (int i = 0; i < session.Player.Team.Count; i++)
        {
            var item = new SimulatorTeamSlotItem
            {
                TeamIndex = i,
                Species = session.Player.Team[i].Species
            };

            BattleTeam.Add(item);
            _ = LoadTeamSlotSpriteAsync(item, session.Player.Team[i].IsShiny);
        }
    }

    private async Task LoadTeamSlotSpriteAsync(SimulatorTeamSlotItem item, bool shiny)
    {
        Bitmap? bitmap = await spriteProvider.GetSpriteAsync(item.Species, shiny);

        if (BattleTeam.Contains(item))
            item.Sprite = bitmap;
    }

    private TeamBuildResult BuildOpponentTeam(List<PokemonState> mirror, int seed)
    {
        // §168: the team recipe moved verbatim into the engine's
        // RandomTeams (the admin Battle Lab builds the same teams
        // headlessly); the seed derivation stays here, so this battle's
        // opponents are byte-identical to what §154/§161 produced.
        return RandomTeams.Build(speciesSource, mirror.Count, seed ^ 0x5f3759df);
    }

    // ---- battle commands ----

    [RelayCommand] private Task UseMove1() => UseMoveAsync(0);
    [RelayCommand] private Task UseMove2() => UseMoveAsync(1);
    [RelayCommand] private Task UseMove3() => UseMoveAsync(2);
    [RelayCommand] private Task UseMove4() => UseMoveAsync(3);

    private async Task UseMoveAsync(int index)
    {
        if (session == null || IsResolving || MustReplace || BattleOver)
            return;

        IReadOnlyList<BattleAction> legal = session.PlayerLegalActions();

        var active = session.Player.ActivePokemon;

        BattleAction? action = active.Charging
            ? legal.FirstOrDefault(a => a.Move != null)
            : legal.FirstOrDefault(a =>
                a.Move != null &&
                (a.Move.Name == "Struggle"
                    ? index == 0
                    : active.Moves.IndexOf(a.Move) == index));

        if (action == null)
            return;

        // §161: apply the armed toggles to this move action, then disarm
        // them - both are once per battle anyway.
        if (action.Type == BattleActionType.Move && action.Move != null)
        {
            if (MegaArmed && MegaAvailable)
                action.MegaEvolve = true;

            if (ZArmed && ZAvailable &&
                ZMoves.CanUse(session.State, session.Player, active, action.Move))
            {
                action.UseZMove = true;
            }
        }

        MegaArmed = false;
        ZArmed = false;

        await ResolveTurnAsync(action);
    }

    /// <summary>§157: clicking a teammate in the team row. After a faint
    /// it sends that Pokemon in; mid-battle it plays a switch action.
    /// §160: the click asks first - a confirmation card with the
    /// teammate's HP, status and held item - so an accidental click on
    /// the row cannot throw the turn away.</summary>
    [RelayCommand]
    private async Task SwitchTo(SimulatorTeamSlotItem? item)
    {
        if (session == null || item == null || IsResolving || BattleOver || !item.CanSwitch)
            return;

        if (RequestSwitchConfirm != null &&
            !await RequestSwitchConfirm(item, MustReplace))
        {
            return;
        }

        // Re-checked after the dialog: the guards are cheap and the state
        // must still hold once the user has confirmed.
        if (session == null || IsResolving || BattleOver || !item.CanSwitch)
            return;

        int index = item.TeamIndex;

        if (MustReplace)
        {
            session.ReplacePlayerPokemon(index);
            MustReplace = session.PlayerMustReplace;
            RefreshBattleView();
            return;
        }

        IReadOnlyList<BattleAction> legal = session.PlayerLegalActions();

        BattleAction? action = legal.FirstOrDefault(a =>
            a.Type == BattleActionType.Switch &&
            a.SwitchTarget == session.Player.Team[index]);

        if (action == null)
            return;

        await ResolveTurnAsync(action);
    }

    /// <summary>§217. The engine's report that a move was used. This runs
    /// on whichever thread is resolving the turn, so it does the least
    /// possible: classify the move (pure, no allocation beyond the record)
    /// and remember it. Nothing here touches the UI.</summary>
    private void OnBattleEvent(BattleEvent battleEvent, BattleState state)
    {
        if (battleEvent.Type != BattleEventType.BeforeMove)
            return;

        MoveState? move = battleEvent.Move;

        if (move == null)
            return;

        lock (animationGate)
        {
            pendingAnimations.Add(new PendingAnimation(
                MoveAnimationCatalog.Classify(move),
                MoveAnimationCatalog.PaletteFor(move),
                ReferenceEquals(battleEvent.Source, state.Player1.ActivePokemon),
                HashCode.Combine(move.Name, state.TurnNumber, pendingAnimations.Count)));
        }
    }

    /// <summary>§217. Replays the turn's moves in the order the engine used
    /// them, one at a time, waiting for each. The engine has already
    /// resolved the whole turn by this point, so the health bars catch up
    /// afterwards in RefreshBattleView: the pictures come first, then the
    /// numbers. Interleaving them properly would mean the engine yielding
    /// mid-turn, which is a bigger change than a coat of paint deserves.</summary>
    private async Task PlayPendingAnimationsAsync()
    {
        PendingAnimation[] steps;

        lock (animationGate)
        {
            steps = pendingAnimations.ToArray();
            pendingAnimations.Clear();
        }

        if (steps.Length == 0)
            return;

        // §372: one speed. See AnimationFactor.
        double factor = AnimationFactor;

        // §372: THIS battle's token, taken once. The loop used to read the
        // field on every step, and Restart Battle replaces the field with a
        // fresh, uncancelled token the moment the new battle starts - so a
        // turn's pictures still playing from the old one would have carried
        // on under the new one's colours. A captured token belongs to the
        // battle whose pictures these are, and is cancelled with it.
        CancellationTokenSource? cts = battleCts;

        foreach (PendingAnimation step in steps)
        {
            if (cts == null || cts.IsCancellationRequested)
                return;

            (Point attacker, Rect attackerBox) = SceneBox(step.ByPlayer);
            (Point target, Rect targetBox) = SceneBox(!step.ByPlayer);

            var request = new BattleEffectRequest
            {
                Spec = step.Spec,
                Palette = step.Palette,
                Attacker = attacker,
                Target = target,
                AttackerBox = attackerBox,
                TargetBox = targetBox,
                AttackerIsPlayer = step.ByPlayer,
                SpeedFactor = factor,
                Seed = step.Seed,
            };

            CurrentEffect = request;

            // The layer finishes the request when its last cue fades. The
            // timeout is the seatbelt: a battle must never be wedged by a
            // picture that failed to draw, so after a generous wait the turn
            // simply carries on without it.
            Task timeout = Task.Delay(TimeSpan.FromSeconds(6));

            if (await Task.WhenAny(request.Completed.Task, timeout) == timeout)
                request.Finish();

            CurrentEffect = null;
        }
    }

    /// <summary>§217. Where a side's sprite actually is, in the field's own
    /// 937x755 coordinates - the same numbers Stand produced for the Image.
    /// Falls back to the painted pad when the sprite has not loaded, so an
    /// animation never fires at the origin.</summary>
    private (Point Centre, Rect Box) SceneBox(bool player)
    {
        double width = player ? ScenePlayerWidth : SceneOpponentWidth;
        double height = player ? ScenePlayerHeight : SceneOpponentHeight;
        Thickness margin = player ? ScenePlayerMargin : SceneOpponentMargin;

        if (width <= 0 || height <= 0)
        {
            double feetX = player ? PlayerFeetX : OpponentFeetX;
            double feetY = player ? PlayerFeetY : OpponentFeetY;

            return (new Point(feetX, feetY - 90), default);
        }

        var box = new Rect(margin.Left, margin.Top, width, height);

        return (box.Center, box);
    }

    private async Task ResolveTurnAsync(BattleAction action)
    {
        if (session == null || battleCts == null)
            return;

        IsResolving = true;

        // §217: anything left over from a turn that was cancelled or that
        // threw is not this turn's business.
        lock (animationGate)
            pendingAnimations.Clear();

        try
        {
            await session.PlayTurnAsync(action, battleCts.Token);

            // §217: IsResolving is still true here, so the move buttons stay
            // disabled for the length of the animation and the player cannot
            // queue a second turn on top of the pictures.
            await PlayPendingAnimationsAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Simulator: a turn failed.");
            LogLines.Add($"(the turn failed: {ex.Message})");
        }
        finally
        {
            CurrentEffect = null;
            IsResolving = false;
        }

        RefreshBattleView();
    }

    /// <summary>§203. Cancelling a battle leaves it. It used to stop the
    /// battle and stay on the battle screen, so getting back to the team
    /// builder was a second button - and the two buttons then did the same
    /// thing, differing only in which of them was greyed out. There is one
    /// now, and it says which it is.</summary>
    [RelayCommand]
    private void LeaveBattle()
    {
        if (session != null && session.Outcome == BattleOutcome.Unfinished)
        {
            battleCts?.Cancel();
            session.Cancel();
        }

        InBattle = false;
    }

    /// <summary>§372. Restart Battle: the same team, the same opponent at
    /// the same difficulty, a fresh roll of the dice. Everything StartBattle
    /// reads - the team cards, the opponent radios, the selected boss and
    /// its difficulty, the stadium toggle - is still exactly as it was when
    /// the battle began, so a restart IS a start, after leaving the one in
    /// progress. A fresh seed rather than the same one on purpose: a
    /// rematch that played out identically would look broken, and the seed
    /// is no longer shown anywhere to be replayed from.</summary>
    [RelayCommand]
    private void RestartBattle()
    {
        LeaveBattle();
        StartBattle();
    }

    // ---- refresh ----

    private void RefreshBattleView()
    {
        if (session == null)
            return;

        BattleState state = session.State;

        TurnText = $"Turn {Math.Max(1, state.TurnNumber + (state.Outcome == BattleOutcome.Unfinished ? 1 : 0))}";
        // §372: "None" with a capital, the way the mockup has it and the
        // way the enum names it when there IS weather - "Sun", "Rain".
        WeatherText = "Weather: " + (state.Environment.Weather == WeatherType.None ? "None" : state.Environment.Weather.ToString());
        TerrainText = "Terrain: " + (state.Environment.Terrain == TerrainType.None ? "None" : state.Environment.Terrain.ToString());

        var player = session.Player.ActivePokemon;
        var opponent = session.Opponent.ActivePokemon;

        PlayerHeader = $"{player.Species}  Lv. {player.Level}";
        PlayerHp = Math.Max(0, player.CurrentHP);
        PlayerMaxHp = Math.Max(1, player.MaxHP);
        PlayerHpText = $"{Math.Max(0, player.CurrentHP)} / {player.MaxHP}";
        PlayerStatusText = DescribeStatus(player);

        OpponentHeader = $"{opponent.Species}  Lv. {opponent.Level}";
        OpponentHp = Math.Max(0, opponent.CurrentHP);
        OpponentMaxHp = Math.Max(1, opponent.MaxHP);
        OpponentHpText = $"{Math.Max(0, opponent.CurrentHP)} / {opponent.MaxHP}";
        OpponentStatusText = DescribeStatus(opponent);

        // §159: what each active is holding (or that its item is spent).
        PlayerItemText = DescribeItem(player);
        OpponentItemText = DescribeItem(opponent);
        PlayerItemSprite = player.HeldItemId != null ? ItemSpriteService.GetSprite(player.HeldItemId) : null;
        OpponentItemSprite = opponent.HeldItemId != null ? ItemSpriteService.GetSprite(opponent.HeldItemId) : null;

        RefreshSprites(player, opponent);

        MustReplace = session.PlayerMustReplace;
        BattleOver = session.Outcome != BattleOutcome.Unfinished;

        // §183: the session folds the battle into the book as it ends; this
        // is the write to disk, once, on the transition rather than on
        // every refresh that happens to run afterwards.
        if (BattleOver && !memoryWritten)
        {
            memoryWritten = true;
            OpponentMemoryStore.Save();

            // §372: the card records, on the same once-per-battle
            // transition. A win or a loss goes to every Pokemon on the team
            // - it was on the team for it, which is what the corner of the
            // card says it counts. A draw or a cancelled battle is neither.
            bool? won = session.Outcome switch
            {
                BattleOutcome.Player1Wins => true,
                BattleOutcome.Player2Wins => false,
                _ => null,
            };

            if (won is bool decided)
            {
                SimulatorPokemonStorage.RecordBattle(
                    TeamSlots.Where(t => t.Imported != null).Select(t => t.Imported!),
                    decided);

                foreach (SimulatorSlotViewModel slot in TeamSlots)
                    slot.RefreshRecord();
            }
        }

        ResultText = session.Outcome switch
        {
            BattleOutcome.Player1Wins => "You win!",
            BattleOutcome.Player2Wins => "The opponent wins.",
            BattleOutcome.Draw => "It's a draw.",
            BattleOutcome.Cancelled => "Battle cancelled.",
            _ => MustReplace ? $"{player.Species} fainted - choose who to send in." : string.Empty
        };

        // §161: the once-per-battle toggles - shown only while they could
        // actually do something this turn.
        MegaAvailable = !BattleOver && !MustReplace &&
            MegaEvolutions.CanMegaEvolve(state, session.Player, player);
        ZAvailable = !BattleOver && !MustReplace &&
            ZMoves.AvailableFor(state, session.Player, player);

        if (!MegaAvailable)
            MegaArmed = false;

        if (!ZAvailable)
            ZArmed = false;

        RefreshMoveButtons();
        RefreshBattleTeamRow();

        if (observer != null)
        {
            string agreement = observer.MoveDecisionCount > 0
                ? $" - shadow agreed {observer.ShadowAgreedCount}/{observer.MoveDecisionCount}"
                : string.Empty;

            ObserverBattleText = $"Observer: {observer.RecordedCount} recorded{agreement}";

            if (BattleOver)
            {
                ObserverStatusText = observer.LastStatus;
                RefreshStoredCounts();
            }
        }

        for (int i = consumedLogLines; i < state.Log.Lines.Count; i++)
            LogLines.Add(state.Log.Lines[i]);

        consumedLogLines = state.Log.Lines.Count;
    }

    /// <summary>§159: the held-item line under the HP bar - the item, or
    /// a note that it has been used up, or nothing at all.</summary>
    private static string DescribeItem(PokemonState pokemon) =>
        pokemon.HeldItemId != null
            ? "@ " + HeldItems.DisplayName(pokemon.HeldItemId)
            : pokemon.LostItem ? "@ (item used)" : string.Empty;

    private static string DescribeStatus(PokemonState pokemon)
    {
        var notes = new List<string>();

        if (pokemon.Status != StatusCondition.None)
            notes.Add(pokemon.Status.ToString());

        if (pokemon.SubstituteHP > 0)
            notes.Add($"Substitute {pokemon.SubstituteHP}");

        if (pokemon.Charging)
            notes.Add("Charging");

        return notes.Count == 0 ? "" : string.Join(" - ", notes);
    }

    private void RefreshSprites(PokemonState player, PokemonState opponent)
    {
        // §164: the cache key carries shininess so the right artwork loads
        // and a species change (switch, mega) still refreshes.
        // §199: and the chosen picture, so switching between two slots that
        // named different art refreshes even when they share a species.
        // §200: and the exact form, for the same reason.
        string playerKey = $"{player.Species}|{player.IsShiny}|{player.SpritePath}|{player.DexNumber}";
        string opponentKey = $"{opponent.Species}|{opponent.IsShiny}|{opponent.SpritePath}|{opponent.DexNumber}";

        if (playerSpriteFor != playerKey)
        {
            playerSpriteFor = playerKey;
            _ = LoadBattleSpriteAsync(playerKey, player, isPlayer: true);
        }

        if (opponentSpriteFor != opponentKey)
        {
            opponentSpriteFor = opponentKey;
            _ = LoadBattleSpriteAsync(opponentKey, opponent, isPlayer: false);
        }

        // §201: the scene's own pair. Same keys - what changes the panel's
        // picture changes the scene's - but a different resolution, because
        // the player is seen from behind and both are cropped to their feet.
        if (playerSceneFor != playerKey)
        {
            playerSceneFor = playerKey;
            _ = LoadSceneSpriteAsync(playerKey, player, isPlayer: true);
        }

        if (opponentSceneFor != opponentKey)
        {
            opponentSceneFor = opponentKey;
            _ = LoadSceneSpriteAsync(opponentKey, opponent, isPlayer: false);
        }
    }

    /// <summary>§201. The scene's artwork and where it stands. The sprite
    /// arrives cropped to its opaque box, so its bottom row is the Pokemon's
    /// feet and the margin below puts that row exactly on the pad.</summary>
    private async Task LoadSceneSpriteAsync(string key, PokemonState pokemon, bool isPlayer)
    {
        Bitmap? bitmap = await spriteProvider.GetBattleSpriteAsync(pokemon, back: isPlayer);

        if (isPlayer)
        {
            if (playerSceneFor != key)
                return;

            ScenePlayerSprite = bitmap;
            (ScenePlayerWidth, ScenePlayerHeight, ScenePlayerMargin) =
                Stand(bitmap, PlayerFeetX, PlayerFeetY, PlayerScale);
        }
        else
        {
            if (opponentSceneFor != key)
                return;

            SceneOpponentSprite = bitmap;
            (SceneOpponentWidth, SceneOpponentHeight, SceneOpponentMargin) =
                Stand(bitmap, OpponentFeetX, OpponentFeetY, OpponentScale);
        }
    }

    /// <summary>§201. A trimmed sprite's size and position on the field, in
    /// the field's own coordinates. The scale multiplies the TRIMMED pixels,
    /// which is what keeps the relative sizes honest: every library sprite
    /// shares a 96x96 canvas, so a Caterpie occupies less of it than a
    /// Snorlax and still comes out smaller here. Scaling both to one height
    /// would have thrown that away.</summary>
    private static (double Width, double Height, Thickness Margin) Stand(
        Bitmap? sprite, double feetX, double feetY, double scale)
    {
        if (sprite == null)
            return (0, 0, default);

        double width = sprite.PixelSize.Width * scale;
        double height = sprite.PixelSize.Height * scale;

        return (width, height, new Thickness(feetX - width / 2, feetY - height, 0, 0));
    }

    private async Task LoadBattleSpriteAsync(string key, PokemonState pokemon, bool isPlayer)
    {
        // The ladder, most specific first:
        //
        //   §199 SpritePath - an author naming one exact file. Wins outright.
        //   §200 DexNumber  - an author naming one exact form.
        //        the species name - a guess, and the only rung that existed
        //        before. It cannot tell a Hisuian Typhlosion from a Johto one.
        //
        // Every rung falls through on a miss rather than leaving the view
        // empty; a broken path is reported in the editor, where the author
        // can still do something about it.
        Bitmap? bitmap = await spriteProvider.GetSpriteFromPathAsync(pokemon.SpritePath)
                         ?? await spriteProvider.GetSpriteByDexAsync(pokemon.DexNumber)
                         ?? await spriteProvider.GetSpriteAsync(pokemon.Species, pokemon.IsShiny);

        if (isPlayer && playerSpriteFor == key)
            PlayerSprite = bitmap;
        else if (!isPlayer && opponentSpriteFor == key)
            OpponentSprite = bitmap;
    }

    private void RefreshMoveButtons()
    {
        var buttons = new[] { MoveButton1, MoveButton2, MoveButton3, MoveButton4 };

        foreach (var button in buttons)
        {
            button.Label = "-";
            button.Detail = "";
            button.IsEnabled = false;
        }

        if (session == null || BattleOver || MustReplace)
            return;

        var active = session.Player.ActivePokemon;
        IReadOnlyList<BattleAction> legal = session.PlayerLegalActions();

        var legalMoves = legal.Where(a => a.Move != null).Select(a => a.Move!).ToList();

        if (legalMoves.Count == 1 && legalMoves[0].Name == "Struggle")
        {
            MoveButton1.Label = "Struggle";
            MoveButton1.Detail = "No PP left on any move";
            MoveButton1.IsEnabled = !IsResolving;
            return;
        }

        for (int i = 0; i < active.Moves.Count && i < buttons.Length; i++)
        {
            MoveState move = active.Moves[i];

            buttons[i].Label = move.Name;
            buttons[i].Detail = $"{move.Type} - {move.Category} - PP {move.CurrentPP}/{move.MaxPP}";
            buttons[i].IsEnabled = !IsResolving && legalMoves.Contains(move);
        }

        // §161: with Z-Power armed, eligible moves show their Z-Move and
        // the rest sit out until the toggle is released.
        if (ZArmed)
        {
            for (int i = 0; i < active.Moves.Count && i < buttons.Length; i++)
            {
                MoveState move = active.Moves[i];

                if (ZMoves.CanUse(session.State, session.Player, active, move))
                {
                    buttons[i].Label = ZMoves.MoveNameFor(move.Type);
                    buttons[i].Detail = $"{move.Type} - {move.Category} - {ZMoves.ZPower(move.Power)} power - via {move.Name}";
                }
                else
                {
                    buttons[i].IsEnabled = false;
                }
            }
        }
    }

    private void RefreshBattleTeamRow()
    {
        if (session == null)
            return;

        IReadOnlyList<int> switchable = BattleOver
            ? new List<int>()
            : MustReplace
                ? session.PlayerReplacementChoices()
                : session.PlayerLegalActions()
                    .Where(a => a.Type == BattleActionType.Switch && a.SwitchTarget != null)
                    .Select(a => session.Player.Team.IndexOf(a.SwitchTarget!))
                    .ToList();

        foreach (SimulatorTeamSlotItem item in BattleTeam)
        {
            var pokemon = session.Player.Team[item.TeamIndex];

            item.IsActive = ReferenceEquals(pokemon, session.Player.ActivePokemon);
            item.SpriteOpacity = pokemon.Fainted ? 0.3 : 1.0;
            item.HpTip = $"{pokemon.Species}  {Math.Max(0, pokemon.CurrentHP)}/{pokemon.MaxHP} HP" +
                         (pokemon.Status != StatusCondition.None ? $"  ({pokemon.Status})" : string.Empty) +
                         (pokemon.HeldItemId != null ? $"  @ {HeldItems.DisplayName(pokemon.HeldItemId)}" : string.Empty);

            // §160: the switch-confirmation card shows the item outright.
            item.ItemText = DescribeItem(pokemon);
            item.ItemSprite = pokemon.HeldItemId != null ? ItemSpriteService.GetSprite(pokemon.HeldItemId) : null;
            item.CanSwitch = !IsResolving && switchable.Contains(item.TeamIndex) &&
                             (MustReplace || !item.IsActive);
        }
    }

    /// <summary>Let the current observer finish writing and drop the
    /// reference. Called when a new battle starts and on window close.</summary>
    private void RetireObserver()
    {
        BattleObserver? retiring = observer;
        observer = null;
        ObserverActive = false;
        ObserverBattleText = string.Empty;

        if (retiring != null)
            _ = retiring.DisposeAsync();
    }

    public void Dispose()
    {
        // §156: a battle still running gets its observation cut off
        // cleanly - Cancel abandons queued writes instead of blocking
        // window close on the disk.
        if (session != null && session.Outcome == BattleOutcome.Unfinished)
            observer?.Cancel();

        RetireObserver();

        (shadowEvaluator as IDisposable)?.Dispose();

        battleCts?.Cancel();
        battleCts?.Dispose();

        if (session != null && session.Outcome == BattleOutcome.Unfinished)
            session.Cancel();
    }
}

/// <summary>§154. One of the four battle move buttons.</summary>
public sealed partial class SimulatorMoveButton : ObservableObject
{
    [ObservableProperty] private string label = "-";
    [ObservableProperty] private string detail = "";
    [ObservableProperty] private bool isEnabled;
}
