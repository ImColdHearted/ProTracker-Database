using PokemonSim.Models;

namespace PokemonSim.Engine
{
    /// <summary>One damaging hit's base numbers. Section 154: rolls come
    /// from the battle's seeded rng (the ThreadLocal Random made every test
    /// unrepeatable), stats go through the stage-aware StatResolver, the
    /// terrain bonus is applied exactly once (it was applied twice - once
    /// flat, once grounded), Struggle's typeless hit skips chart and STAB,
    /// a connecting hit deals at least 1, and the caller gets the
    /// effectiveness and crit back for logging instead of damage alone.</summary>
    public readonly struct DamageResult
    {
        public readonly int Damage;
        public readonly double Effectiveness;
        public readonly bool CriticalHit;

        public DamageResult(int damage, double effectiveness, bool criticalHit)
        {
            Damage = damage;
            Effectiveness = effectiveness;
            CriticalHit = criticalHit;
        }
    }

    public static class DamageCalculator
    {
        static string Ability(PokemonState pokemon) =>
            Abilities.AbilityFactory.Normalize(pokemon.AbilityId);

        public static DamageResult CalculateDamage(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move)
        {
            double attack;
            double defense;

            // Section 158: Unaware - a defender with it ignores the
            // attacker's offensive stages; an attacker with it ignores the
            // defender's defensive stages.
            bool ignoreOffenseStages =
                !state.IgnoreDefenderAbilities && Ability(defender) == "unaware";
            bool ignoreDefenseStages = Ability(attacker) == "unaware";

            MoveCategory category = move.Category;

            // Section 158: Tera Blast-class - listed Special, but runs
            // physical when the user's Attack beats its Sp. Atk.
            if (move.UsesHigherOffense && category == MoveCategory.Special &&
                StatResolver.GetStat(state, attacker, "Attack") >
                StatResolver.GetStat(state, attacker, "SpAttack"))
            {
                category = MoveCategory.Physical;
            }

            if (category == MoveCategory.Physical)
            {
                // Section 158: Foul Play swings with the TARGET's Attack;
                // Body Press with the user's own Defense.
                attack = move.UsesTargetAttack
                    ? StatResolver.GetStat(state, defender, "Attack", ignoreOffenseStages)
                    : move.UsesDefenseAsOffense
                        ? StatResolver.GetStat(state, attacker, "Defense", ignoreOffenseStages)
                        : StatResolver.GetStat(state, attacker, "Attack", ignoreOffenseStages);

                defense = StatResolver.GetStat(state, defender, "Defense", ignoreDefenseStages);

                // Burn penalty (still applies AFTER ability modifiers).
                // Section 158: Guts shrugs the penalty off.
                if (attacker.Status == StatusCondition.Burn && Ability(attacker) != "guts")
                    attack *= 0.5;
            }
            else if (category == MoveCategory.Special)
            {
                attack = StatResolver.GetStat(state, attacker, "SpAttack", ignoreOffenseStages);
                // Section 155: Psyshock-class Special moves hit the
                // target's physical Defense instead.
                defense = StatResolver.GetStat(state, defender,
                    move.UsesTargetDefense ? "Defense" : "SpDefense", ignoreDefenseStages);
            }
            else
            {
                return new DamageResult(0, 1.0, false);
            }

            double stab = 1.0;

            if (!move.Typeless && attacker.Types.Contains(move.Type))
            {
                stab = 1.5;
            }

            double effectiveness = 1.0;

            if (!move.Typeless)
            {
                bool scrappy = Ability(attacker) == "scrappy" &&
                    (move.Type == PokemonType.Normal || move.Type == PokemonType.Fighting);

                foreach (var type in defender.Types)
                {
                    // Section 158: Scrappy lands Normal and Fighting hits
                    // on Ghosts.
                    if (scrappy && type == PokemonType.Ghost)
                        continue;

                    effectiveness *= TypeChart.GetMultiplier(move.Type, type);
                }
            }

            if (effectiveness <= 0)
                return new DamageResult(0, 0.0, false);

            double modifier = stab * effectiveness;

            var weather = state.Environment.Weather;

            if (weather == WeatherType.Rain)
            {
                if (move.Type == PokemonType.Water)
                    modifier *= 1.5;

                if (move.Type == PokemonType.Fire)
                    modifier *= 0.5;
            }

            if (weather == WeatherType.Sun)
            {
                if (move.Type == PokemonType.Fire)
                    modifier *= 1.5;

                if (move.Type == PokemonType.Water)
                    modifier *= 0.5;
            }

            // Terrain boosts the matching move type for GROUNDED attackers,
            // once (the old path multiplied the bonus in twice).
            if (Grounding.IsGrounded(state, attacker))
            {
                modifier *= TerrainEffects.GetDamageModifier(
                    state.Environment.Terrain,
                    move.Type
                );
            }

            // Section 158: Misty Terrain shields grounded targets from
            // Dragon moves.
            if (state.Environment.Terrain == TerrainType.Misty &&
                move.Type == PokemonType.Dragon &&
                Grounding.IsGrounded(state, defender))
            {
                modifier *= 0.5;
            }

            // Random variance, 85%..100%.
            double randomFactor = state.Rng.Next(85, 101) / 100.0;
            modifier *= randomFactor;

            double damage =
                (((2 * attacker.Level / 5 + 2)
                * move.Power
                * attack / defense) / 50) + 2;

            int[] critTable = { 24, 8, 2, 1 };

            // Section 158: Super Luck sharpens the odds a stage; §159: so
            // does a held Scope Lens.
            int critStage = move.CritStage + (Ability(attacker) == "superluck" ? 1 : 0)
                + Items.HeldItems.CritStageBonus(attacker);

            int stage = System.Math.Clamp(critStage, 0, 3);

            bool crit = state.Rng.Next(critTable[stage]) == 0;

            // Section 158: Shell Armor and Battle Armor never take crits.
            if (crit && !state.IgnoreDefenderAbilities &&
                (Ability(defender) == "shellarmor" || Ability(defender) == "battlearmor"))
            {
                crit = false;
            }

            if (crit)
            {
                damage = (int)(damage * 1.5);
            }

            damage = (int)(damage * modifier);

            // A hit that connects does at least 1.
            int final = (int)damage;

            if (final < 1 && move.Power > 0)
                final = 1;

            return new DamageResult(final, effectiveness, crit);
        }
    }
}