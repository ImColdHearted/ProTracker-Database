using System;
using System.Drawing;
using Tesseract;

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

            engine.DefaultPageSegMode =
                PageSegMode.SingleBlock;
        }

        public static RareEncounterType Detect(
            Bitmap screenshot,
            Rectangle battleBounds)
        {
            if (screenshot == null)
                return RareEncounterType.None;

            Rectangle region =
                GetRareEncounterRegion(
                    battleBounds,
                    screenshot.Size
                );

            if (region.Width <= 0 ||
                region.Height <= 0)
            {
                return RareEncounterType.None;
            }

            using Bitmap crop =
                ScreenCapture.CropImage(
                    screenshot,
                    region
                );

            using Bitmap prepared =
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
                        PageSegMode.SingleBlock
                    );

                return page.GetText() ??
                       string.Empty;
            }
        }

        private static Bitmap PrepareForOcr(
            Bitmap source)
        {
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

                graphics.DrawImage(
                    source,
                    0,
                    0,
                    resized.Width,
                    resized.Height
                );
            }

            for (int y = 0;
                 y < resized.Height;
                 y++)
            {
                for (int x = 0;
                     x < resized.Width;
                     x++)
                {
                    Color color =
                        resized.GetPixel(x, y);

                    int brightness =
                        (color.R +
                         color.G +
                         color.B) / 3;

                    Color output =
                        brightness >= 145
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

        private static Rectangle
            GetRareEncounterRegion(
                Rectangle battleBounds,
                Size screenshotSize)
        {
            // Approximate popup position based on
            // the screenshot you provided.
            //
            // Center-left section of the battle window.

            int x =
                battleBounds.X +
                (int)(battleBounds.Width * 0.25);

            int y =
                battleBounds.Y +
                (int)(battleBounds.Height * 0.22);

            int width =
                (int)(battleBounds.Width * 0.50);

            int height =
                (int)(battleBounds.Height * 0.35);

            Rectangle region =
                new Rectangle(
                    x,
                    y,
                    width,
                    height
                );

            region.Intersect(
                new Rectangle(
                    0,
                    0,
                    screenshotSize.Width,
                    screenshotSize.Height
                )
            );

            return region;
        }
    }
}