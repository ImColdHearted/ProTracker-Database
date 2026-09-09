using PokemonSim.Models;

namespace PokemonSim.Engine
{
    public static class TerrainEffects
    {
        public static double GetDamageModifier(
            TerrainType terrain,
            PokemonType moveType)
        {
            if (terrain == TerrainType.Electric &&
                moveType == PokemonType.Electric)
                return 1.3;

            if (terrain == TerrainType.Grassy &&
                moveType == PokemonType.Grass)
                return 1.3;

            if (terrain == TerrainType.Psychic &&
                moveType == PokemonType.Psychic)
                return 1.3;

            return 1.0;
        }
    }
}