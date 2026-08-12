using System;
using System.Drawing;
using Tesseract;

namespace Foot_Tracker.Tracking
{
    public enum CatchResult
    {
        None,
        Success,
        Failed,
        RunAway
    }

    public static class CatchDetector
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
                PageSegMode.SingleLine;
        }

        public static CatchResult Detect(
            Bitmap screenshot,
            Rectangle battleBounds)
        {
            Rectangle region =
                GetBattleMessageRegion(
                    battleBounds,
                    screenshot.Size
                );

            if (region.Width <= 0 ||
                region.Height <= 0)
            {
                return CatchResult.None;
            }

            using Bitmap crop =
                ScreenCapture.CropImage(
                    screenshot,
                    region
                );

            using Bitmap prepared =
                PrepareForOcr(crop);

            string rawText =
                ReadText(prepared);

            if (string.IsNullOrWhiteSpace(rawText))
                return CatchResult.None;

            return Classify(rawText);
        }

        private static CatchResult Classify(
            string text)
        {
            string normalized =
                text
                    .Replace("\r", " ")
                    .Replace("\n", " ")
                    .Replace("|", " ")
                    .Trim();

            // Successful catch ends the battle.
            if (normalized.Contains(
                    "you caught",
                    StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(
                    "success",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CatchResult.Success;
            }

            // Failed ball does NOT end the battle.
            if (normalized.Contains(
                    "broke free",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CatchResult.Failed;
            }

            // Running successfully ends the battle.
            if (normalized.Contains(
                    "run away",
                    StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(
                    "ran away",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CatchResult.RunAway;
            }

            return CatchResult.None;
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

        private static Rectangle GetBattleMessageRegion(
            Rectangle battleBounds,
            Size screenshotSize)
        {
            // Bottom-left text box of PRO's battle window.
            int x =
                battleBounds.X +
                (int)(battleBounds.Width * 0.02);

            int y =
                battleBounds.Y +
                (int)(battleBounds.Height * 0.76);

            int width =
                (int)(battleBounds.Width * 0.72);

            int height =
                (int)(battleBounds.Height * 0.20);

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