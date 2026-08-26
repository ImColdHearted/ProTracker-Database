using System.Collections.Generic;

namespace Foot_Tracker.Models
{
    public class CounterpartEntry
    {
        public string Name { get; set; } = string.Empty;

        public string Image { get; set; } = string.Empty;

        public List<string> SpawnLocations { get; set; } = new();

        public string Rarity { get; set; } = string.Empty;

        public string Event { get; set; } = string.Empty;

        public string Notes { get; set; } = string.Empty;
    }
}