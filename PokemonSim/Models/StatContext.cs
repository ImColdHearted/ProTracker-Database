namespace PokemonSim.Models
{
    // Section 154: moved into PokemonSim.Models (was global). Content as
    // before: the packet a CalculateStat-phase effect (Huge Power, Guts,
    // Swift Swim, Chlorophyll, Marvel Scale) modifies.
    public class StatContext
    {
        public BattleState State { get; set; } = null!;
        public PokemonState Pokemon { get; set; } = null!;
        public string Stat { get; set; } = "";

        public double Value;
    }
}