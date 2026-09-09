using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    public class FlashFireAbility : IAbility
    {
        public string Id => "flashfire";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new FlashFireEffect();
            yield return new FlashFireBoostEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        {
            if (!pokemon.AbilityState.ContainsKey("flashfire"))
                pokemon.AbilityState["flashfire"] = false;
        }
    }
}