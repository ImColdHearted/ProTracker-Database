using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>Section 154: yields ThickFatEffect - it yielded TorrentEffect, so Thick Fat boosted Water moves instead of halving Fire and Ice.</summary>
    public class ThickFatAbility : IAbility
    {
        public string Id => "thickfat";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new ThickFatEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        { }
    }
}