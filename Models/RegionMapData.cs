using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Foot_Tracker.Models
{
    public class RegionMapData
    {
        [JsonPropertyName("regionId")]
        public string RegionId { get; set; } = string.Empty;

        [JsonPropertyName("regionName")]
        public string RegionName { get; set; } = string.Empty;

        [JsonPropertyName("locations")]
        public List<MapLocationReference> Locations { get; set; } = new();
    }

    public class MapLocationReference
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("dataFile")]
        public string DataFile { get; set; } = string.Empty;
    }

    public class MapLocationData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("pokemon")]
        public List<MapPokemonData> Pokemon { get; set; } = new();

        [JsonPropertyName("notables")]
        public List<MapNotableData> Notables { get; set; } = new();
    }

    public class MapPokemonData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("dexNumber")]
        public int DexNumber { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; } = "Land";

        [JsonPropertyName("membershipRequired")]
        public bool MembershipRequired { get; set; }
    }

    public class MapNotableData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }
}