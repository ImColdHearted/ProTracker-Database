using SkiaSharp;

namespace Foot_Tracker.Tracking
{
    /// <summary>
    /// Draws colored boxes on a screenshot showing exactly where each OCR/pixel-
    /// color detector is reading from - battle title (wild encounter/boss name),
    /// the wild Pokemon's level tag, its gender icon, the message log (catch
    /// results), and the rare-encounter popup (shiny/form notifications). Used by
    /// "Report a Problem" so a report shows precisely what the detectors saw, not
    /// just a plain screenshot - lets a region landing in the wrong place (a real
    /// bug) be told apart from correct-region-but-misread-text at a glance,
    /// instead of needing to compare logged coordinates by hand. Level and Gender
    /// were added after a report that Level was going unread almost every time
    /// while Gender read fine on the same encounters - a box for both makes the
    /// next such report show directly whether either region has drifted out of
    /// place, rather than only the raw OCR/pixel-match numbers each detector
    /// already logs on its own.
    ///
    /// Deliberately reuses the real detectors' own GetXxxRegion methods rather
    /// than recalculating region positions here - guarantees the boxes always
    /// match what real detection actually reads, with no risk of silently
    /// drifting out of sync if those methods are tuned differently later.
    /// </summary>
    public static class DebugRegionOverlay
    {
        private static readonly SKColor TitleRegionColor = new(0xFF, 0x40, 0x40);       // red
        private static readonly SKColor LevelRegionColor = new(0x30, 0xE0, 0x50);       // green
        private static readonly SKColor GenderRegionColor = new(0xE0, 0x30, 0xC0);      // magenta
        private static readonly SKColor MessageRegionColor = new(0x40, 0xC8, 0xFF);     // cyan
        private static readonly SKColor RareEncounterRegionColor = new(0xFF, 0xE0, 0x30); // yellow

        // §206: the popup as actually FOUND on this frame, when it is up -
        // drawn in a different colour from the fallback crop above so a
        // report shows at a glance which of the two the detector used.
        private static readonly SKColor LocatedPopupColor = new(0x30, 0xFF, 0xC0);        // teal

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
            DrawBox(canvas, LevelDetector.GetLevelRegion(battleBounds), LevelRegionColor);
            DrawBox(canvas, GenderDetector.GetGenderRegion(battleBounds), GenderRegionColor);
            DrawBox(canvas, CatchDetector.GetBattleMessageRegion(battleBounds, screenshotSize), MessageRegionColor);
            DrawBox(canvas, RareEncounterDetector.GetRareEncounterRegion(battleBounds, screenshotSize), RareEncounterRegionColor);

            // §206: only present when a popup is actually on screen, which is
            // exactly when it matters - if a report shows the yellow box and
            // no teal one while a popup is plainly visible, the locator is
            // what needs looking at rather than the crop percentages.
            if (RarePopupLocator.TryLocate(screenshot, out RarePopupBox popup))
            {
                DrawBox(canvas, popup.Header, LocatedPopupColor);
                DrawBox(canvas, popup.Body, LocatedPopupColor);
            }

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