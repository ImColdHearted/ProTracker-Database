using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    public class TechnicianAbility : IAbility
    {
        public string Id => "technician";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new TechnicianEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        { }
    }
}