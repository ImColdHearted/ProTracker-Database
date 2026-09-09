using System;
using System.Linq;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// Section 158. Rest, the team cures, the stat copies, and the
    /// identity-bending status moves (Skill Swap, Worry Seed, Soak).
    /// </summary>
    public class RestEffect : BaseMoveEffect
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
            cancelled = true;

            string ability = Abilities.AbilityFactory.Normalize(attacker.AbilityId);

            if (attacker.CurrentHP >= attacker.MaxHP ||
                attacker.Status == StatusCondition.Sleep ||
                ability == "insomnia" || ability == "vitalspirit" || ability == "purifyingsalt")
            {
                state.Log.Write("But it failed!");
                return;
            }

            attacker.CurrentHP = attacker.MaxHP;
            attacker.Status = StatusCondition.Sleep;
            attacker.SleepTurns = 2;
            attacker.ToxicCounter = 0;

            state.Log.Write($"{attacker.Species} slept and became healthy!");
        }
    }

    /// <summary>Heal Bell / Aromatherapy: cures the whole team's status
    /// conditions.</summary>
    public class HealBellEffect : BaseMoveEffect
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
            cancelled = true;

            bool cured = false;

            foreach (var pokemon in state.GetOwner(attacker).Team)
            {
                if (!pokemon.Fainted && pokemon.Status != StatusCondition.None)
                {
                    pokemon.Status = StatusCondition.None;
                    pokemon.SleepTurns = 0;
                    pokemon.ToxicCounter = 0;
                    cured = true;
                }
            }

            state.Log.Write(cured
                ? "A bell chimed! The team's status conditions were healed!"
                : "But it failed!");
        }
    }

    /// <summary>Sleep Talk while AWAKE - the sleeping path lives in
    /// MoveResolver, which never lets this effect run from sleep.</summary>
    public class SleepTalkEffect : BaseMoveEffect
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
            state.Log.Write("But it failed!");
            cancelled = true;
        }
    }

    /// <summary>Snore's gate: awake means nothing happens. The sleeping
    /// path is let through by MoveResolver's sleep branch.</summary>
    public class SnoreGateEffect : BaseMoveEffect
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
            if (attacker.Status != StatusCondition.Sleep)
            {
                state.Log.Write("But it failed!");
                cancelled = true;
            }
        }
    }

    /// <summary>Belly Drum: half the user's max HP for a maximized Attack.</summary>
    public class BellyDrumEffect : BaseMoveEffect
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
            cancelled = true;

            if (attacker.CurrentHP <= attacker.MaxHP / 2 || attacker.AttackStage >= 6)
            {
                state.Log.Write("But it failed!");
                return;
            }

            attacker.CurrentHP -= attacker.MaxHP / 2;
            attacker.AttackStage = 6;

            state.Log.Write($"{attacker.Species} cut its own HP and maximized its Attack!");
        }
    }

    /// <summary>Psych Up: copies the target's stat stages.</summary>
    public class PsychUpEffect : BaseMoveEffect
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

            attacker.AttackStage = defender.AttackStage;
            attacker.DefenseStage = defender.DefenseStage;
            attacker.SpAttackStage = defender.SpAttackStage;
            attacker.SpDefenseStage = defender.SpDefenseStage;
            attacker.SpeedStage = defender.SpeedStage;
            attacker.AccuracyStage = defender.AccuracyStage;
            attacker.EvasionStage = defender.EvasionStage;

            state.Log.Write($"{attacker.Species} copied {defender.Species}'s stat changes!");
        }
    }

    /// <summary>Haze: every stage on both actives resets to zero.</summary>
    public class HazeEffect : BaseMoveEffect
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
            cancelled = true;

            foreach (var pokemon in new[] { attacker, defender })
            {
                pokemon.AttackStage = 0;
                pokemon.DefenseStage = 0;
                pokemon.SpAttackStage = 0;
                pokemon.SpDefenseStage = 0;
                pokemon.SpeedStage = 0;
                pokemon.AccuracyStage = 0;
                pokemon.EvasionStage = 0;
            }

            state.Log.Write("All stat changes were erased!");
        }
    }

    /// <summary>Skill Swap: the two actives trade abilities; the wiring is
    /// rebuilt through AbilityFactory.Restore without re-running switch-in
    /// side effects.</summary>
    public class SkillSwapEffect : BaseMoveEffect
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

            if (string.IsNullOrWhiteSpace(attacker.AbilityId) ||
                string.IsNullOrWhiteSpace(defender.AbilityId))
            {
                state.Log.Write("But it failed!");
                return;
            }

            (attacker.AbilityId, defender.AbilityId) = (defender.AbilityId, attacker.AbilityId);

            attacker.HasMagicGuard = false;
            defender.HasMagicGuard = false;

            Abilities.AbilityFactory.Restore(attacker, state);
            Abilities.AbilityFactory.Restore(defender, state);

            state.Log.Write($"{attacker.Species} swapped abilities with {defender.Species}!");
        }
    }

    /// <summary>Worry Seed: the target's ability becomes Insomnia.</summary>
    public class WorrySeedEffect : BaseMoveEffect
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

            defender.AbilityId = "insomnia";
            defender.HasMagicGuard = false;
            Abilities.AbilityFactory.Restore(defender, state);

            state.Log.Write($"{defender.Species} became worried and can't fall asleep!");
        }
    }

    /// <summary>Soak: the target becomes pure Water.</summary>
    public class SoakWaterEffect : BaseMoveEffect
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

            defender.Types = new System.Collections.Generic.List<PokemonType> { PokemonType.Water };
            state.Log.Write($"{defender.Species} became a Water type!");
        }
    }

    /// <summary>Magic Powder: the target becomes pure Psychic.</summary>
    public class MagicPowderEffect : BaseMoveEffect
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

            defender.Types = new System.Collections.Generic.List<PokemonType> { PokemonType.Psychic };
            state.Log.Write($"{defender.Species} became a Psychic type!");
        }
    }

    /// <summary>Healing Wish: the user faints; whoever comes in next is
    /// fully restored (see SwitchResolver).</summary>
    public class HealingWishEffect : BaseMoveEffect
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
            cancelled = true;

            var player = state.GetOwner(attacker);

            if (player.Team.Count(p => !p.Fainted) <= 1)
            {
                state.Log.Write("But it failed!");
                return;
            }

            attacker.CurrentHP = 0;
            state.HealingWish(player) = true;

            state.Log.Write($"{attacker.Species} made a healing wish!");
            state.Log.Write($"{attacker.Species} fainted!");
        }
    }
}