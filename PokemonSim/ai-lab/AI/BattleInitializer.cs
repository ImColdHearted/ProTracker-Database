using PokemonSim.Models;

namespace PokemonSim.Engine
{
    public static class BattleInitializer
    {
        public static void Initialize(BattleState state)
        {
            foreach (var p in state.Player1.Team)
                p.Ability?.OnAttach(p, state);

            foreach (var p in state.Player2.Team)
                p.Ability?.OnAttach(p, state);
        }
    }
}