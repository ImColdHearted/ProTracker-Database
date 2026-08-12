using Foot_Tracker.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    public static class SpriteMapRepository
    {
        private static Dictionary<string, SpriteMapEntry>? _entriesByName;

        public static void Load()
        {
            if (_entriesByName != null)
                return;

            string filePath = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Sprites",
                "sprite-map.json");

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    $"The sprite map could not be found:\n{filePath}",
                    filePath);
            }

            string json = File.ReadAllText(filePath);

            JsonSerializerOptions options = new()
            {
                PropertyNameCaseInsensitive = true
            };

            Dictionary<string, SpriteMapEntry>? entriesByNumber =
                JsonSerializer.Deserialize<
                    Dictionary<string, SpriteMapEntry>>(
                        json,
                        options);

            if (entriesByNumber == null)
            {
                throw new InvalidDataException(
                    "The sprite map could not be read.");
            }

            _entriesByName = entriesByNumber.Values
                .Where(entry =>
                    !string.IsNullOrWhiteSpace(entry.Name))
                .GroupBy(
                    entry => entry.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
        }

        public static SpriteMapEntry? FindByName(
            string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return null;

            Load();

            return _entriesByName!.TryGetValue(
                pokemonName.Trim(),
                out SpriteMapEntry? entry)
                    ? entry
                    : null;
        }

        public static int GetDexNumber(
            string pokemonName)
        {
            return FindByName(pokemonName)?.DexNumber ?? 0;
        }

        public static string GetSpriteFile(
            string pokemonName)
        {
            return FindByName(pokemonName)?.Sprite
                ?? "0.png";
        }
    }
}