using System;
using System.Collections.Generic;
using System.Linq;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// All the game math behind the Calculators menu (Damage / World Quest /
    /// IV) in one place, ported from the PokemonSim side project's Engine
    /// (DamageCalculator/StatCalculator/NatureCalculator/StatStageCalculator/
    /// TypeChart) and completed where that project only carried what its
    /// simulator needed - see MIGRATION_GUIDE.md §89 for what was ported
    /// as-is, what was completed (natures: 13 -> all 25), and what was fixed
    /// (the type chart's duplicated (Dragon, Steel) entry, whose second copy
    /// was meant to be Dragon vs Fairy = 0x - as written it would also have
    /// thrown at runtime, since a Dictionary collection initializer rejects
    /// duplicate keys).
    ///
    /// Every table and formula here was verified before this file was
    /// written, not after: the type chart against the canonical 51/61/8
    /// super-effective/not-very/immune checksum, stats against known values
    /// (Adamant 252 Garchomp = 394 Atk at 100 / 200 at 50, max HP Blissey =
    /// 714), Hidden Power against Bulbapedia's own formula page, and the
    /// damage chain (modifier order and round-half-down behavior) against
    /// Bulbapedia's Generation V+ formula. The C# table initializers below
    /// were generated from those verified tables programmatically rather
    /// than retyped by hand. See MIGRATION_GUIDE.md §89.
    /// </summary>
    public static class PokemonBattleMath
    {
        // ====================================================================
        // Rounding
        // ====================================================================

        /// <summary>The games' own "round half DOWN" (5325/4096-style chained
        /// modifiers round this way) - used for every damage modifier step.
        /// Math.Round can't be used here: its default is banker's rounding,
        /// and MidpointRounding has no half-down mode.</summary>
        public static int PokeRound(double value)
        {
            int floor = (int)Math.Floor(value);
            return value - floor > 0.5 ? floor + 1 : floor;
        }

        // ====================================================================
        // Stats (canonical Gen 3+ formulas)
        // ====================================================================

        /// <summary>HP = floor((2*Base + IV + floor(EV/4)) * Level / 100) + Level + 10.
        /// Shedinja (base HP 1) is special-cased to 1 the way the games do.</summary>
        public static int CalculateHp(int baseStat, int iv, int ev, int level)
        {
            if (baseStat == 1)
                return 1;

            return (int)Math.Floor((2 * baseStat + iv + ev / 4) * level / 100.0) + level + 10;
        }

        /// <summary>Any non-HP stat = floor((floor((2*Base + IV + floor(EV/4)) * Level / 100) + 5) * nature).</summary>
        public static int CalculateStat(int baseStat, int iv, int ev, int level, double natureMultiplier)
        {
            int inner = (int)Math.Floor((2 * baseStat + iv + ev / 4) * level / 100.0) + 5;
            return (int)Math.Floor(inner * natureMultiplier);
        }

        /// <summary>+1..+6 -> (2+s)/2, -1..-6 -> 2/(2-s) - the standard in-battle
        /// stat stage multipliers (x2 at +2, x0.5 at -2, x4/x0.25 at the caps).</summary>
        public static double StageMultiplier(int stage)
        {
            stage = Math.Clamp(stage, -6, 6);
            return stage >= 0 ? (2.0 + stage) / 2.0 : 2.0 / (2.0 - stage);
        }

        // ====================================================================
        // Natures - complete 25-nature catalog (PokemonSim carried the 13 its
        // simulator's preferred-nature lists used; the missing 7 non-neutral
        // ones and 5 neutral ones are filled in here). Verified: every stat
        // is boosted by exactly 4 natures and hindered by exactly 4, and all
        // 13 PokemonSim mappings agree with this table.
        // ====================================================================

        /// <summary>Nature name -> (boosted stat, hindered stat), null/null for
        /// the five neutral natures. Stat names: Attack, Defense, SpAttack,
        /// SpDefense, Speed.</summary>
        public static readonly IReadOnlyDictionary<string, (string? Plus, string? Minus)> NatureCatalog =
            new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase)
            {
            ["Hardy"] = (null, null),
            ["Docile"] = (null, null),
            ["Serious"] = (null, null),
            ["Bashful"] = (null, null),
            ["Quirky"] = (null, null),
            ["Lonely"] = ("Attack", "Defense"),
            ["Brave"] = ("Attack", "Speed"),
            ["Adamant"] = ("Attack", "SpAttack"),
            ["Naughty"] = ("Attack", "SpDefense"),
            ["Bold"] = ("Defense", "Attack"),
            ["Relaxed"] = ("Defense", "Speed"),
            ["Impish"] = ("Defense", "SpAttack"),
            ["Lax"] = ("Defense", "SpDefense"),
            ["Timid"] = ("Speed", "Attack"),
            ["Hasty"] = ("Speed", "Defense"),
            ["Jolly"] = ("Speed", "SpAttack"),
            ["Naive"] = ("Speed", "SpDefense"),
            ["Modest"] = ("SpAttack", "Attack"),
            ["Mild"] = ("SpAttack", "Defense"),
            ["Quiet"] = ("SpAttack", "Speed"),
            ["Rash"] = ("SpAttack", "SpDefense"),
            ["Calm"] = ("SpDefense", "Attack"),
            ["Gentle"] = ("SpDefense", "Defense"),
            ["Sassy"] = ("SpDefense", "Speed"),
            ["Careful"] = ("SpDefense", "SpAttack"),
            };

        /// <summary>All 25 nature names, alphabetical, for dropdowns.</summary>
        public static readonly IReadOnlyList<string> NatureNames =
            NatureCatalog.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        public static double GetNatureMultiplier(string? natureName, string statName)
        {
            if (natureName != null &&
                NatureCatalog.TryGetValue(natureName, out (string? Plus, string? Minus) nature))
            {
                if (string.Equals(nature.Plus, statName, StringComparison.OrdinalIgnoreCase))
                    return 1.1;
                if (string.Equals(nature.Minus, statName, StringComparison.OrdinalIgnoreCase))
                    return 0.9;
            }

            return 1.0;
        }

        // ====================================================================
        // Type chart - canonical Gen 6+ (Fairy included), generated from a
        // table that passed the 51 super-effective / 61 not-very-effective /
        // 8 immune checksum. Keyed (attacking type, defending type); every
        // pair not listed is neutral (x1).
        // ====================================================================

        private static readonly Dictionary<(string, string), double> TypeChartEntries = new()
        {
            [("Normal", "Rock")] = 0.5,
            [("Normal", "Ghost")] = 0.0,
            [("Normal", "Steel")] = 0.5,
            [("Fire", "Fire")] = 0.5,
            [("Fire", "Water")] = 0.5,
            [("Fire", "Grass")] = 2.0,
            [("Fire", "Ice")] = 2.0,
            [("Fire", "Bug")] = 2.0,
            [("Fire", "Rock")] = 0.5,
            [("Fire", "Dragon")] = 0.5,
            [("Fire", "Steel")] = 2.0,
            [("Water", "Fire")] = 2.0,
            [("Water", "Water")] = 0.5,
            [("Water", "Grass")] = 0.5,
            [("Water", "Ground")] = 2.0,
            [("Water", "Rock")] = 2.0,
            [("Water", "Dragon")] = 0.5,
            [("Electric", "Water")] = 2.0,
            [("Electric", "Electric")] = 0.5,
            [("Electric", "Grass")] = 0.5,
            [("Electric", "Ground")] = 0.0,
            [("Electric", "Flying")] = 2.0,
            [("Electric", "Dragon")] = 0.5,
            [("Grass", "Fire")] = 0.5,
            [("Grass", "Water")] = 2.0,
            [("Grass", "Grass")] = 0.5,
            [("Grass", "Poison")] = 0.5,
            [("Grass", "Ground")] = 2.0,
            [("Grass", "Flying")] = 0.5,
            [("Grass", "Bug")] = 0.5,
            [("Grass", "Rock")] = 2.0,
            [("Grass", "Dragon")] = 0.5,
            [("Grass", "Steel")] = 0.5,
            [("Ice", "Fire")] = 0.5,
            [("Ice", "Water")] = 0.5,
            [("Ice", "Grass")] = 2.0,
            [("Ice", "Ice")] = 0.5,
            [("Ice", "Ground")] = 2.0,
            [("Ice", "Flying")] = 2.0,
            [("Ice", "Dragon")] = 2.0,
            [("Ice", "Steel")] = 0.5,
            [("Fighting", "Normal")] = 2.0,
            [("Fighting", "Ice")] = 2.0,
            [("Fighting", "Poison")] = 0.5,
            [("Fighting", "Flying")] = 0.5,
            [("Fighting", "Psychic")] = 0.5,
            [("Fighting", "Bug")] = 0.5,
            [("Fighting", "Rock")] = 2.0,
            [("Fighting", "Ghost")] = 0.0,
            [("Fighting", "Dark")] = 2.0,
            [("Fighting", "Steel")] = 2.0,
            [("Fighting", "Fairy")] = 0.5,
            [("Poison", "Grass")] = 2.0,
            [("Poison", "Poison")] = 0.5,
            [("Poison", "Ground")] = 0.5,
            [("Poison", "Rock")] = 0.5,
            [("Poison", "Ghost")] = 0.5,
            [("Poison", "Steel")] = 0.0,
            [("Poison", "Fairy")] = 2.0,
            [("Ground", "Fire")] = 2.0,
            [("Ground", "Electric")] = 2.0,
            [("Ground", "Grass")] = 0.5,
            [("Ground", "Poison")] = 2.0,
            [("Ground", "Flying")] = 0.0,
            [("Ground", "Bug")] = 0.5,
            [("Ground", "Rock")] = 2.0,
            [("Ground", "Steel")] = 2.0,
            [("Flying", "Electric")] = 0.5,
            [("Flying", "Grass")] = 2.0,
            [("Flying", "Fighting")] = 2.0,
            [("Flying", "Bug")] = 2.0,
            [("Flying", "Rock")] = 0.5,
            [("Flying", "Steel")] = 0.5,
            [("Psychic", "Fighting")] = 2.0,
            [("Psychic", "Poison")] = 2.0,
            [("Psychic", "Psychic")] = 0.5,
            [("Psychic", "Dark")] = 0.0,
            [("Psychic", "Steel")] = 0.5,
            [("Bug", "Fire")] = 0.5,
            [("Bug", "Grass")] = 2.0,
            [("Bug", "Fighting")] = 0.5,
            [("Bug", "Poison")] = 0.5,
            [("Bug", "Flying")] = 0.5,
            [("Bug", "Psychic")] = 2.0,
            [("Bug", "Ghost")] = 0.5,
            [("Bug", "Dark")] = 2.0,
            [("Bug", "Steel")] = 0.5,
            [("Bug", "Fairy")] = 0.5,
            [("Rock", "Fire")] = 2.0,
            [("Rock", "Ice")] = 2.0,
            [("Rock", "Fighting")] = 0.5,
            [("Rock", "Ground")] = 0.5,
            [("Rock", "Flying")] = 2.0,
            [("Rock", "Bug")] = 2.0,
            [("Rock", "Steel")] = 0.5,
            [("Ghost", "Normal")] = 0.0,
            [("Ghost", "Psychic")] = 2.0,
            [("Ghost", "Ghost")] = 2.0,
            [("Ghost", "Dark")] = 0.5,
            [("Dragon", "Dragon")] = 2.0,
            [("Dragon", "Steel")] = 0.5,
            [("Dragon", "Fairy")] = 0.0,
            [("Dark", "Fighting")] = 0.5,
            [("Dark", "Psychic")] = 2.0,
            [("Dark", "Ghost")] = 2.0,
            [("Dark", "Dark")] = 0.5,
            [("Dark", "Fairy")] = 0.5,
            [("Steel", "Fire")] = 0.5,
            [("Steel", "Water")] = 0.5,
            [("Steel", "Electric")] = 0.5,
            [("Steel", "Ice")] = 2.0,
            [("Steel", "Rock")] = 2.0,
            [("Steel", "Steel")] = 0.5,
            [("Steel", "Fairy")] = 2.0,
            [("Fairy", "Fire")] = 0.5,
            [("Fairy", "Fighting")] = 2.0,
            [("Fairy", "Poison")] = 0.5,
            [("Fairy", "Dragon")] = 2.0,
            [("Fairy", "Dark")] = 2.0,
            [("Fairy", "Steel")] = 0.5,
        };

        private static readonly Dictionary<string, Dictionary<string, double>> TypeChart = BuildTypeChart();

        private static Dictionary<string, Dictionary<string, double>> BuildTypeChart()
        {
            var chart = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<(string Attack, string Defend), double> entry in TypeChartEntries)
            {
                if (!chart.TryGetValue(entry.Key.Attack, out Dictionary<string, double>? row))
                {
                    row = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    chart[entry.Key.Attack] = row;
                }

                row[entry.Key.Defend] = entry.Value;
            }

            return chart;
        }

        /// <summary>x0 / x0.25 / x0.5 / x1 / x2 / x4 for a move type against one
        /// or two defending types. Unknown type names count as neutral rather
        /// than throwing - data files, not code, supply these strings.</summary>
        public static double GetTypeEffectiveness(string moveType, IReadOnlyList<string> defenderTypes)
        {
            double result = 1.0;

            if (string.IsNullOrWhiteSpace(moveType))
                return result;

            foreach (string defenderType in defenderTypes)
            {
                if (TypeChart.TryGetValue(moveType.Trim(), out Dictionary<string, double>? row) &&
                    row.TryGetValue(defenderType.Trim(), out double multiplier))
                {
                    result *= multiplier;
                }
            }

            return result;
        }

        // ====================================================================
        // Hidden Power - type from IVs. Gen 3+ formula, confirmed against
        // Bulbapedia's calculation page: floor((a+2b+4c+8d+16e+32f)*15/63),
        // a..f = the low bit of the HP/Atk/Def/Spe/SpA/SpD IVs IN THAT ORDER
        // - note Speed comes before the special stats. Verified anchors:
        // all-31 IVs -> Dark, all-even -> Fighting, 31/30/30/31/31/31 -> Ice.
        // PRO uses the fixed 60 power (see moves.json's own Hidden Power row),
        // so only the type needs computing.
        // ====================================================================

        private static readonly string[] HiddenPowerTypes =
        {
            "Fighting", "Flying", "Poison", "Ground", "Rock", "Bug", "Ghost", "Steel", "Fire", "Water", "Grass", "Electric", "Psychic", "Ice", "Dragon", "Dark"
        };

        /// <summary>ivs must be ordered HP, Attack, Defense, Speed, SpAttack, SpDefense.</summary>
        public static string GetHiddenPowerType(IReadOnlyList<int> ivs)
        {
            if (ivs.Count != 6)
                return "?";

            int value = 0;

            for (int i = 0; i < 6; i++)
                value |= (ivs[i] & 1) << i;

            return HiddenPowerTypes[value * 15 / 63];
        }

        // ====================================================================
        // Damage - deterministic min/max range instead of PokemonSim's single
        // random roll: the chain below is run once with the 85% roll and once
        // with the 100% roll (the game rolls an integer 85..100). Modifier
        // order per Bulbapedia's Gen V+ formula: base, weather, crit, random,
        // STAB, type effectiveness, burn, then "other" (screens, Multiscale,
        // Filter, Expert Belt, Life Orb, Tinted Lens), round-half-down at
        // each step except random/type which floor, minimum 1 unless immune.
        // Exact per-step rounding in the real games runs in 1/4096ths and can
        // drift by a point in rare edge cases - close enough for a planning
        // calculator, and the honest limit is documented here on purpose.
        // ====================================================================

        public sealed class DamageRequest
        {
            public int Level { get; init; } = 100;
            public int MovePower { get; init; }
            public required string MoveType { get; init; }
            public required string MoveCategory { get; init; } // "Physical" / "Special"
            public required IReadOnlyList<string> AttackerTypes { get; init; }
            public required IReadOnlyList<string> DefenderTypes { get; init; }

            /// <summary>The already-computed attacking stat (Attack or SpAttack)
            /// BEFORE stages/items/abilities - CalculateStat's output.</summary>
            public int AttackStat { get; init; }
            public int AttackStage { get; init; }
            public string AttackerAbility { get; init; } = "None";
            public string AttackerItem { get; init; } = "None";
            public bool AttackerBurned { get; init; }

            /// <summary>The already-computed defending stat (Defense or SpDefense)
            /// BEFORE stages - CalculateStat's output.</summary>
            public int DefenseStat { get; init; }
            public int DefenseStage { get; init; }
            public string DefenderAbility { get; init; } = "None";
            public bool DefenderAtFullHp { get; init; } = true;

            public string Weather { get; init; } = "None"; // see WeatherOptions
            public bool ScreenUp { get; init; }

            /// <summary>The selected move's display name - lets weather apply
            /// its move-specific interactions (Weather Ball, Solar Beam/
            /// Solar Blade) without a second move-data lookup here (§103).
            /// Empty is fine: only those exact names change anything.</summary>
            public string MoveName { get; init; } = "";
        }

        public sealed class DamageResult
        {
            /// <summary>What the selected weather actually did to THIS
            /// calculation, in words - empty when it did nothing. The §103
            /// anti-nonfunctional-dropdown rule made this explicit: Hail
            /// (Snow) in particular has NO damage-side effect in PRO (chip
            /// damage only, no Ice-type Defense boost - PRO implements Hail,
            /// not modern Snow, verified against the PRO wiki), and saying
            /// so beats silently multiplying by 1.</summary>
            public string WeatherNote { get; init; } = "";

            public int MinDamage { get; init; }
            public int MaxDamage { get; init; }
            public int CritMinDamage { get; init; }
            public int CritMaxDamage { get; init; }
            public double TypeEffectiveness { get; init; }
            public bool HasStab { get; init; }
            public int EffectiveAttack { get; init; }
            public int EffectiveDefense { get; init; }
        }

        // Shown in the Damage window's dropdowns - deliberately curated to the
        // modifiers whose math is simple and known, rather than every item and
        // ability in the game. "Huge Power" covers Pure Power (identical x2),
        // "Filter" covers Solid Rock (identical x0.75).
        public static readonly IReadOnlyList<string> AttackerItems = new[]
            { "None", "Choice Band", "Choice Specs", "Life Orb", "Expert Belt", "Muscle Band", "Wise Glasses" };

        public static readonly IReadOnlyList<string> AttackerAbilities = new[]
            { "None", "Adaptability", "Guts", "Huge Power", "Sheer Force", "Technician", "Tinted Lens" };

        public static readonly IReadOnlyList<string> DefenderAbilities = new[]
            { "None", "Filter", "Flash Fire", "Levitate", "Multiscale", "Thick Fat", "Volt Absorb", "Water Absorb" };

        // §103: Sandstorm and the ice weather joined Sun/Rain. PRO's ice
        // weather IS Hail (chip 1/16, Blizzard accuracy, no defensive boost -
        // PRO wiki, checked 2026-08-29); the modern games renamed it Snow and
        // added an Ice-type Defense boost PRO does not have, so the option
        // carries both names and deliberately implements PRO's version.
        // Sandstorm's Rock-type Special Defense x1.5 IS in PRO and applies
        // below as a defensive-stat multiplier (not a final-damage fudge).
        // Residual chip damage stays out of scope - this calculator has no
        // end-of-turn model at all.
        public static readonly IReadOnlyList<string> WeatherOptions = new[] { "None", "Sun", "Rain", "Sandstorm", "Hail (Snow)" };

        public static DamageResult Calculate(DamageRequest request)
        {
            bool physical = string.Equals(request.MoveCategory, "Physical", StringComparison.OrdinalIgnoreCase);
            bool special = string.Equals(request.MoveCategory, "Special", StringComparison.OrdinalIgnoreCase);

            // ---- §103 move-specific weather interactions, resolved before
            // anything reads the move's type or power ----
            string moveType = request.MoveType;
            int movePower = request.MovePower;
            string weatherNote = "";
            string moveName = request.MoveName?.Trim() ?? "";
            bool weatherActive = request.Weather is "Sun" or "Rain" or "Sandstorm" or "Hail (Snow)";

            if (weatherActive && string.Equals(moveName, "Weather Ball", StringComparison.OrdinalIgnoreCase))
            {
                moveType = request.Weather switch
                {
                    "Sun" => "Fire",
                    "Rain" => "Water",
                    "Sandstorm" => "Rock",
                    _ => "Ice",
                };
                movePower *= 2;
                weatherNote = $"Weather Ball became {moveType}-type at double power in {request.Weather}. ";
            }
            else if ((string.Equals(moveName, "Solar Beam", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(moveName, "Solar Blade", StringComparison.OrdinalIgnoreCase)) &&
                     request.Weather is "Rain" or "Sandstorm" or "Hail (Snow)")
            {
                movePower = (int)Math.Floor(movePower * 0.5);
                weatherNote = $"{moveName}'s power is halved in {request.Weather}. ";
            }

            double typeEffectiveness = GetTypeEffectiveness(moveType, request.DefenderTypes);

            // Absorbing/immunity abilities zero the move out entirely.
            typeEffectiveness *= (request.DefenderAbility, NormalizedType(moveType)) switch
            {
                ("Levitate", "Ground") => 0.0,
                ("Flash Fire", "Fire") => 0.0,
                ("Volt Absorb", "Electric") => 0.0,
                ("Water Absorb", "Water") => 0.0,
                _ => 1.0
            };

            bool hasStab = request.AttackerTypes.Any(t =>
                string.Equals(t?.Trim(), moveType.Trim(), StringComparison.OrdinalIgnoreCase));

            if ((!physical && !special) || movePower <= 0 || typeEffectiveness == 0)
            {
                return new DamageResult
                {
                    TypeEffectiveness = typeEffectiveness,
                    HasStab = hasStab,
                    EffectiveAttack = request.AttackStat,
                    EffectiveDefense = request.DefenseStat,
                    WeatherNote = weatherNote.Trim()
                };
            }

            // ---- Power-side modifiers ----
            int power = movePower;

            if (request.AttackerAbility == "Technician" && power <= 60)
                power = (int)Math.Floor(power * 1.5);

            if (request.AttackerAbility == "Sheer Force")
                power = (int)Math.Floor(power * 1.3);

            if (request.AttackerItem == "Muscle Band" && physical)
                power = (int)Math.Floor(power * 1.1);

            if (request.AttackerItem == "Wise Glasses" && special)
                power = (int)Math.Floor(power * 1.1);

            // ---- Attack-side stat (normal and crit variants - a crit ignores
            // the attacker's NEGATIVE stages and the defender's POSITIVE ones) ----
            int AttackFor(bool crit)
            {
                int stage = crit ? Math.Max(request.AttackStage, 0) : request.AttackStage;
                int value = (int)Math.Floor(request.AttackStat * StageMultiplier(stage));

                if (request.AttackerAbility == "Huge Power" && physical)
                    value = PokeRound(value * 2.0);

                if (request.AttackerAbility == "Guts" && request.AttackerBurned)
                    value = PokeRound(value * 1.5);

                if (request.AttackerItem == "Choice Band" && physical)
                    value = PokeRound(value * 1.5);

                if (request.AttackerItem == "Choice Specs" && special)
                    value = PokeRound(value * 1.5);

                if (request.DefenderAbility == "Thick Fat" &&
                    (NormalizedType(moveType) is "Fire" or "Ice"))
                {
                    value = PokeRound(value * 0.5);
                }

                return Math.Max(value, 1);
            }

            bool rockDefender = request.DefenderTypes.Any(t =>
                string.Equals(t?.Trim(), "Rock", StringComparison.OrdinalIgnoreCase));

            bool sandstormSpDefApplies = special && request.Weather == "Sandstorm" && rockDefender;

            int DefenseFor(bool crit)
            {
                int stage = crit ? Math.Min(request.DefenseStage, 0) : request.DefenseStage;
                int value = Math.Max((int)Math.Floor(request.DefenseStat * StageMultiplier(stage)), 1);

                // §103 Sandstorm: Rock-type Pokémon get Special Defense x1.5
                // (PRO wiki, exact main-series mechanic). Applied here on the
                // defensive STAT - the calculator models defense separately,
                // so a final-damage fudge would be the wrong layer - and not
                // gated on crit, because it is a stat boost, not a screen.
                if (sandstormSpDefApplies)
                    value = PokeRound(value * 1.5);

                return Math.Max(value, 1);
            }

            double weather = (request.Weather, NormalizedType(moveType)) switch
            {
                ("Sun", "Fire") => 1.5,
                ("Sun", "Water") => 0.5,
                ("Rain", "Water") => 1.5,
                ("Rain", "Fire") => 0.5,
                _ => 1.0
            };

            // ---- §103: say what the weather actually did (or honestly
            // didn't) - the dropdown must never be a silent no-op. ----
            if (sandstormSpDefApplies)
                weatherNote += "Sandstorm: the Rock-type defender's Special Defense is x1.5. ";

            if (weather != 1.0)
                weatherNote += $"{request.Weather}: {NormalizedType(moveType)} move power x{weather:0.#}. ";

            if (weatherNote.Length == 0 && request.Weather == "Sandstorm")
                weatherNote = "Sandstorm: no effect on this matchup (end-of-turn chip damage is outside this calculator's scope).";

            if (weatherNote.Length == 0 && request.Weather == "Hail (Snow)")
                weatherNote = "Hail (Snow): no effect on this matchup - PRO implements Hail (no Ice-type Defense boost; chip damage is outside this calculator's scope).";

            double stab = hasStab ? (request.AttackerAbility == "Adaptability" ? 2.0 : 1.5) : 1.0;

            // Burn halves physical damage - unless Guts is what's powering the
            // attacker through it (Guts both ignores the halving and got its
            // x1.5 above).
            bool burnApplies = request.AttackerBurned && physical && request.AttackerAbility != "Guts";

            int Chain(int roll, bool crit)
            {
                int attack = AttackFor(crit);
                int defense = DefenseFor(crit);

                int baseDamage = (int)Math.Floor(Math.Floor(
                    (Math.Floor(2.0 * request.Level / 5.0) + 2.0) * power * attack / defense) / 50.0) + 2;

                int damage = baseDamage;
                damage = PokeRound(damage * weather);

                if (crit)
                    damage = PokeRound(damage * 1.5);

                damage = (int)Math.Floor(damage * roll / 100.0);
                damage = PokeRound(damage * stab);
                damage = (int)Math.Floor(damage * typeEffectiveness);

                if (burnApplies)
                    damage = PokeRound(damage * 0.5);

                // "Other" modifiers, applied in a stable order. Screens don't
                // apply on a crit; Multiscale only at full HP.
                if (request.ScreenUp && !crit)
                    damage = PokeRound(damage * 0.5);

                if (request.DefenderAbility == "Multiscale" && request.DefenderAtFullHp)
                    damage = PokeRound(damage * 0.5);

                if (request.DefenderAbility == "Filter" && typeEffectiveness > 1.0)
                    damage = PokeRound(damage * 0.75);

                if (request.AttackerAbility == "Tinted Lens" && typeEffectiveness < 1.0)
                    damage = PokeRound(damage * 2.0);

                if (request.AttackerItem == "Expert Belt" && typeEffectiveness > 1.0)
                    damage = PokeRound(damage * 1.2);

                if (request.AttackerItem == "Life Orb")
                    damage = PokeRound(damage * 1.3);

                return Math.Max(damage, 1);
            }

            return new DamageResult
            {
                MinDamage = Chain(85, crit: false),
                MaxDamage = Chain(100, crit: false),
                CritMinDamage = Chain(85, crit: true),
                CritMaxDamage = Chain(100, crit: true),
                TypeEffectiveness = typeEffectiveness,
                HasStab = hasStab,
                EffectiveAttack = AttackFor(false),
                EffectiveDefense = DefenseFor(false),
                WeatherNote = weatherNote.Trim()
            };
        }

        private static string NormalizedType(string type)
        {
            string trimmed = type.Trim();

            // Normalize casing so switch patterns above match however the data
            // files spell it ("Fire"/"fire"/"FIRE" all become "Fire").
            return trimmed.Length == 0
                ? trimmed
                : char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
        }
    }
}
