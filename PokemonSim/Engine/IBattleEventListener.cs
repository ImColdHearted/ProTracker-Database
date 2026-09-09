using PokemonSim.Models;

namespace PokemonSim.Engine
{
    public interface IBattleEventListener
    {
        void OnEvent(BattleEvent battleEvent, BattleState state);
    }
}