using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    public class MarvelScaleAbility : IAbility
    {
        public string Id => "marvelscale";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new MarvelScaleEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        { }
    }
}