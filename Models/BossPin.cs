using System;
using System.Text.Json.Serialization;

namespace Foot_Tracker.Models
{
    /// <summary>
    /// §409. Where a boss stands on the world picture: one point in the
    /// picture's own pixels, with the picture's size (as a map's box
    /// carries it, §399) so a pin placed on one version of the picture can
    /// be placed on another by proportion. Published from the Map Boxes
    /// editor, read by every tracker for the Maps window.
    /// </summary>
    public sealed class BossPin
    {
        /// <summary>The boss file's name without its extension - the id
        /// BossRepository opens ("LtSurge"), never the display name.</summary>
        public string BossId { get; set; } = string.Empty;

        /// <summary>The display name, for a tracker whose files lack the
        /// boss.</summary>
        public string Boss { get; set; } = string.Empty;

        public int X { get; set; }

        public int Y { get; set; }

        public int ImageWidth { get; set; }

        public int ImageHeight { get; set; }

        public DateTime? UpdatedUtc { get; set; }

        public string UpdatedBy { get; set; } = string.Empty;

        [JsonIgnore]
        public bool IsValid =>
            BossId.Length > 0 && ImageWidth > 0 && ImageHeight > 0
            && X >= 0 && Y >= 0 && X < ImageWidth && Y < ImageHeight;

        /// <summary>The server's key for the pin: the id folded as a map's
        /// name is (§397).</summary>
        [JsonIgnore]
        public string Key => SpawnMap.KeyFor(BossId);
    }
}
