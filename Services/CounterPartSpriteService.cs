using System.Text.Json;

namespace Foot_Tracker.Services
{
    public static class CounterpartSpriteService
    {
        private static readonly List<CounterpartVariant> variants = new();

        public static void Load()
        {
            variants.Clear();

            string path = Path.Combine(
                AppContext.BaseDirectory,
                "DataFiles",
                "counterparts.json"
            );

            if (!File.Exists(path))
                return;

            string json = File.ReadAllText(path);

            var data =
                JsonSerializer.Deserialize<
                    Dictionary<string, List<CounterpartJsonEntry>>
                >(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }
                );

            if (data == null)
                return;

            foreach (var category in data)
            {
                string eventName = category.Key;

                foreach (var entry in category.Value)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name) ||
                        string.IsNullOrWhiteSpace(entry.Image))
                    {
                        continue;
                    }

                    variants.Add(
                        new CounterpartVariant
                        {
                            Event = eventName,
                            Name = entry.Name.Trim(),
                            ImagePath = entry.Image
                        }
                    );
                }
            }
        }

        public static IReadOnlyList<CounterpartVariant>
            GetForPokemon(string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return Array.Empty<CounterpartVariant>();

            return variants
                .Where(v =>
                    IsMatch(
                        v.Name,
                        pokemonName
                    ))
                .OrderBy(v => v.Event)
                .ThenBy(v => v.Name)
                .ToList();
        }

        private static bool IsMatch(
            string counterpartName,
            string speciesName)
        {
            string counterpart =
                Normalize(counterpartName);

            string species =
                Normalize(speciesName);

            // Exact normal counterpart.
            if (counterpart == species)
                return true;

            // Handles things like:
            // Pikachu Male
            // Pikachu Female
            // Mega Pikachu (if one ever existed)
            // etc.
            if (counterpart == species)
                return true;

            string[] allowedSuffixes =
            {
    " male",
    " female",
    " m",
    " f"
};

            foreach (string suffix in allowedSuffixes)
            {
                if (counterpart ==
                    species + suffix)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Normalize(string value)
        {
            return value
                .Trim()
                .Replace("-", " ")
                .Replace("_", " ")
                .ToLowerInvariant();
        }

        public static Avalonia.Media.Imaging.Bitmap? GetImage(
            CounterpartVariant variant)
        {
            string relativePath =
                variant.ImagePath
                    .Replace('/', Path.DirectorySeparatorChar);

            string fullPath =
                Path.Combine(
                    AppContext.BaseDirectory,
                    relativePath
                );

            if (!File.Exists(fullPath))
                return null;

            using var stream =
                new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read
                );

            // System.Drawing.Image.FromStream -> Avalonia.Media.Imaging.Bitmap
            return new Avalonia.Media.Imaging.Bitmap(stream);
        }
    }

    public class CounterpartVariant
    {
        public string Event { get; set; } =
            string.Empty;

        public string Name { get; set; } =
            string.Empty;

        public string ImagePath { get; set; } =
            string.Empty;
    }

    internal class CounterpartJsonEntry
    {
        public string Name { get; set; } =
            string.Empty;

        public string Image { get; set; } =
            string.Empty;

        public string Notes { get; set; } =
            string.Empty;

        public List<string> SpawnLocations { get; set; } =
            new();
    }
}