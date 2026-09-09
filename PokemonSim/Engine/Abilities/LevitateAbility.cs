using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    public class LevitateAbility : IAbility
    {
        public string Id => "levitate";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new LevitateEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        { }
    }
}