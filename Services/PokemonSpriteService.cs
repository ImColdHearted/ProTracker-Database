using System.Text.Json;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services
{
    public static class PokemonSpriteService
    {
        private static readonly Dictionary<string, string> spriteLookup =
            new(StringComparer.OrdinalIgnoreCase);

        // Name/OCR-alias -> that species' types, populated alongside spriteLookup
        // below (species entries only - see PokemonLibraryEntry.Types). Backs
        // GetTypes, which ViewModels expose as a small Types list next to a
        // Pokemon's name - TypeIconConverter (Converters/TypeIconConverter.cs)
        // turns each name into an icon at the XAML layer via GetTypeIcon below.
        private static readonly Dictionary<string, List<string>> typeLookup =
            new(StringComparer.OrdinalIgnoreCase);

        // Type name (e.g. "Fire") -> its loaded icon, cached after first use since
        // there are only 18 possible values and every row that shows a Pokemon's
        // name re-requests the same handful of icons.
        private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> typeIconCache =
            new(StringComparer.OrdinalIgnoreCase);

        // Sprite file name -> its loaded bitmap, cached after first use for the
        // same reason as typeIconCache above. MainWindowViewModel.UpdateTrackerDisplay
        // re-resolves every current/previous/target sprite on every HuntTimer_Tick
        // (once a second, for as long as a hunt is running) even though the
        // underlying Pokemon usually hasn't changed since the last tick - without
        // this cache, GetSprite/LoadSprite reopened and re-decoded the same image
        // file from disk every single second, on the UI thread. Keyed by sprite
        // file name rather than Pokemon name since several names/OCR aliases can
        // point at the same underlying file (see spriteLookup above).
        private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> spriteBitmapCache =
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
            typeLookup.Clear();
            typeIconCache.Clear();
            spriteBitmapCache.Clear();
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

                typeLookup[entry.Name] =
                    entry.Types;

                foreach (string alias in entry.OcrAliases)
                {
                    if (!string.IsNullOrWhiteSpace(alias))
                    {
                        spriteLookup[alias] =
                            entry.Sprite;

                        typeLookup[alias] =
                            entry.Types;
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

            return LoadSprite(spriteFile);
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

            if (spriteBitmapCache.TryGetValue(spriteFile, out Avalonia.Media.Imaging.Bitmap? cached))
            {
                return cached;
            }

            string path = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Sprites",
                spriteFile
            );

            Avalonia.Media.Imaging.Bitmap? bitmap = null;

            if (File.Exists(path))
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read
                );

                // System.Drawing.Image.FromStream + new Bitmap(source) -> Avalonia.Media.Imaging.Bitmap
                bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
            }

            // Cache even a miss (null) under this file name, same as
            // GetTypeIcon does above - a sprite file that doesn't exist on disk
            // isn't going to start existing mid-session, so there's no point
            // re-touching the filesystem for it on every future call.
            spriteBitmapCache[spriteFile] = bitmap;

            return bitmap;
        }

        public static IReadOnlyList<string> GetTypes(string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return Array.Empty<string>();

            return typeLookup.TryGetValue(
                    pokemonName.Trim(),
                    out List<string>? types)
                ? types
                : Array.Empty<string>();
        }

        public static Avalonia.Media.Imaging.Bitmap? GetTypeIcon(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            string key = typeName.Trim();

            if (typeIconCache.TryGetValue(
                    key,
                    out Avalonia.Media.Imaging.Bitmap? cached))
            {
                return cached;
            }

            string iconPath = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Typings",
                $"{key.ToLowerInvariant()}.png"
            );

            Avalonia.Media.Imaging.Bitmap? icon = File.Exists(iconPath)
                ? new Avalonia.Media.Imaging.Bitmap(iconPath)
                : null;

            typeIconCache[key] = icon;

            return icon;
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

        // Maps straight onto pokemon-species.json's "types" array (1-2 entries,
        // e.g. ["Grass", "Poison"]) added when every species got its typing
        // filled in - see MIGRATION_GUIDE.md. PropertyNameCaseInsensitive
        // (already used below) matches "types" -> Types with no extra
        // attribute needed, same as every other property here. Only species
        // entries carry this - pokemon-forms.json wasn't part of that pass, so
        // regional/alternate forms resolve to an empty list via GetTypes.
        public List<string> Types { get; set; } = new();

        public string Identifier { get; set; } = string.Empty;

        public string? FormIdentifier { get; set; }

        public bool IsDefaultForm { get; set; }

        public string Sprite { get; set; } = string.Empty;

        public List<string> OcrAliases { get; set; } = new();
    }