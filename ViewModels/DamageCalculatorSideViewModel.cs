using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using PokemonSim.Data;
using PokemonSim.Engine.Items;
using PokemonSim.Simulation;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// §261. One of a side's four move slots: the move it holds, what that move is,
/// and what it does to the other side. The result lines are written by
/// DamageCalculatorViewModel, which is the only thing that can see both sides
/// and the field at once.
/// </summary>
public sealed partial class DamageMoveSlot : ViewModelBase
{
    /// <summary>1..4, for the slot's label.</summary>
    public required int Number { get; init; }

    /// <summary>The move names this slot offers - the side's list, replaced
    /// whole whenever the species or the learnset filter changes. Every slot
    /// on a side shows the same list, so the filter is set once per side
    /// rather than once per slot.</summary>
    [ObservableProperty] private IReadOnlyList<string> moveOptions = Array.Empty<string>();

    [ObservableProperty] private string? selectedMoveName;

    /// <summary>"Ice - Special - 110", or why this move has no number.</summary>
    [ObservableProperty] private string moveInfo = "";

    /// <summary>The whole Showdown sentence, shown in the banner when this
    /// slot is the selected one.</summary>
    [ObservableProperty] private string sentence = "";

    [ObservableProperty] private bool hasResult;

    /// <summary>§263. The one slot whose sentence the banner is showing - the
    /// radio button beside the move. Exclusivity is enforced by the parent
    /// rather than by RadioButton's own grouping, because the eight slots live
    /// in two separate ItemsControls inside two separate ContentControls and
    /// nothing about that tree guarantees one group.</summary>
    [ObservableProperty] private bool isSelected;
}

/// <summary>
/// §261. One side of the damage calculator - the Pokemon, its spread, its four
/// moves and the screens standing on its own half of the field.
///
/// The old window had an attacker column and a defender column that shared
/// nothing: roughly fifty properties, each written twice with an Attacker or
/// Defender prefix, and two separate ability lists because each side only ever
/// played one role. This window computes both directions at once, so a side is
/// a thing rather than a position, and there is one of these per column.
/// </summary>
public sealed partial class DamageCalculatorSideViewModel : ViewModelBase
{
    /// <summary>"Attacker" / "Defender" - the column heading, and the word the
    /// result sentences use.</summary>
    public required string Title { get; init; }

    /// <summary>Raised whenever anything on this side changes that could move
    /// a number. The parent recomputes both directions; a side never
    /// calculates anything itself, because it can only see half the fight.</summary>
    public event Action? Changed;

    /// <summary>§263. Raised when one of this side's slots is picked as the one
    /// to show in the banner. The parent owns the exclusivity, because it is
    /// the only thing that can see all eight slots.</summary>
    public event Action<DamageMoveSlot>? SlotSelected;

    public IReadOnlyList<string> NatureOptions { get; } = PokemonBattleMath.NatureNames;
    /// <summary>
    /// §307. The abilities THIS Pokemon can have, rather than every ability
    /// the calculator happened to implement.
    ///
    /// It used to be a fixed list of thirteen, offered to everything - so
    /// Ferrothorn was offered Levitate, Huge Power and Flash Fire, none of
    /// which it can have, and was not offered Iron Barbs, which it does. The
    /// reason was the data: only 61 of 804 species had any abilities recorded.
    /// §307 filled in the other 743 from Showdown's dex, so the list can be
    /// the species' own now, exactly as the move list is its learnset.
    ///
    /// A species the dex does not know falls back to the old fixed list
    /// rather than to an empty box.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<string> abilityOptions = PokemonBattleMath.AllAbilities;
    /// <summary>
    /// §309: the engine's hundred and five, where §261 wrote down seven.
    ///
    /// Unlike the ability list this sits beside, it is NOT built per species -
    /// any Pokemon can hold any item, so there is nothing to filter by. What
    /// there is instead is a question of whether an item can change anything
    /// in a window that asks "what does this hit do": the weather rocks, the
    /// mega stones and the Z-Crystals cannot, and they are offered anyway
    /// with the banner saying so, because a picker that quietly omits what
    /// somebody is holding is worse than one that admits it does nothing.
    /// </summary>
    public IReadOnlyList<string> ItemOptions { get; } = BuildItemOptions();

    static IReadOnlyList<string> BuildItemOptions()
    {
        try
        {
            var names = new List<string> { "None" };
            names.AddRange(HeldItems.DisplayNamesInOrder);
            return names;
        }
        catch
        {
            // Same fallback the ability list takes: the old fixed seven
            // rather than an empty box.
            return PokemonBattleMath.AttackerItems;
        }
    }

    /// <summary>
    /// §306: all six, where §261 offered Burn alone.
    ///
    /// Burn was the only one that changed a damage roll while the calculator
    /// did its own arithmetic. Now that the simulator answers what a move's
    /// power is, four more of them do: Facade doubles off any status of the
    /// user's, Hex and Venoshock off the target's, and Wake-Up Slap doubles
    /// into sleep.
    /// So each of these can now change a number, which is the test §103 asks
    /// a control to pass.
    /// </summary>
    public IReadOnlyList<string> StatusOptions { get; } =
        new[] { "None", "Burned", "Poisoned", "Badly Poisoned", "Paralyzed", "Asleep", "Frozen" };

    /// <summary>§306. 0 to 255, the way the games count it. 255 is the
    /// maximum and the simulator's own default, so Return opens at the 102
    /// power it has always shown.</summary>
    public IReadOnlyList<string> HappinessOptions { get; } =
        new[] { "255", "200", "160", "128", "100", "64", "32", "0" };

    /// <summary>§306. Nought to three layers, the way the move stacks.</summary>
    public IReadOnlyList<string> SpikesOptions { get; } = new[] { "0", "1", "2", "3" };

    public IReadOnlyList<string> StageOptions { get; } =
        new[] { "+6", "+5", "+4", "+3", "+2", "+1", "0", "-1", "-2", "-3", "-4", "-5", "-6" };

    /// <summary>§262: four, not six. Six was the mockup's count and it made
    /// the window far taller than it needed to be - a Pokemon carries four
    /// moves, which is also what Showdown shows.</summary>
    public const int MoveSlotCount = 4;

    public ObservableCollection<DamageMoveSlot> MoveSlots { get; } = new();

    // ---- the Pokemon ----
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private IReadOnlyList<string> speciesNames = Array.Empty<string>();
    [ObservableProperty] private string? selectedSpeciesName;
    /// <summary>§262. The chosen Pokemon's sprite, shown beside the spread
    /// controls. Null until something is picked, and for anything the sprite
    /// library does not have.</summary>
    [ObservableProperty] private Bitmap? sprite;

    public bool HasSprite => Sprite is not null;

    [ObservableProperty] private string levelText = "100";
    [ObservableProperty] private string? selectedNature = "Hardy";
    [ObservableProperty] private string? selectedAbility = "None";
    [ObservableProperty] private string? selectedItem = "None";
    [ObservableProperty] private string? selectedStatus = "None";

    // ---- the spread ----
    [ObservableProperty] private string ivHpText = "31";
    [ObservableProperty] private string ivAttackText = "31";
    [ObservableProperty] private string ivDefenseText = "31";
    [ObservableProperty] private string ivSpAttackText = "31";
    [ObservableProperty] private string ivSpDefenseText = "31";
    [ObservableProperty] private string ivSpeedText = "31";

    [ObservableProperty] private string evHpText = "0";
    [ObservableProperty] private string evAttackText = "0";
    [ObservableProperty] private string evDefenseText = "0";
    [ObservableProperty] private string evSpAttackText = "0";
    [ObservableProperty] private string evSpDefenseText = "0";
    [ObservableProperty] private string evSpeedText = "0";

    // §265: one stage per stat, not one per role. Until now a side had a
    // single "offense" stage applied to whichever of Attack and Sp. Atk the
    // move used, and one "defense" stage for whichever it hit - so a Swords
    // Dance and a Nasty Plot were the same control, and a Calm Mind could not
    // be told from an Iron Defense. Four stages is what the game has.
    [ObservableProperty] private string? selectedAtkStage = "0";
    [ObservableProperty] private string? selectedDefStage = "0";
    [ObservableProperty] private string? selectedSpAStage = "0";
    [ObservableProperty] private string? selectedSpDStage = "0";

    // ---- this side's half of the field ----
    // ---- §306: what the simulator needs in order to answer a move ----

    /// <summary>Percent of maximum HP still standing. Reversal, Flail,
    /// Eruption and Water Spout read the user's; Hard Press, Crush Grip and
    /// Wring Out read the target's. §262 removed the old "At full HP"
    /// checkbox as a control with one sensible answer - this is not that
    /// control, because for these moves every value is a different answer.</summary>
    [ObservableProperty] private string hpPercentText = "100";

    /// <summary>Return and Frustration, and nothing else in the game.</summary>
    [ObservableProperty] private string? selectedHappiness = "255";

    /// <summary>Rage Fist: fifty more power per hit taken, up to 350.</summary>
    [ObservableProperty] private string timesAttackedText = "0";

    /// <summary>Fury Cutter, Echoed Voice and Rollout, counting this use. One
    /// is the first use, which is what a calculator is usually asked about.</summary>
    [ObservableProperty] private string consecutiveUsesText = "1";

    // ---- §306: this side's half of the field ----

    /// <summary>Charge doubles the user's next Electric move. The simulator
    /// spends it in DamageCalculator, so the bridge only has to say it is
    /// up.</summary>
    [ObservableProperty] private bool charge;

    /// <summary>Foresight lets Normal and Fighting moves reach a Ghost.</summary>
    [ObservableProperty] private bool foresight;

    /// <summary>Tailwind doubles this side's Speed, which Gyro Ball and
    /// Electro Ball both read.</summary>
    [ObservableProperty] private bool tailwind;

    /// <summary>Flower Gift: in sun, Cherrim and its allies get half again
    /// their Attack and Special Defense.</summary>
    [ObservableProperty] private bool flowerGift;

    // ---- §306: the residuals. None of these changes a damage roll; they
    // change how many hits a KO takes, which is what the sentence counts. ----

    [ObservableProperty] private bool stealthRock;

    /// <summary>0 to 3 layers.</summary>
    [ObservableProperty] private string? selectedSpikes = "0";

    [ObservableProperty] private bool leechSeed;
    [ObservableProperty] private bool reflect;
    [ObservableProperty] private bool lightScreen;
    [ObservableProperty] private bool auroraVeil;

    public DamageCalculatorSideViewModel()
    {
        for (int i = 1; i <= MoveSlotCount; i++)
        {
            var slot = new DamageMoveSlot { Number = i };
            slot.PropertyChanged += OnSlotPropertyChanged;
            MoveSlots.Add(slot);
        }

        speciesNames = CalculatorDataService.AllSpeciesNames;
    }

    /// <summary>The looked-up species, or null while nothing is picked.</summary>
    public CalculatorDataService.CalcSpecies? Species { get; private set; }

    public bool Burned => string.Equals(SelectedStatus, "Burned", StringComparison.OrdinalIgnoreCase);

    public int Level => ParseClamped(LevelText, 1, 100, 100);

    public IReadOnlyList<string> Types => Species?.Types ?? Array.Empty<string>();

    /// <summary>§261: Flying and Levitate hover - the battle engine's own rule,
    /// which is why it lives in PokemonBattleMath rather than being written
    /// again here.</summary>
    public bool Grounded => PokemonBattleMath.IsGrounded(Types, SelectedAbility);

    public int MaxHp => Species is null
        ? 0
        : PokemonBattleMath.CalculateHp(
            Species.BaseHp,
            ParseClamped(IvHpText, 0, 31, 0),
            ParseClamped(EvHpText, 0, 252, 0),
            Level);

    /// <summary>§265. The attacking stage for a move of this category -
    /// Attack for a physical one, Sp. Atk for a special one.</summary>
    public int OffenseStage(bool physical) =>
        ParseStage(physical ? SelectedAtkStage : SelectedSpAStage);

    /// <summary>§265. And the defending stage the incoming move meets.</summary>
    public int DefenseStage(bool physical) =>
        ParseStage(physical ? SelectedDefStage : SelectedSpDStage);

    /// <summary>The attacking stat for a move of this category, before stages
    /// (which the damage chain applies itself).</summary>
    public int OffenseStat(bool physical) => Species is null ? 0 : physical
        ? PokemonBattleMath.CalculateStat(Species.BaseAttack,
            ParseClamped(IvAttackText, 0, 31, 0), ParseClamped(EvAttackText, 0, 252, 0),
            Level, PokemonBattleMath.GetNatureMultiplier(SelectedNature ?? "Hardy", "Attack"))
        : PokemonBattleMath.CalculateStat(Species.BaseSpAttack,
            ParseClamped(IvSpAttackText, 0, 31, 0), ParseClamped(EvSpAttackText, 0, 252, 0),
            Level, PokemonBattleMath.GetNatureMultiplier(SelectedNature ?? "Hardy", "SpAttack"));

    public int DefenseStat(bool physical) => Species is null ? 0 : physical
        ? PokemonBattleMath.CalculateStat(Species.BaseDefense,
            ParseClamped(IvDefenseText, 0, 31, 0), ParseClamped(EvDefenseText, 0, 252, 0),
            Level, PokemonBattleMath.GetNatureMultiplier(SelectedNature ?? "Hardy", "Defense"))
        : PokemonBattleMath.CalculateStat(Species.BaseSpDefense,
            ParseClamped(IvSpDefenseText, 0, 31, 0), ParseClamped(EvSpDefenseText, 0, 252, 0),
            Level, PokemonBattleMath.GetNatureMultiplier(SelectedNature ?? "Hardy", "SpDefense"));

    // ---- §306: what the bridge asks a side for ----

    /// <summary>Percent of maximum HP, 1 to 100. Never zero: a Pokemon at
    /// zero has fainted and there is no calculation to do.</summary>
    public int HpPercent => ParseClamped(HpPercentText, 1, 100, 100);

    /// <summary>The HP that percentage comes to, rounded the way a health bar
    /// rounds - at least one, because the Pokemon is still standing.</summary>
    public int CurrentHp => MaxHp <= 0 ? 0 : Math.Max(1, MaxHp * HpPercent / 100);

    public int Happiness => ParseClamped(SelectedHappiness, 0, 255, 255);

    public int TimesAttacked => ParseClamped(TimesAttackedText, 0, 6, 0);

    public int ConsecutiveUses => ParseClamped(ConsecutiveUsesText, 1, 5, 1);

    public int SpikesLayers => ParseClamped(SelectedSpikes, 0, 3, 0);

    /// <summary>The Speed the simulator should see - Tailwind doubles a whole
    /// side's, and Gyro Ball and Electro Ball are both built on it.</summary>
    public int SpeedStat => Species is null ? 0 :
        PokemonBattleMath.CalculateStat(Species.BaseSpeed,
            ParseClamped(IvSpeedText, 0, 31, 0), ParseClamped(EvSpeedText, 0, 252, 0),
            Level, PokemonBattleMath.GetNatureMultiplier(SelectedNature ?? "Hardy", "Speed"))
        * (Tailwind ? 2 : 1);

    /// <summary>§306. Everything this side is, in the terms the bridge takes.
    ///
    /// §308: HurtThisTurn, LastMoveFailed and DefenseCurled are deliberately
    /// NOT set here. The window stopped asking for them, so every side this
    /// method builds has them false - which is the unconditional reading of
    /// Avalanche, Assurance, Stomping Tantrum and Rollout, and the reading
    /// the row shows. DamageCalculatorViewModel.ConditionalNote turns each of
    /// them on afterwards, one at a time, on a throwaway copy, purely to find
    /// out what the other number would be and say so.</summary>
    public MoveOracle.Side ToBridgeSide()
    {
        return new MoveOracle.Side
        {
            Species = SelectedSpeciesName ?? "",
            Types = Types,
            Level = Level,
            MaxHp = MaxHp,
            CurrentHp = CurrentHp,
            Attack = OffenseStat(physical: true),
            Defense = DefenseStat(physical: true),
            SpAttack = OffenseStat(physical: false),
            SpDefense = DefenseStat(physical: false),
            Speed = SpeedStat,
            AttackStage = OffenseStage(physical: true),
            DefenseStage = DefenseStage(physical: true),
            SpAttackStage = OffenseStage(physical: false),
            SpDefenseStage = DefenseStage(physical: false),
            Status = SelectedStatus ?? "None",
            Ability = SelectedAbility ?? "None",
            Item = SelectedItem ?? "None",
            Happiness = Happiness,
            TimesAttacked = TimesAttacked,
            ConsecutiveUses = ConsecutiveUses,
            Charged = Charge,
        };
    }

    /// <summary>The six IVs in GetHiddenPowerType's order - HP, Atk, Def,
    /// SPEED, SpA, SpD. Speed sits third, not last; see PokemonBattleMath's
    /// own comment on that method.</summary>
    public IReadOnlyList<int> HiddenPowerIvs => new[]
    {
        ParseClamped(IvHpText, 0, 31, 0),
        ParseClamped(IvAttackText, 0, 31, 0),
        ParseClamped(IvDefenseText, 0, 31, 0),
        ParseClamped(IvSpeedText, 0, 31, 0),
        ParseClamped(IvSpAttackText, 0, 31, 0),
        ParseClamped(IvSpDefenseText, 0, 31, 0),
    };

    /// <summary>"252+ SpA Abomasnow" - the way Showdown names a spread in its
    /// output line. The nature sign is shown only when the nature actually
    /// moves the stat this move uses.</summary>
    public string DescribeOffense(bool physical)
    {
        string ev = physical ? EvAttackText : EvSpAttackText;
        string statName = physical ? "Atk" : "SpA";
        double nature = PokemonBattleMath.GetNatureMultiplier(SelectedNature ?? "Hardy", physical ? "Attack" : "SpAttack");
        string sign = nature > 1.0 ? "+" : nature < 1.0 ? "-" : "";

        return $"{ParseClamped(ev, 0, 252, 0)}{sign} {statName} {SelectedSpeciesName}";
    }

    public string DescribeDefense(bool physical)
    {
        string ev = physical ? EvDefenseText : EvSpDefenseText;
        string statName = physical ? "Def" : "SpD";

        return $"{ParseClamped(EvHpText, 0, 252, 0)} HP / {ParseClamped(ev, 0, 252, 0)} {statName} {SelectedSpeciesName}";
    }

    // ---- reacting to edits -------------------------------------------------

    partial void OnSearchTextChanged(string value) => SpeciesNames = BuildSpeciesList();

    partial void OnSelectedSpeciesNameChanged(string? value)
    {
        Species = CalculatorDataService.Find(value);

        // §344: the display door, not the OCR one. These names come from
        // calc-pokedex.json, where every alternate form is spelled the way
        // the roster spells it rather than the way the sprite library does.
        Sprite = Species is null ? null : PokemonSpriteService.GetDisplaySprite(Species.Name);

        // Neither is an ObservableProperty - they are computed from Species,
        // which is a plain field, so the view is told by hand or not at all.
        OnPropertyChanged(nameof(HasSprite));
        OnPropertyChanged(nameof(Types));

        RebuildMoveOptions();
        RebuildAbilityOptions();
        Raise();
    }

    // Every other edit just means "recompute". Listing them one per line is
    // noisy but it is the whole mechanism, and a missed one is a control that
    // silently does nothing until something else is touched.
    partial void OnLevelTextChanged(string value) => Raise();
    partial void OnSelectedNatureChanged(string? value) => Raise();
    partial void OnSelectedAbilityChanged(string? value) => Raise();
    partial void OnSelectedItemChanged(string? value) => Raise();
    partial void OnSelectedStatusChanged(string? value) => Raise();
    partial void OnIvHpTextChanged(string value) => Raise();
    partial void OnIvAttackTextChanged(string value) => Raise();
    partial void OnIvDefenseTextChanged(string value) => Raise();
    partial void OnIvSpAttackTextChanged(string value) => Raise();
    partial void OnIvSpDefenseTextChanged(string value) => Raise();
    partial void OnIvSpeedTextChanged(string value) => Raise();
    partial void OnEvHpTextChanged(string value) => Raise();
    partial void OnEvAttackTextChanged(string value) => Raise();
    partial void OnEvDefenseTextChanged(string value) => Raise();
    partial void OnEvSpAttackTextChanged(string value) => Raise();
    partial void OnEvSpDefenseTextChanged(string value) => Raise();
    partial void OnEvSpeedTextChanged(string value) => Raise();
    partial void OnSelectedAtkStageChanged(string? value) => Raise();
    partial void OnSelectedDefStageChanged(string? value) => Raise();
    partial void OnSelectedSpAStageChanged(string? value) => Raise();
    partial void OnSelectedSpDStageChanged(string? value) => Raise();
    partial void OnReflectChanged(bool value) => Raise();
    partial void OnLightScreenChanged(bool value) => Raise();
    partial void OnAuroraVeilChanged(bool value) => Raise();

    // §306: every new input moves a number, so every one of them recomputes.
    partial void OnHpPercentTextChanged(string value) => Raise();
    partial void OnSelectedHappinessChanged(string? value) => Raise();
    partial void OnTimesAttackedTextChanged(string value) => Raise();
    partial void OnConsecutiveUsesTextChanged(string value) => Raise();
    partial void OnChargeChanged(bool value) => Raise();
    partial void OnForesightChanged(bool value) => Raise();
    partial void OnTailwindChanged(bool value) => Raise();
    partial void OnFlowerGiftChanged(bool value) => Raise();
    partial void OnStealthRockChanged(bool value) => Raise();
    partial void OnSelectedSpikesChanged(string? value) => Raise();
    partial void OnLeechSeedChanged(bool value) => Raise();

    private void OnSlotPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only these two matter here. The rest are what the parent WRITES
        // back onto the slot after a calculation, and reacting to those would
        // recompute forever.
        if (e.PropertyName == nameof(DamageMoveSlot.SelectedMoveName))
            Raise();
        else if (e.PropertyName == nameof(DamageMoveSlot.IsSelected) &&
                 sender is DamageMoveSlot { IsSelected: true } picked)
        {
            // Only the turning-ON edge. Clearing the other seven raises this
            // with false, and acting on that would chase its own tail.
            SlotSelected?.Invoke(picked);
        }
    }

    private void Raise() => Changed?.Invoke();

    private IReadOnlyList<string> BuildSpeciesList()
    {
        string needle = SearchText.Trim();

        if (needle.Length == 0)
            return CalculatorDataService.AllSpeciesNames;

        return CalculatorDataService.AllSpeciesNames
            .Where(n => n.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// The move list every slot on this side offers: damaging moves only,
    /// because a status move has no damage line to show, narrowed to this
    /// species' learnset.
    ///
    /// §262: the learnset filter was a checkbox and is now simply how it
    /// works. Nobody wants a calculator offering a Pokemon moves it cannot
    /// learn, and the one case the checkbox existed for - a species whose
    /// learnset the data file does not carry - is handled by the guard below
    /// rather than by asking the player to notice and switch it off.
    ///
    /// §306a: "damaging" cannot be decided from THIS file's power alone. It
    /// carries no power for twenty-six moves whose power is not a constant -
    /// Low Kick, Reversal, Seismic Toss and the rest - and §306 taught the
    /// calculator to answer every one of them by asking the simulator. A list
    /// filtered on a null power dropped all twenty-six before the learnset
    /// was even consulted, so the window knew the answer and never offered
    /// the question. The simulator is asked here too.
    /// </summary>
    /// <summary>§307. "None", then whatever the dex says this species can
    /// have. The current pick is kept if the new species can also have it -
    /// comparing two Arcanine builds should not silently reset the ability
    /// every time the other side changes.</summary>
    private void RebuildAbilityOptions()
    {
        IReadOnlyList<string> own = PokemonDex.AbilitiesOf(SelectedSpeciesName);

        if (own.Count == 0)
        {
            AbilityOptions = PokemonBattleMath.AllAbilities;
        }
        else
        {
            var list = new List<string> { "None" };
            list.AddRange(own);
            AbilityOptions = list;
        }

        if (SelectedAbility is { Length: > 0 } picked &&
            !AbilityOptions.Contains(picked, StringComparer.OrdinalIgnoreCase))
        {
            SelectedAbility = "None";
        }
    }

    private void RebuildMoveOptions()
    {
        IEnumerable<MoveData> moves = MoveLookupService.AllMoves
            .Where(m => (string.Equals(m.Category, "Physical", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(m.Category, "Special", StringComparison.OrdinalIgnoreCase)) &&
                        (m.Power is > 0 || MoveOracle.IsDamaging(m.Name)));

        if (Species is { LearnsetMoveNames.Count: > 0 })
        {
            var learnset = new HashSet<string>(Species.LearnsetMoveNames, StringComparer.OrdinalIgnoreCase);
            moves = moves.Where(m => learnset.Contains(m.Name));
        }

        string[] names = moves.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToArray();

        foreach (DamageMoveSlot slot in MoveSlots)
        {
            slot.MoveOptions = names;

            // A move the new list does not contain would sit in the slot
            // looking chosen while the box shows nothing.
            if (slot.SelectedMoveName is { Length: > 0 } picked &&
                !names.Contains(picked, StringComparer.OrdinalIgnoreCase))
            {
                slot.SelectedMoveName = null;
            }
        }
    }

    internal static int ParseClamped(string? text, int min, int max, int fallback) =>
        int.TryParse((text ?? "").Trim(), out int value) ? Math.Clamp(value, min, max) : fallback;

    internal static int ParseStage(string? stageText) =>
        int.TryParse((stageText ?? "0").Trim(), out int value) ? Math.Clamp(value, -6, 6) : 0;
}
