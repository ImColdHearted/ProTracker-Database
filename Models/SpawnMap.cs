using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Foot_Tracker.Models
{
    /// <summary>
    /// §397. The five pages under Game Data → Spawns, in the order the menu
    /// lists them. Four are PRO's regions; Other is everything the game has
    /// that is not one of those - the Sevii Islands, Vulcan Island, the
    /// event islands, the custom maps.
    ///
    /// A map belongs to whichever page the admin filed it under
    /// (SpawnMap.Region), not to the location catalog's own region: the
    /// catalog only suggests a page when a map is picked in the console.
    /// </summary>
    public static class SpawnRegions
    {
        public const string Kanto = "Kanto";
        public const string Johto = "Johto";
        public const string Hoenn = "Hoenn";
        public const string Sinnoh = "Sinnoh";
        public const string Other = "Other";

        /// <summary>Menu order - the game's order, then the catch-all.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Kanto, Johto, Hoenn, Sinnoh, Other };

        public static bool IsKnown(string? region) => Canonical(region) is not null;

        /// <summary>The page's own spelling of a region name, or null for
        /// anything that is not one of the five.</summary>
        public static string? Canonical(string? region)
        {
            if (string.IsNullOrWhiteSpace(region))
                return null;

            string trimmed = region.Trim();

            return All.FirstOrDefault(r => string.Equals(r, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Which page the location catalog's region suggests: the
        /// four named regions as themselves, everything else Other. Only a
        /// suggestion - the console lets the admin file a map anywhere.</summary>
        public static string ForCatalogRegion(string? catalogRegion) =>
            Canonical(catalogRegion) is string known && known != Other ? known : Other;
    }

    /// <summary>
    /// §397. One species on one map, as the admin published it: the same
    /// five icon facts and the membership colour §281 reads off the Pokedex,
    /// keyed by species rather than by map because the page it is shown on
    /// IS the map.
    /// </summary>
    public sealed class SpawnPokemon
    {
        public string Name { get; set; } = string.Empty;

        public bool Land { get; set; }

        public bool Water { get; set; }

        public bool Morning { get; set; }

        public bool Day { get; set; }

        public bool Night { get; set; }

        public bool MembersOnly { get; set; }

        /// <summary>§287's rule, unchanged: neither icon lit means a rod.</summary>
        [JsonIgnore]
        public bool Fishing => !Land && !Water;

        [JsonIgnore]
        public string MethodText =>
            Fishing ? "Fishing"
            : Land && Water ? "Grass, Surf"
            : Land ? "Grass"
            : "Surf";

        [JsonIgnore]
        public string TimeText =>
            Morning && Day && Night ? "All day"
            : !(Morning || Day || Night) ? "-"
            : string.Join(", ", TimeParts());

        private IEnumerable<string> TimeParts()
        {
            if (Morning) yield return "Morning";
            if (Day) yield return "Day";
            if (Night) yield return "Night";
        }

        /// <summary>A scanned Pokedex row, turned round: the species it was
        /// scanned for becomes the name, the row's facts come along.</summary>
        public static SpawnPokemon From(string species, PokedexSpawn spawn) => new()
        {
            Name = species.Trim(),
            Land = spawn.Land,
            Water = spawn.Water,
            Morning = spawn.Morning,
            Day = spawn.Day,
            Night = spawn.Night,
            MembersOnly = spawn.MembersOnly,
        };
    }

    /// <summary>
    /// §399. Where a map sits on its region's picture: a box in the
    /// picture's own pixels, drawn by the admin in the Map Boxes editor.
    /// The picture's size travels with it, so a box drawn on one version of
    /// the picture can be placed on a resized one by proportion, and a box
    /// that falls outside its picture is refused as nonsense. §402: a map
    /// may have several - a route the picture draws in two pieces gets a
    /// box per piece.
    /// </summary>
    public sealed class MapMarker
    {
        public int X { get; set; }

        public int Y { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int ImageWidth { get; set; }

        public int ImageHeight { get; set; }

        [JsonIgnore]
        public bool IsValid =>
            Width > 0 && Height > 0 && ImageWidth > 0 && ImageHeight > 0
            && X >= 0 && Y >= 0 && X + Width <= ImageWidth && Y + Height <= ImageHeight;

        /// <summary>"120,80 22×18".</summary>
        [JsonIgnore]
        public string Describe => $"{X},{Y} {Width}×{Height}";

        /// <summary>§402. The same box, pixel for pixel - how a box is
        /// found again after a republish hands back new objects.</summary>
        public bool SameAs(MapMarker? other) =>
            other is not null
            && X == other.X && Y == other.Y && Width == other.Width && Height == other.Height
            && ImageWidth == other.ImageWidth && ImageHeight == other.ImageHeight;
    }

    /// <summary>
    /// §397. One map as published: the page it is filed under, its name as
    /// the admin typed it, and every species scanned for it at the moment
    /// it was published. Scans made afterwards reach players only when the
    /// map is republished - the console has a button for that, per map and
    /// for all of them. §399: and, when the admin has drawn them, its boxes
    /// on the region's picture (§402: any number; none is an empty list).
    /// </summary>
    public sealed class SpawnMap
    {
        public string Region { get; set; } = SpawnRegions.Other;

        public string Map { get; set; } = string.Empty;

        public List<SpawnPokemon> Pokemon { get; set; } = new();

        public List<MapMarker> Markers { get; set; } = new();

        /// <summary>§402. Whether any box on the region picture is drawn
        /// for this map.</summary>
        [JsonIgnore]
        public bool HasBoxes => Markers.Any(m => m.IsValid);

        /// <summary>§412. The map whose spot on the picture this one shares -
        /// the six areas of a safari zone behind one box, the rooms of a cave
        /// behind its entrance - or null. Set in the Map Boxes editor's Link
        /// mode on a map with no boxes of its own; a map's own boxes always
        /// win over a link.</summary>
        public string? LinkedTo { get; set; }

        [JsonIgnore]
        public bool IsLinked => !string.IsNullOrWhiteSpace(LinkedTo);

        public DateTime? UpdatedUtc { get; set; }

        public string UpdatedBy { get; set; } = string.Empty;

        /// <summary>The server's key for this map - see <see cref="KeyFor"/>.</summary>
        [JsonIgnore]
        public string Key => KeyFor(Map);

        /// <summary>The key a map name is stored and addressed under, on the
        /// Worker and here: lower-case letters and digits only, everything
        /// else dropped, so "Mt. Moon 1F", "Mt Moon 1F" and "MT. MOON 1F"
        /// are one map. The Worker applies the identical rule to the name in
        /// the body and refuses a request whose path key disagrees, which is
        /// what keeps the two sides from ever filing one map twice.</summary>
        public static string KeyFor(string? map) =>
            string.IsNullOrWhiteSpace(map)
                ? string.Empty
                : Regex.Replace(map.ToLowerInvariant(), "[^a-z0-9]", string.Empty);

        /// <summary>"12 Pokémon", "1 Pokémon", "no Pokémon yet".</summary>
        [JsonIgnore]
        public string PokemonCountText =>
            Pokemon.Count == 0 ? "no Pokémon yet" : $"{Pokemon.Count} Pokémon";
    }
}
