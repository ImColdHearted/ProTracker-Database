using System;
using System.Linq;
using PokemonSim.Engine.Items;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// Section 159. The moves whose whole premise is a held item - real at
    /// last. Trick swaps, Fling throws, Poltergeist needs a target with
    /// pockets, Knock Off and Thief take. Each replaces a §158 FailNoItems
    /// stub or upgrades a plain-damage entry.
    /// </summary>
    public class TrickItemEffect : BaseMoveEffect
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

            if (attacker.HeldItemId == null && defender.HeldItemId == null)
            {
                state.Log.Write("But it failed!");
                return;
            }

            // Section 161: Z-Crystals and owned mega stones stay put.
            if (HeldItems.IsSticky(attacker) || HeldItems.IsSticky(defender))
            {
                state.Log.Write("But it failed!");
                return;
            }

            (attacker.HeldItemId, defender.HeldItemId) = (defender.HeldItemId, attacker.HeldItemId);

            // A traded Choice lock makes no sense for either side.
            attacker.ChoiceLockedMoveName = null;
            defender.ChoiceLockedMoveName = null;

            state.Log.Write($"{attacker.Species} switched items with its target!");

            if (attacker.HeldItemId != null)
                state.Log.Write($"{attacker.Species} obtained one {HeldItems.DisplayName(attacker.HeldItemId)}.");

            if (defender.HeldItemId != null)
                state.Log.Write($"{defender.Species} obtained one {HeldItems.DisplayName(defender.HeldItemId)}.");
        }
    }

    /// <summary>Fling: fails empty-handed; a thrown Flame or Toxic Orb
    /// passes its status along; the item is gone either way.</summary>
    public class FlingItemEffect : BaseMoveEffect
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
            // Section 161: a Z-Crystal or an owned mega stone cannot be
            // flung any more than it can be knocked off.
            if (attacker.HeldItemId == null || HeldItems.IsSticky(attacker))
            {
                state.Log.Write("But it failed!");
                cancelled = true;
            }
        }
    }

    public class FlingThrowEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (cancelled || attacker.HeldItemId == null)
                return;

            string flung = HeldItems.Normalize(attacker.HeldItemId);

            state.Log.Write($"{attacker.Species} flung its {HeldItems.DisplayName(attacker.HeldItemId)}!");

            attacker.HeldItemId = null;
            attacker.LostItem = true;
            attacker.ChoiceLockedMoveName = null;

            if (damage > 0 && !defender.Fainted)
            {
                if (flung == "flameorb")
                    MoveResolver.TryInflictStatus(state, defender, StatusCondition.Burn,
                        announceFailure: false, substituteBlocks: false, source: attacker);
                else if (flung == "toxicorb")
                    MoveResolver.TryInflictStatus(state, defender, StatusCondition.Toxic,
                        announceFailure: false, substituteBlocks: false, source: attacker);
            }
        }
    }

    /// <summary>Poltergeist: only works against a target holding
    /// something.</summary>
    public class PoltergeistGateEffect : BaseMoveEffect
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
            if (defender.HeldItemId == null)
            {
                state.Log.Write("But it failed!");
                cancelled = true;
                return;
            }

            state.Log.Write($"{defender.Species} is about to be attacked by its {HeldItems.DisplayName(defender.HeldItemId)}!");
        }
    }

    /// <summary>Knock Off: half again as strong against a held item, and
    /// the item is batted away for the rest of the battle.</summary>
    public class KnockOffEffect : BaseMoveEffect
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
            if (damage > 0 && defender.HeldItemId != null &&
                !HeldItems.IsSticky(defender))
            {
                damage = damage * 3 / 2;
            }
        }
    }

    public class KnockOffRemoveEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (cancelled || damage <= 0 || defender.HeldItemId == null)
                return;

            // Section 161: the sticky items hang on.
            if (HeldItems.IsSticky(defender))
            {
                state.Log.Write($"{defender.Species} held on to its {HeldItems.DisplayName(defender.HeldItemId)}!");
                return;
            }

            string knocked = HeldItems.DisplayName(defender.HeldItemId);

            HeldItems.Remove(state, defender);
            state.Log.Write($"{attacker.Species} knocked off {defender.Species}'s {knocked}!");
        }
    }

    /// <summary>Thief / Covet: an empty-handed attacker takes what the
    /// target holds.</summary>
    public class StealItemEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (cancelled || damage <= 0 || attacker.Fainted ||
                attacker.HeldItemId != null || defender.HeldItemId == null)
            {
                return;
            }

            // Section 161: sticky items cannot be stolen either.
            if (HeldItems.IsSticky(defender))
                return;

            attacker.HeldItemId = defender.HeldItemId;

            HeldItems.Remove(state, defender);
            state.Log.Write($"{attacker.Species} stole {defender.Species}'s {HeldItems.DisplayName(attacker.HeldItemId)}!");
        }
    }
}