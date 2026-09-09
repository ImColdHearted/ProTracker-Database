using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    public class TorrentAbility : IAbility
    {
        public string Id => "torrent";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new TorrentEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        { }
    }
}