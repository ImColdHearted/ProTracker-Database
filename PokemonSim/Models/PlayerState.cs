using System.Collections.Generic;
using System.Linq;

namespace PokemonSim.Models
{
    public class PlayerState
    {
        public required string Name;

        public required List<PokemonState> Team;

        /// <summary>
        /// §305. The Pokemon this side has on the field, one per slot. One
        /// entry in singles; two when a doubles format arrives.
        ///
        /// This is the real state. ActivePokemon below is slot zero under
        /// its old name, kept because seventy-odd places across the engine,
        /// the strategies, the observer and the tracker's own Simulator
        /// window read it - and every one of them means slot zero, because
        /// slot zero is the only slot there has ever been. Converting them
        /// all at once would be a hundred-and-forty-call-site change with no
        /// way to tell a typo from a design decision; leaving the name in
        /// place means the shape changes here and the behaviour does not
        /// change anywhere.
        /// </summary>
        public List<PokemonState> Active { get; set; } = new();

        /// <summary>Slot zero. See Active - this is the old name for it, and
        /// in singles it is the whole field.</summary>
        public required PokemonState ActivePokemon
        {
            get => Active[0];
            set
            {
                if (Active.Count == 0)
                    Active.Add(value);
                else
                    Active[0] = value;
            }
        }

        /// <summary>Which slot a Pokemon of this side is standing in, or -1
        /// if it is on the bench. The question a forced switch has to answer
        /// before it can put a replacement anywhere.</summary>
        public int SlotOf(PokemonState pokemon)
        {
            for (int i = 0; i < Active.Count; i++)
            {
                if (ReferenceEquals(Active[i], pokemon))
                    return i;
            }

            return -1;
        }

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

        /// <summary>Every slot that still has something standing in it.</summary>
        public IEnumerable<PokemonState> Standing()
        {
            foreach (PokemonState pokemon in Active)
            {
                if (!pokemon.Fainted)
                    yield return pokemon;
            }
        }

        public PokemonState? GetNextAvailablePokemon()
        {
            return Team.FirstOrDefault(p => !p.Fainted && !Active.Contains(p));
        }

        public PlayerState Clone()
        {
            var clonedTeam = Team.Select(p => p.Clone()).ToList();

            // §305: every slot, not just the first - and each mapped onto
            // the clone's OWN team member, the way the single active always
            // was.
            var clonedActive = Active
                .Select(p => clonedTeam[Team.IndexOf(p)])
                .ToList();

            return new PlayerState
            {
                Name = Name,
                Team = clonedTeam,
                Active = clonedActive,
                ActivePokemon = clonedActive[0],
                StealthRock = StealthRock,
                SpikesLayers = SpikesLayers,
                ToxicSpikesLayers = ToxicSpikesLayers,
                UsedMegaEvolution = UsedMegaEvolution,
                UsedZMove = UsedZMove,
            };
        }
    }
}