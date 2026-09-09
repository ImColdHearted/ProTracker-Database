using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    public class GutsAbility : IAbility
    {
        public string Id => "guts";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new GutsEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        { }
    }
}