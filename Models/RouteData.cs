using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Foot_Tracker.Models
{
    public class RouteEncounterGroup
    {
        public string Id { get; set; } = "";
        public string MapId { get; set; } = "";
        public string Region { get; set; } = "";
        public string DisplayName { get; set; } = "";

        public string Method { get; set; } = "";

        public bool RequiresMembership { get; set; }

        public List<RouteEncounter> Encounters { get; set; } = new();

        public List<RoutePokemonData> Pokemon { get; set; } = new List<RoutePokemonData>();
        public List<RouteNotableData> Notables { get; set; } = new List<RouteNotableData>();
    }

    public class RouteEncounter
    {
        public string Pokemon { get; set; } = "";

        public List<string> TimePeriods { get; set; } = new();

        public int MinLevel { get; set; }

        public int MaxLevel { get; set; }

        public string? HeldItem { get; set; }
    }

    public class RoutePokemonData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("dexNumber")]
        public int DexNumber { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("membershipRequired")]
        public bool MembershipRequired { get; set; }
    }

    public class RouteNotableData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }
}