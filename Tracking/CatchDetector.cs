using System;
using System.Drawing;
using Tesseract;
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

        public static Rectangle GetBattleMessageRegion(
            Rectangle battleBounds,
            Size screenshotSize)
        {
            // The catch result text sits in the lower dialogue box.
            // Keep this deliberately tight so Tesseract sees primarily
            // the sentence rather than Kadabra / HP bars / battlefield.

            int x =
                battleBounds.X +
                (int)(battleBounds.Width * 0.035);

            int y =
                battleBounds.Y +
                (int)(battleBounds.Height * 0.86);

            int width =
                (int)(battleBounds.Width * 0.62);

            int height =
                (int)(battleBounds.Height * 0.11);

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