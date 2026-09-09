using System;
using System.Linq;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// Section 158. The damage-arithmetic batch: fixed-damage moves in the
    /// Section 155 LevelDamage tradition (the listed power is a
    /// placeholder; type immunity has already zeroed the hit before these
    /// run), and the power-scaling moves that bend the computed number by
    /// a ratio, keeping every calculator modifier that already applied.
    /// </summary>
    public class SuperFangEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0)
                return;

            damage = Math.Max(1, defender.CurrentHP / 2);
        }
    }

    /// <summary>Endeavor: cuts the target down to the user's own HP.</summary>
    public class EndeavorEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0)
                return;

            if (attacker.CurrentHP >= defender.CurrentHP)
            {
                state.Log.Write("But it failed!");
                cancelled = true;
                damage = 0;
                return;
            }

            damage = defender.CurrentHP - attacker.CurrentHP;
        }
    }

    /// <summary>Psywave: 50% to 150% of the user's level.</summary>
    public class PsywaveEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0)
                return;

            damage = Math.Max(1, attacker.Level * state.Rng.Next(50, 151) / 100);
        }
    }

    /// <summary>Sheer Cold / Fissure: a one-hit KO when the 30%-accuracy
    /// roll lands. Sturdy shrugs it off entirely.</summary>
    public class OhkoEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0)
                return;

            if (!state.IgnoreDefenderAbilities &&
                Abilities.AbilityFactory.Normalize(defender.AbilityId) == "sturdy")
            {
                damage = 0;
                state.Log.Write($"{defender.Species} was protected by Sturdy!");
                return;
            }

            damage = defender.CurrentHP;
            state.Log.Write("It's a one-hit KO!");
        }
    }

    /// <summary>Counter (physical) and Mirror Coat (special): twice the
    /// matching damage taken this turn, or nothing at all.</summary>
    public class CounterEffect : BaseMoveEffect
    {
        readonly bool physical;

        public CounterEffect(bool physical)
        {
            this.physical = physical;
        }

        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0)
                return;

            int taken = physical
                ? attacker.LastPhysicalDamageTaken
                : attacker.LastSpecialDamageTaken;

            if (taken <= 0)
            {
                state.Log.Write("But it failed!");
                cancelled = true;
                damage = 0;
                return;
            }

            damage = taken * 2;
        }
    }

    /// <summary>Metal Burst: one and a half times everything taken this
    /// turn, either category.</summary>
    public class MetalBurstEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0)
                return;

            int taken = attacker.LastPhysicalDamageTaken + attacker.LastSpecialDamageTaken;

            if (taken <= 0)
            {
                state.Log.Write("But it failed!");
                cancelled = true;
                damage = 0;
                return;
            }

            damage = taken * 3 / 2;
        }
    }

    /// <summary>Pain Split: both battlers' HP becomes their average.</summary>
    public class PainSplitEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (cancelled || defender.Fainted)
                return;

            int average = Math.Max(1, (attacker.CurrentHP + defender.CurrentHP) / 2);

            attacker.CurrentHP = Math.Min(attacker.MaxHP, average);
            defender.CurrentHP = Math.Min(defender.MaxHP, average);

            state.Log.Write("The battlers shared their pain!");
        }
    }

    /// <summary>False Swipe: always leaves at least 1 HP.</summary>
    public class FalseSwipeEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage >= defender.CurrentHP)
                damage = Math.Max(0, defender.CurrentHP - 1);
        }
    }

    /// <summary>Stored Power / Power Trip: 20 power plus 20 per positive
    /// stage - expressed as a ratio over the listed base 20.</summary>
    public class StoredPowerEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0)
                return;

            int stages =
                Math.Max(0, attacker.AttackStage) +
                Math.Max(0, attacker.DefenseStage) +
                Math.Max(0, attacker.SpAttackStage) +
                Math.Max(0, attacker.SpDefenseStage) +
                Math.Max(0, attacker.SpeedStage) +
                Math.Max(0, attacker.AccuracyStage) +
                Math.Max(0, attacker.EvasionStage);

            damage = damage * (20 + 20 * stages) / 20;
        }
    }

    /// <summary>Flail / Reversal: listed at 100 power, rescaled to the
    /// classic 20..200 band by remaining HP.</summary>
    public class FlailPowerEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0)
                return;

            double ratio = (double)attacker.CurrentHP / attacker.MaxHP;

            int band = ratio >= 0.6875 ? 20
                : ratio >= 0.3542 ? 40
                : ratio >= 0.2083 ? 80
                : ratio >= 0.1042 ? 100
                : ratio >= 0.0417 ? 150
                : 200;

            damage = Math.Max(1, damage * band / 100);
        }
    }

    /// <summary>Electro Ball: listed at 100 power, rescaled 40..150 by the
    /// speed ratio.</summary>
    public class ElectroBallPowerEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0)
                return;

            double mine = Math.Max(1.0, StatResolver.GetStat(state, attacker, "Speed"));
            double theirs = Math.Max(1.0, StatResolver.GetStat(state, defender, "Speed"));
            double ratio = mine / theirs;

            int band = ratio >= 4 ? 150
                : ratio >= 3 ? 120
                : ratio >= 2 ? 80
                : ratio >= 1 ? 60
                : 40;

            damage = Math.Max(1, damage * band / 100);
        }
    }

    /// <summary>Hard Press: listed at 100 power, scaled by the TARGET's
    /// remaining HP fraction.</summary>
    public class TargetHpPowerEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0)
                return;

            damage = Math.Max(1, (int)((long)damage * Math.Max(1, defender.CurrentHP) / defender.MaxHP));
        }
    }

    /// <summary>Water Spout / Eruption-class: scaled by the USER's
    /// remaining HP fraction.</summary>
    public class UserHpPowerEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0)
                return;

            damage = Math.Max(1, (int)((long)damage * Math.Max(1, attacker.CurrentHP) / attacker.MaxHP));
        }
    }

    /// <summary>Brine: doubled once the target is at half or less.</summary>
    public class BrineBoostEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage > 0 && defender.CurrentHP * 2 <= defender.MaxHP)
                damage *= 2;
        }
    }

    /// <summary>Expanding Force: half again as strong for a grounded user
    /// on Psychic Terrain (on top of the terrain's own boost).</summary>
    public class ExpandingForceEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage > 0 &&
                state.Environment.Terrain == TerrainType.Psychic &&
                Grounding.IsGrounded(state, attacker))
            {
                damage = damage * 3 / 2;
            }
        }
    }

    /// <summary>Weather Ball: type and power follow the current weather.
    /// The mutation self-corrects at every use, so the move instance never
    /// drifts.</summary>
    public class WeatherBallEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            var weather = state.Environment.Weather;

            move.Power = weather == WeatherType.None ? 50 : 100;

            move.Type = weather switch
            {
                WeatherType.Rain => PokemonType.Water,
                WeatherType.Sun => PokemonType.Fire,
                WeatherType.Sandstorm => PokemonType.Rock,
                WeatherType.Hail => PokemonType.Ice,
                _ => PokemonType.Normal
            };
        }
    }

    /// <summary>Meteor Beam's charging boost - listed BEFORE TwoTurn in
    /// the move's effect order so it runs before the charge cancels the
    /// first turn.</summary>
    public class MeteorChargeEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (!attacker.Charging)
                MoveResolver.ApplyStatChange(state, attacker, "SpAttack", 1);
        }
    }

    /// <summary>Dream Eater's gate: only a sleeping target can be fed on
    /// (the Drain effect rides behind this in the effect list).</summary>
    public class DreamEaterGateEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (defender.Status != StatusCondition.Sleep)
            {
                state.Log.Write("But it failed!");
                cancelled = true;
            }
        }
    }

    /// <summary>Steel Beam's cost: half the user's max HP, hit or miss.</summary>
    public class HalfHpCostEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (attacker.Fainted)
                return;

            attacker.CurrentHP = Math.Max(0, attacker.CurrentHP - Math.Max(1, attacker.MaxHP / 2));
            state.Log.Write($"{attacker.Species} is damaged by the blast!");
        }
    }

    /// <summary>Last Resort: legal only after every other move has been
    /// used at least once - approximated as "has spent PP".</summary>
    public class LastResortEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (attacker.Moves.Any(m => m != move && m.CurrentPP == m.MaxPP))
            {
                state.Log.Write("But it failed!");
                cancelled = true;
            }
        }
    }
}