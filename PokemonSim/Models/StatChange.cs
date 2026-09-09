namespace PokemonSim.Models
{
    public class StatChange
    {
        public string Stat { get; set; } = "";
        public int Stages { get; set; }
        public string Target { get; set; } = "self";
    }
}