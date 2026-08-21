using System;
using SkiaSharp;
using TesseractOCR;
using TesseractOCR.Enums;
using System.Linq;

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

        public static CatchResult Detect(
            SKBitmap screenshot,
            SKRectI battleBounds)
        {
            SKRectI region =
                GetBattleMessageRegion(
                    battleBounds,
                    new SKSizeI(screenshot.Width, screenshot.Height)
                );

            if (region.Width <= 0 ||
                region.Height <= 0)
            {
                return CatchResult.None;
            }

            using SKBitmap crop =
                ImageOps.Crop(
                    screenshot,
                    region
                );

            using SKBitmap prepared =
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

            // ============================================================
            // FAILED CATCH
            // ============================================================

            // Exact result first.
            if (normalized.Contains(
                    "broke free",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CatchResult.Failed;
            }

            // Remove punctuation/spaces so OCR fragmentation matters less.
            string compact =
                new string(
                    normalized
                        .ToLowerInvariant()
                        .Where(char.IsLetter)
                        .ToArray()
                );

            // Common OCR variations.
            if (compact.Contains("brokefree") ||
                compact.Contains("brokefre") ||
                compact.Contains("brokfree") ||
                compact.Contains("brokefre") ||
                compact.Contains("broxefree") ||
                compact.Contains("brokeftee"))
            {
                return CatchResult.Failed;
            }

            // Final tolerant comparison against "brokefree".
            if (ContainsFuzzyText(
                    compact,
                    "brokefree",
                    2))
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

        private static bool ContainsFuzzyText(
    string source,
    string target,
    int maximumDistance)
        {
            if (string.IsNullOrWhiteSpace(source) ||
                string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            if (source.Contains(
                    target,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            int minimumLength =
                Math.Max(
                    1,
                    target.Length - maximumDistance
                );

            int maximumLength =
                Math.Min(
                    source.Length,
                    target.Length + maximumDistance
                );

            for (int length = minimumLength;
                 length <= maximumLength;
                 length++)
            {
                for (int start = 0;
                     start + length <= source.Length;
                     start++)
                {
                    string section =
                        source.Substring(
                            start,
                            length
                        );

                    if (LevenshteinDistance(
                            section,
                            target) <= maximumDistance)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static int LevenshteinDistance(
    string a,
    string b)
        {
            int[,] distance =
                new int[
                    a.Length + 1,
                    b.Length + 1
                ];

            for (int i = 0;
                 i <= a.Length;
                 i++)
            {
                distance[i, 0] = i;
            }

            for (int j = 0;
                 j <= b.Length;
                 j++)
            {
                distance[0, j] = j;
            }

            for (int i = 1;
                 i <= a.Length;
                 i++)
            {
                for (int j = 1;
                     j <= b.Length;
                     j++)
                {
                    int cost =
                        a[i - 1] == b[j - 1]
                            ? 0
                            : 1;

                    distance[i, j] =
                        Math.Min(
                            Math.Min(
                                distance[i - 1, j] + 1,
                                distance[i, j - 1] + 1
                            ),
                            distance[i - 1, j - 1] + cost
                        );
                }
            }

            return distance[
                a.Length,
                b.Length
            ];
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

            ImageOps.ThresholdToBlackAndWhite(resized, 150);

            return resized;
        }

        public static SKRectI GetBattleMessageRegion(
            SKRectI battleBounds,
            SKSizeI screenshotSize)
        {
            // The catch result text sits in the lower dialogue box.
            // Keep this deliberately tight so Tesseract sees primarily
            // the sentence rather than Kadabra / HP bars / battlefield.

            int x =
                battleBounds.Left +
                (int)(battleBounds.Width * 0.035);

            int y =
                battleBounds.Top +
                (int)(battleBounds.Height * 0.86);

            int width =
                (int)(battleBounds.Width * 0.62);

            int height =
                (int)(battleBounds.Height * 0.11);

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