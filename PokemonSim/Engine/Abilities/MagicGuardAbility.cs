using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    public class MagicGuardAbility : IAbility
    {
        public string Id => "magicguard";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield break;
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        {
            pokemon.HasMagicGuard = true;
        }
    }
}