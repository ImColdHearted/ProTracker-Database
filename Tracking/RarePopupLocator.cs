using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Foot_Tracker.Tracking
{
    /// <summary>§206. Where the rare-encounter popup actually is on this
    /// frame: the header band (its title) and the body box (its
    /// sentence).</summary>
    public readonly struct RarePopupBox
    {
        public RarePopupBox(SKRectI header, SKRectI body, int width)
        {
            Header = header;
            Body = body;
            Width = width;
        }

        /// <summary>The band between the popup's two horizontal rules -
        /// "Rare Encounter!".</summary>
        public SKRectI Header { get; }

        /// <summary>The box below the lower rule - "You encountered a rare
        /// form Pokemon!".</summary>
        public SKRectI Body { get; }

        /// <summary>The popup's own width in pixels, which is what every
        /// tolerance here is a fraction of.</summary>
        public int Width { get; }
    }

    /// <summary>
    /// §206. Finds the shiny/form popup by looking for the popup, instead of
    /// cropping a fixed percentage of the battle window and hoping.
    ///
    /// The percentage crop had been recalibrated four times (see
    /// RareEncounterDetector.GetRareEncounterRegion for that history) and was
    /// still marginal. A 30-second recording of a real Summer Rattata popup
    /// settled why. Measured on that frame the popup sits at x 483..796 with
    /// its sentence running to about x 775, while the crop's right edge lands
    /// somewhere between x 725 and x 793 depending on the width the battle
    /// locator settles on - so whether the last word survives is luck.
    /// That is exactly the "You encountered a rare form Pokem" section 94
    /// wrote its clipped-sentence fallbacks for.
    ///
    /// The same measurement found something worse: the crop's TOP edge lands
    /// at y 259-277, below the popup's header, which ends at y 272. The
    /// shortest, highest-contrast, least clippable string in the whole popup
    /// - its title - was never being read at all. The detector only ever saw
    /// the long sentence, which is the part most likely to be cut.
    ///
    /// So this finds the popup's own frame. It has a signature nothing else
    /// on screen has: two horizontal rules of IDENTICAL length, flat mid-grey,
    /// a short vertical gap apart, with a darker band between them. On the
    /// recording that pair is 314px long at y 240 and y 272, and it is there
    /// on all 39 frames the popup is up and on none of the other 82 - two
    /// full battles, two stretches of overworld, the battle title bar, the
    /// chat panel, the party sidebar and the hotbar all included.
    ///
    /// Every threshold below is a fraction of the rule's own length rather
    /// than of the screen or of the battle window, so a different GUI scale
    /// moves the popup and the numbers together. That is the whole point:
    /// nothing here is tuned to one resolution the way four recalibrations
    /// of a percentage crop were.
    /// </summary>
    public static class RarePopupLocator
    {
        /// <summary>Shorter than this is a UI line, not a popup rule. The
        /// measured popup is 314px at 1280x720; this leaves room for a much
        /// smaller window before it stops looking.</summary>
        internal const int MinRuleLength = 120;

        // A rule is flat mid-grey: bright enough to stand out from the body
        // it frames, dark enough not to be text. Measured 118-123 on the
        // recording, against a 16-46 body.
        internal const double RuleLumMin = 95;
        internal const double RuleLumMax = 150;

        /// <summary>How much a rule's brightness may wander along its length.
        /// A rule is flat; a row of text is not, which is what keeps the
        /// title row itself from being mistaken for a rule.</summary>
        internal const double RuleFlatness = 12.0;

        // The header's height as a fraction of the popup's width. Measured
        // 32/314 = 0.102; the range either side allows for a different
        // font size without letting two unrelated lines pair up.
        internal const double GapMinFraction = 0.06;
        internal const double GapMaxFraction = 0.20;

        /// <summary>The band between the rules has to be darker than the
        /// rules themselves by at least this much, or it is not a header.</summary>
        internal const double BandDarkerBy = 12;

        /// <summary>The body box's height, as a multiple of the header's.
        /// Measured 2.6 and deliberately generous - over-reading downward
        /// costs an empty strip of popup, under-reading costs the
        /// sentence.</summary>
        internal const double BodyHeightFactor = 2.6;

        /// <summary>The two ends of the rules may disagree by this many
        /// pixels and still be the same popup - antialiasing on the corners.</summary>
        internal const int EndTolerance = 4;

        /// <summary>True when this frame has a rare-encounter popup on it,
        /// with the two regions worth reading text out of. Never throws.</summary>
        public static bool TryLocate(SKBitmap screenshot, out RarePopupBox box)
        {
            box = default;

            if (screenshot == null || screenshot.Width < MinRuleLength || screenshot.Height < 16)
                return false;

            int width = screenshot.Width;
            int height = screenshot.Height;

            // The same bulk Pixels read BattleWindowLocator uses, and for the
            // same reason: this runs on every polling tick, and GetPixel()
            // crosses into native Skia once per pixel.
            SKColor[] pixels = screenshot.Pixels;

            // Rules keyed by where they start and end, so the two halves of
            // one popup frame find each other.
            var byExtent = new Dictionary<(int Start, int End), List<int>>();

            for (int y = 0; y < height; y++)
                CollectRules(pixels, width, y, byExtent);

            int bestLength = 0;
            bool found = false;

            foreach (KeyValuePair<(int Start, int End), List<int>> pair in byExtent)
            {
                List<int> rows = pair.Value;

                if (rows.Count < 2)
                    continue;

                int start = pair.Key.Start;
                int end = pair.Key.End;
                int length = end - start + 1;

                if (length <= bestLength)
                    continue;

                double minGap = length * GapMinFraction;
                double maxGap = length * GapMaxFraction;

                for (int i = 0; i < rows.Count; i++)
                {
                    for (int j = i + 1; j < rows.Count; j++)
                    {
                        int gap = rows[j] - rows[i];

                        if (gap < 8 || gap < minGap || gap > maxGap)
                            continue;

                        if (!BandIsDarker(pixels, width, start, end, rows[i], rows[j]))
                            continue;

                        int pad = Math.Max(2, length / 100);
                        int headerTop = rows[i] + 2;
                        int headerHeight = gap - 3;
                        int bodyTop = rows[j] + 2;
                        int bodyHeight = (int)(gap * BodyHeightFactor);

                        if (headerHeight <= 0 || bodyTop >= height)
                            continue;

                        SKRectI frame = ImageOps.MakeRect(0, 0, width, height);

                        box = new RarePopupBox(
                            ImageOps.Intersect(
                                ImageOps.MakeRect(start + pad, headerTop, length - pad * 2, headerHeight),
                                frame),
                            ImageOps.Intersect(
                                ImageOps.MakeRect(start + pad, bodyTop, length - pad * 2, bodyHeight),
                                frame),
                            length);

                        bestLength = length;
                        found = true;
                        break;
                    }

                    if (bestLength == length)
                        break;
                }
            }

            return found;
        }

        /// <summary>Every flat mid-grey run on one row, long enough to be a
        /// popup rule, filed under the pixels it spans.</summary>
        private static void CollectRules(
            SKColor[] pixels,
            int width,
            int y,
            Dictionary<(int, int), List<int>> byExtent)
        {
            int rowStart = y * width;
            int runStart = -1;

            for (int x = 0; x <= width; x++)
            {
                bool inRange = false;

                if (x < width)
                {
                    SKColor c = pixels[rowStart + x];
                    double lum = (c.Red + c.Green + c.Blue) / 3.0;
                    inRange = lum >= RuleLumMin && lum <= RuleLumMax;
                }

                if (inRange)
                {
                    if (runStart < 0)
                        runStart = x;

                    continue;
                }

                if (runStart >= 0)
                {
                    int end = x - 1;

                    if (end - runStart + 1 >= MinRuleLength && IsFlat(pixels, rowStart, runStart, end))
                    {
                        (int, int) key = Snap(runStart, end);

                        if (!byExtent.TryGetValue(key, out List<int>? rows))
                        {
                            rows = new List<int>();
                            byExtent[key] = rows;
                        }

                        rows.Add(y);
                    }

                    runStart = -1;
                }
            }
        }

        /// <summary>Rule ends are snapped to a small grid so that two rules
        /// whose antialiased corners differ by a pixel still file together.</summary>
        private static (int, int) Snap(int start, int end) =>
            (start / EndTolerance * EndTolerance, end / EndTolerance * EndTolerance);

        private static bool IsFlat(SKColor[] pixels, int rowStart, int from, int to)
        {
            double sum = 0;
            double sumSquares = 0;
            int n = to - from + 1;

            for (int x = from; x <= to; x++)
            {
                SKColor c = pixels[rowStart + x];
                double lum = (c.Red + c.Green + c.Blue) / 3.0;
                sum += lum;
                sumSquares += lum * lum;
            }

            double mean = sum / n;
            double variance = Math.Max(0, sumSquares / n - mean * mean);

            return Math.Sqrt(variance) < RuleFlatness;
        }

        private static bool BandIsDarker(
            SKColor[] pixels, int width, int start, int end, int topRule, int bottomRule)
        {
            double ruleSum = 0;
            int ruleCount = 0;

            for (int x = start; x <= end; x++)
            {
                SKColor c = pixels[topRule * width + x];
                ruleSum += (c.Red + c.Green + c.Blue) / 3.0;
                ruleCount++;
            }

            double bandSum = 0;
            int bandCount = 0;

            for (int y = topRule + 2; y < bottomRule - 1; y++)
            {
                for (int x = start + 4; x <= end - 4; x++)
                {
                    SKColor c = pixels[y * width + x];
                    bandSum += (c.Red + c.Green + c.Blue) / 3.0;
                    bandCount++;
                }
            }

            if (ruleCount == 0 || bandCount == 0)
                return false;

            return bandSum / bandCount <= ruleSum / ruleCount - BandDarkerBy;
        }
    }
}