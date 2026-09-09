namespace PokemonSim.Engine
{
    public static class StatStageCalculator
    {
        public static double GetMultiplier(int stage)
        {
            if (stage >= 0)
                return (2.0 + stage) / 2.0;

            return 2.0 / (2.0 - stage);
        }
    }
}