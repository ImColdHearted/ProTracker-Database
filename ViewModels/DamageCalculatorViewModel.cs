using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using PokemonSim.Data;
using PokemonSim.Engine.Items;
using PokemonSim.Simulation;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// §261. The Damage Calculator, rebuilt in Showdown's shape: two sides that
/// each hold a Pokemon, a spread and four moves, a field between them, and a
/// damage line for every one of the eight moves at once.
///
/// What changed and why:
///
/// The old window had one attacker, one defender and ONE move, and answered
/// only the question you had already narrowed down to. Four moves a side, both
/// directions, answers the question people actually open a calculator with -
/// what trades - without re-picking anything. (§261 built this with six slots
/// a side; §262 cut it to four - what a Pokemon carries, and what Showdown
/// shows - because twelve rows made the window far taller than the answer
/// needed. §262 also dropped the base-stat and HP readouts, the "At full HP"
/// checkbox and the learnset toggle: three readouts nobody was reading and two
/// choices that had one sensible answer each.)
///
/// The two columns are now one class used twice
/// (DamageCalculatorSideViewModel), where they used to be fifty properties
/// written out twice with Attacker and Defender prefixes.
///
/// Terrain and weather are toggle buttons rather than dropdowns, because they
/// are states of the field rather than a list to scroll, and because the field
/// column is where a Showdown user looks for them. Only one terrain and one
/// weather can be up at a time, so pressing one clears its siblings and
/// pressing the lit one turns it off.
///
/// The percentages TRUNCATE rather than round. That is not a nicety: 162 of
/// 321 HP is 50.467%, which Showdown prints as 50.4%, and a calculator whose
/// numbers do not match the one people cross-check against gets distrusted for
/// a rounding rule. The old window rounded, and printed 50.5%.
///
/// §308 took eleven controls back out, on the rule that a window should ask
/// only about things PRO actually has. Helping Hand and Friend Guard are an
/// ally's and PRO has no doubles; Salt Cure is a generation past it; Protect
/// and Power Trick moved no roll worth a control. The other six - moving
/// first, the target switching out, hurt this turn, last move failed,
/// Defense Curl - fed eleven real moves, and dropping a control that feeds a
/// real move is normally how a calculator starts lying.
///
/// It does not lie here, because ConditionalNote measures each of the six off
/// the simulator and the move's own line says what the other number would be.
/// That is the trade this section is: the window shows Pursuit's plain 40
/// without making anyone tick a box first, and tells them 80 is what it does
/// to something that is leaving.
/// </summary>
public sealed partial class DamageCalculatorViewModel : ViewModelBase
{
    public DamageCalculatorSideViewModel Attacker { get; } = new() { Title = "Attacker" };
    public DamageCalculatorSideViewModel Defender { get; } = new() { Title = "Defender" };

    /// <summary>The eight rows, attacker's four first. Kept as one list so the
    /// banner can point at any of them without caring which way it runs.</summary>
    public ObservableCollection<DamageMoveSlot> AttackerRows => Attacker.MoveSlots;
    public ObservableCollection<DamageMoveSlot> DefenderRows => Defender.MoveSlots;

    // ---- the field ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsElectricTerrain))]
    [NotifyPropertyChangedFor(nameof(IsGrassyTerrain))]
    [NotifyPropertyChangedFor(nameof(IsMistyTerrain))]
    [NotifyPropertyChangedFor(nameof(IsPsychicTerrain))]
    private string selectedTerrain = "None";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSun))]
    [NotifyPropertyChangedFor(nameof(IsRain))]
    [NotifyPropertyChangedFor(nameof(IsSandstorm))]
    [NotifyPropertyChangedFor(nameof(IsHail))]
    private string selectedWeather = "None";

    public bool IsElectricTerrain => SelectedTerrain == "Electric";
    public bool IsGrassyTerrain => SelectedTerrain == "Grassy";
    public bool IsMistyTerrain => SelectedTerrain == "Misty";
    public bool IsPsychicTerrain => SelectedTerrain == "Psychic";

    // ---- §306: the rest of the field ----

    /// <summary>Wonder Room swaps every Pokemon's Defense and Special
    /// Defense - so a physical hit meets the Sp. Def stat and the other way
    /// round. The stages stay with their own stat, which is the part people
    /// get wrong.</summary>
    [ObservableProperty] private bool wonderRoom;

    /// <summary>Magic Room switches every held item off.</summary>
    [ObservableProperty] private bool magicRoom;

    /// <summary>Gravity grounds everything, which is the only thing about it
    /// that changes a damage roll: a grounded attacker gets its terrain's
    /// boost, and a grounded target can be shielded by Misty.</summary>
    [ObservableProperty] private bool gravity;

    public bool IsSun => SelectedWeather == "Sun";
    public bool IsRain => SelectedWeather == "Rain";
    public bool IsSandstorm => SelectedWeather == "Sandstorm";
    public bool IsHail => SelectedWeather == "Hail (Snow)";

    // ---- the banner ----
    [ObservableProperty] private string bannerSentence =
        "Pick a Pokemon on each side and give one of them a move.";
    [ObservableProperty] private string statusMessage = "";

    public DamageCalculatorViewModel()
    {
        Attacker.Changed += Recalculate;
        Defender.Changed += Recalculate;

        Attacker.SlotSelected += OnSlotSelected;
        Defender.SlotSelected += OnSlotSelected;

        if (CalculatorDataService.AllSpeciesNames.Count == 0)
        {
            statusMessage = "Pokemon data couldn't be loaded, so both pickers are empty.";
        }
        else if (MoveLookupService.AllMoves.Count == 0)
        {
            statusMessage = "Move data couldn't be loaded (SharedPokemonLibrary/Data/Moves/moves.json " +
                            "is missing or unreadable), so the move slots are empty.";
        }
    }

    partial void OnSelectedTerrainChanged(string value) => Recalculate();
    partial void OnSelectedWeatherChanged(string value) => Recalculate();
    partial void OnWonderRoomChanged(bool value) => Recalculate();
    partial void OnMagicRoomChanged(bool value) => Recalculate();
    partial void OnGravityChanged(bool value) => Recalculate();

    /// <summary>Press a terrain to raise it, press the lit one to clear it.
    /// One terrain at a time, as in a real battle.</summary>
    [RelayCommand]
    private void ToggleTerrain(string? terrain) =>
        SelectedTerrain = SelectedTerrain == terrain ? "None" : terrain ?? "None";

    [RelayCommand]
    private void ToggleWeather(string? weather) =>
        SelectedWeather = SelectedWeather == weather ? "None" : weather ?? "None";

    /// <summary>§263. One slot at a time is the one being calculated - its
    /// radio button is lit and the banner is showing its sentence. The other
    /// seven are cleared here, because this is the only place that can see all
    /// eight of them.</summary>
    private void OnSlotSelected(DamageMoveSlot picked)
    {
        foreach (DamageMoveSlot slot in AllSlots)
        {
            if (!ReferenceEquals(slot, picked))
                slot.IsSelected = false;
        }

        BannerSentence = picked.Sentence;
    }

    private IEnumerable<DamageMoveSlot> AllSlots =>
        Attacker.MoveSlots.Concat(Defender.MoveSlots);

    // ---- the calculation --------------------------------------------------

    private void Recalculate()
    {
        foreach (DamageMoveSlot slot in Attacker.MoveSlots)
            Fill(slot, Attacker, Defender);

        foreach (DamageMoveSlot slot in Defender.MoveSlots)
            Fill(slot, Defender, Attacker);

        UpdateBanner();
    }

    /// <summary>§263. The banner shows whichever slot is selected. A selection
    /// that no longer has an answer - its move was cleared, or the species
    /// changed under it - is dropped, and the first slot that does have one
    /// takes over, so the banner is never left quoting a calculation that is
    /// no longer on screen.</summary>
    private void UpdateBanner()
    {
        DamageMoveSlot? selected = AllSlots.FirstOrDefault(s => s.IsSelected && s.HasResult);

        if (selected is not null)
        {
            BannerSentence = selected.Sentence;
            return;
        }

        // Materialised first: the predicate tests the very property the loop
        // clears, and a lazy query over it is a trap waiting for an edit.
        foreach (DamageMoveSlot stale in AllSlots.Where(s => s.IsSelected).ToList())
            stale.IsSelected = false;

        DamageMoveSlot? first = AllSlots.FirstOrDefault(s => s.HasResult);

        if (first is null)
        {
            BannerSentence = Attacker.Species is null || Defender.Species is null
                ? "Pick a Pokemon on each side and give one of them a move."
                : "Give one of the eight slots a move.";
            return;
        }

        // Setting this raises SlotSelected, which writes the banner.
        first.IsSelected = true;
    }

    private void Fill(DamageMoveSlot slot, DamageCalculatorSideViewModel attacker, DamageCalculatorSideViewModel defender)
    {
        void Clear(string info = "")
        {
            slot.MoveInfo = info;
            slot.Sentence = "";
            slot.HasResult = false;
        }

        if (attacker.Species is null || defender.Species is null)
        {
            Clear();
            return;
        }

        MoveData? move = MoveLookupService.Find(slot.SelectedMoveName);

        if (move is null)
        {
            Clear();
            return;
        }

        bool physical = string.Equals(move.Category, "Physical", StringComparison.OrdinalIgnoreCase);
        bool special = string.Equals(move.Category, "Special", StringComparison.OrdinalIgnoreCase);

        if (!physical && !special)
        {
            Clear($"{move.Type} - Status - no direct damage");
            return;
        }

        // §306. Ask the simulator what this move actually does. It owns the
        // formulas - §158, §303 and §304 built them - and the calculator's
        // own move file cannot answer for sixty-two of them: thirteen it has
        // no power for at all, thirteen more whose answer is a set damage
        // rather than a roll, and thirty-six it would otherwise price at a
        // listed power that never varies.
        MoveOracle.Answer simulator =
            MoveOracle.Ask(move.Name, BuildContext(attacker, defender));

        if (simulator.Kind == MoveOracle.AnswerKind.NoEffect)
        {
            slot.MoveInfo = $"{move.Type} - {move.Category} - no effect";
            slot.Sentence = $"{attacker.DescribeOffense(physical)} {move.Name} vs. " +
                            $"{defender.DescribeDefense(physical)}: 0 damage. {simulator.Note}";
            slot.HasResult = true;
            return;
        }

        // A move that names its own damage has no roll to report: Seismic
        // Toss is the user's level, Super Fang is half what is left, an OHKO
        // is all of it. Showing "x-y (a% - b%)" for any of those would be
        // inventing a spread the game does not have.
        if (simulator.Kind == MoveOracle.AnswerKind.FixedDamage)
        {
            FillFixed(slot, attacker, defender, move, physical, simulator);
            return;
        }

        int power = simulator.Kind == MoveOracle.AnswerKind.Unknown
            ? move.Power ?? 0
            : simulator.Power;

        if (power <= 0)
        {
            Clear($"{move.Type} - {move.Category} - power varies, and the simulator has no rule for it either");
            return;
        }

        // Weather Ball is a different type in every weather, and the
        // simulator is the thing that knows which.
        string moveType = simulator.Kind == MoveOracle.AnswerKind.Unknown ||
                          string.IsNullOrEmpty(simulator.Type)
            ? move.Type
            : simulator.Type;

        string hiddenPowerNote = simulator.Note.Length > 0 ? " " + simulator.Note : "";

        if (move.Name.StartsWith("Hidden Power", StringComparison.OrdinalIgnoreCase))
        {
            moveType = PokemonBattleMath.GetHiddenPowerType(attacker.HiddenPowerIvs);
            hiddenPowerNote += $" Hidden Power came out {moveType} from the attacker's IVs.";
        }

        // The calculator's own move file carries no hit count, so a five-hit
        // Icicle Spear used to read as a 25-power tickle. The simulator's
        // does carry it.
        if (simulator.MaxHits > 1)
        {
            hiddenPowerNote += simulator.MinHits == simulator.MaxHits
                ? $" Hits {simulator.MaxHits} times - the line below is one hit."
                : $" Hits {simulator.MinHits}-{simulator.MaxHits} times - the line below is one hit.";
        }

        slot.MoveInfo = $"{moveType} - {move.Category} - {power}";

        PokemonBattleMath.DamageResult result = PokemonBattleMath.Calculate(new PokemonBattleMath.DamageRequest
        {
            Level = attacker.Level,
            MovePower = power,
            MoveType = moveType,
            MoveCategory = physical ? "Physical" : "Special",
            MoveName = move.Name,
            AttackerTypes = attacker.Types,
            DefenderTypes = defender.Types,

            // §306: Power Trick swaps the user's Attack and Defense, and
            // Flower Gift gives it half again in sun. Both belong to the
            // stat rather than to the damage chain, so both happen here.
            AttackStat = AttackAfterFieldEffects(attacker, physical),
            AttackStage = attacker.OffenseStage(physical),
            AttackerAbility = attacker.SelectedAbility ?? "None",

            AttackerBurned = attacker.Burned,

            // §306: Wonder Room swaps everyone's two defences - so a physical
            // hit meets the Special Defense STAT. The stages do not move with
            // them, which is the part that is easy to get wrong, so the stage
            // below still reads the move's own category.
            DefenseStat = DefenseAfterFieldEffects(defender, physical),
            DefenseStage = defender.DefenseStage(physical),
            DefenderAbility = defender.SelectedAbility ?? "None",
            // §262: the "At full HP" checkbox is gone. A damage calculator
            // answers "what does this hit do", and the hit it is asked about
            // is the first one - which is the case Multiscale halves. Showdown
            // defaults its HP field to full for the same reason.
            DefenderAtFullHp = true,

            Weather = SelectedWeather,
            Terrain = SelectedTerrain,

            // §306: Gravity pulls everything down, which is the only thing
            // about it that moves a damage number - a grounded attacker gets
            // its terrain's boost, and a grounded target can be shielded by
            // Misty.
            AttackerGrounded = attacker.Grounded || Gravity,
            DefenderGrounded = defender.Grounded || Gravity,

            // A screen belongs to the side it was set up on, so it is the
            // DEFENDER's three that matter for this direction - which is the
            // whole reason they live on a side rather than on the field.
            Reflect = defender.Reflect,
            LightScreen = defender.LightScreen,
            AuroraVeil = defender.AuroraVeil,

            AttackerCharged = attacker.Charge,
            DefenderForesighted = defender.Foresight,
        });

        // §307, then §309. What the two abilities and the two held items do
        // to this hit, measured off the simulator by resolving the move with
        // them and without them. The calculator's own chain no longer knows a
        // single ability or item by name - it used to know seven abilities
        // and four items, and the engine knows 124 and 105.
        //
        // One context, two factors, and they compose EXACTLY: the ability
        // factor is measured over a world that still has the items and the
        // item factor over a world that has already lost the abilities, so
        // the middle term cancels and the product is the engine's own answer
        // for both at once. See MoveOracle.ItemFactor.
        MoveOracle.Context measured = BuildContext(attacker, defender);

        double abilityFactor = MoveOracle.AbilityFactor(move.Name, measured);
        double itemFactor = MoveOracle.ItemFactor(move.Name, measured);

        string abilityNote = DescribeAbilities(attacker, defender, abilityFactor);
        string itemNote = DescribeItems(attacker, defender, itemFactor);

        // Scaled ONCE by the product rather than twice in a row, so a 1.3 and
        // a 1.5 cannot each round the number on their way past.
        result = Scale(result, abilityFactor * itemFactor);

        int hp = defender.MaxHp;

        if (result.MaxDamage <= 0 || hp <= 0)
        {
            slot.Sentence = $"{attacker.DescribeOffense(physical)} {move.Name} vs. " +
                            $"{defender.DescribeDefense(physical)}: 0 damage - immune.";
            slot.HasResult = true;
            return;
        }

        string line = $"{result.MinDamage}-{result.MaxDamage} " +
                      $"({Percent(result.MinDamage, hp)} - {Percent(result.MaxDamage, hp)}%) -- " +
                      DescribeKo(hp, result.MinDamage, result.MaxDamage, KoProfileOf(defender));

        string conditionNote = ConditionalNote(attacker, defender, move, simulator, power);

        string notes = string.Join(" ", new[]
            {
                result.WeatherNote, result.TerrainNote, abilityNote, itemNote,
                hiddenPowerNote.Trim(), conditionNote,
            }
            .Where(n => !string.IsNullOrWhiteSpace(n)));

        slot.Sentence = $"{attacker.DescribeOffense(physical)} {move.Name} vs. " +
                        $"{defender.DescribeDefense(physical)}: {line}" +
                        (notes.Length > 0 ? "  |  " + notes : "");

        slot.HasResult = true;
    }

    /// <summary>§307. Multiply a computed range by the abilities' factor. A
    /// factor of zero is an immunity and comes out as a zero range, which the
    /// caller already has a sentence for.</summary>
    private static PokemonBattleMath.DamageResult Scale(
        PokemonBattleMath.DamageResult result, double factor)
    {
        if (Math.Abs(factor - 1.0) < 0.0001)
            return result;

        int At(int value) => factor <= 0 ? 0 : Math.Max(1, (int)Math.Round(value * factor));

        return new PokemonBattleMath.DamageResult
        {
            MinDamage = At(result.MinDamage),
            MaxDamage = At(result.MaxDamage),
            CritMinDamage = At(result.CritMinDamage),
            CritMaxDamage = At(result.CritMaxDamage),
            TypeEffectiveness = result.TypeEffectiveness,
            HasStab = result.HasStab,
            EffectiveAttack = result.EffectiveAttack,
            EffectiveDefense = result.EffectiveDefense,
            WeatherNote = result.WeatherNote,
            TerrainNote = result.TerrainNote,
        };
    }

    /// <summary>
    /// §307. What to say about the abilities, which is §103's rule applied to
    /// a list that is no longer curated: the window now offers whatever the
    /// species can have, and the engine has a rule for 124 of the 308 names
    /// in the dex. An ability it cannot simulate must SAY it changed nothing,
    /// because the alternative is a dropdown that silently does nothing - and
    /// that is the thing every one of these sections has been about.
    /// </summary>
    private static string DescribeAbilities(
        DamageCalculatorSideViewModel attacker,
        DamageCalculatorSideViewModel defender,
        double factor)
    {
        var unsimulated = new List<string>();

        foreach (DamageCalculatorSideViewModel side in new[] { attacker, defender })
        {
            string? ability = side.SelectedAbility;

            if (ability is null or "None" || PokemonDex.AbilityIsSimulated(ability))
                continue;

            unsimulated.Add($"{side.Title}'s {ability}");
        }

        if (unsimulated.Count > 0)
        {
            return $"The simulator has no rule for {string.Join(" or ", unsimulated)}, " +
                   "so this roll is unchanged by it.";
        }

        if (factor <= 0)
            return "An ability makes the target immune.";

        if (Math.Abs(factor - 1.0) < 0.0001)
            return "";

        return $"Abilities move this hit by x{factor:0.##}.";
    }

    /// <summary>
    /// §308. The conditions the window stopped asking about, measured
    /// instead of demanded.
    ///
    /// The mockup dropped six switches - the user moving first, the target
    /// switching out, either side hurt this turn, the last move failing,
    /// Defense Curl - and those six feed ten real moves: Payback, Pursuit,
    /// Bolt Beak, Fishious Rend, Avalanche, Revenge, Assurance, Stomping
    /// Tantrum, Temper Flare and Rollout. (Ice Ball reads Defense Curl too,
    /// and is not in the simulator's move file at all - so the window cannot
    /// offer it and this cannot answer for it either.) The instruction was that
    /// Pursuit should just show a number rather than make someone tick a box
    /// to get one, and it does: BuildContext now describes the plainest turn
    /// there is, so Pursuit prices at its listed 40.
    ///
    /// A number shown without the condition attached to it is the exact thing
    /// §103 and everything after it has been against, so the condition is
    /// still reported - it has only stopped being the user's job to supply.
    /// Each switch is flipped on a throwaway context, the move is re-asked,
    /// and if the power moves the row says by how much and on what.
    ///
    /// Nothing is written down twice. The 80 in "or 80 if the target is
    /// switching out" is the simulator's own answer to its own move file, so
    /// a change to PowerIfTargetSwitchingEffect or to Pursuit's row in
    /// moves.json changes this sentence with it, and a move added later that
    /// happens to read one of the six is covered without anybody noticing it
    /// needed to be.
    ///
    /// Only a move the simulator actually priced is probed. An Unknown answer
    /// means the power on the line came from the tracker's own file, and
    /// comparing that against the simulator's would be comparing two
    /// different things.
    /// </summary>
    private string ConditionalNote(
        DamageCalculatorSideViewModel attacker,
        DamageCalculatorSideViewModel defender,
        MoveData move,
        MoveOracle.Answer simulator,
        int basePower)
    {
        if (simulator.Kind != MoveOracle.AnswerKind.Power &&
            simulator.Kind != MoveOracle.AnswerKind.Listed)
        {
            return "";
        }

        var found = new List<string>();

        void Probe(string phrase, Action<MoveOracle.Context> bend)
        {
            MoveOracle.Context context = BuildContext(attacker, defender);
            bend(context);

            MoveOracle.Answer answer = MoveOracle.Ask(move.Name, context);

            bool comparable = answer.Kind is MoveOracle.AnswerKind.Power
                                          or MoveOracle.AnswerKind.Listed;

            if (comparable && answer.Power > 0 && answer.Power != basePower)
                found.Add($"{answer.Power} if {phrase}");
        }

        Probe("the user moves after the target", c => c.AttackerMovesFirst = false);
        Probe("the target is switching out", c => c.DefenderSwitchingOut = true);
        Probe("the user was hurt this turn", c => c.Attacker.HurtThisTurn = true);
        Probe("the target was already hurt this turn", c => c.Defender.HurtThisTurn = true);
        Probe("the user's last move failed", c => c.Attacker.LastMoveFailed = true);
        Probe("the user has curled up", c => c.Attacker.DefenseCurled = true);

        return found.Count == 0
            ? ""
            : $"Power is {basePower} as set up here, or {string.Join(", or ", found)}.";
    }

    /// <summary>
    /// §309. What to say about the two held items - §307's rule for
    /// abilities, applied to a list that is not curated either.
    ///
    /// There are two different silences to break. One is an item the engine
    /// models perfectly well but which has nowhere to land in a window that
    /// asks what a hit does: a Heat Rock lengthens the sun and the sun's
    /// multiplier is the same either way, a mega stone does nothing until
    /// its holder has already mega evolved, and a calculator picks the mega
    /// forme as a species. Those are offered anyway - omitting what somebody
    /// is actually holding is worse - and they SAY so.
    ///
    /// The other is the half of an item that lands after the hit rather than
    /// on it. Life Orb's tithe, Rocky Helmet's thorns and Weakness Policy's
    /// two stages are all real and none of them is in the roll above, so the
    /// ones that cost a definite number get told, and Weakness Policy gets
    /// told it is not counted.
    /// </summary>
    private static string DescribeItems(
        DamageCalculatorSideViewModel attacker,
        DamageCalculatorSideViewModel defender,
        double factor)
    {
        var parts = new List<string>();
        var idle = new List<string>();

        foreach (DamageCalculatorSideViewModel side in new[] { attacker, defender })
        {
            string? item = side.SelectedItem;

            if (item is null or "None" || HeldItems.AffectsDamageOrKo(item))
                continue;

            idle.Add($"{side.Title}'s {item}");
        }

        if (idle.Count > 0)
            parts.Add($"{string.Join(" and ", idle)} cannot change a number in this window.");

        if (factor <= 0)
        {
            parts.Add("An item makes the target immune.");
        }
        else if (Math.Abs(factor - 1.0) >= 0.0001)
        {
            parts.Add($"Items move this hit by x{factor:0.##}.");
        }

        // The after-the-hit half, which no factor can carry.
        string attackerItem = HeldItems.Normalize(attacker.SelectedItem);
        string defenderItem = HeldItems.Normalize(defender.SelectedItem);

        if (attackerItem == "lifeorb" && attacker.MaxHp > 0)
            parts.Add($"Life Orb costs its holder {Math.Max(1, attacker.MaxHp / 10)} a hit.");

        if (defenderItem == "rockyhelmet" && attacker.MaxHp > 0)
            parts.Add($"Rocky Helmet costs the attacker {Math.Max(1, attacker.MaxHp / 6)} a contact hit.");

        if (defenderItem == "weaknesspolicy")
            parts.Add("Weakness Policy's +2/+2 lands after this hit, and the count below does not follow it.");

        return string.Join(" ", parts);
    }

    /// <summary>§306. Flower Gift adds half again to this side's Attack while
    /// the sun is out. That is a change to the STAT, so it happens before the
    /// damage chain reads it - and it does not touch a special attacker.
    /// §308 took Power Trick's swap out of here with its control.</summary>
    private int AttackAfterFieldEffects(DamageCalculatorSideViewModel side, bool physical)
    {
        int attack = side.OffenseStat(physical);

        if (physical && side.FlowerGift && IsSun)
            attack = attack * 3 / 2;

        return attack;
    }

    /// <summary>§306. Wonder Room swaps the two defending stats for everyone.
    /// Flower Gift raises Special Defense in sun.</summary>
    private int DefenseAfterFieldEffects(DamageCalculatorSideViewModel side, bool physical)
    {
        bool readsPhysicalStat = WonderRoom ? !physical : physical;

        int defense = side.DefenseStat(readsPhysicalStat);

        if (!readsPhysicalStat && side.FlowerGift && IsSun)
            defense = defense * 3 / 2;

        return defense;
    }

    /// <summary>§306. The battle as the simulator needs to see it. Which
    /// side is the attacker matters - Avalanche reads the user's damage taken
    /// and Assurance the target's - so this is built per direction rather
    /// than once per turn.
    ///
    /// §308: the turn-order and switching switches went with the mockup, so
    /// this now always describes the plainest turn there is - the user of
    /// this move goes first, nobody is leaving, nothing has been hurt yet.
    /// That is the number the row shows. It is a FRESH graph on every call
    /// (ToBridgeSide builds new Sides), which is what lets ConditionalNote
    /// take one of these and bend it without disturbing anything.</summary>
    private MoveOracle.Context BuildContext(
        DamageCalculatorSideViewModel attacker,
        DamageCalculatorSideViewModel defender)
    {
        MoveOracle.Side attackerSide = attacker.ToBridgeSide();
        MoveOracle.Side defenderSide = defender.ToBridgeSide();

        // §309. Magic Room switches every held item off - for the SIMULATOR
        // as well as for the damage chain. Before §309 only the chain was
        // told, so Acrobatics, which reads whether its user is carrying
        // anything, still saw the item straight through the Magic Room.
        if (MagicRoom)
        {
            attackerSide.Item = "None";
            defenderSide.Item = "None";
        }

        return new MoveOracle.Context
        {
            Attacker = attackerSide,
            Defender = defenderSide,
            Weather = SelectedWeather,
            Terrain = SelectedTerrain,
            Gravity = Gravity,
            AttackerMovesFirst = true,
            DefenderSwitchingOut = false,
        };
    }

    /// <summary>§306. A move that names its own damage: the user's level, half
    /// what the target has left, all of it. There is no 85-100% roll to
    /// report, so the line says one number and the KO sentence counts with
    /// it.</summary>
    private void FillFixed(
        DamageMoveSlot slot,
        DamageCalculatorSideViewModel attacker,
        DamageCalculatorSideViewModel defender,
        MoveData move,
        bool physical,
        MoveOracle.Answer simulator)
    {
        int hp = defender.MaxHp;
        int damage = Math.Min(simulator.Damage, Math.Max(0, defender.CurrentHp));

        slot.MoveInfo = $"{move.Type} - {move.Category} - set damage";

        if (damage <= 0 || hp <= 0)
        {
            slot.Sentence = $"{attacker.DescribeOffense(physical)} {move.Name} vs. " +
                            $"{defender.DescribeDefense(physical)}: 0 damage.";
            slot.HasResult = true;
            return;
        }

        slot.Sentence = $"{attacker.DescribeOffense(physical)} {move.Name} vs. " +
                        $"{defender.DescribeDefense(physical)}: {damage} " +
                        $"({Percent(damage, hp)}%) -- " +
                        DescribeKo(hp, damage, damage, KoProfileOf(defender)) +
                        "  |  " + simulator.Note;

        slot.HasResult = true;
    }

    /// <summary>
    /// Showdown TRUNCATES its percentages to one decimal - 162 of 321 HP is
    /// 50.467%, and it prints 50.4%, not 50.5%. Rounding here is the one
    /// difference that would make every cross-check look like a disagreement.
    /// </summary>
    internal static string Percent(int damage, int hp) =>
        (Math.Floor(1000.0 * damage / hp) / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Guaranteed when the worst roll still gets there in the same number of
    /// hits as the best one; a range otherwise. "1HKO" is written OHKO, as
    /// everyone writes it.
    /// </summary>
    internal static string DescribeKo(int hp, int minDamage, int maxDamage) =>
        DescribeKo(hp, minDamage, maxDamage, default);

    /// <summary>
    /// §306. The same sentence, now counting what happens BETWEEN the hits.
    ///
    /// §261 left the hazards and residuals out with a note saying they change
    /// no damage roll - which is true, and beside the point. They change how
    /// many hits a KO takes, which is the other half of what a calculator is
    /// for. Stealth Rock and Spikes are a one-off as the target comes in;
    /// Leech Seed comes off at the end of every turn (§308 dropped Salt Cure,
    /// which is a generation past PRO, and Nightmare with it).
    ///
    /// Counted honestly: the chip lands after each hit, so a target that is
    /// already dying finishes on its own, and a two-hit KO with chip in
    /// between may become a one-and-a-bit. Nothing here pretends to know the
    /// turn order of two residuals against each other, because it does not
    /// change the count.
    /// </summary>
    internal static string DescribeKo(int hp, int minDamage, int maxDamage, KoProfile profile)
    {
        if (minDamage <= 0 && profile.ResidualPerTurn <= 0)
            return "never a KO";

        string Name(int hits) => hits == 1 ? "OHKO" : $"{hits}HKO";

        int best = HitsToKo(hp, maxDamage, profile);
        int worst = HitsToKo(hp, minDamage, profile);

        if (worst < 0)
            return "never a KO";

        string sentence = best == worst
            ? $"guaranteed {Name(worst)}"
            : $"{Name(best)} possible, {Name(worst)} guaranteed";

        // §309: an item can now push the count the other way, so the
        // parenthesis has to be able to say which way it went.
        bool chip = profile.ResidualPerTurn > 0 || profile.UpfrontChip > 0;
        bool help = profile.ResidualPerTurn < 0 || profile.FocusSash || profile.SitrusHeal > 0;

        if (chip && help)
            sentence += " (counting chip damage and what it holds on with)";
        else if (chip)
            sentence += " (counting chip damage)";
        else if (help)
            sentence += " (counting what it holds on with)";

        return sentence;
    }

    /// <summary>
    /// §309. Everything about a side that changes how many hits it takes
    /// rather than how much each one does.
    ///
    /// It is a bundle rather than four more parameters because §309 added
    /// three at once and the next section will add another: a Leftovers that
    /// heals a sixteenth (so the residual is SIGNED now, and a slow enough
    /// attack is never a KO at all), a Focus Sash that spends itself on the
    /// first lethal hit, and a Sitrus Berry that spends itself the moment
    /// half the bar is gone.
    /// </summary>
    internal readonly record struct KoProfile(
        int ResidualPerTurn,
        int UpfrontChip,
        bool FocusSash,
        int SitrusHeal);

    internal static KoProfile KoProfileOf(DamageCalculatorSideViewModel side)
    {
        string item = HeldItems.Normalize(side.SelectedItem);
        int hp = side.MaxHp;

        return new KoProfile(
            ResidualPerTurn: ResidualPerTurn(side),
            UpfrontChip: UpfrontChip(side),
            FocusSash: item == "focussash",
            SitrusHeal: item == "sitrusberry" && hp > 0 ? Math.Max(1, hp / 4) : 0);
    }

    /// <summary>How many of these hits it takes, with the chip counted
    /// between them. -1 when it never gets there.</summary>
    internal static int HitsToKo(int hp, int damage, KoProfile profile)
    {
        int left = hp - Math.Max(0, profile.UpfrontChip);

        if (left <= 0)
            return 0;

        // §309. A Focus Sash only holds for a holder at FULL HP, so the
        // hazards on the way in spend it before the first hit ever lands.
        // That falls out of reading `left` after the chip rather than hp, and
        // it is the right answer: a Sash under Stealth Rock is a Sash that
        // has already failed.
        bool sash = profile.FocusSash && left >= hp;
        int sitrus = profile.SitrusHeal;

        if (damage <= 0 && profile.ResidualPerTurn <= 0)
            return -1;

        // A calculator that cannot answer in sixteen turns is answering the
        // wrong question; the cap is there so a zero-damage move against a
        // zero-chip target cannot spin - and, since §309, so a Leftovers
        // out-healing the damage comes back as "never a KO" instead.
        for (int hits = 1; hits <= 16; hits++)
        {
            left -= damage;

            if (left <= 0 && sash)
            {
                left = 1;
                sash = false;
            }

            left = Spend(ref sitrus, hp, left);

            if (left <= 0)
                return hits;

            left -= profile.ResidualPerTurn;

            // Nothing heals past full, and the Sash is gone the moment its
            // holder is not.
            left = Math.Min(left, hp);
            sash = sash && left >= hp;

            left = Spend(ref sitrus, hp, left);

            if (left <= 0)
                return hits;
        }

        return -1;
    }

    /// <summary>§309. A Sitrus Berry is eaten the first time half the bar is
    /// gone, and heals a quarter of it. Once, which is why the reserve is
    /// passed by reference and zeroed on the way out.</summary>
    private static int Spend(ref int sitrus, int hp, int left)
    {
        if (sitrus <= 0 || left <= 0 || left * 2 > hp)
            return left;

        int healed = Math.Min(hp, left + sitrus);
        sitrus = 0;
        return healed;
    }

    /// <summary>§306. What this side loses at the end of every turn.</summary>
    internal static int ResidualPerTurn(DamageCalculatorSideViewModel side)
    {
        int hp = side.MaxHp;

        if (hp <= 0)
            return 0;

        int total = 0;

        if (side.LeechSeed)
            total += hp / 8;

        bool healthy = string.Equals(side.SelectedStatus, "None", StringComparison.OrdinalIgnoreCase)
                       || string.IsNullOrWhiteSpace(side.SelectedStatus);

        if (string.Equals(side.SelectedStatus, "Burned", StringComparison.OrdinalIgnoreCase))
            total += hp / 16;
        else if (string.Equals(side.SelectedStatus, "Poisoned", StringComparison.OrdinalIgnoreCase))
            total += hp / 8;
        else if (string.Equals(side.SelectedStatus, "Badly Poisoned", StringComparison.OrdinalIgnoreCase))
            total += hp / 16;

        // §309. The end-of-turn items, in the engine's own fractions (see
        // HeldItems.EndOfTurn). A Leftovers is NEGATIVE chip, which is why
        // this method's answer is signed now and why HitsToKo has to be able
        // to run out of turns rather than out of HP.
        switch (HeldItems.Normalize(side.SelectedItem))
        {
            case "leftovers":
                total -= Math.Max(1, hp / 16);
                break;

            case "blacksludge":
                bool poison = side.Types.Any(t =>
                    string.Equals(t, "Poison", StringComparison.OrdinalIgnoreCase));

                total += poison ? -Math.Max(1, hp / 16) : Math.Max(1, hp / 8);
                break;

            // The orbs inflict at the END of the turn, and only on something
            // that has nothing yet - so they are a burn or a toxic that the
            // Status box was not told about, starting one turn late. The
            // first hit lands on a healthy target either way, which is the
            // hit this window is reporting.
            case "flameorb":
                if (healthy)
                    total += hp / 16;
                break;

            case "toxicorb":
                if (healthy)
                    total += hp / 16;
                break;
        }

        return total;
    }

    /// <summary>§306. What the hazards on this side's half take as its owner
    /// walks in - a one-off, before the first hit.</summary>
    internal static int UpfrontChip(DamageCalculatorSideViewModel side)
    {
        int hp = side.MaxHp;

        if (hp <= 0)
            return 0;

        int total = 0;

        if (side.StealthRock)
        {
            // An eighth, doubled or halved by how Rock treats its types.
            double rock = PokemonBattleMath.GetTypeEffectiveness("Rock", side.Types);
            total += (int)(hp * rock / 8.0);
        }

        int layers = side.SpikesLayers;

        if (layers > 0 && side.Grounded)
            total += hp / (layers == 1 ? 8 : layers == 2 ? 6 : 4);

        return total;
    }
}
