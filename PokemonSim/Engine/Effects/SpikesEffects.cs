using System;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    public class SpikesEffect : BaseMoveEffect
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
            bool isP1 = state.GetOwner(attacker) == state.Player1;

            int layers = isP1 ? state.SpikesP2 : state.SpikesP1;

            if (layers >= 3)
            {
                state.Log.Write("But it failed!");
                return;
            }

            if (isP1)
                state.SpikesP2 = Math.Min(3, state.SpikesP2 + 1);
            else
                state.SpikesP1 = Math.Min(3, state.SpikesP1 + 1);

            state.Log.Write("Spikes were scattered!");
        }
    }
}