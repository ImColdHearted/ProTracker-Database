using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    public class HugePowerAbility : IAbility
    {
        public string Id => "hugepower";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new HugePowerEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        { }
    }
}