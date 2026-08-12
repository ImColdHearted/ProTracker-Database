using Foot_Tracker.Services;
using System;
using System.Drawing;
using System.Linq;
using Tesseract;

namespace Foot_Tracker.Tracking
{
    public static class EncounterDetector
    {
        private static readonly object ocrLock = new();

        private static TesseractEngine? engine;

        public static void Initialize()
        {
            if (engine != null)
                return;

            string tessDataPath = Path.Combine(
                AppContext.BaseDirectory,
                "tessdata"
            );

            if (!Directory.Exists(tessDataPath))
            {
                throw new DirectoryNotFoundException(
                    $"Tesseract data folder not found:\n{tessDataPath}"
                );
            }

            engine = new TesseractEngine(
                tessDataPath,
                "eng",
                EngineMode.Default
            );

            // We are only reading one short line.
            engine.DefaultPageSegMode =
                PageSegMode.SingleLine;
        }

        public static bool TryDetectEncounter(
            Bitmap screenshot,
            out string pokemonName)
        {
            pokemonName = string.Empty;

            if (screenshot == null)
                return false;

            if (!BattleWindowLocator.TryLocate(
                    screenshot,
                    out Rectangle battleBounds))
            {
                return false;
            }

            Rectangle titleRegion =
                ScreenCapture.GetBattleTitleRegion(
                    battleBounds
                );

            using Bitmap titleCrop =
                ScreenCapture.CropImage(
                    screenshot,
                    titleRegion
                );

            using Bitmap prepared =
                PrepareForOcr(titleCrop);

            if (!ContainsEnoughBrightPixels(titleCrop))
            {
                return false;
            }

            string rawText =
                ReadText(prepared);

            if (string.IsNullOrWhiteSpace(rawText))
                return false;

            string cleaned =
                NormalizeOcrText(rawText);

            return TryMatchPokemon(
                cleaned,
                out pokemonName
            );
        }

        private static string ReadText(
            Bitmap bitmap)
        {
            Initialize();

            lock (ocrLock)
            {
                using Pix pix =
                    PixConverter.ToPix(bitmap);

                using Page page =
                    engine!.Process(
                        pix,
                        PageSegMode.SingleLine
                    );

                return page.GetText() ?? string.Empty;
            }
        }

        private static bool ContainsEnoughBrightPixels(
    Bitmap source)
        {
            int brightPixels = 0;
            int checkedPixels = 0;

            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    Color color = source.GetPixel(x, y);

                    int brightness =
                        (color.R + color.G + color.B) / 3;

                    checkedPixels++;

                    // Wild Pokémon title text is nearly white.
                    if (brightness >= 210)
                    {
                        brightPixels++;
                    }
                }
            }

            if (checkedPixels == 0)
                return false;

            double brightRatio =
                brightPixels / (double)checkedPixels;

            return brightRatio >= 0.01;
        }

        private static Bitmap PrepareForOcr(
            Bitmap source)
        {
            // Upscale because the title text is fairly small.
            const int scale = 3;

            Bitmap resized =
                new Bitmap(
                    source.Width * scale,
                    source.Height * scale
                );

            using (Graphics graphics =
                   Graphics.FromImage(resized))
            {
                graphics.InterpolationMode =
                    System.Drawing.Drawing2D
                        .InterpolationMode.NearestNeighbor;

                graphics.PixelOffsetMode =
                    System.Drawing.Drawing2D
                        .PixelOffsetMode.HighQuality;

                graphics.DrawImage(
                    source,
                    0,
                    0,
                    resized.Width,
                    resized.Height
                );
            }

            // Convert to high-contrast black/white.
            for (int y = 0; y < resized.Height; y++)
            {
                for (int x = 0; x < resized.Width; x++)
                {
                    Color color =
                        resized.GetPixel(x, y);

                    int brightness =
                        (color.R +
                         color.G +
                         color.B) / 3;

                    // The battle-title text is bright.
                    Color output =
                        brightness >= 150
                            ? Color.Black
                            : Color.White;

                    resized.SetPixel(
                        x,
                        y,
                        output
                    );
                }
            }

            return resized;
        }

        private static string NormalizeOcrText(
            string text)
        {
            return text
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("|", " ")
                .Trim();
        }

        private static bool TryMatchPokemon(
     string ocrText,
     out string pokemonName)
        {
            pokemonName = string.Empty;

            if (string.IsNullOrWhiteSpace(ocrText))
                return false;

            // =====================================================
            // 1. CHECK ALTERNATE / REGIONAL FORMS FIRST
            // =====================================================
            //
            // This MUST happen before normal species.
            //
            // Example:
            // "Wild Voltorb-Hisui"
            //
            // If we checked normal species first,
            // "Voltorb" would match before "Voltorb-Hisui".
            // =====================================================

            foreach (var form in
                     PokemonSpriteService.AllForms
                         .OrderByDescending(
                             f => f.Name.Length))
            {
                // Canonical form name
                if (ocrText.Contains(
                        form.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    pokemonName = form.Name;
                    return true;
                }

                // OCR aliases
                foreach (string alias in form.OcrAliases
                             .OrderByDescending(a => a.Length))
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        continue;

                    // Don't allow the base species alias to steal
                    // a normal encounter.
                    if (alias.Equals(
                            form.SpeciesName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (ocrText.Contains(
                            alias,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        pokemonName = form.Name;
                        return true;
                    }
                }
            }

            // =====================================================
            // 2. NORMAL SPECIES
            // =====================================================

            foreach (var pokemon in
                     PokemonSpriteService.AllPokemon
                         .OrderByDescending(
                             p => p.Name.Length))
            {
                if (ocrText.Contains(
                        pokemon.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    pokemonName = pokemon.Name;
                    return true;
                }
            }

            // =====================================================
            // 3. NORMAL SPECIES OCR ALIASES
            // =====================================================

            foreach (var pokemon in
                     PokemonSpriteService.AllPokemon)
            {
                foreach (string alias in pokemon.OcrAliases
                             .OrderByDescending(a => a.Length))
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        continue;

                    if (ocrText.Contains(
                            alias,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        pokemonName = pokemon.Name;
                        return true;
                    }
                }
            }

            return false;
        }
        public static Rectangle GetBattleTitleRegion(
    Rectangle battleBounds)
        {
            int x =
                battleBounds.X +
                (int)(battleBounds.Width * 0.48);

            int y =
                battleBounds.Y;

            int width =
                battleBounds.Right - x;

            int height = 45;

            return new Rectangle(
                x,
                y,
                width,
                height
            );
        }
    }
}