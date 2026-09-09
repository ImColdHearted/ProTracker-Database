using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    // Section 154: moved into PokemonSim.Engine.Effects (was global).
    public static class HazardResolver
    {
        public static void ClearSide(BattleState state, PlayerState player)
        {
            if (player == state.Player1)
            {
                state.SpikesP1 = 0;
                state.ToxicSpikesP1 = 0;
                state.StealthRockP1 = false;
                state.StickyWebP1 = false;
            }
            else
            {
                state.SpikesP2 = 0;
                state.ToxicSpikesP2 = 0;
                state.StealthRockP2 = false;
                state.StickyWebP2 = false;
            }
        }
    }
}