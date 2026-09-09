using System.Text.Json.Serialization;

namespace Foot_Tracker.Models
{
    /// <summary>
    /// One row from SharedPokemonLibrary/Data/Moves/moves.json - the full
    /// current-generation move list (generations 1-9) built from
    /// pokemondb.net/move/all, see MIGRATION_GUIDE.md §41. Property names
    /// mirror that file's fields exactly.
    /// </summary>
    public class MoveData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("power")]
        public int? Power { get; set; }

        [JsonPropertyName("accuracy")]
        public int? Accuracy { get; set; }

        [JsonPropertyName("alwaysHits")]
        public bool AlwaysHits { get; set; }

        [JsonPropertyName("pp")]
        public int? Pp { get; set; }

        [JsonPropertyName("effect")]
        public string? Effect { get; set; }

        [JsonPropertyName("generation")]
        public int Generation { get; set; }
    }
}
