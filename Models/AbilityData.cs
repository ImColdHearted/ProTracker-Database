using System.Text.Json.Serialization;

namespace Foot_Tracker.Models
{
    /// <summary>
    /// One row from SharedPokemonLibrary/Data/Abilities/abilities.json - see
    /// MIGRATION_GUIDE.md §41. Property names mirror that file's fields exactly.
    /// </summary>
    public class AbilityData
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }
}
