using System.Collections.Generic;

namespace PokemonSim.Models
{
    public class PokemonSpeciesJson
    {
        public required List<string> types { get; set; }
        public required StatsJson stats { get; set; }
        public required List<string> moves { get; set; }
        public List<string>? preferredNatures { get; set; }

        // Section 154: the data file always carried abilities for some
        // species; the loader just never read them.
        public List<string>? abilities { get; set; }
    }

    public class StatsJson
    {
        public int hp { get; set; }
        public int attack { get; set; }
        public int defense { get; set; }
        public int spAttack { get; set; }
        public int spDefense { get; set; }
        public int speed { get; set; }
    }
}