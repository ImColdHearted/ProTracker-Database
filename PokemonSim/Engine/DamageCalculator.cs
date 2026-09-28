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

            // §304: Darkest Lariat and Sacred Sword do the same thing the
            // ability does, for one move, without needing the ability.
            if (move.IgnoresDefensiveBoosts)
                ignoreDefenseStages = true;

            MoveCategory category = move.Category;

            // Section 158: Tera Blast-class - listed Special, but runs
            // physical when the user's Attack beats its Sp. Atk.
            if (move.UsesHigherOffense && category == MoveCategory.Special &&
                StatResolver.GetStat(state, attacker, "Attack") >
                StatResolver.GetStat(state, attacker, "SpAttack"))
            {
                category = MoveCategory.Physical;
            }

            // §304: Shell Side Arm's rule is stronger than that one. It
            // compares the two hits it could actually deal - the user's
            // attacking stat against the matching defence on the other side
            // - and takes the bigger. A Pokemon with the higher Sp. Atk
            // still throws it physically into something whose Defense is
            // soft enough, which is the whole point of the move.
            if (move.UsesBetterDamage)
            {
                double physical =
                    StatResolver.GetStat(state, attacker, "Attack", ignoreOffenseStages) /
                    System.Math.Max(1.0, StatResolver.GetStat(state, defender, "Defense", ignoreDefenseStages));

                double special =
                    StatResolver.GetStat(state, attacker, "SpAttack", ignoreOffenseStages) /
                    System.Math.Max(1.0, StatResolver.GetStat(state, defender, "SpDefense", ignoreDefenseStages));

                category = physical > special ? MoveCategory.Physical : MoveCategory.Special;
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

            // §304: Plasma Fists turns every Normal move on the field
            // Electric for the turn. Computed once here rather than written
            // onto the move, because MoveState instances are the Pokemon's
            // own and a move that came out Electric once must not stay that
            // way for the rest of the battle.
            PokemonType moveType = move.Type;

            if (state.IonDelugeTurns > 0 && moveType == PokemonType.Normal)
                moveType = PokemonType.Electric;

            double stab = 1.0;

            if (!move.Typeless && attacker.Types.Contains(moveType))
            {
                stab = 1.5;
            }

            double effectiveness = 1.0;

            if (!move.Typeless)
            {
                bool scrappy = Ability(attacker) == "scrappy" &&
                    (moveType == PokemonType.Normal || moveType == PokemonType.Fighting);

                foreach (var type in defender.Types)
                {
                    // Section 158: Scrappy lands Normal and Fighting hits
                    // on Ghosts.
                    if (scrappy && type == PokemonType.Ghost)
                        continue;

                    // §304: Roost - the target gave its Flying type up for
                    // the turn, so the chart must not consult it.
                    if (defender.RoostedThisTurn && type == PokemonType.Flying)
                        continue;

                    // §304: Thousand Arrows reaches a Flying target, and
                    // reaches it for neutral rather than for its usual
                    // nothing. Every other type it has still counts.
                    if (move.IgnoresFlyingImmunity && type == PokemonType.Flying)
                        continue;

                    effectiveness *= TypeChart.GetMultiplier(moveType, type);
                }

                // §309: an unpopped Air Balloon is a Ground immunity, and
                // until now it was not one. Grounding.IsGrounded has known
                // about the balloon since §159, but nothing consulted it
                // here - Ground immunity in this engine belongs to
                // LevitateEffect, which is keyed on the ABILITY - so the
                // balloon lifted its holder over Spikes and the terrain and
                // then took an Earthquake in full.
                //
                // Written as an effectiveness of zero rather than as a
                // cancelled move so that the resolver's "It doesn't affect"
                // line, the crash-damage rule and the calculator's immunity
                // sentence all fall out of the one answer, the way
                // Levitate's do.
                if (moveType == PokemonType.Ground &&
                    !move.IgnoresFlyingImmunity &&
                    Items.HeldItems.Normalize(defender.HeldItemId) == "airballoon")
                {
                    effectiveness = 0.0;
                }
            }

            if (effectiveness <= 0)
                return new DamageResult(0, 0.0, false);

            double modifier = stab * effectiveness;

            // §304: Charge spends itself on the user's next Electric move,
            // whichever move that turns out to be. Doubling it here rather
            // than in an effect is the only way a flag set by one move can
            // reach the damage of another.
            if (attacker.ChargeActive && moveType == PokemonType.Electric)
                modifier *= 2.0;

            // §304: Glaive Rush leaves its user standing - until it moves
            // again everything hits it twice as hard.
            if (defender.GlaiveRushActive)
                modifier *= 2.0;

            // §304: and a Minimized target is twice as easy to flatten.
            if (move.IsFlattening && defender.Minimized)
                modifier *= 2.0;

            var weather = state.Environment.Weather;

            if (weather == WeatherType.Rain)
            {
                if (moveType == PokemonType.Water)
                    modifier *= 1.5;

                if (moveType == PokemonType.Fire)
                    modifier *= 0.5;
            }

            if (weather == WeatherType.Sun)
            {
                if (moveType == PokemonType.Fire)
                    modifier *= 1.5;

                if (moveType == PokemonType.Water)
                    modifier *= 0.5;
            }

            // Terrain boosts the matching move type for GROUNDED attackers,
            // once (the old path multiplied the bonus in twice).
            if (Grounding.IsGrounded(state, attacker))
            {
                modifier *= TerrainEffects.GetDamageModifier(
                    state.Environment.Terrain,
                    moveType
                );
            }

            // Section 158: Misty Terrain shields grounded targets from
            // Dragon moves.
            if (state.Environment.Terrain == TerrainType.Misty &&
                moveType == PokemonType.Dragon &&
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