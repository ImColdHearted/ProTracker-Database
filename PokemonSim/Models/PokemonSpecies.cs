using System.Collections.Generic;

namespace PokemonSim.Models
{
    public class PokemonSpecies
    {
        public required string Name;

        public required List<PokemonType> Types;

        public required Stats BaseStats;
        public List<string> Abilities { get; set; } = new();

        public required List<MoveState> Learnset;

        public List<Nature> PreferredNatures = new();
    }
}