using System.Collections.Generic;
using System.Linq;

namespace PokemonSim.Models
{
    public class PlayerState
    {
        public required string Name;

        public required List<PokemonState> Team;

        public required PokemonState ActivePokemon;

        // Legacy per-player hazard fields from an earlier layout. The live
        // hazard state is BattleState's SpikesP1/P2, ToxicSpikesP1/P2 and
        // StealthRockP1/P2 (which every hazard effect and SwitchResolver
        // read); these three stay only so nothing that stored them breaks,
        // and section 154 documents them as unused.
        public bool StealthRock;
        public int SpikesLayers;
        public int ToxicSpikesLayers;

        // Section 161: one mega evolution and one Z-Move per side per
        // battle - the games' rules, latched where both a human player and
        // a computer strategy can read them.
        public bool UsedMegaEvolution;
        public bool UsedZMove;

        public bool HasLost()
        {
            return Team.All(p => p.Fainted);
        }

        public PokemonState? GetNextAvailablePokemon()
        {
            return Team.FirstOrDefault(p => !p.Fainted && p != ActivePokemon);
        }

        public PlayerState Clone()
        {
            var clonedTeam = Team.Select(p => p.Clone()).ToList();

            return new PlayerState
            {
                Name = Name,
                Team = clonedTeam,
                ActivePokemon = clonedTeam[Team.IndexOf(ActivePokemon)],
                StealthRock = StealthRock,
                SpikesLayers = SpikesLayers,
                ToxicSpikesLayers = ToxicSpikesLayers,
                UsedMegaEvolution = UsedMegaEvolution,
                UsedZMove = UsedZMove,
            };
        }
    }
}