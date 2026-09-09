using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// The Calculators -> IV window: a stat planner - pick a species, set level /
/// nature / IVs / EVs, and see the six real stats that combination produces,
/// plus the Hidden Power type those IVs roll. Species base stats come from
/// CalculatorDataService (the cleaned PokemonSim pokedex - the one data set
/// the tracker's own pokemon-species.json doesn't carry), formulas from
/// PokemonBattleMath (verified per MIGRATION_GUIDE.md §89). Defaults are the
/// hunt-planning case: level 100, 31 IVs, 0 EVs, neutral nature - "what would
/// this catch hit at the cap?". Calculation happens on the button rather than
/// per keystroke, same as the Damage window, so half-typed numbers never
/// flash misleading stats.
/// </summary>
public sealed partial class IvCalculatorViewModel : ViewModelBase
{
    public IReadOnlyList<string> NatureOptions { get; } = PokemonBattleMath.NatureNames;

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private IReadOnlyList<string> speciesNames;
    [ObservableProperty] private string? selectedSpeciesName;
    [ObservableProperty] private Bitmap? spriteImage;
    [ObservableProperty] private string typesLine = "";
    [ObservableProperty] private string baseStatsLine = "";

    [ObservableProperty] private string levelText = "100";
    [ObservableProperty] private string? selectedNature = "Hardy";

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

    [ObservableProperty] private string hpLine = "";
    [ObservableProperty] private string attackLine = "";
    [ObservableProperty] private string defenseLine = "";
    [ObservableProperty] private string spAttackLine = "";
    [ObservableProperty] private string spDefenseLine = "";
    [ObservableProperty] private string speedLine = "";
    [ObservableProperty] private string hiddenPowerLine = "";
    [ObservableProperty] private string statusMessage = "";

    public IvCalculatorViewModel()
    {
        speciesNames = CalculatorDataService.AllSpeciesNames;

        if (speciesNames.Count == 0)
            statusMessage = "Species data couldn't be loaded (SharedPokemonLibrary/Data/Calculator/calc-pokedex.json is missing or unreadable), so stats can't be calculated.";
    }

    partial void OnSearchTextChanged(string value)
    {
        string query = value.Trim();
        IReadOnlyList<string> all = CalculatorDataService.AllSpeciesNames;

        SpeciesNames = query.Length == 0
            ? all
            : all.Where(name => name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    partial void OnSelectedSpeciesNameChanged(string? value)
    {
        CalculatorDataService.CalcSpecies? species = CalculatorDataService.Find(value);

        if (species == null)
        {
            SpriteImage = null;
            TypesLine = "";
            BaseStatsLine = "";
            return;
        }

        // Null-safe: forms the sprite pack doesn't carry just show no image.
        SpriteImage = PokemonSpriteService.GetEncounterSprite(species.Name);
        TypesLine = "Type: " + string.Join(" / ", species.Types);
        BaseStatsLine = $"Base stats: {species.BaseHp} HP / {species.BaseAttack} Atk / " +
                        $"{species.BaseDefense} Def / {species.BaseSpAttack} SpA / " +
                        $"{species.BaseSpDefense} SpD / {species.BaseSpeed} Spe";
    }

    [RelayCommand]
    private void Calculate()
    {
        CalculatorDataService.CalcSpecies? species = CalculatorDataService.Find(SelectedSpeciesName);

        if (species == null)
        {
            StatusMessage = "Pick a Pokémon first.";
            return;
        }

        StatusMessage = "";

        int level = ParseClamped(LevelText, 1, 100, 100);
        string nature = SelectedNature ?? "Hardy";

        int ivHp = ParseClamped(IvHpText, 0, 31, 0);
        int ivAttack = ParseClamped(IvAttackText, 0, 31, 0);
        int ivDefense = ParseClamped(IvDefenseText, 0, 31, 0);
        int ivSpAttack = ParseClamped(IvSpAttackText, 0, 31, 0);
        int ivSpDefense = ParseClamped(IvSpDefenseText, 0, 31, 0);
        int ivSpeed = ParseClamped(IvSpeedText, 0, 31, 0);

        int evHp = ParseClamped(EvHpText, 0, 252, 0);
        int evAttack = ParseClamped(EvAttackText, 0, 252, 0);
        int evDefense = ParseClamped(EvDefenseText, 0, 252, 0);
        int evSpAttack = ParseClamped(EvSpAttackText, 0, 252, 0);
        int evSpDefense = ParseClamped(EvSpDefenseText, 0, 252, 0);
        int evSpeed = ParseClamped(EvSpeedText, 0, 252, 0);

        HpLine = $"HP: {PokemonBattleMath.CalculateHp(species.BaseHp, ivHp, evHp, level)}";
        AttackLine = StatLine("Attack", species.BaseAttack, ivAttack, evAttack, level, nature);
        DefenseLine = StatLine("Defense", species.BaseDefense, ivDefense, evDefense, level, nature);
        SpAttackLine = StatLine("SpAttack", species.BaseSpAttack, ivSpAttack, evSpAttack, level, nature);
        SpDefenseLine = StatLine("SpDefense", species.BaseSpDefense, ivSpDefense, evSpDefense, level, nature);
        SpeedLine = StatLine("Speed", species.BaseSpeed, ivSpeed, evSpeed, level, nature);

        // GetHiddenPowerType wants HP, Atk, Def, SPEED, SpA, SpD - Speed sits
        // third, not last (see PokemonBattleMath's own comment and §89).
        string hiddenPowerType = PokemonBattleMath.GetHiddenPowerType(
            new[] { ivHp, ivAttack, ivDefense, ivSpeed, ivSpAttack, ivSpDefense });

        HiddenPowerLine = $"Hidden Power: {hiddenPowerType} (always 60 power in PRO)";

        int evTotal = evHp + evAttack + evDefense + evSpAttack + evSpDefense + evSpeed;

        if (evTotal > 510)
            StatusMessage = $"Note: that's {evTotal} EVs total - the game caps EVs at 510 total (252 per stat).";
    }

    /// <summary>One display row, e.g. "Attack: 394 (+10% nature)" - the marker
    /// makes it obvious which stats the chosen nature is bending.</summary>
    private static string StatLine(string statName, int baseStat, int iv, int ev, int level, string nature)
    {
        double natureMultiplier = PokemonBattleMath.GetNatureMultiplier(nature, statName);
        int value = PokemonBattleMath.CalculateStat(baseStat, iv, ev, level, natureMultiplier);

        string display = statName switch
        {
            "SpAttack" => "Sp. Attack",
            "SpDefense" => "Sp. Defense",
            _ => statName,
        };

        string marker = natureMultiplier > 1.0 ? " (+10% nature)"
                      : natureMultiplier < 1.0 ? " (-10% nature)"
                      : "";

        return $"{display}: {value}{marker}";
    }

    private static int ParseClamped(string? text, int min, int max, int fallback)
    {
        return int.TryParse((text ?? "").Trim(), out int value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }
}
