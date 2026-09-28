using System;
using System.Collections.Generic;

namespace Foot_Tracker.Models
{
    /// <summary>
    /// §281. One line of PRO's Pokedex "Area" list: where a species spawns,
    /// how, when, and whether the area needs membership.
    ///
    /// Every field but the name is read from PIXELS, not from text. Each row
    /// carries five icons - land, water, morning, day, night - drawn in one of
    /// two palettes, and the area's own name is drawn in one of two colours.
    /// See PokedexAreaReader for the measurements behind that.
    /// </summary>
    public class PokedexSpawn
    {
        /// <summary>The area exactly as the Pokedex prints it - the one field
        /// that needs OCR, and the key a merge matches on.</summary>
        public string MapName { get; set; } = string.Empty;

        /// <summary>Grass and surf, the two spawn methods the row's first two
        /// icons stand for. Both false is possible and is not an error: it is
        /// what a row whose icons could not be read looks like, and
        /// <see cref="HasAnyMethod"/> is how a reader says so.</summary>
        public bool Land { get; set; }

        public bool Water { get; set; }

        public bool Morning { get; set; }

        public bool Day { get; set; }

        public bool Night { get; set; }

        /// <summary>§281. The area's name is drawn pink rather than cyan when
        /// it needs membership - confirmed against the Safari areas. One exact
        /// colour, not a shade, so this is as certain as the icons.</summary>
        public bool MembersOnly { get; set; }

        /// <summary>§420. Typed into the console by hand rather than read off
        /// the panel - for a species the Pokedex on this machine cannot show
        /// yet (a spawn just added to the game, a Pokemon nobody here owns).
        /// Kept on the row so the viewer can mark it and so a later scan of
        /// the real panel can be seen to confirm or correct it. A file saved
        /// before this field existed reads back as false, which is right:
        /// everything before §420 was scanned.</summary>
        public bool Manual { get; set; }

        public DateTime FirstSeenUtc { get; set; }

        public DateTime LastSeenUtc { get; set; }

        public bool HasAnyMethod => Land || Water;

        public bool HasAnyTime => Morning || Day || Night;

        /// <summary>"Grass, Surf" - for a list that has no room for icons.</summary>
        /// <summary>§287. Neither icon lit means FISHING. The Pokedex has
        /// icons for grass and surf and none for a rod, so a fished area is
        /// drawn with both dark - Poliwag's and Slowpoke's rows are the proof,
        /// and the user's own reading of the panel confirmed it.</summary>
        public bool Fishing => !HasAnyMethod;

        public string MethodText =>
            Fishing ? "Fishing"
            : Land && Water ? "Grass, Surf"
            : Land ? "Grass"
            : "Surf";

        /// <summary>"Morning, Day" - and "All day" when every time is set,
        /// because three words for "always" is three words too many.</summary>
        public string TimeText =>
            Morning && Day && Night ? "All day"
            : !HasAnyTime ? "-"
            : string.Join(", ", Parts());

        private IEnumerable<string> Parts()
        {
            if (Morning) yield return "Morning";
            if (Day) yield return "Day";
            if (Night) yield return "Night";
        }

        /// <summary>Whether two readings describe the same spawn - everything
        /// but the timestamps. A merge that finds a row unchanged writes
        /// nothing, which is what makes re-scanning the same screen free.</summary>
        public bool SameFactsAs(PokedexSpawn other) =>
            other is not null
            && Land == other.Land
            && Water == other.Water
            && Morning == other.Morning
            && Day == other.Day
            && Night == other.Night
            && MembersOnly == other.MembersOnly;
    }

    /// <summary>§281. Everything scanned for one species, newest scan last.</summary>
    public class PokedexEntry
    {
        public string Species { get; set; } = string.Empty;

        public List<PokedexSpawn> Spawns { get; set; } = new();

        public DateTime? LastScannedUtc { get; set; }
    }
}
