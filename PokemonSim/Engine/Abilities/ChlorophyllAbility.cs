using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>Section 154: actually yields its speed effect (it yielded nothing), and that effect is a CalculateStat doubling in sun rather than a permanent Stats.Speed overwrite.</summary>
    public class ChlorophyllAbility : IAbility
    {
        public string Id => "chlorophyll";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new ChlorophyllEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        { }
    }
}