using SkiaSharp;

namespace Foot_Tracker.Tracking
{
    /// <summary>
    /// Draws colored boxes on a screenshot showing exactly where each OCR detector
    /// is reading from - battle title (wild encounter/boss name), the message log
    /// (catch results), and the rare-encounter popup (shiny/form notifications).
    /// Used by "Report a Problem" so a report shows precisely what the OCR saw,
    /// not just a plain screenshot - lets a region landing in the wrong place (a
    /// real bug) be told apart from correct-region-but-misread-text at a glance,
    /// instead of needing to compare logged coordinates by hand.
    ///
    /// Deliberately reuses the real detectors' own GetXxxRegion methods rather
    /// than recalculating region positions here - guarantees the boxes always
    /// match what real detection actually reads, with no risk of silently
    /// drifting out of sync if those methods are tuned differently later.
    /// </summary>
    public static class DebugRegionOverlay
    {
        private static readonly SKColor TitleRegionColor = new(0xFF, 0x40, 0x40);       // red
        private static readonly SKColor MessageRegionColor = new(0x40, 0xC8, 0xFF);     // cyan
        private static readonly SKColor RareEncounterRegionColor = new(0xFF, 0xE0, 0x30); // yellow

        private const int BoxThickness = 3;

        /// <summary>Returns a copy of the screenshot with region boxes drawn on it,
        /// or the original screenshot unchanged if no battle is currently visible
        /// (nothing to annotate - not an error).</summary>
        public static SKBitmap DrawDetectionRegions(SKBitmap screenshot)
        {
            if (!BattleWindowLocator.TryLocate(screenshot, out SKRectI battleBounds))
                return screenshot.Copy();

            SKBitmap annotated = screenshot.Copy();

            using var canvas = new SKCanvas(annotated);
            var screenshotSize = new SKSizeI(screenshot.Width, screenshot.Height);

            DrawBox(canvas, BattleWindowLocator.GetBattleTitleRegion(battleBounds), TitleRegionColor);
            DrawBox(canvas, CatchDetector.GetBattleMessageRegion(battleBounds, screenshotSize), MessageRegionColor);
            DrawBox(canvas, RareEncounterDetector.GetRareEncounterRegion(battleBounds, screenshotSize), RareEncounterRegionColor);

            return annotated;
        }

        private static void DrawBox(SKCanvas canvas, SKRectI region, SKColor color)
        {
            if (region.Width <= 0 || region.Height <= 0)
                return;

            using var paint = new SKPaint
            {
                Color = color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = BoxThickness,
                IsAntialias = true
            };

            canvas.DrawRect(SKRect.Create(region.Left, region.Top, region.Width, region.Height), paint);
        }
    }
}