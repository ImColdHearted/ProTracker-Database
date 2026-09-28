using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>§397. One line of the location catalog, as the Spawns
    /// console needs it: the map's canonical name and the region the
    /// catalog files it under (the game's own regions plus the islands and
    /// custom areas - "Sevii Islands", "Vulcan Island", "Custom"...).</summary>
    public sealed record CatalogLocation(string Name, string Region);

    /// <summary>
    /// §397. The names in DataFiles/pro-locations.json, for picking a map
    /// in the Admin Console's Spawns section.
    ///
    /// LocationDictionaryService reads the same file, but for a different
    /// job - matching a corner-HUD OCR read to a canonical name - and it
    /// keeps only what that needs (names and aliases, normalised for
    /// matching). This reads the file for its names and regions and nothing
    /// else, so a typed map can be offered from the list and its region can
    /// be suggested. Read once, lazily; a missing or damaged file is an
    /// empty list and a logged warning, and the console still takes a name
    /// typed by hand - the same fail-soft rule every DataFiles-backed
    /// catalog in this app follows.
    /// </summary>
    public static class LocationCatalogService
    {
        private static readonly string CatalogPath =
            Path.Combine(AppContext.BaseDirectory, "DataFiles", "pro-locations.json");

        private static readonly object Gate = new();

        private static IReadOnlyList<CatalogLocation>? all;

        /// <summary>Every catalog location, alphabetical by name.</summary>
        public static IReadOnlyList<CatalogLocation> All
        {
            get
            {
                lock (Gate)
                {
                    return all ??= Load();
                }
            }
        }

        /// <summary>The catalog's entry for a name typed or picked in the
        /// console, matched case-insensitively on the canonical name; null
        /// for a name the catalog does not carry.</summary>
        public static CatalogLocation? Find(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string trimmed = name.Trim();

            return All.FirstOrDefault(l => string.Equals(l.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        private static IReadOnlyList<CatalogLocation> Load()
        {
            try
            {
                if (!File.Exists(CatalogPath))
                {
                    Log.Warning("Location catalog: {Path} is missing - the Spawns console offers no map names.", CatalogPath);
                    return Array.Empty<CatalogLocation>();
                }

                CatalogFile? file = JsonSerializer.Deserialize<CatalogFile>(
                    File.ReadAllText(CatalogPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (file is null)
                    return Array.Empty<CatalogLocation>();

                return file.Locations
                    .Where(l => !string.IsNullOrWhiteSpace(l.Name))
                    .Select(l => new CatalogLocation(l.Name.Trim(), (l.Region ?? string.Empty).Trim()))
                    .GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Location catalog: {Path} could not be read - the Spawns console offers no map names.", CatalogPath);
                return Array.Empty<CatalogLocation>();
            }
        }

        private sealed class CatalogFile
        {
            public List<CatalogEntry> Locations { get; set; } = new();
        }

        private sealed class CatalogEntry
        {
            public string Name { get; set; } = string.Empty;
            public string? Region { get; set; }
        }
    }
}
