using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Foot_Tracker.Tracking;
using PokemonSim.Models;
using Serilog;
using SkiaSharp;

namespace Foot_Tracker.Services.Simulator
{
    /// <summary>
    /// §201. The artwork the battle SCENE draws, which is a different
    /// question from the artwork the side panels draw.
    ///
    /// Two things make it different. The player is seen from behind, so it
    /// wants the back sprite; and both Pokemon stand on a painted pad, so
    /// the picture has to be trimmed to its opaque box first - the library's
    /// sprites sit on a shared 96x96 canvas with an inconsistent amount of
    /// empty space underneath (Charizard has 8 rows, Blastoise 16), and
    /// aligning those canvases by their bottom edge leaves one of them
    /// hovering half a foot above the floor.
    ///
    /// Trimming keeps the relative sizes honest, which the alternative -
    /// scaling every sprite to one height - would not: the scene multiplies
    /// the trimmed pixels by a fixed factor per side, so a Caterpie still
    /// comes out smaller than a Snorlax exactly as the shared canvas
    /// intends.
    ///
    /// Everything here falls back a rung at a time rather than failing. A
    /// library with no back folder simply shows the front sprite, which is
    /// what every install looks like until somebody syncs the back sprites.
    /// </summary>
    public static class BattleSpriteService
    {
        public const string BackFolder = "back";
        public const string ShinyFolder = "shiny";

        static readonly Dictionary<string, Bitmap?> trimmedCache =
            new(StringComparer.OrdinalIgnoreCase);

        static readonly object gate = new();

        public static string SpritesRoot => Path.Combine(
            AppContext.BaseDirectory, "SharedPokemonLibrary", "Assets", "Sprites");

        /// <summary>§201. The stadium the battle is fought on: the field
        /// band of the full picture, cut to 937x755 so both painted pads and
        /// enough headroom for a tall Pokemon fit a landscape panel. Loaded
        /// once and cached like everything else here; null when the asset is
        /// missing, which turns the scene off rather than drawing Pokemon on
        /// nothing.</summary>
        public static Bitmap? LoadField() => LoadPlain(Path.Combine(
            AppContext.BaseDirectory, "SharedPokemonLibrary", "Assets", "Battle", "stadium-field.png"));

        static Bitmap? LoadPlain(string path)
        {
            lock (gate)
            {
                if (trimmedCache.TryGetValue(path, out Bitmap? cached))
                    return cached;
            }

            Bitmap? bitmap = null;

            try
            {
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
                    bitmap = new Bitmap(stream);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Simulator: the battle field {Path} could not be loaded.", path);
            }

            lock (gate)
            {
                trimmedCache[path] = bitmap;
            }

            return bitmap;
        }

        /// <summary>The library file name for a Pokemon - "25.png" - by the
        /// §200 dex id when it has one, and by the species name otherwise.
        /// Null when neither answers.</summary>
        public static string? FileNameFor(PokemonState pokemon)
        {
            if (pokemon.DexNumber > 0)
            {
                string byDex = pokemon.DexNumber + ".png";

                if (File.Exists(Path.Combine(SpritesRoot, byDex)))
                    return byDex;
            }

            // §224. allowRelaxedMatch: the roster spells a form
            // "Mega Heracross" where the library spells it "Heracross-Mega",
            // and 174 of the roster's 565 names were drawing an empty pad
            // because of it. The scene is a display, so a relaxed spelling
            // is exactly what it wants; CounterpartMatcher, the other caller
            // of this method, deliberately does not opt in.
            return PokemonSpriteService.TryGetSpritePath(
                       pokemon.Species,
                       out string? full,
                       allowRelaxedMatch: true) &&
                   !string.IsNullOrEmpty(full)
                ? Path.GetFileName(full)
                : null;
        }

        /// <summary>The file this Pokemon should be drawn from, seen from
        /// behind or from the front. Null when the library has nothing.
        ///
        /// The rungs, most specific first: the §199 explicit file (which has
        /// no back or shiny variant - it is one named picture), then back
        /// shiny, back, shiny, and the ordinary front sprite. Each one is
        /// only taken if the file is actually there, so a missing back
        /// folder costs the back view and nothing else.</summary>
        public static string? ResolvePath(PokemonState pokemon, bool back)
        {
            if (!string.IsNullOrWhiteSpace(pokemon.SpritePath))
            {
                string named = Path.Combine(
                    AppContext.BaseDirectory,
                    pokemon.SpritePath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(named))
                    return named;
            }

            string? file = FileNameFor(pokemon);

            if (string.IsNullOrEmpty(file))
                return null;

            var candidates = new List<string>();

            if (back && pokemon.IsShiny)
                candidates.Add(Path.Combine(SpritesRoot, BackFolder, ShinyFolder, file));

            if (back)
                candidates.Add(Path.Combine(SpritesRoot, BackFolder, file));

            if (pokemon.IsShiny)
                candidates.Add(Path.Combine(SpritesRoot, ShinyFolder, file));

            candidates.Add(Path.Combine(SpritesRoot, file));

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>The picture at a path, cropped to its opaque box so the
        /// bottom row of the bitmap IS the Pokemon's feet. Cached by path,
        /// misses included, so a sprite costs one read however many turns it
        /// stays on the field.</summary>
        public static Bitmap? LoadTrimmed(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            lock (gate)
            {
                if (trimmedCache.TryGetValue(path, out Bitmap? cached))
                    return cached;
            }

            Bitmap? bitmap = null;

            try
            {
                if (File.Exists(path))
                    bitmap = Trim(File.ReadAllBytes(path));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Simulator: the battle sprite {Path} could not be prepared.", path);
            }

            lock (gate)
            {
                trimmedCache[path] = bitmap;
            }

            return bitmap;
        }

        /// <summary>The crop itself, on the PNG bytes so it can be checked
        /// without a file. Same bulk Pixels round trip
        /// CounterpartSpriteService.BuildCardSprite uses - one native
        /// crossing each way rather than one per pixel.</summary>
        internal static Bitmap? Trim(byte[] pngBytes)
        {
            using SKBitmap? source = ImageOps.DecodePng(pngBytes);

            if (source is null || source.Width <= 0 || source.Height <= 0)
                return null;

            SKColor[] pixels = source.Pixels;
            int width = source.Width;
            int height = source.Height;

            int left = width;
            int top = height;
            int right = -1;
            int bottom = -1;

            // Every pixel that draws at all, so the soft edge pixels around
            // an outline come along with it.
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

            // A fully transparent picture has no feet to stand on.
            if (right < 0)
                return null;

            int cropWidth = right - left + 1;
            int cropHeight = bottom - top + 1;

            var cropped = new SKColor[cropWidth * cropHeight];

            for (int y = 0; y < cropHeight; y++)
            {
                for (int x = 0; x < cropWidth; x++)
                    cropped[y * cropWidth + x] = pixels[(top + y) * width + left + x];
            }

            using var trimmed = new SKBitmap(cropWidth, cropHeight);
            trimmed.Pixels = cropped;

            using var stream = new MemoryStream(ImageOps.EncodePng(trimmed));

            return new Bitmap(stream);
        }
    }
}
