using System;
using System.Text.Json.Serialization;

namespace Foot_Tracker.Models
{
    /// <summary>
    /// §429. The lowest and highest level anyone has met one species at on
    /// one map, as the events server keeps it: every tracker whose owner
    /// opted in adds what it saw, and the server keeps the widest range it
    /// has ever been told. No per-encounter rows and nothing about who saw
    /// what - a range and a count is the whole record.
    /// </summary>
    public sealed class SpawnLevelRange
    {
        /// <summary>The map's key, as SpawnMap.KeyFor folds a name
        /// ("route1", "mtpyre1f").</summary>
        public string MapKey { get; set; } = string.Empty;

        /// <summary>The library's name for the species ("Linoone-Galarian").</summary>
        public string Species { get; set; } = string.Empty;

        public int Min { get; set; }

        public int Max { get; set; }

        /// <summary>How many sightings went into the range - a sense of how
        /// sure it is. One sighting is a data point; a hundred is a range.</summary>
        public int Samples { get; set; }

        public DateTime? UpdatedUtc { get; set; }

        /// <summary>"12-18", or "15" when only one level has ever been seen.</summary>
        [JsonIgnore]
        public string Text => Min == Max ? Min.ToString() : $"{Min}-{Max}";

        /// <summary>The key a lookup matches on: map and species, case-folded.</summary>
        [JsonIgnore]
        public string Key => KeyFor(MapKey, Species);

        public static string KeyFor(string mapKey, string species) =>
            mapKey + "|" + species.Trim().ToLowerInvariant();
    }
}
