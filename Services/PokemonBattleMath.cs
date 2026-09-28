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
            /// <summary>§307: kept for the notes and for DefenderAtFullHp's
            /// sake, but nothing in the chain below reads it any more. Every
            /// ability reaches a damage number through MoveOracle's measured
            /// factor instead, which knows 124 of them rather than the seven
            /// this file used to carry - and which cannot disagree with the
            /// simulator, because it IS the simulator.</summary>
            public string AttackerAbility { get; init; } = "None";
            public bool AttackerBurned { get; init; }

            /// <summary>The already-computed defending stat (Defense or SpDefense)
            /// BEFORE stages - CalculateStat's output.</summary>
            public int DefenseStat { get; init; }
            public int DefenseStage { get; init; }
            /// <summary>§307: see AttackerAbility. Not read by the chain.</summary>
            public string DefenderAbility { get; init; } = "None";
            public bool DefenderAtFullHp { get; init; } = true;

            public string Weather { get; init; } = "None"; // see WeatherOptions

            /// <summary>§261. One of TerrainOptions. The rules are ported from
            /// the battle engine's own (PokemonSim/Engine/TerrainEffects.cs and
            /// the Misty clause in its DamageCalculator) rather than written a
            /// second time here, so the calculator and the simulator cannot
            /// disagree about what a terrain does.</summary>
            public string Terrain { get; init; } = "None";

            /// <summary>Terrain only boosts a GROUNDED attacker's move, and
            /// Misty only shields a GROUNDED target - see IsGrounded.</summary>
            public bool AttackerGrounded { get; init; } = true;
            public bool DefenderGrounded { get; init; } = true;

            /// <summary>§261. The defender's side screens, replacing §72's
            /// single ScreenUp flag. Reflect covers Physical, Light Screen
            /// covers Special, Aurora Veil covers both - and none of the three
            /// applies to a critical hit.</summary>
            public bool Reflect { get; init; }
            public bool LightScreen { get; init; }
            public bool AuroraVeil { get; init; }

            /// <summary>The selected move's display name - lets weather apply
            /// its move-specific interactions (Solar Beam / Solar Blade)
            /// without a second move-data lookup here (§103). Empty is fine:
            /// only those exact names change anything.
            ///
            /// §306: Weather Ball came off this list. The simulator answers
            /// its type and its power now, and doing it here as well would
            /// double the same boost twice.</summary>
            public string MoveName { get; init; } = "";

            // ---- §306 ----

            /// <summary>Charge: the user's next Electric move hits twice as
            /// hard. It lives here rather than in the simulator bridge
            /// because the simulator applies it in its own damage chain
            /// rather than in a move effect, and the bridge only measures
            /// effects.</summary>
            public bool AttackerCharged { get; init; }

            /// <summary>Foresight: the target's Ghost type stops refusing
            /// Normal and Fighting moves. It is the only §306 field option
            /// that needed the type chart, which is why it is a flag here and
            /// not a multiplier at the call site.</summary>
            public bool DefenderForesighted { get; init; }
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

            /// <summary>§261. The same promise §103 made for weather, kept for
            /// terrain: what the selected terrain did to THIS calculation, or
            /// why it did nothing. A toggle that silently multiplies by 1 is
            /// the thing §103 existed to stop.</summary>
            public string TerrainNote { get; init; } = "";

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
        /// <summary>§309: the seven this window offered before the engine
        /// owned items. Kept only as the fallback for a build where
        /// HeldItems cannot be reached - the Damage window builds its list
        /// out of the engine's hundred and five now, and every entry reaches
        /// the damage through MoveOracle's measured factor.</summary>
        public static readonly IReadOnlyList<string> AttackerItems = new[]
            { "None", "Choice Band", "Choice Specs", "Life Orb", "Expert Belt", "Muscle Band", "Wise Glasses" };

        /// <summary>§307: these two lists are what the window offered before
        /// it knew which Pokemon it was asking about. They are kept because
        /// the IV and World Quest calculators still name them, and because a
        /// species the dex does not know falls back to them - but the Damage
        /// window builds its list per species now, out of the dex, and every
        /// entry reaches the damage through the simulator.</summary>
        public static readonly IReadOnlyList<string> AttackerAbilities = new[]
            { "None", "Adaptability", "Guts", "Huge Power", "Sheer Force", "Technician", "Tinted Lens" };

        public static readonly IReadOnlyList<string> DefenderAbilities = new[]
            { "None", "Filter", "Flash Fire", "Levitate", "Multiscale", "Thick Fat", "Volt Absorb", "Water Absorb" };

        /// <summary>§261. Both ability lists in one, for a window where each
        /// side both attacks and defends: the old window had a fixed attacker
        /// and a fixed defender, so two lists were enough. Every entry still
        /// does something (§103) - the offensive ones in the power and attack
        /// steps, the defensive ones in effectiveness, defense and the final
        /// modifiers - and an ability listed for the wrong role simply does
        /// not fire, which is what it does in a real battle too.</summary>
        public static readonly IReadOnlyList<string> AllAbilities =
            AttackerAbilities.Concat(DefenderAbilities.Where(a => a != "None"))
                             .OrderBy(a => a == "None" ? 0 : 1)
                             .ThenBy(a => a, StringComparer.Ordinal)
                             .ToArray();

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

        // §261. The four terrains the battle engine models. Electric, Grassy
        // and Psychic each boost their own type by x1.3 for a grounded
        // attacker; Misty gives no boost at all and instead halves Dragon
        // moves aimed at a grounded target. Those are exactly the engine's
        // rules - see TerrainEffects.GetDamageModifier and the Misty clause in
        // its DamageCalculator.
        //
        // What is deliberately NOT here, because the engine does not have it
        // either: Grassy Terrain halving Earthquake, Bulldoze and Magnitude
        // into grounded targets. Adding it on this side alone would make the
        // calculator and the simulator disagree about the same move on the
        // same field, which is worse than a known, named gap. It wants adding
        // to both at once, in a section of its own.
        public static readonly IReadOnlyList<string> TerrainOptions =
            new[] { "None", "Electric", "Grassy", "Psychic", "Misty" };

        /// <summary>
        /// §261. Ported from the battle engine's Grounding.IsGrounded: a
        /// Flying type hovers, and so does Levitate.
        ///
        /// The engine also grounds for Gravity and Ingrain and un-grounds for
        /// Magnet Rise and an unpopped Air Balloon. This calculator has no
        /// input for any of those four, so they cannot be wrong here - they
        /// are simply not askable, which is why this takes types and an
        /// ability rather than pretending to a fuller model.
        /// </summary>
        public static bool IsGrounded(IReadOnlyList<string> types, string? ability)
        {
            if (string.Equals(ability?.Replace(" ", "").Trim(), "levitate", StringComparison.OrdinalIgnoreCase))
                return false;

            foreach (string? type in types)
            {
                if (string.Equals(type?.Trim(), "Flying", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

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
            // §306: Weather Ball used to be handled here, doubling its power
            // and swapping its type. The simulator's own WeatherBall effect
            // does both now and the bridge reports the answer, so doing it
            // twice would double a boost the caller has already applied.
            // Solar Beam stays: nothing in the simulator models its halving.
            if ((string.Equals(moveName, "Solar Beam", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(moveName, "Solar Blade", StringComparison.OrdinalIgnoreCase)) &&
                     request.Weather is "Rain" or "Sandstorm" or "Hail (Snow)")
            {
                movePower = (int)Math.Floor(movePower * 0.5);
                weatherNote = $"{moveName}'s power is halved in {request.Weather}. ";
            }

            // §306: Foresight (and Odor Sleuth, and Scrappy) let Normal and
            // Fighting through a Ghost's immunity. Everything else about the
            // matchup is unchanged, so the Ghost type is simply not consulted
            // for those two move types.
            IReadOnlyList<string> defenderTypes = request.DefenderTypes;

            if (request.DefenderForesighted &&
                (NormalizedType(moveType) is "Normal" or "Fighting"))
            {
                defenderTypes = defenderTypes
                    .Where(t => !string.Equals(t?.Trim(), "Ghost", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            double typeEffectiveness = GetTypeEffectiveness(moveType, defenderTypes);

            // §307: the absorbing and immunity abilities used to be a table
            // here. Every ability now comes through AbilityFactor, measured
            // off the simulator, which reports an immunity as a factor of
            // zero - and knows 124 of them rather than these four.

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
            // §309: Muscle Band and Wise Glasses used to add their tenth
            // here. Every item reaches the damage through MoveOracle's
            // measured factor now, for §307's reason applied to items.
            int power = movePower;

            // ---- Attack-side stat (normal and crit variants - a crit ignores
            // the attacker's NEGATIVE stages and the defender's POSITIVE ones) ----
            int AttackFor(bool crit)
            {
                int stage = crit ? Math.Max(request.AttackStage, 0) : request.AttackStage;
                // §309: the Choice items' half again used to be applied here
                // AND by the engine's StatResolver. Only the engine does now.
                int value = (int)Math.Floor(request.AttackStat * StageMultiplier(stage));

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

            // ---- §261 terrain, by the battle engine's rules ----
            //
            // Electric/Grassy/Psychic: x1.3 on their own type, and only when
            // the ATTACKER is grounded. Misty: no boost at all - it halves
            // Dragon moves aimed at a grounded DEFENDER. One terrain is up at
            // a time, so the two clauses can never both fire.
            string terrainName = (request.Terrain ?? "None").Trim();
            string terrainMoveType = NormalizedType(moveType);
            double terrain = 1.0;
            string terrainNote = "";

            if (request.AttackerGrounded)
            {
                bool boosted =
                    (terrainName == "Electric" && terrainMoveType == "Electric") ||
                    (terrainName == "Grassy" && terrainMoveType == "Grass") ||
                    (terrainName == "Psychic" && terrainMoveType == "Psychic");

                if (boosted)
                {
                    terrain = 1.3;
                    terrainNote = $"{terrainName} Terrain: {terrainMoveType} move power x1.3. ";
                }
            }

            if (terrainName == "Misty" && terrainMoveType == "Dragon" && request.DefenderGrounded)
            {
                terrain *= 0.5;
                terrainNote += "Misty Terrain: Dragon damage into the grounded defender x0.5. ";
            }

            // §103's rule, kept: a terrain that changed nothing says so, and
            // says which of the three reasons it was.
            if (terrainNote.Length == 0 && terrainName != "None")
            {
                terrainNote = terrainName == "Misty"
                    ? (terrainMoveType == "Dragon"
                        ? "Misty Terrain: no effect - it only shields a GROUNDED defender, and this one is not."
                        : "Misty Terrain: no effect on this move - it halves Dragon moves only, and gives no boost of its own.")
                    : (!request.AttackerGrounded
                        ? $"{terrainName} Terrain: no effect - terrain only boosts a GROUNDED attacker, and this one hovers."
                        : $"{terrainName} Terrain: no effect on this move - it boosts {(terrainName == "Grassy" ? "Grass" : terrainName)} moves only.");
            }

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

            double stab = hasStab ? 1.5 : 1.0;

            // Burn halves physical damage. §307: the Guts exception that used
            // to live on this line is gone, and it has to be - AbilityFactor
            // measures the ability against a baseline that IS burned, so Guts
            // comes back as a factor of three (it undoes this halving and
            // adds half again). Keeping the exception here as well would undo
            // the halving twice.
            bool burnApplies = request.AttackerBurned && physical;

            int Chain(int roll, bool crit)
            {
                int attack = AttackFor(crit);
                int defense = DefenseFor(crit);

                int baseDamage = (int)Math.Floor(Math.Floor(
                    (Math.Floor(2.0 * request.Level / 5.0) + 2.0) * power * attack / defense) / 50.0) + 2;

                int damage = baseDamage;
                damage = PokeRound(damage * weather);

                // §261: terrain sits beside weather, the other field-wide
                // power modifier, and is applied the same way.
                damage = PokeRound(damage * terrain);

                if (crit)
                    damage = PokeRound(damage * 1.5);

                damage = (int)Math.Floor(damage * roll / 100.0);
                damage = PokeRound(damage * stab);
                damage = (int)Math.Floor(damage * typeEffectiveness);

                if (burnApplies)
                    damage = PokeRound(damage * 0.5);

                // "Other" modifiers, applied in a stable order. Screens don't
                // apply on a crit; Multiscale only at full HP.
                //
                // §261: three screens, each covering the category it really
                // covers, replacing §72's one flag that halved everything.
                // Aurora Veil covers both categories, so the three are one
                // test rather than three - stacking a veil with a screen
                // would halve twice, which no pair of screens does.
                if (!crit &&
                    (request.AuroraVeil ||
                     (request.Reflect && physical) ||
                     (request.LightScreen && special)))
                {
                    damage = PokeRound(damage * 0.5);
                }

                // §306: Charge doubles an Electric move, once, for the one
                // move that follows it.
                if (request.AttackerCharged && NormalizedType(moveType) == "Electric")
                    damage = PokeRound(damage * 2.0);

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
                WeatherNote = weatherNote.Trim(),
                TerrainNote = terrainNote.Trim()
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
