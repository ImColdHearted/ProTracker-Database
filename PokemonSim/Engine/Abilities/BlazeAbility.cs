using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>Section 154: yields BlazeEffect - it yielded TorrentEffect, so Blaze boosted Water moves.</summary>
    public class BlazeAbility : IAbility
    {
        public string Id => "blaze";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new BlazeEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        { }
    }
}