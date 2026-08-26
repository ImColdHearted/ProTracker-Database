using System;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;
using Serilog;
using TesseractOCR;
using TesseractOCR.Enums;
using Foot_Tracker.Services;

namespace Foot_Tracker.Tracking
{
    /// <summary>
    /// Detects PVP battles (as opposed to wild encounters, boss battles, or NPC
    /// trainer battles) from the same "&lt;LocalPlayer&gt; VS. &lt;Opponent&gt;"
    /// title bar the other detectors read - see Tracking/PvpTracker.cs for the
    /// polling loop that calls this.
    ///
    /// Classification used to work purely by elimination, same idea as
    /// BossBattleDetector.cs: not a wild encounter, not a recognized boss ->
    /// whatever's left of the title after "VS" must be a player's username. That
    /// turned out to be wrong - a real report showed plain road NPCs ("VS.
    /// Trainer", "VS. Master Uno") both getting logged as PVP opponents, because a
    /// named NPC trainer battle renders an identical-looking title. Two things
    /// were added to handle this: an exact-match reject for the literal word
    /// "Trainer" (PRO's generic, unnamed road-trainer placeholder - see the
    /// TryDetectPvp check below), and HasPvpIndicatorBar, a pixel-color check for
    /// a bright green bar the user found only shows up in a real PVP match's UI,
    /// for every other case where the NPC has an actual name and OCR alone truly
    /// can't tell the two apart. See HasPvpIndicatorBar's own doc comment for how
    /// confident that second check actually is - it's a first-pass calibration
    /// from one reference screenshot, not a proven-across-many-captures value the
    /// way BattleWindowLocator's dimensions are.
    /// </summary>
    public static class PvpBattleDetector
    {
        private static readonly object ocrLock = new();
        private static Engine? engine;

        public static void Initialize()
        {
            if (engine != null)
                return;

            string tessDataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");

            if (!Directory.Exists(tessDataPath))
            {
                throw new DirectoryNotFoundException(
                    $"Tesseract data folder not found:\n{tessDataPath}"
                );
            }

            engine = new Engine(tessDataPath, Language.English, EngineMode.Default);
        }

        /// <summary>
        /// Checks the battle title for a PVP matchup. Returns false for wild
        /// encounters, for anything matching a known boss name (BossCooldownTracker
        /// already owns those), and for unreadable/too-short OCR text.
        ///
        /// <paramref name="confirmedNotPvp"/> is set true only when the OCR text
        /// unambiguously identified this as NOT a PVP battle (a wild encounter or a
        /// recognized boss) - mirrors BossBattleDetector's confirmedWild signal, so
        /// PvpTracker can stop retrying immediately instead of spending its whole
        /// detection-attempt budget on a battle that was never going to be PVP.
        /// Left false for garbled/too-short OCR so the caller keeps retrying.
        /// </summary>
        public static bool TryDetectPvp(
            SKBitmap screenshot,
            SKRectI battleBounds,
            out string? opponentName,
            out bool confirmedNotPvp)
        {
            opponentName = null;
            confirmedNotPvp = false;

            SKRectI titleRegion = BattleWindowLocator.GetBattleTitleRegion(battleBounds);

            using SKBitmap titleCrop = ImageOps.Crop(screenshot, titleRegion);
            using SKBitmap prepared = PrepareForOcr(titleCrop);

            string rawText = ReadText(prepared, PageSegMode.SingleLine);

            if (string.IsNullOrWhiteSpace(rawText))
            {
                LogOcrAttemptIfChanged("(empty)", titleRegion, battleBounds);
                return false;
            }

            string normalized = rawText
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            LogOcrAttemptIfChanged(normalized, titleRegion, battleBounds);

            // Anchor on a whole-word "vs" (not a bare substring match) so an
            // embedded "vs" inside a player's own name/nickname (e.g. "Silversword")
            // can't be mistaken for the title's real "PlayerA VS. PlayerB" separator.
            Match vsMatch = Regex.Match(normalized, @"\bvs\b", RegexOptions.IgnoreCase);

            if (!vsMatch.Success)
                return false; // Not a battle title yet (or none at all).

            if (normalized.Contains("wild", StringComparison.OrdinalIgnoreCase))
            {
                confirmedNotPvp = true; // Wild encounter - EncounterTracker's job.
                return false;
            }

            if (MatchesKnownBoss(normalized))
            {
                confirmedNotPvp = true; // A boss - BossCooldownTracker's job.
                return false;
            }

            string candidate = normalized[(vsMatch.Index + vsMatch.Length)..].Trim();

            // OCR noise cleanup: PRO's title renders "VS." (with punctuation) and
            // OCR sometimes reads the period as a comma/colon, or leaves stray
            // whitespace - strip whatever separator glyph is still stuck to the front.
            candidate = candidate.TrimStart('.', ',', ':', ';', ' ');

            // A real PRO username is never this short - guards against OCR reading
            // only a stray character or two off a title bar that hasn't finished
            // rendering yet (same intro-animation garbling BossBattleDetector deals
            // with). Left as "inconclusive" (confirmedNotPvp stays false) rather
            // than a hard rejection, so the caller keeps retrying.
            if (candidate.Length < 3)
                return false;

            // Confirmed via a real report: PRO's generic, unnamed road-trainer NPCs
            // render a literal "VS. Trainer" title, textually indistinguishable
            // from a real PVP title. No real PRO account is registered under the
            // bare username "Trainer", so this is a safe, zero-risk exclusion -
            // unlike the indicator-bar check below, there's no rendering-timing
            // question to hedge on here, so this can reject immediately instead of
            // leaving the caller to keep retrying something that will never change.
            if (candidate.Equals("Trainer", StringComparison.OrdinalIgnoreCase))
            {
                confirmedNotPvp = true;

                Log.Information(
                    "PvpBattleDetector rejected literal NPC placeholder name 'Trainer' from OCR text '{OcrText}'",
                    normalized);

                return false;
            }

            // A title reading "<You> VS. <Name>" that isn't wild, isn't a known
            // boss, and isn't the literal word "Trainer" still isn't necessarily
            // PVP - the same report that surfaced the "Trainer" case also showed a
            // NAMED NPC ("Master Uno") logged as a PVP opponent, and OCR text alone
            // can't tell a named NPC trainer from a real player's username. The
            // indicator bar is the one signal available so far that tells the two
            // apart - see HasPvpIndicatorBar. Left as inconclusive rather than a
            // hard rejection: if the bar simply hasn't rendered yet this tick on a
            // genuine PVP battle, the caller keeps retrying within its own attempt
            // budget instead of this being a one-shot miss.
            if (!HasPvpIndicatorBar(screenshot))
                return false;

            opponentName = candidate;

            Log.Information(
                "PvpBattleDetector detected PVP opponent '{OpponentName}' from OCR text '{OcrText}'",
                opponentName, normalized);

            return true;
        }

        // Sampled from a real PVP match screenshot the user provided: that battle's
        // UI shows a persistent bright-green bar (an ELO/points readout, going by
        // its "177" label) in the top-right corner - a widget that was completely
        // absent from both false-positive NPC screenshots checked against this
        // (zero matching pixels in this same region in either one; the only stray
        // green pixels found anywhere in those screenshots were elsewhere on
        // screen entirely - see PvpIndicatorMinMatchFraction's remarks on why the
        // region has to stay this tight). Sampled as a fraction of the FULL
        // screenshot, not battleBounds, since - like RouteDetector's corner HUD -
        // this looks like persistent on-screen chrome rather than something drawn
        // inside the battle window itself.
        //
        // Open question this hasn't been tested against: the reference screenshot
        // was a different resolution/aspect ratio than this app's usual capture
        // and showed a visually different battle layout (a full stadium-arena
        // view, not the floating box over the map that wild/boss/NPC battles use)
        // - it isn't confirmed whether BattleWindowLocator even successfully finds
        // battleBounds for that layout at all. If a real ranked/tournament PVP
        // match stops showing up as "PVP battle detected" in the log entirely
        // (not even an OCR attempt logged), that's a BattleWindowLocator problem,
        // not this check - worth a fresh report either way.
        private const float PvpIndicatorRegionX = 0.70f;
        private const float PvpIndicatorRegionY = 0f;
        private const float PvpIndicatorRegionWidth = 0.30f;
        private const float PvpIndicatorRegionHeight = 0.10f;

        // Measured color from the reference screenshot: RGB(97, 226, 8) / #61E208,
        // a saturated lime green. +/-50 per channel is generous enough to survive
        // compression/anti-aliasing without drifting into unrelated colors -
        // nothing this bright and this green-dominant turned up anywhere in either
        // false-positive screenshot this was checked against.
        private const int PvpIndicatorTargetR = 97;
        private const int PvpIndicatorTargetG = 226;
        private const int PvpIndicatorTargetB = 8;
        private const int PvpIndicatorColorTolerance = 50;

        // Out of the sampled region's pixels, how many need to match before this
        // counts as "the bar is showing." Measured at roughly 30% in the one
        // reference screenshot available; 2% leaves a lot of margin for a smaller
        // or slightly differently positioned bar while staying far above what
        // stray noise could produce - both false-positive screenshots checked
        // against this had exactly zero matching pixels in this region, not a
        // handful.
        private const double PvpIndicatorMinMatchFraction = 0.02;

        /// <summary>
        /// True if the PVP indicator bar is visible in the screenshot's top-right
        /// corner - see the constants above for where this came from and what it's
        /// worth. This is a first-pass calibration from a single reference
        /// screenshot at one window size, not a confirmed-across-many-captures
        /// value the way BattleWindowLocator's dimensions are. If real PVP battles
        /// start getting missed entirely (no "PVP battle detected" log line where
        /// one should appear, despite a PvpBattleDetector OCR attempt line showing
        /// the right title text), this region or tolerance is the first place to
        /// adjust - ideally against a screenshot from an actual live match rather
        /// than guessed again.
        /// </summary>
        private static bool HasPvpIndicatorBar(SKBitmap screenshot)
        {
            int x = (int)(screenshot.Width * PvpIndicatorRegionX);
            int y = (int)(screenshot.Height * PvpIndicatorRegionY);
            int width = (int)(screenshot.Width * PvpIndicatorRegionWidth);
            int height = (int)(screenshot.Height * PvpIndicatorRegionHeight);

            SKRectI region = ImageOps.MakeRect(x, y, width, height);
            SKRectI bounds = ImageOps.MakeRect(0, 0, screenshot.Width, screenshot.Height);
            SKRectI clamped = ImageOps.Intersect(region, bounds);

            if (ImageOps.IsEmpty(clamped))
                return false;

            SKColor[] pixels = screenshot.Pixels;
            int screenshotWidth = screenshot.Width;
            int totalPixels = clamped.Width * clamped.Height;
            int matchingPixels = 0;

            for (int py = clamped.Top; py < clamped.Bottom; py++)
            {
                int rowStart = py * screenshotWidth;

                for (int px = clamped.Left; px < clamped.Right; px++)
                {
                    SKColor color = pixels[rowStart + px];

                    if (Math.Abs(color.Red - PvpIndicatorTargetR) <= PvpIndicatorColorTolerance &&
                        Math.Abs(color.Green - PvpIndicatorTargetG) <= PvpIndicatorColorTolerance &&
                        Math.Abs(color.Blue - PvpIndicatorTargetB) <= PvpIndicatorColorTolerance)
                    {
                        matchingPixels++;
                    }
                }
            }

            bool found = totalPixels > 0 &&
                matchingPixels / (double)totalPixels >= PvpIndicatorMinMatchFraction;

            LogPvpIndicatorCheckIfChanged(found, matchingPixels, totalPixels, clamped);

            return found;
        }

        private static bool? lastLoggedPvpIndicatorResult;

        private static void LogPvpIndicatorCheckIfChanged(
            bool found, int matchingPixels, int totalPixels, SKRectI region)
        {
            if (found == lastLoggedPvpIndicatorResult)
                return;

            lastLoggedPvpIndicatorResult = found;

            Log.Information(
                "PvpBattleDetector indicator bar check: found={Found}, " +
                "matchingPixels={MatchingPixels}/{TotalPixels}, region=({X},{Y},{W}x{H})",
                found, matchingPixels, totalPixels,
                region.Left, region.Top, region.Width, region.Height);
        }

        /// <summary>Same full-name/last-word matching BossBattleDetector.cs uses
        /// against the boss catalog, duplicated here rather than shared - keeps
        /// this detector self-contained (matching this codebase's existing
        /// per-detector style) and avoids a second, redundant OCR read of the same
        /// title crop that calling BossBattleDetector.TryDetectBoss directly would
        /// require.</summary>
        private static bool MatchesKnownBoss(string normalizedTitle)
        {
            foreach (var (_, name) in BossCooldownService.GetAllBossNames())
            {
                string cleanedName = StripQualifierSuffix(name);

                if (normalizedTitle.Contains(cleanedName, StringComparison.OrdinalIgnoreCase))
                    return true;

                string lastWord = cleanedName
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault() ?? string.Empty;

                if (lastWord.Length >= 3 &&
                    normalizedTitle.Contains(lastWord, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string StripQualifierSuffix(string name)
        {
            int parenIndex = name.IndexOf('(');
            return parenIndex > 0 ? name[..parenIndex].Trim() : name;
        }

        // Diagnostic logging - only logs when the OCR result actually changes, to
        // avoid spamming the log with identical lines every scan tick during a
        // long battle. Same pattern as BossBattleDetector.cs/CatchDetector.cs.
        private static string? lastLoggedOcrText;

        private static void LogOcrAttemptIfChanged(
            string ocrText, SKRectI titleRegion, SKRectI battleBounds)
        {
            if (ocrText == lastLoggedOcrText)
                return;

            lastLoggedOcrText = ocrText;

            Log.Information(
                "PvpBattleDetector OCR attempt: text='{OcrText}', " +
                "titleRegion=({TX},{TY},{TW}x{TH}), battleBounds=({BX},{BY},{BW}x{BH})",
                ocrText,
                titleRegion.Left, titleRegion.Top, titleRegion.Width, titleRegion.Height,
                battleBounds.Left, battleBounds.Top, battleBounds.Width, battleBounds.Height);
        }

        private static string ReadText(SKBitmap bitmap, PageSegMode pageSegMode)
        {
            Initialize();

            lock (ocrLock)
            {
                byte[] pngBytes = ImageOps.EncodePng(bitmap);

                using TesseractOCR.Pix.Image image = TesseractOCR.Pix.Image.LoadFromMemory(pngBytes);
                using TesseractOCR.Page page = engine!.Process(image, pageSegMode);

                return page.Text ?? string.Empty;
            }
        }

        private static SKBitmap PrepareForOcr(SKBitmap source)
        {
            const int scale = 3;

            SKBitmap resized = ImageOps.Resize(source, source.Width * scale, source.Height * scale);
            ImageOps.ThresholdToBlackAndWhite(resized, 150);

            return resized;
        }
    }
}
