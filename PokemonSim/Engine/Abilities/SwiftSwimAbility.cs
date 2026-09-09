using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    public class SwiftSwimAbility : IAbility
    {
        public string Id => "swiftswim";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new SwiftSwimEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        { }
    }
}