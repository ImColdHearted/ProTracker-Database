using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    public class StealthRockEffect : BaseMoveEffect
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

            bool already = isP1 ? state.StealthRockP2 : state.StealthRockP1;

            if (already)
            {
                state.Log.Write("But it failed!");
                return;
            }

            if (isP1)
                state.StealthRockP2 = true;
            else
                state.StealthRockP1 = true;

            state.Log.Write("Pointed stones float in the air!");
        }
    }
}