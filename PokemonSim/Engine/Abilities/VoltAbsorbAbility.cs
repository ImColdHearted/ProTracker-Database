using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    public class VoltAbsorbAbility : IAbility
    {
        public string Id => "voltabsorb";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new VoltAbsorbEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        { }
    }
}