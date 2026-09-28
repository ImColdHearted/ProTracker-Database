using System;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    public class DrainEffect : BaseMoveEffect
    {
        readonly double fraction;

        public DrainEffect() : this(0.5) { }

        /// <summary>Section 184: Oblivion Wing drains three quarters
        /// rather than a half, so the share is a constructor argument the
        /// way HealEffect's already is.</summary>
        public DrainEffect(double fraction)
        {
            this.fraction = fraction;
        }

        public override MovePhase Phase => MovePhase.AfterDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0 || attacker.Fainted || attacker.CurrentHP >= attacker.MaxHP)
                return;

            // §304: a heal-blocked attacker still deals the damage; it
            // simply keeps none of it.
            if (attacker.HealBlockTurns > 0)
            {
                state.Log.Write($"{attacker.Species} cannot heal!");
                return;
            }

            int heal = Math.Max(1, (int)(damage * fraction));

            attacker.CurrentHP =
                Math.Min(attacker.MaxHP,
                         attacker.CurrentHP + heal);

            state.Log.Write($"{attacker.Species} drained health!");
        }
    }
}