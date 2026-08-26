using System;
using SkiaSharp;
using Serilog;
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

            // Diagnostic logging - see BossBattleDetector.cs for why this exact
            // pattern (log region + battle bounds + raw OCR text, only when it
            // changes) is worth having here too: different users' GUI scale or
            // resolution may mean this region's fixed-percentage crop (tuned
            // against one specific screenshot) doesn't land in the right place on
            // every setup, even though it's confirmed working for several users
            // already. If a report ever comes in about a missed shiny/form
            // detection, this is what will show whether the crop position itself
            // is the problem (garbled/empty OCR text) or something else
            // (readable text that just doesn't match the expected phrases).
            LogOcrAttemptIfChanged(text, region, battleBounds, screenshot.Width, screenshot.Height);

            if (string.IsNullOrWhiteSpace(text))
                return RareEncounterType.None;

            RareEncounterType result = Classify(text);

            if (result != RareEncounterType.None)
            {
                Log.Information(
                    "RareEncounterDetector matched {Result} from OCR text '{OcrText}'",
                    result, text);
            }

            return result;
        }

        // Only logs when the OCR result actually changes, to avoid spamming the
        // log with identical lines every scan tick.
        private static string? lastLoggedText;

        private static void LogOcrAttemptIfChanged(
            string text, SKRectI region, SKRectI battleBounds, int screenshotWidth, int screenshotHeight)
        {
            string normalized = string.IsNullOrWhiteSpace(text) ? "(empty)" : text.Trim();

            if (normalized == lastLoggedText)
                return;

            lastLoggedText = normalized;

            Log.Information(
                "RareEncounterDetector OCR attempt: text='{OcrText}', region=({RX},{RY},{RW}x{RH}), " +
                "battleBounds=({BX},{BY},{BW}x{BH}), screenshot={SW}x{SH}",
                normalized,
                region.Left, region.Top, region.Width, region.Height,
                battleBounds.Left, battleBounds.Top, battleBounds.Width, battleBounds.Height,
                screenshotWidth, screenshotHeight);
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

        public static SKRectI
            GetRareEncounterRegion(
                SKRectI battleBounds,
                SKSizeI screenshotSize)
        {
            // Third recalibration. The second recalibration (2% left, 26% top,
            // 88% width, 41% height) targeted the game's entire bordered
            // "battlefield" section - but a real "Rare Encounter!" popup log from
            // a user, plus a screenshot of the genuine popup with debug-overlay
            // boxes drawn on it (see DebugRegionOverlay.cs), showed that popup is
            // actually a much smaller, centered dialog that only covers roughly
            // the middle half of that section - not the whole thing.
            //
            // That log showed COMPLETELY garbled OCR text from this region on
            // every single encounter checked, rare or not - not just misreads on
            // the rare ones. That is the signature of a region that is mostly
            // battle-scene artwork (grass/trees/sprites): for an ordinary
            // encounter there is no popup at all, so 88%-of-section-width of pure
            // scene art, thresholded to black-and-white, reliably produces
            // line-noise garbage. For a genuine rare encounter, the popup only
            // filled a fraction of that same oversized region, so the rest of the
            // captured scene art still corrupted the OCR pass enough that the
            // real "You encountered a rare form Pokemon!" text was never read
            // cleanly either - consistent with the user's report that a real
            // event-form encounter still went undetected.
            //
            // Recalibrated by measuring the actual popup's pixel bounds directly
            // (locating its dark dialog panel's edges, and the white message text
            // inside it, in the reference screenshot) and converting to a
            // percentage of battleBounds using the same red debug-box math
            // BattleWindowLocator itself uses - cross-checked against
            // CatchDetector's own message-region percentages, which matched the
            // reference screenshot's message box to within a pixel or two,
            // confirming the conversion is sound. Measured popup: roughly 27%-68%
            // of battleBounds width, 40%-73% of its height. The percentages below
            // add a several-percentage-point margin around that measurement -
            // generous enough to tolerate some GUI-scale variance and either
            // "Shiny"/"rare form" wording, while still being roughly HALF the
            // width the previous recalibration used, so a lot less scene artwork
            // gets pulled into the OCR pass.
            //
            // Still a percentage-of-battleBounds approximation calibrated from a
            // single user's reference screenshots, not actual pixel detection of
            // the popup's own border - see MIGRATION_GUIDE.md §19 and
            // DebugRegionOverlay.cs for how to verify this against the next real
            // occurrence.

            int x =
                battleBounds.Left +
                (int)(battleBounds.Width * 0.19);

            int y =
                battleBounds.Top +
                (int)(battleBounds.Height * 0.34);

            int width =
                (int)(battleBounds.Width * 0.53);

            int height =
                (int)(battleBounds.Height * 0.42);

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