using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>Section 154: yields OvergrowEffect - it yielded TorrentEffect, so Overgrow boosted Water moves.</summary>
    public class OvergrowAbility : IAbility
    {
        public string Id => "overgrow";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new OvergrowEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        { }
    }
}