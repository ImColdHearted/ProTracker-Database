using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>Section 154: the failure paths say why (already has a
    /// substitute / not enough HP), and MoveResolver now routes incoming
    /// damage and statuses through the substitute this creates.</summary>
    public class SubstituteEffect : BaseMoveEffect
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

            if (attacker.SubstituteHP > 0)
            {
                state.Log.Write($"{attacker.Species} already has a substitute!");
                return;
            }

            int cost = attacker.MaxHP / 4;

            if (attacker.CurrentHP <= cost)
            {
                state.Log.Write($"{attacker.Species} is too weak to make a substitute!");
                return;
            }

            attacker.CurrentHP -= cost;
            attacker.SubstituteHP = cost;

            state.Log.Write($"{attacker.Species} made a substitute!");
        }
    }
}