using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine
{
    public interface IAbility
    {
        string Id { get; }

        IEnumerable<IMoveEffect> GetEffects();

        void OnAttach(PokemonState pokemon, BattleState state);
    }
}