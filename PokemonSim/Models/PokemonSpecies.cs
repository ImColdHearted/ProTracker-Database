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

        /// <summary>§303: kilograms. Low Kick, Grass Knot, Heavy Slam and
        /// Heat Crash are all weight; zero means the dex row had none.</summary>
        public double WeightKg;

        public List<Nature> PreferredNatures = new();
    }
}