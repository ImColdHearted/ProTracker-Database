using System;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    public class HealEffect : BaseMoveEffect
    {
        readonly double fraction;

        public HealEffect() : this(0.5) { }

        /// <summary>Section 158: Life Dew-class quarter heals share this
        /// class with the classic half heals.</summary>
        public HealEffect(double fraction)
        {
            this.fraction = fraction;
        }

        public override MovePhase Phase => MovePhase.BeforeMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (attacker.CurrentHP >= attacker.MaxHP)
            {
                state.Log.Write($"{attacker.Species}'s HP is already full!");
                cancelled = true;
                return;
            }

            int heal = Math.Max(1, (int)(attacker.MaxHP * fraction));

            attacker.CurrentHP =
                Math.Min(attacker.MaxHP,
                         attacker.CurrentHP + heal);

            state.Log.Write($"{attacker.Species} restored HP!");
        }
    }
}
