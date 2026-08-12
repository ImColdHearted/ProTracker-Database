using System.Text.Json.Serialization;

namespace Foot_Tracker.Models
{
    public class SpriteMapEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("dexNumber")]
        public int DexNumber { get; set; }

        [JsonPropertyName("sprite")]
        public string Sprite { get; set; } = string.Empty;
    }
}