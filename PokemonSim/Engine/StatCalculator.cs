using PokemonSim.Models;

namespace PokemonSim.Engine
{
    public static class StatCalculator
    {
        public static int CalculateHP(
            int baseStat,
            int iv,
            int ev,
            int level)
        {
            return (int)Math.Floor(
                ((2 * baseStat + iv + (ev / 4.0)) * level) / 100
            ) + level + 10;
        }

        public static int CalculateStat(
            int baseStat,
            int iv,
            int ev,
            int level,
            double nature)
        {
            double value =
                ((2 * baseStat + iv + (ev / 4.0)) * level) / 100 + 5;

            value *= nature;

            return (int)Math.Floor(value);
        }
    }
}