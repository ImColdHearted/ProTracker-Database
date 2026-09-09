using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// Section 158. The Protect family beyond plain Protect. Endure and
    /// King's Shield share Protect's diminishing-success streak
    /// (ConsecutiveProtects); the team guards do not - they simply raise a
    /// one-turn flag MoveResolver checks. All flags fall at end of turn
    /// with Protected.
    /// </summary>
    public class EndureEffect : BaseMoveEffect
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

            double successChance = 1.0 / (1 << System.Math.Min(attacker.ConsecutiveProtects, 8));

            attacker.ProtectedThisTurn = true;

            if (!state.Rng.Chance(successChance))
            {
                attacker.ConsecutiveProtects = 0;
                state.Log.Write("But it failed!");
                return;
            }

            attacker.Enduring = true;
            attacker.ConsecutiveProtects++;

            state.Log.Write($"{attacker.Species} braced itself!");
        }
    }

    /// <summary>King's Shield: protects like Protect, but status moves
    /// pass through and a blocked contact move costs the attacker two
    /// stages of Attack (see MoveResolver's protection block).</summary>
    public class KingsShieldEffect : BaseMoveEffect
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

            double successChance = 1.0 / (1 << System.Math.Min(attacker.ConsecutiveProtects, 8));

            attacker.ProtectedThisTurn = true;

            if (!state.Rng.Chance(successChance))
            {
                attacker.ConsecutiveProtects = 0;
                state.Log.Write("But it failed!");
                return;
            }

            attacker.Protected = true;
            attacker.KingsShieldUp = true;
            attacker.ConsecutiveProtects++;

            state.Log.Write($"{attacker.Species} protected itself!");
        }
    }

    /// <summary>Section 162: Baneful Bunker - Toxapex's signature.
    /// Protects like Protect (status moves included, unlike King's
    /// Shield), and an attacker that touches it is poisoned - the poke
    /// happens in MoveResolver's protection block, where the blocked
    /// contact is known.</summary>
    public class BanefulBunkerEffect : BaseMoveEffect
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

            double successChance = 1.0 / (1 << System.Math.Min(attacker.ConsecutiveProtects, 8));

            attacker.ProtectedThisTurn = true;

            if (!state.Rng.Chance(successChance))
            {
                attacker.ConsecutiveProtects = 0;
                state.Log.Write("But it failed!");
                return;
            }

            attacker.Protected = true;
            attacker.BanefulBunkerUp = true;
            attacker.ConsecutiveProtects++;

            state.Log.Write($"{attacker.Species} protected itself!");
        }
    }

    /// <summary>Quick Guard: this turn, priority attacks aimed at the user
    /// fail.</summary>
    public class QuickGuardEffect : BaseMoveEffect
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

            attacker.QuickGuardUp = true;
            state.Log.Write($"{attacker.Species} shielded its side from priority moves!");
        }
    }

    /// <summary>Wide Guard: this turn, spread moves aimed at the user
    /// fail.</summary>
    public class WideGuardEffect : BaseMoveEffect
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

            attacker.WideGuardUp = true;
            state.Log.Write($"{attacker.Species} shielded its side from wide attacks!");
        }
    }

    /// <summary>Feint: lifts the target's protection before the hit - the
    /// one move in the data that punches through Protect.</summary>
    public class FeintEffect : BaseMoveEffect
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
            if (defender.Protected || defender.QuickGuardUp || defender.WideGuardUp)
            {
                defender.Protected = false;
                defender.KingsShieldUp = false;
                defender.QuickGuardUp = false;
                defender.WideGuardUp = false;

                state.Log.Write($"{defender.Species} fell for the feint!");
            }
        }
    }
}