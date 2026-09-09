using System;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    public class ToxicSpikesEffect : BaseMoveEffect
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

            int layers = isP1 ? state.ToxicSpikesP2 : state.ToxicSpikesP1;

            if (layers >= 2)
            {
                state.Log.Write("But it failed!");
                return;
            }

            if (isP1)
                state.ToxicSpikesP2 = Math.Min(2, state.ToxicSpikesP2 + 1);
            else
                state.ToxicSpikesP1 = Math.Min(2, state.ToxicSpikesP1 + 1);

            state.Log.Write("Toxic Spikes were scattered!");
        }
    }
}