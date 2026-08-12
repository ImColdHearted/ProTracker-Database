namespace Foot_Tracker.Models
{
    public class PokemonFormEntry
    {
        public int PokemonId { get; set; }

        public int DexNumber { get; set; }

        public string Name { get; set; } = string.Empty;

        public string SpeciesName { get; set; } = string.Empty;

        public string Identifier { get; set; } = string.Empty;

        public string FormIdentifier { get; set; } = string.Empty;

        public bool IsDefaultForm { get; set; }

        public string Sprite { get; set; } = string.Empty;

        public List<string> OcrAliases { get; set; } = new();
    }
}