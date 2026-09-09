using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// The Calculators -> Damage window, in the classic attacker / move / defender
/// three-column shape. Species (base stats, types, learnsets) come from
/// CalculatorDataService, the move list from MoveLookupService.AllMoves (the
/// same moves.json the boss detail panel reads - it carries category/power,
/// which the PokemonSim project's own move file didn't for most moves), and
/// every formula from PokemonBattleMath - see MIGRATION_GUIDE.md §89 for how
/// each table was verified. Results are a deterministic min-max range (the
/// game rolls 85-100%), plus percent-of-HP, a hits-to-KO range, and the crit
/// range. Everything recalculates only on the Calculate button: with ~30
/// inputs, per-keystroke recalc would mostly display half-edited states.
///
/// The item/ability dropdowns are deliberately curated (PokemonBattleMath's
/// lists) to modifiers with simple, known math - it's a planning tool, not a
/// full battle simulator.
/// </summary>
public sealed partial class DamageCalculatorViewModel : ViewModelBase
{
    // Dropdown option lists - instance properties so the XAML's compiled
    // bindings can reach them straight off the DataContext (no x:Static).
    public IReadOnlyList<string> NatureOptions { get; } = PokemonBattleMath.NatureNames;
    public IReadOnlyList<string> StageOptions { get; } =
        new[] { "+6", "+5", "+4", "+3", "+2", "+1", "0", "-1", "-2", "-3", "-4", "-5", "-6" };
    public IReadOnlyList<string> AttackerItemOptions { get; } = PokemonBattleMath.AttackerItems;
    public IReadOnlyList<string> AttackerAbilityOptions { get; } = PokemonBattleMath.AttackerAbilities;
    public IReadOnlyList<string> DefenderAbilityOptions { get; } = PokemonBattleMath.DefenderAbilities;
    public IReadOnlyList<string> WeatherChoices { get; } = PokemonBattleMath.WeatherOptions;

    // ---- Attacker ----
    [ObservableProperty] private string attackerSearchText = "";
    [ObservableProperty] private IReadOnlyList<string> attackerNames;
    [ObservableProperty] private string? selectedAttackerName;
    [ObservableProperty] private string attackerTypesLine = "";
    [ObservableProperty] private string attackerBaseLine = "";
    [ObservableProperty] private string attackerLevelText = "100";
    [ObservableProperty] private string? selectedAttackerNature = "Hardy";

    [ObservableProperty] private string attackerIvHpText = "31";
    [ObservableProperty] private string attackerIvAttackText = "31";
    [ObservableProperty] private string attackerIvDefenseText = "31";
    [ObservableProperty] private string attackerIvSpAttackText = "31";
    [ObservableProperty] private string attackerIvSpDefenseText = "31";
    [ObservableProperty] private string attackerIvSpeedText = "31";

    [ObservableProperty] private string attackerEvHpText = "0";
    [ObservableProperty] private string attackerEvAttackText = "0";
    [ObservableProperty] private string attackerEvDefenseText = "0";
    [ObservableProperty] private string attackerEvSpAttackText = "0";
    [ObservableProperty] private string attackerEvSpDefenseText = "0";
    [ObservableProperty] private string attackerEvSpeedText = "0";

    [ObservableProperty] private string? selectedAttackStage = "0";
    [ObservableProperty] private string? selectedAttackerItem = "None";
    [ObservableProperty] private string? selectedAttackerAbility = "None";
    [ObservableProperty] private bool attackerBurned;

    // ---- Defender ----
    [ObservableProperty] private string defenderSearchText = "";
    [ObservableProperty] private IReadOnlyList<string> defenderNames;
    [ObservableProperty] private string? selectedDefenderName;
    [ObservableProperty] private string defenderTypesLine = "";
    [ObservableProperty] private string defenderBaseLine = "";
    [ObservableProperty] private string defenderLevelText = "100";
    [ObservableProperty] private string? selectedDefenderNature = "Hardy";

    [ObservableProperty] private string defenderIvHpText = "31";
    [ObservableProperty] private string defenderIvAttackText = "31";
    [ObservableProperty] private string defenderIvDefenseText = "31";
    [ObservableProperty] private string defenderIvSpAttackText = "31";
    [ObservableProperty] private string defenderIvSpDefenseText = "31";
    [ObservableProperty] private string defenderIvSpeedText = "31";

    [ObservableProperty] private string defenderEvHpText = "0";
    [ObservableProperty] private string defenderEvAttackText = "0";
    [ObservableProperty] private string defenderEvDefenseText = "0";
    [ObservableProperty] private string defenderEvSpAttackText = "0";
    [ObservableProperty] private string defenderEvSpDefenseText = "0";
    [ObservableProperty] private string defenderEvSpeedText = "0";

    [ObservableProperty] private string? selectedDefenseStage = "0";
    [ObservableProperty] private string? selectedDefenderAbility = "None";
    [ObservableProperty] private bool defenderAtFullHp = true;
    [ObservableProperty] private bool screenUp;

    // ---- Move / field ----
    [ObservableProperty] private string moveSearchText = "";
    [ObservableProperty] private IReadOnlyList<string> moveNames;
    [ObservableProperty] private string? selectedMoveName;
    [ObservableProperty] private string moveInfoLine = "";
    [ObservableProperty] private string movePowerText = "";
    [ObservableProperty] private bool onlyDamagingMoves = true;
    [ObservableProperty] private bool onlyLearnsetMoves = true;
    [ObservableProperty] private string? selectedWeather = "None";

    // ---- Results ----
    [ObservableProperty] private string damageRangeLine = "";
    [ObservableProperty] private string percentLine = "";
    [ObservableProperty] private string koLine = "";
    [ObservableProperty] private string critLine = "";
    [ObservableProperty] private string effectivenessLine = "";
    [ObservableProperty] private string weatherLine = "";
    [ObservableProperty] private string statsLine = "";
    [ObservableProperty] private string statusMessage = "";

    public DamageCalculatorViewModel()
    {
        attackerNames = CalculatorDataService.AllSpeciesNames;
        defenderNames = CalculatorDataService.AllSpeciesNames;
        moveNames = BuildMoveList();

        // Both data files load lazily and never throw - if one is missing the
        // window still opens, it just says so instead of showing empty pickers
        // with no explanation.
        if (attackerNames.Count == 0)
            statusMessage = "Species data couldn't be loaded (SharedPokemonLibrary/Data/Calculator/calc-pokedex.json is missing or unreadable), so the Pokémon pickers are empty.";
        else if (MoveLookupService.AllMoves.Count == 0)
            statusMessage = "Move data couldn't be loaded (SharedPokemonLibrary/Data/Moves/moves.json is missing or unreadable), so the move picker is empty.";
    }

    // ---- Species pickers ----

    partial void OnAttackerSearchTextChanged(string value) =>
        AttackerNames = FilterSpecies(value);

    partial void OnDefenderSearchTextChanged(string value) =>
        DefenderNames = FilterSpecies(value);

    private static IReadOnlyList<string> FilterSpecies(string searchText)
    {
        string query = searchText.Trim();
        IReadOnlyList<string> all = CalculatorDataService.AllSpeciesNames;

        return query.Length == 0
            ? all
            : all.Where(name => name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    partial void OnSelectedAttackerNameChanged(string? value)
    {
        (AttackerTypesLine, AttackerBaseLine) = DescribeSpecies(value);

        // The learnset filter follows the attacker, so the move list changes
        // with the selection.
        MoveNames = BuildMoveList();
    }

    partial void OnSelectedDefenderNameChanged(string? value)
    {
        (DefenderTypesLine, DefenderBaseLine) = DescribeSpecies(value);
    }

    private static (string Types, string Bases) DescribeSpecies(string? name)
    {
        CalculatorDataService.CalcSpecies? species = CalculatorDataService.Find(name);

        if (species == null)
            return ("", "");

        return (
            "Type: " + string.Join(" / ", species.Types),
            $"Base: {species.BaseHp} HP / {species.BaseAttack} Atk / {species.BaseDefense} Def / " +
            $"{species.BaseSpAttack} SpA / {species.BaseSpDefense} SpD / {species.BaseSpeed} Spe");
    }

    // ---- Move picker ----

    partial void OnMoveSearchTextChanged(string value) => MoveNames = BuildMoveList();
    partial void OnOnlyDamagingMovesChanged(bool value) => MoveNames = BuildMoveList();
    partial void OnOnlyLearnsetMovesChanged(bool value) => MoveNames = BuildMoveList();

    private IReadOnlyList<string> BuildMoveList()
    {
        IEnumerable<MoveData> moves = MoveLookupService.AllMoves;

        if (OnlyDamagingMoves)
            moves = moves.Where(move =>
                string.Equals(move.Category, "Physical", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(move.Category, "Special", StringComparison.OrdinalIgnoreCase));

        // Learnsets come from Pokémon Showdown's data (MIGRATION_GUIDE.md §90
        // - the PokemonSim source only carried a two-move placeholder for most
        // species), spelled to match moves.json's names, so anything unmatched
        // drops out harmlessly. The checkbox still matters: untick it for
        // what-if math with moves the species can't actually learn.
        if (OnlyLearnsetMoves &&
            CalculatorDataService.Find(SelectedAttackerName) is { LearnsetMoveNames.Count: > 0 } attacker)
        {
            var learnset = new HashSet<string>(attacker.LearnsetMoveNames, StringComparer.OrdinalIgnoreCase);
            moves = moves.Where(move => learnset.Contains(move.Name));
        }

        string query = MoveSearchText.Trim();

        if (query.Length > 0)
            moves = moves.Where(move => move.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        return moves.Select(move => move.Name).ToList();
    }

    partial void OnSelectedMoveNameChanged(string? value)
    {
        MoveData? move = MoveLookupService.Find(value);

        if (move == null)
        {
            MoveInfoLine = "";
            MovePowerText = "";
            return;
        }

        // Pre-fill the Power box from moves.json but leave it editable - a few
        // damaging moves have no fixed power there (Low Kick, Gyro Ball, ...),
        // and an editable box beats silently calculating with a wrong number.
        MovePowerText = move.Power?.ToString() ?? "";

        string info = $"{move.Type} / {move.Category ?? "?"}";

        info += move.Power is int power
            ? $" / {power} power"
            : " / power varies - type it into the Power box";

        if (move.Name.StartsWith("Hidden Power", StringComparison.OrdinalIgnoreCase))
            info += " / its type follows the attacker's IVs (worked out automatically)";

        MoveInfoLine = info;
    }

    // ---- Calculation ----

    [RelayCommand]
    private void Calculate()
    {
        DamageRangeLine = "";
        PercentLine = "";
        KoLine = "";
        CritLine = "";
        EffectivenessLine = "";
        WeatherLine = "";
        StatsLine = "";

        CalculatorDataService.CalcSpecies? attacker = CalculatorDataService.Find(SelectedAttackerName);
        CalculatorDataService.CalcSpecies? defender = CalculatorDataService.Find(SelectedDefenderName);

        if (attacker == null || defender == null)
        {
            StatusMessage = "Pick an attacker and a defender first.";
            return;
        }

        MoveData? move = MoveLookupService.Find(SelectedMoveName);

        if (move == null)
        {
            StatusMessage = "Pick a move.";
            return;
        }

        bool physical = string.Equals(move.Category, "Physical", StringComparison.OrdinalIgnoreCase);
        bool special = string.Equals(move.Category, "Special", StringComparison.OrdinalIgnoreCase);

        if (!physical && !special)
        {
            StatusMessage = $"{move.Name} is a status move - it deals no direct damage.";
            return;
        }

        if (!int.TryParse(MovePowerText.Trim(), out int movePower) || movePower <= 0)
        {
            StatusMessage = move.Power == null
                ? $"{move.Name}'s power varies - enter the power to use in the Power box."
                : "Enter a move power above 0 in the Power box.";
            return;
        }

        StatusMessage = "";

        int attackerLevel = ParseClamped(AttackerLevelText, 1, 100, 100);
        int defenderLevel = ParseClamped(DefenderLevelText, 1, 100, 100);
        string attackerNature = SelectedAttackerNature ?? "Hardy";
        string defenderNature = SelectedDefenderNature ?? "Hardy";

        string moveType = move.Type;
        string hiddenPowerNote = "";

        if (move.Name.StartsWith("Hidden Power", StringComparison.OrdinalIgnoreCase))
        {
            // GetHiddenPowerType wants HP, Atk, Def, SPEED, SpA, SpD - Speed
            // sits third, not last (see PokemonBattleMath's own comment).
            moveType = PokemonBattleMath.GetHiddenPowerType(new[]
            {
                ParseClamped(AttackerIvHpText, 0, 31, 0),
                ParseClamped(AttackerIvAttackText, 0, 31, 0),
                ParseClamped(AttackerIvDefenseText, 0, 31, 0),
                ParseClamped(AttackerIvSpeedText, 0, 31, 0),
                ParseClamped(AttackerIvSpAttackText, 0, 31, 0),
                ParseClamped(AttackerIvSpDefenseText, 0, 31, 0),
            });
            hiddenPowerNote = $" - Hidden Power came out {moveType} from the attacker's IVs";
        }

        int attackStat = physical
            ? PokemonBattleMath.CalculateStat(
                attacker.BaseAttack,
                ParseClamped(AttackerIvAttackText, 0, 31, 0),
                ParseClamped(AttackerEvAttackText, 0, 252, 0),
                attackerLevel,
                PokemonBattleMath.GetNatureMultiplier(attackerNature, "Attack"))
            : PokemonBattleMath.CalculateStat(
                attacker.BaseSpAttack,
                ParseClamped(AttackerIvSpAttackText, 0, 31, 0),
                ParseClamped(AttackerEvSpAttackText, 0, 252, 0),
                attackerLevel,
                PokemonBattleMath.GetNatureMultiplier(attackerNature, "SpAttack"));

        int defenseStat = physical
            ? PokemonBattleMath.CalculateStat(
                defender.BaseDefense,
                ParseClamped(DefenderIvDefenseText, 0, 31, 0),
                ParseClamped(DefenderEvDefenseText, 0, 252, 0),
                defenderLevel,
                PokemonBattleMath.GetNatureMultiplier(defenderNature, "Defense"))
            : PokemonBattleMath.CalculateStat(
                defender.BaseSpDefense,
                ParseClamped(DefenderIvSpDefenseText, 0, 31, 0),
                ParseClamped(DefenderEvSpDefenseText, 0, 252, 0),
                defenderLevel,
                PokemonBattleMath.GetNatureMultiplier(defenderNature, "SpDefense"));

        int defenderHp = PokemonBattleMath.CalculateHp(
            defender.BaseHp,
            ParseClamped(DefenderIvHpText, 0, 31, 0),
            ParseClamped(DefenderEvHpText, 0, 252, 0),
            defenderLevel);

        PokemonBattleMath.DamageResult result = PokemonBattleMath.Calculate(new PokemonBattleMath.DamageRequest
        {
            Level = attackerLevel,
            MovePower = movePower,
            MoveType = moveType,
            MoveCategory = physical ? "Physical" : "Special",
            AttackerTypes = attacker.Types,
            DefenderTypes = defender.Types,
            AttackStat = attackStat,
            AttackStage = ParseStage(SelectedAttackStage),
            AttackerAbility = SelectedAttackerAbility ?? "None",
            AttackerItem = SelectedAttackerItem ?? "None",
            AttackerBurned = AttackerBurned,
            DefenseStat = defenseStat,
            DefenseStage = ParseStage(SelectedDefenseStage),
            DefenderAbility = SelectedDefenderAbility ?? "None",
            DefenderAtFullHp = DefenderAtFullHp,
            Weather = SelectedWeather ?? "None",
            ScreenUp = ScreenUp,
            MoveName = move.Name,
        });

        EffectivenessLine = DescribeEffectiveness(result) + hiddenPowerNote;
        WeatherLine = result.WeatherNote;
        StatsLine = $"Attack used: {result.EffectiveAttack} / Defense used: {result.EffectiveDefense} / Defender HP: {defenderHp}";

        if (result.MaxDamage <= 0)
        {
            DamageRangeLine = "Damage: 0";
            KoLine = "This move can't damage that defender.";
            return;
        }

        DamageRangeLine = $"Damage: {result.MinDamage} - {result.MaxDamage}";
        PercentLine = $"{100.0 * result.MinDamage / defenderHp:0.#}% - {100.0 * result.MaxDamage / defenderHp:0.#}% of the defender's {defenderHp} HP";
        CritLine = $"On a critical hit: {result.CritMinDamage} - {result.CritMaxDamage}";
        KoLine = DescribeKo(defenderHp, result.MinDamage, result.MaxDamage);
    }

    private static string DescribeKo(int defenderHp, int minDamage, int maxDamage)
    {
        if (minDamage >= defenderHp)
            return "Guaranteed one-hit KO.";

        // Best case every roll lands max, worst case every roll lands min -
        // minDamage is at least 1 here (a 0 max already returned above, and
        // min is only 0 when the move can't damage at all).
        int bestCase = (int)Math.Ceiling(defenderHp / (double)maxDamage);
        int worstCase = (int)Math.Ceiling(defenderHp / (double)minDamage);

        return bestCase == worstCase
            ? $"KO in {worstCase} hits."
            : $"KO in {bestCase}-{worstCase} hits.";
    }

    private static string DescribeEffectiveness(PokemonBattleMath.DamageResult result)
    {
        string text = result.TypeEffectiveness switch
        {
            0.0 => "Immune (x0)",
            < 1.0 => $"Not very effective (x{result.TypeEffectiveness:0.##})",
            > 1.0 => $"Super effective (x{result.TypeEffectiveness:0.##})",
            _ => "Neutral effectiveness (x1)",
        };

        return result.HasStab ? text + " - with STAB" : text;
    }

    private static int ParseClamped(string? text, int min, int max, int fallback)
    {
        return int.TryParse((text ?? "").Trim(), out int value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    private static int ParseStage(string? stageText)
    {
        // int.TryParse accepts the leading "+" the stage dropdown shows.
        return int.TryParse((stageText ?? "0").Trim(), out int value)
            ? Math.Clamp(value, -6, 6)
            : 0;
    }
}
