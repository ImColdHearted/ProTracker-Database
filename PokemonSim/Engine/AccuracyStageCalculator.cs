namespace PokemonSim.Engine
{
    public static class AccuracyStageCalculator
    {
        public static double GetMultiplier(int stage)
        {
            if (stage >= 0)
                return (3.0 + stage) / 3.0;

            return 3.0 / (3.0 - stage);
        }
    }
}