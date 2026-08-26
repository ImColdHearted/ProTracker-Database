using System;
using System.IO;
using System.Text.Json;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services
{
    public static class RegionMapRepository
    {
        public static RegionMapData Load(string regionId)
        {
            if (string.IsNullOrWhiteSpace(regionId))
            {
                throw new ArgumentException(
                    "A region ID must be provided.",
                    nameof(regionId));
            }

            string filePath = Path.Combine(
                AppContext.BaseDirectory,
                "DataFiles",
                "Maps",
                $"{regionId}.json");

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    $"The map file could not be found:\n{filePath}",
                    filePath);
            }

            string json = File.ReadAllText(filePath);

            JsonSerializerOptions options = new()
            {
                PropertyNameCaseInsensitive = true
            };

            RegionMapData? map =
                JsonSerializer.Deserialize<RegionMapData>(
                    json,
                    options);

            if (map == null)
            {
                throw new InvalidDataException(
                    $"The map file '{regionId}.json' could not be read.");
            }

            return map;
        }
    }
}