namespace PokemonSim.Models
{
    public class BattleEvent
    {
        public BattleEventType Type;
        public PokemonState? Source { get; set; }
        public PokemonState? Target { get; set; }
        public MoveState? Move { get; set; }

        public int Value; // damage, stat stage, etc.
    }
}