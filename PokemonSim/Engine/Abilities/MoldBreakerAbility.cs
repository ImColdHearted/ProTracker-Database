using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    public class MoldBreakerAbility : IAbility
    {
        public string Id => "moldbreaker";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new MoldBreakerEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        { }
    }
}