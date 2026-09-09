namespace PokemonSim.Models
{
    public enum WeatherType
    {
        None,
        Rain,
        Sun,
        Sandstorm,
        Hail
    }

    public enum TerrainType
    {
        None,
        Electric,
        Grassy,
        Psychic,

        // Section 158: Misty Terrain - no damage boost; it blocks status
        // conditions on grounded Pokemon and halves Dragon damage into
        // grounded targets.
        Misty
    }

    public class BattleEnvironment
    {
        public WeatherType Weather = WeatherType.None;
        public int WeatherTurns;

        public TerrainType Terrain = TerrainType.None;
        public int TerrainTurns;
    }
}