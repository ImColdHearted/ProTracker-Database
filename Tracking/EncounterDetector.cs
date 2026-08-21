using Foot_Tracker.Services;
using System;
using System.Linq;
using SkiaSharp;
using TesseractOCR;
using TesseractOCR.Enums;

namespace Foot_Tracker.Tracking
{
    public static class EncounterDetector
    {
        private static readonly object ocrLock = new();

        private static Engine? engine;

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

            engine = new Engine(
                tessDataPath,
                Language.English,
                EngineMode.Default
            );
        }

        public static bool TryDetectEncounter(
            SKBitmap screenshot,
            out string pokemonName)
        {
            pokemonName = string.Empty;

            if (screenshot == null)
                return false;

            if (!BattleWindowLocator.TryLocate(
                    screenshot,
                    out SKRectI battleBounds))
            {
                return false;
            }

            SKRectI titleRegion =
                BattleWindowLocator.GetBattleTitleRegion(
                    battleBounds
                );

            using SKBitmap titleCrop =
                ImageOps.Crop(
                    screenshot,
                    titleRegion
                );

            using SKBitmap prepared =
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
            SKBitmap bitmap)
        {
            Initialize();

            lock (ocrLock)
            {
                byte[] pngBytes =
                    ImageOps.EncodePng(bitmap);

                using TesseractOCR.Pix.Image image =
                    TesseractOCR.Pix.Image.LoadFromMemory(pngBytes);

                using TesseractOCR.Page page =
                    engine!.Process(
                        image,
                        PageSegMode.SingleLine
                    );

                return page.Text ?? string.Empty;
            }
        }

        private static bool ContainsEnoughBrightPixels(
    SKBitmap source)
        {
            SKColor[] pixels = source.Pixels;

            int brightPixels = 0;
            int checkedPixels = pixels.Length;

            for (int i = 0; i < pixels.Length; i++)
            {
                SKColor color = pixels[i];

                int brightness =
                    (color.Red + color.Green + color.Blue) / 3;

                // Wild Pokémon title text is nearly white.
                if (brightness >= 210)
                {
                    brightPixels++;
                }
            }

            if (checkedPixels == 0)
                return false;

            double brightRatio =
                brightPixels / (double)checkedPixels;

            return brightRatio >= 0.01;
        }

        private static SKBitmap PrepareForOcr(
            SKBitmap source)
        {
            // Upscale because the title text is fairly small.
            const int scale = 3;

            SKBitmap resized =
                ImageOps.Resize(
                    source,
                    source.Width * scale,
                    source.Height * scale
                );

            // Convert to high-contrast black/white.
            // The battle-title text is bright.
            ImageOps.ThresholdToBlackAndWhite(resized, 150);

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

        // NOTE: kept for parity with the original (which also had this method
        // unused/dead - live callers go through BattleWindowLocator.GetBattleTitleRegion).
        public static SKRectI GetBattleTitleRegion(
            SKRectI battleBounds)
        {
            int titleHeight =
                (int)Math.Round(
                    battleBounds.Height * 0.09
                );

            titleHeight =
                Math.Clamp(
                    titleHeight,
                    45,
                    75
                );

            return ImageOps.MakeRect(
                battleBounds.Left,
                battleBounds.Top,
                battleBounds.Width,
                titleHeight
            );
        }
    }
}