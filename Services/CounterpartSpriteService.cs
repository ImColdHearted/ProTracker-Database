using System.Text.Json;
using Foot_Tracker.Tracking;
using Serilog;
using SkiaSharp;

namespace Foot_Tracker.Services
{
    public static class CounterpartSpriteService
    {
        private static readonly List<CounterpartVariant> variants = new();

        /// <summary>§213. The catalog's CATEGORY KEYS, in declaration order.
        ///
        /// THE BUG THIS EXISTS FOR. Every list of events in the app was built
        /// from AllVariants, which is built from ENTRIES. A category declared
        /// with no entries yet contributed nothing and so existed nowhere in
        /// the UI - which makes the obvious workflow impossible by
        /// construction, because you cannot scrape sprites INTO a category
        /// that only appears once it has sprites. Creating the asset folder
        /// does nothing either: a folder is not a category, only the catalog
        /// is.
        ///
        /// Keys rather than entries, registered before the entries are read,
        /// so a category exists the moment it is declared.</summary>
        private static readonly List<string> eventNames = new();

        // §139. Card-ready copies of the counterpart images, keyed by the
        // catalog-relative path and cached for the same reason
        // PokemonSpriteService caches its sprites: UpdateTrackerDisplay asks
        // for the current and previous card once a second.
        private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> cardSpriteCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>§139. The Current/Previous Encounter cards are 96x96 -
        /// the size of the library's species sprites, which sit centred on
        /// their canvas. The counterpart images are 120x120 (a few 100x100)
        /// with the sprite standing on the bottom edge, so shown as they are
        /// with Stretch="Uniform" they would come out a fifth smaller than
        /// the species sprite they replace, and sunk to the foot of the box.
        /// GetCardSprite re-centres them on a canvas of this size instead.</summary>
        public const int CardSize = 96;

        public static void Load()
        {
            variants.Clear();
            cardSpriteCache.Clear();
            eventNames.Clear();

            int skipped = 0;

            string path = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Data",
                "Counterparts",
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
                string eventName = (category.Key ?? string.Empty).Trim();

                if (eventName.Length == 0)
                {
                    Log.Warning("Counterparts: a category with a blank name was ignored");
                    continue;
                }

                // §213: declared is enough. The category exists now, before it
                // has a single sprite, so it can be chosen and scraped into.
                eventNames.Add(eventName);

                int before = variants.Count;

                foreach (var entry in category.Value)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name) ||
                        string.IsNullOrWhiteSpace(entry.Image))
                    {
                        skipped++;
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

                if (variants.Count == before)
                {
                    Log.Information(
                        "Counterparts: category {Event} is declared with no usable entries - it can be picked and scraped into, and matches nothing until it has sprites",
                        eventName);
                }
            }

            Log.Information(
                "Counterparts: {Categories} categories, {Sprites} sprites{Skipped}",
                eventNames.Count, variants.Count,
                skipped == 0 ? string.Empty : $", {skipped} entries skipped for a blank name or image");

            ReportLibraryGaps();
        }

        /// <summary>§213. Says, once per start, where the catalog and the
        /// files on disk disagree - in both directions.
        ///
        /// A folder is easy to create and easy to believe in, and an image
        /// path is easy to mistype; neither used to make a sound. The two
        /// checks here are the ones that would have caught both of the things
        /// this section was opened for: a Clone folder holding thirteen
        /// sprites that no entry pointed at, and an entry pointing at a FIFA
        /// folder that did not exist.
        ///
        /// Best effort throughout - the catalog is already in memory by this
        /// point, and a folder that will not list is not a reason to fail the
        /// load.</summary>
        private static void ReportLibraryGaps()
        {
            try
            {
                string root = Path.Combine(
                    AppContext.BaseDirectory, "SharedPokemonLibrary", "Assets", "Counterparts");

                // Entries whose image is not on disk. These load, appear in
                // every list, and can never match - the worst kind of quiet.
                var missing = new List<string>();

                foreach (CounterpartVariant variant in variants)
                {
                    string full = Path.Combine(
                        AppContext.BaseDirectory,
                        variant.ImagePath.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(full))
                        missing.Add($"{variant.Event}/{variant.Name} -> {variant.ImagePath}");
                }

                if (missing.Count > 0)
                {
                    Log.Warning(
                        "Counterparts: {Count} entries point at an image that is not there, so they can never match. First few: {Examples}",
                        missing.Count, string.Join("; ", missing.Take(5)));
                }

                if (!Directory.Exists(root))
                    return;

                // And the other direction: folders nothing points at.
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (CounterpartVariant variant in variants)
                {
                    string[] parts = variant.ImagePath.Replace('\\', '/').Split('/');

                    if (parts.Length >= 2)
                        referenced.Add(parts[^2]);
                }

                foreach (string directory in Directory.GetDirectories(root))
                {
                    string folder = Path.GetFileName(directory);

                    if (referenced.Contains(folder))
                        continue;

                    Log.Warning(
                        "Counterparts: asset folder {Folder} holds {Images} images and no catalog entry points at it - nothing can match against them",
                        folder, Directory.GetFiles(directory, "*.png").Length);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Counterparts: could not compare the catalog against the asset folders");
            }
        }

        /// <summary>§213. The folder a category's sprites live in - asked of
        /// the catalog first, and only guessed at if the catalog has nothing
        /// to say.
        ///
        /// §211's Save wrote to the event name WITH its spaces, so the first
        /// scrape into any multi-word event would have quietly made a second
        /// folder beside the real one. Removing the spaces fixes that for the
        /// nine categories that follow the convention, but it is still a
        /// guess, and "FIFA World Cup" is the proof: its sprites live in
        /// WorldCup, which is neither the name nor the name despaced.
        ///
        /// So the answer comes from the entries that are already there. A
        /// scrape lands beside its own category's sprites whatever that folder
        /// is called, and nobody has to know a naming rule. The despaced name
        /// is the fallback for a category that has no entries yet - which is
        /// exactly the case §213 made possible, so it does still get used.</summary>
        public static string FolderFor(string eventName)
        {
            string name = (eventName ?? string.Empty).Trim();

            if (name.Length == 0)
                return string.Empty;

            foreach (CounterpartVariant variant in variants)
            {
                if (!string.Equals(variant.Event, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] parts = variant.ImagePath.Replace('\\', '/').Split('/');

                if (parts.Length >= 2 && parts[^2].Length > 0)
                    return parts[^2];
            }

            return name.Replace(" ", string.Empty);
        }

        /// <summary>§213. Every category the catalog declares, whether or not
        /// it has sprites yet - see eventNames for why this cannot be built
        /// from AllVariants.</summary>
        public static IReadOnlyList<string> EventNames => eventNames.ToList();

        /// <summary>§199. Every counterpart the catalog knows, event
        /// first and then name - what the Simulator's sprite picker lists
        /// so a custom opponent can be told to use one. GetForPokemon
        /// answers "which skins does THIS species have", which is the
        /// question the hunt asks; this is the other one.</summary>
        public static IReadOnlyList<CounterpartVariant> AllVariants =>
            variants
                .OrderBy(v => v.Event, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

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
            // §138: apostrophes and periods dropped too. The tracker's OCR
            // names are "Farfetchd" and "Mr Mime"; the catalog spells the
            // same species "Farfetch'd" in one event and "Farfetchd" in
            // another. Both must match, in the picker and in the matcher.
            // §405: the catalog writes "Nidoran ♀", the library "Nidoran F"
            // - the sign becomes the letter, so the two meet.
            return string.Join(
                " ",
                value
                    .Trim()
                    .Replace("-", " ")
                    .Replace("_", " ")
                    .Replace("'", string.Empty)
                    .Replace(".", string.Empty)
                    .Replace("\u2640", " f")
                    .Replace("\u2642", " m")
                    .ToLowerInvariant()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
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

        /// <summary>§139. The counterpart image at
        /// <paramref name="relativeImagePath"/> (as the catalog spells it,
        /// e.g. "SharedPokemonLibrary/Assets/Counterparts/Summer/Wingull.png")
        /// cropped to its opaque box and re-centred on a CardSize canvas at
        /// 1:1, so the encounter card draws it exactly as it draws a species
        /// sprite. A sprite too big for the card keeps its own pixels on a
        /// larger square canvas and lets the card's uniform stretch shrink
        /// it, the same way an oversized species sprite would be shown. Null
        /// when the file is missing, empty or will not decode - the miss is
        /// cached too, so a broken asset costs one attempt.
        ///
        /// §199: the Simulator's sprite override goes through here too. The
        /// body was never counterpart-specific - it reads a path under the
        /// application folder - and an ABSOLUTE path also works, because
        /// Path.Combine drops its first argument when the second is rooted.
        /// That is what lets a boss slot point at a file the author browsed
        /// to from anywhere on their disk.</summary>
        public static Avalonia.Media.Imaging.Bitmap? GetCardSprite(string? relativeImagePath)
        {
            if (string.IsNullOrWhiteSpace(relativeImagePath))
                return null;

            if (cardSpriteCache.TryGetValue(relativeImagePath, out Avalonia.Media.Imaging.Bitmap? cached))
                return cached;

            Avalonia.Media.Imaging.Bitmap? bitmap = null;

            try
            {
                string fullPath = Path.Combine(
                    AppContext.BaseDirectory,
                    relativeImagePath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(fullPath))
                    bitmap = BuildCardSprite(File.ReadAllBytes(fullPath));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not prepare the counterpart card sprite {Path}", relativeImagePath);
            }

            cardSpriteCache[relativeImagePath] = bitmap;

            return bitmap;
        }

        /// <summary>The re-centring itself, on the PNG bytes so it can be
        /// checked without a file. Same bulk Pixels round trip as ImageOps
        /// uses - one native crossing each way rather than one per pixel.</summary>
        internal static Avalonia.Media.Imaging.Bitmap? BuildCardSprite(byte[] pngBytes)
        {
            using SKBitmap? source = ImageOps.DecodePng(pngBytes);

            if (source is null || source.Width <= 0 || source.Height <= 0)
                return null;

            SKColor[] pixels = source.Pixels;
            int width = source.Width;
            int height = source.Height;

            // The opaque box - every pixel that draws at all, so the soft
            // edge pixels around the outline come along with it.
            int left = width;
            int top = height;
            int right = -1;
            int bottom = -1;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (pixels[y * width + x].Alpha == 0)
                        continue;

                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }

            if (right < 0)
                return null;

            int spriteWidth = right - left + 1;
            int spriteHeight = bottom - top + 1;
            int side = Math.Max(CardSize, Math.Max(spriteWidth, spriteHeight));
            int offsetX = (side - spriteWidth) / 2;
            int offsetY = (side - spriteHeight) / 2;

            // default(SKColor) is fully transparent, so the canvas needs no
            // clearing - only the sprite's own box is written.
            var cardPixels = new SKColor[side * side];

            for (int y = 0; y < spriteHeight; y++)
            {
                for (int x = 0; x < spriteWidth; x++)
                {
                    cardPixels[(offsetY + y) * side + offsetX + x] =
                        pixels[(top + y) * width + left + x];
                }
            }

            using var card = new SKBitmap(side, side);
            card.Pixels = cardPixels;

            using var stream = new MemoryStream(ImageOps.EncodePng(card));

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