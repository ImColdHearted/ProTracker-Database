using System.Text.Json;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services
{
    public static class PokemonSpriteService
    {
        private static readonly Dictionary<string, string> spriteLookup =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, PokemonFormEntry> formLookup =
    new(StringComparer.OrdinalIgnoreCase);

        private static readonly List<PokemonLibraryEntry> pokemonEntries = new();

        public static IReadOnlyList<PokemonLibraryEntry> AllPokemon =>
            pokemonEntries;

        public static IReadOnlyList<PokemonFormEntry> AllForms =>
    formEntries;

        private static readonly List<PokemonFormEntry>
    formEntries = new();

        public static IReadOnlyList<PokemonFormEntry>
            GetHuntableRegionalForms()
        {
            return formEntries
                .Where(IsHuntableRegionalForm)
                .GroupBy(
                    f => f.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(f => f.DexNumber)
                .ThenBy(f => f.Name)
                .ToList();
        }

        private static bool IsHuntableRegionalForm(
    PokemonFormEntry form)
        {
            string name = form.Name;

            return
                name.Contains(
                    "Alolan",
                    StringComparison.OrdinalIgnoreCase) ||
                name.Contains(
                    "Alola",
                    StringComparison.OrdinalIgnoreCase) ||

                name.Contains(
                    "Galarian",
                    StringComparison.OrdinalIgnoreCase) ||
                name.Contains(
                    "Galar",
                    StringComparison.OrdinalIgnoreCase) ||

                name.Contains(
                    "Hisuian",
                    StringComparison.OrdinalIgnoreCase) ||
                name.Contains(
                    "Hisui",
                    StringComparison.OrdinalIgnoreCase);
        }

        public static void Load()
        {
            spriteLookup.Clear();
            formLookup.Clear();
            pokemonEntries.Clear();
            formEntries.Clear();

            // =========================================================
            // 1. LOAD NORMAL SPECIES
            // =========================================================

            string jsonPath = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Data",
                "Pokemon",
                "pokemon-species.json"
            );

            if (!File.Exists(jsonPath))
            {
                throw new FileNotFoundException(
                    "Pokemon library could not be found.",
                    jsonPath
                );
            }

            string json = File.ReadAllText(jsonPath);

            var entries =
                JsonSerializer.Deserialize<List<PokemonLibraryEntry>>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }
                ) ?? new List<PokemonLibraryEntry>();

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name) ||
                    string.IsNullOrWhiteSpace(entry.Sprite))
                {
                    continue;
                }

                pokemonEntries.Add(entry);

                spriteLookup[entry.Name] =
                    entry.Sprite;

                foreach (string alias in entry.OcrAliases)
                {
                    if (!string.IsNullOrWhiteSpace(alias))
                    {
                        spriteLookup[alias] =
                            entry.Sprite;
                    }
                }
            }

            // =========================================================
            // 2. LOAD ALTERNATE / REGIONAL FORMS
            // =========================================================

            string formsJsonPath = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Data",
                "Pokemon",
                "pokemon-forms.json"
            );

            if (!File.Exists(formsJsonPath))
                return;

            string formsJson =
                File.ReadAllText(formsJsonPath);

            var forms =
                JsonSerializer.Deserialize<List<PokemonFormEntry>>(
                    formsJson,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }
                ) ?? new List<PokemonFormEntry>();

            foreach (var form in forms)
            {
                if (string.IsNullOrWhiteSpace(form.Name) ||
                    string.IsNullOrWhiteSpace(form.Sprite))
                {
                    continue;
                }

                // Add exactly ONCE.
                formEntries.Add(form);

                // Canonical form name.
                formLookup[form.Name] = form;

                // OCR aliases.
                foreach (string alias in form.OcrAliases)
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        continue;

                    // Prevent "Rattata" from becoming
                    // "Rattata-Alolan", etc.
                    if (alias.Equals(
                            form.SpeciesName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    formLookup[alias] = form;
                }
            }
        }

        public static IReadOnlyList<PokemonFormEntry>
            GetFormsForSpecies(string speciesName)
        {
            if (string.IsNullOrWhiteSpace(speciesName))
                return Array.Empty<PokemonFormEntry>();

            return formEntries
                .Where(f =>
                    f.SpeciesName.Equals(
                        speciesName,
                        StringComparison.OrdinalIgnoreCase))

                // Regional Pokémon are their own hunt targets.
                .Where(f =>
                    !IsHuntableRegionalForm(f))

                .GroupBy(
                    f => f.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(f => f.Name)
                .ToList();
        }

        public static Avalonia.Media.Imaging.Bitmap? GetSprite(string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return null;

            if (!spriteLookup.TryGetValue(
                    pokemonName.Trim(),
                    out string? spriteFile))
            {
                return null;
            }

            string spritePath = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Sprites",
                spriteFile
            );

            if (!File.Exists(spritePath))
                return null;

            using var stream = new FileStream(
                spritePath,
                FileMode.Open,
                FileAccess.Read
            );

            // System.Drawing.Image.FromStream + new Bitmap(source) -> Avalonia.Media.Imaging.Bitmap
            return new Avalonia.Media.Imaging.Bitmap(stream);
        }

        public static Avalonia.Media.Imaging.Bitmap? GetEncounterSprite(string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return null;

            string key = pokemonName.Trim();

            if (formLookup.TryGetValue(key, out var form))
            {
                return LoadSprite(form.Sprite);
            }

            return GetSprite(key);
        }

        private static Avalonia.Media.Imaging.Bitmap? LoadSprite(string spriteFile)
        {
            if (string.IsNullOrWhiteSpace(spriteFile))
                return null;

            string path = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Sprites",
                spriteFile
            );

            if (!File.Exists(path))
                return null;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read
            );

            return new Avalonia.Media.Imaging.Bitmap(stream);
        }

        public static string ResolveEncounterName(
            string detectedName)
        {
            if (string.IsNullOrWhiteSpace(detectedName))
                return string.Empty;

            string key = detectedName.Trim();

            // =========================================================
            // 1. EXACT NORMAL SPECIES MATCH ALWAYS WINS
            // =========================================================
            //
            // If OCR detected "Farfetch'd", "Rattata", "Meowth", etc.,
            // do not allow a regional/form alias to replace it.
            //
            var normalSpecies =
                pokemonEntries.FirstOrDefault(
                    p => p.Name.Equals(
                        key,
                        StringComparison.OrdinalIgnoreCase));

            if (normalSpecies != null)
            {
                return normalSpecies.Name;
            }

            // =========================================================
            // 2. CHECK ALTERNATE / REGIONAL FORM
            // =========================================================

            if (formLookup.TryGetValue(
                    key,
                    out var form))
            {
                return form.Name;
            }

            // =========================================================
            // 3. NOTHING SPECIAL FOUND
            // =========================================================

            return key;
        }
    }
}


    public class PokemonLibraryEntry
    {
        public int PokemonId { get; set; }

        public int DexNumber { get; set; }

        public string Name { get; set; } = string.Empty;

        public string SpeciesName { get; set; } = string.Empty;

        public string Identifier { get; set; } = string.Empty;

        public string? FormIdentifier { get; set; }

        public bool IsDefaultForm { get; set; }

        public string Sprite { get; set; } = string.Empty;

        public List<string> OcrAliases { get; set; } = new();
    }