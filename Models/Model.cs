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

        /// <summary>§427. The form's own typing, as pokemon-species.json
        /// carries a species' - filled for the regional forms, which are
        /// hunt targets in their own right and whose types are NOT their
        /// species' (Hisuian Zorua is Normal/Ghost where Zorua is Dark).
        /// Empty for a form the file does not type yet, and GetTypes then
        /// answers as it always did: nothing, rather than a wrong guess.</summary>
        public List<string> Types { get; set; } = new();

        public List<string> OcrAliases { get; set; } = new();
    }
}