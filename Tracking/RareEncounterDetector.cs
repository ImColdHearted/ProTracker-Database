using System;
using SkiaSharp;
using TesseractOCR;
using TesseractOCR.Enums;

namespace Foot_Tracker.Tracking
{
    public enum RareEncounterType
    {
        None,
        Shiny,
        Form
    }

    public static class RareEncounterDetector
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

        public static RareEncounterType Detect(
            SKBitmap screenshot,
            SKRectI battleBounds)
        {
            if (screenshot == null)
                return RareEncounterType.None;

            SKRectI region =
                GetRareEncounterRegion(
                    battleBounds,
                    new SKSizeI(screenshot.Width, screenshot.Height)
                );

            if (region.Width <= 0 ||
                region.Height <= 0)
            {
                return RareEncounterType.None;
            }

            using SKBitmap crop =
                ImageOps.Crop(
                    screenshot,
                    region
                );

            using SKBitmap prepared =
                PrepareForOcr(crop);

            string text =
                ReadText(prepared);

            if (string.IsNullOrWhiteSpace(text))
                return RareEncounterType.None;

            return Classify(text);
        }

        private static RareEncounterType Classify(
            string text)
        {
            string normalized =
                text
                    .Replace("\r", " ")
                    .Replace("\n", " ")
                    .Replace("|", " ")
                    .Trim();

            // SHINY:
            // "You encountered a Shiny Pokemon!"
            if (normalized.Contains(
                    "Shiny Pokemon",
                    StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(
                    "Shiny Pokémon",
                    StringComparison.OrdinalIgnoreCase))
            {
                return RareEncounterType.Shiny;
            }

            // EVENT / RARE FORM:
            // "You encountered a rare form Pokemon!"
            if (normalized.Contains(
                    "rare form Pokemon",
                    StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(
                    "rare form Pokémon",
                    StringComparison.OrdinalIgnoreCase))
            {
                return RareEncounterType.Form;
            }

            return RareEncounterType.None;
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
                        PageSegMode.SingleBlock
                    );

                return page.Text ??
                       string.Empty;
            }
        }

        private static SKBitmap PrepareForOcr(
            SKBitmap source)
        {
            const int scale = 3;

            SKBitmap resized =
                ImageOps.Resize(
                    source,
                    source.Width * scale,
                    source.Height * scale
                );

            ImageOps.ThresholdToBlackAndWhite(resized, 145);

            return resized;
        }

        private static SKRectI
            GetRareEncounterRegion(
                SKRectI battleBounds,
                SKSizeI screenshotSize)
        {
            // Approximate popup position based on
            // the screenshot you provided.
            //
            // Center-left section of the battle window.

            int x =
                battleBounds.Left +
                (int)(battleBounds.Width * 0.25);

            int y =
                battleBounds.Top +
                (int)(battleBounds.Height * 0.22);

            int width =
                (int)(battleBounds.Width * 0.50);

            int height =
                (int)(battleBounds.Height * 0.35);

            SKRectI region =
                ImageOps.MakeRect(
                    x,
                    y,
                    width,
                    height
                );

            return ImageOps.Intersect(
                region,
                ImageOps.MakeRect(
                    0,
                    0,
                    screenshotSize.Width,
                    screenshotSize.Height
                )
            );
        }
    }
}