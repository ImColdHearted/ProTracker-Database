using System;
using System.Collections.Generic;
using SkiaSharp;
using Foot_Tracker.Tracking;
using PokemonSim.Simulation;
using TesseractOCR;
using TesseractOCR.Enums;

namespace Foot_Tracker.Services.Simulator
{
    /// <summary>Where the summary card sits in a screenshot: the origin of
    /// the "Ability" header wedge plus the vertical pitch between the
    /// right-column wedges - the two numbers every field position derives
    /// from, at any client scale.</summary>
    public sealed class CardFrame
    {
        public required int Ax { get; init; }
        public required int Ay { get; init; }
        public required double Pitch { get; init; }
    }

    /// <summary>
    /// §162. The pixel half of the screenshot importer: finds the PRO
    /// summary card in a screenshot and OCRs its fields into raw strings
    /// for CardImportParser (the engine-side, fully-tested text half).
    ///
    /// Finding the card leans on its most structural feature: the three
    /// black header wedges of the right column (Ability / Nature / OT) are
    /// x-aligned and evenly spaced at every client scale, and nothing else
    /// on a PRO screen looks like that. Their top-left corner plus their
    /// spacing give a scale-free coordinate frame, and every field is a
    /// fixed multiple of that pitch away - the constants below were
    /// calibrated on real screenshots at three client scales and are
    /// mirrored by the §162 battery.
    ///
    /// OCR runs through the tracker's shared Tesseract engine under its
    /// lock, exactly like the hunt detectors; each field is cropped,
    /// flattened to its brightest channel (the card writes in white,
    /// orange, blue and green on near-black), upscaled bilinearly and
    /// binarized by Otsu - except the two fields (HP numbers, card ID)
    /// that read better across scales with several attempts, which get
    /// every attempt handed to the parser to pick from.
    /// </summary>
    public static class CardOcrService
    {
        // Field boxes in wedge-pitch units, relative to the Ability
        // wedge's top-left corner: x0, x1, y0, y1.
        static readonly Dictionary<string, (double X0, double X1, double Y0, double Y1)> Fields = new()
        {
            ["title"] = (-3.25, 4.80, -1.06, -0.52),
            ["idnum"] = (0.55, 3.30, -1.06, -0.52),
            ["hpnum"] = (-1.40, 0.10, 0.02, 0.44),
            ["ability"] = (0.02, 2.30, 0.40, 0.80),
            ["nature"] = (0.02, 2.30, 1.40, 1.80),
            ["moves"] = (-3.05, 1.05, 2.73, 4.42),
            // §165: the stats crop runs to 4.60 pitches - the HP row (the
            // block's last line) ends near 4.30 and the next content below
            // starts near 4.89, so a slightly shifted live capture can no
            // longer clip that line off the bottom.
            ["stats"] = (2.45, 5.45, 2.34, 4.60),
            // §165: a band around the HP row alone. The row is the block's
            // shortest line and the first one a soft window capture loses;
            // a single-line read of just this band is far more robust, and
            // the parser leans on it whenever the block misses the row.
            ["hprow"] = (2.45, 5.45, 3.99, 4.58),
        };

        /// <summary>Finds the card anywhere in the screenshot, or inside
        /// the given region when the user drew a box. Null when no wedge
        /// trio is there to be found.</summary>
        public static CardFrame? FindCard(SKBitmap screenshot, SKRectI? within = null)
        {
            SKRectI bounds = within.HasValue
                ? SKRectI.Intersect(within.Value, new SKRectI(0, 0, screenshot.Width, screenshot.Height))
                : new SKRectI(0, 0, screenshot.Width, screenshot.Height);

            if (bounds.Width < 60 || bounds.Height < 40)
                return null;

            SKColor[] pixels = screenshot.Pixels;
            int width = screenshot.Width;

            // Near-black mask over the search bounds.
            bool[] dark = new bool[bounds.Width * bounds.Height];

            for (int y = 0; y < bounds.Height; y++)
            {
                int rowStart = (bounds.Top + y) * width + bounds.Left;

                for (int x = 0; x < bounds.Width; x++)
                {
                    SKColor c = pixels[rowStart + x];
                    int luminance = (c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000;
                    dark[y * bounds.Width + x] = luminance < 30;
                }
            }

            List<(int X, int Y, int W, int H)> wedges =
                FindWedgeCandidates(dark, bounds.Width, bounds.Height);

            foreach ((int X, int Y, int W, int H) anchor in wedges)
            {
                var column = new List<(int X, int Y, int W, int H)>();

                foreach ((int X, int Y, int W, int H) other in wedges)
                {
                    if (Math.Abs(other.X - anchor.X) <= Math.Max(4, anchor.W * 0.1) &&
                        other.Y >= anchor.Y)
                    {
                        column.Add(other);
                    }
                }

                column.Sort((a, b) => a.Y.CompareTo(b.Y));

                if (column.Count < 3)
                    continue;

                int p1 = column[1].Y - column[0].Y;
                int p2 = column[2].Y - column[1].Y;

                if (p1 > anchor.H && Math.Abs(p1 - p2) <= Math.Max(4, 0.15 * p1))
                {
                    return new CardFrame
                    {
                        Ax = bounds.Left + column[0].X,
                        Ay = bounds.Top + column[0].Y,
                        Pitch = (p1 + p2) / 2.0
                    };
                }
            }

            return null;
        }

        static List<(int X, int Y, int W, int H)> FindWedgeCandidates(bool[] dark, int width, int height)
        {
            var results = new List<(int X, int Y, int W, int H)>();
            int[] label = new int[dark.Length];
            var stack = new Stack<int>();
            int next = 0;

            for (int start = 0; start < dark.Length; start++)
            {
                if (!dark[start] || label[start] != 0)
                    continue;

                next++;
                int minX = int.MaxValue, maxX = int.MinValue;
                int minY = int.MaxValue, maxY = int.MinValue;
                int area = 0;

                stack.Push(start);
                label[start] = next;

                while (stack.Count > 0)
                {
                    int index = stack.Pop();
                    int x = index % width, y = index / width;

                    area++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;

                            int nx = x + dx, ny = y + dy;

                            if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                                continue;

                            int neighbor = ny * width + nx;

                            if (dark[neighbor] && label[neighbor] == 0)
                            {
                                label[neighbor] = next;
                                stack.Push(neighbor);
                            }
                        }
                    }
                }

                int w = maxX - minX + 1, h = maxY - minY + 1;

                if (h < 8 || w < 50) continue;
                if (w / (double)h < 3.5 || w / (double)h > 14) continue;
                if (area / (double)(w * h) < 0.55) continue;

                results.Add((minX, minY, w, h));
            }

            return results;
        }

        /// <summary>§164. The golden S badge on the title bar's ball
        /// icon marks a shiny. The badge box and the yellow mask were
        /// calibrated on real shiny and non-shiny cards: shiny cards put
        /// over 3% saturated-gold pixels in the box, every non-shiny card
        /// puts exactly none, so the threshold sits far from both.</summary>
        public static bool DetectShinyBadge(SKBitmap screenshot, CardFrame frame)
        {
            var region = new SKRectI(
                (int)(frame.Ax - 3.40 * frame.Pitch),
                (int)(frame.Ay - 1.10 * frame.Pitch),
                (int)(frame.Ax - 2.72 * frame.Pitch),
                (int)(frame.Ay - 0.42 * frame.Pitch));

            region = SKRectI.Intersect(region, new SKRectI(0, 0, screenshot.Width, screenshot.Height));

            if (region.Width < 4 || region.Height < 4)
                return false;

            using SKBitmap crop = ImageOps.Crop(screenshot, region);

            SKColor[] pixels = crop.Pixels;
            int gold = 0;

            foreach (SKColor c in pixels)
            {
                if (c.Red > 190 && c.Green > 150 && c.Blue < 130 &&
                    c.Red - c.Blue > 90 && c.Green - c.Blue > 70)
                {
                    gold++;
                }
            }

            return gold >= 6 && gold >= pixels.Length * 0.008;
        }

        // §166's attempt tables, and §165's for the HP row - lifted out of
        // ReadCard by §225 so a second frame of the same card is read at
        // exactly the settings the first one was, instead of by a second
        // copy of this list that could drift away from it. The HP row
        // deliberately has no character whitelist where the HP number does;
        // that is §165's tuning, not an oversight.
        static readonly (int Scale, bool Threshold)[] TitleAbilityAttempts =
            { (6, true), (4, false) };

        static readonly (int Scale, bool Threshold)[] StatsAttempts =
            { (4, true), (3, false) };

        static readonly (int Scale, bool Threshold)[] HpAttempts =
            { (4, true), (4, false), (6, true), (6, false) };

        static readonly (int Scale, bool Threshold)[] IdAttempts =
            { (4, false), (6, false), (4, true) };

        static readonly (int Scale, bool Threshold)[] HpRowAttempts =
            { (4, true), (4, false), (6, true) };

        /// <summary>OCRs every card field. The bitmap must be the whole
        /// screenshot the frame was found in.</summary>
        public static CardOcrTexts ReadCard(SKBitmap screenshot, CardFrame frame)
        {
            var texts = new CardOcrTexts
            {
                Title = ReadField(screenshot, frame, "title", 4, true, PageSegMode.SingleLine),
                Ability = ReadField(screenshot, frame, "ability", 4, true, PageSegMode.SingleLine),
                Nature = ReadField(screenshot, frame, "nature", 4, true, PageSegMode.SingleLine),
                MovesBlock = ReadField(screenshot, frame, "moves", 3, true, PageSegMode.SingleBlock),
                StatsBlock = ReadField(screenshot, frame, "stats", 3, true, PageSegMode.SingleBlock),
            };

            AddCandidates(screenshot, frame, texts);

            return texts;
        }

        /// <summary>§225. Reads the SAME card out of a DIFFERENT frame and
        /// adds every read to the candidate lists.
        ///
        /// §165 and §166 gave the hard fields several reads each, but all of
        /// those read the same pixels: they can disagree about how to
        /// interpret one frame, and not one of them can escape a bad one. A
        /// card caught mid-render, with a tooltip drifting over a stat or
        /// still animating in, poisons every read identically. Only new
        /// pixels answer that, which is what the importer's sweep supplies.
        ///
        /// The five primary fields are deliberately NOT touched. They belong
        /// to the frame the user watched being captured, and ParseId scores
        /// candidates on (Votes, Support, Order) - so leaving the primaries
        /// alone keeps a tie breaking toward that first frame rather than
        /// toward whichever later frame happened to arrive.
        ///
        /// Nature and the moves block get nothing here: neither has a
        /// candidate list to vote across, so an extra read would have
        /// nowhere to go. Giving them one is the obvious next step, and is
        /// deliberately not part of §225.</summary>
        public static void AppendCandidates(
            SKBitmap screenshot, CardFrame frame, CardOcrTexts into)
        {
            // What would have been this frame's primary reads, entered as
            // ballots instead of replacing the first frame's.
            into.TitleCandidates.Add(
                ReadField(screenshot, frame, "title", 4, true, PageSegMode.SingleLine));
            into.AbilityCandidates.Add(
                ReadField(screenshot, frame, "ability", 4, true, PageSegMode.SingleLine));
            into.StatsBlockCandidates.Add(
                ReadField(screenshot, frame, "stats", 3, true, PageSegMode.SingleBlock));

            AddCandidates(screenshot, frame, into);
        }

        /// <summary>§166's alternate reads of every field that owns a
        /// candidate list. Shared by ReadCard and AppendCandidates so the
        /// first frame and every later one are read the same way.</summary>
        static void AddCandidates(SKBitmap screenshot, CardFrame frame, CardOcrTexts texts)
        {
            // §166: every hard field gets more than one chance, the way
            // §165 gave one to the HP row - the parser votes across them.
            foreach ((int Scale, bool Threshold) attempt in TitleAbilityAttempts)
            {
                texts.TitleCandidates.Add(ReadField(screenshot, frame, "title",
                    attempt.Scale, attempt.Threshold, PageSegMode.SingleLine));
                texts.AbilityCandidates.Add(ReadField(screenshot, frame, "ability",
                    attempt.Scale, attempt.Threshold, PageSegMode.SingleLine));
            }

            foreach ((int Scale, bool Threshold) attempt in StatsAttempts)
            {
                texts.StatsBlockCandidates.Add(ReadField(screenshot, frame, "stats",
                    attempt.Scale, attempt.Threshold, PageSegMode.SingleBlock));
            }

            foreach ((int Scale, bool Threshold) attempt in HpAttempts)
            {
                texts.HpCandidates.Add(ReadField(screenshot, frame, "hpnum",
                    attempt.Scale, attempt.Threshold, PageSegMode.SingleLine, "0123456789/"));
            }

            foreach ((int Scale, bool Threshold) attempt in IdAttempts)
            {
                texts.IdCandidates.Add(ReadField(screenshot, frame, "idnum",
                    attempt.Scale, attempt.Threshold, PageSegMode.SingleLine));
            }

            // §165: the HP row's own rescue reads - the parser only uses
            // these when the stats block misses (or misreads) that row.
            foreach ((int Scale, bool Threshold) attempt in HpRowAttempts)
            {
                texts.HpRowCandidates.Add(ReadField(screenshot, frame, "hprow",
                    attempt.Scale, attempt.Threshold, PageSegMode.SingleLine));
            }
        }

        static string ReadField(
            SKBitmap screenshot, CardFrame frame, string field,
            int scale, bool threshold, PageSegMode mode, string? whitelist = null)
        {
            (double x0, double x1, double y0, double y1) = Fields[field];

            var region = new SKRectI(
                (int)(frame.Ax + x0 * frame.Pitch),
                (int)(frame.Ay + y0 * frame.Pitch),
                (int)(frame.Ax + x1 * frame.Pitch),
                (int)(frame.Ay + y1 * frame.Pitch));

            region = SKRectI.Intersect(region, new SKRectI(0, 0, screenshot.Width, screenshot.Height));

            if (region.Width < 4 || region.Height < 4)
                return string.Empty;

            using SKBitmap crop = ImageOps.Crop(screenshot, region);
            using SKBitmap prepared = Prepare(crop, scale, threshold);

            return Ocr(prepared, mode, whitelist);
        }

        /// <summary>Brightest-channel flatten, bilinear upscale, invert -
        /// and an Otsu binarize unless the caller wants the raw grays
        /// (some digits at some scales read better unthresholded).</summary>
        static SKBitmap Prepare(SKBitmap crop, int scale, bool threshold)
        {
            var flat = new SKBitmap(crop.Width, crop.Height);
            SKColor[] source = crop.Pixels;
            var flatPixels = new SKColor[source.Length];

            for (int i = 0; i < source.Length; i++)
            {
                SKColor c = source[i];
                byte value = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
                flatPixels[i] = new SKColor(value, value, value);
            }

            flat.Pixels = flatPixels;

            SKBitmap resized = ImageOps.ResizeBilinear(flat, crop.Width * scale, crop.Height * scale);
            flat.Dispose();

            SKColor[] pixels = resized.Pixels;
            var output = new SKColor[pixels.Length];

            if (!threshold)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    byte inverted = (byte)(255 - pixels[i].Red);
                    output[i] = new SKColor(inverted, inverted, inverted);
                }

                resized.Pixels = output;
                return resized;
            }

            // Otsu over the inverted grays.
            Span<int> histogram = stackalloc int[256];

            for (int i = 0; i < pixels.Length; i++)
                histogram[255 - pixels[i].Red]++;

            int total = pixels.Length;
            long sumAll = 0;

            for (int i = 0; i < 256; i++)
                sumAll += (long)i * histogram[i];

            long sumBackground = 0;
            int weightBackground = 0;
            double bestVariance = -1;
            int otsu = 127;

            for (int t = 0; t < 256; t++)
            {
                weightBackground += histogram[t];

                if (weightBackground == 0) continue;

                int weightForeground = total - weightBackground;

                if (weightForeground == 0) break;

                sumBackground += (long)t * histogram[t];

                double meanBackground = sumBackground / (double)weightBackground;
                double meanForeground = (sumAll - sumBackground) / (double)weightForeground;
                double variance = (double)weightBackground * weightForeground *
                    (meanBackground - meanForeground) * (meanBackground - meanForeground);

                if (variance > bestVariance)
                {
                    bestVariance = variance;
                    otsu = t;
                }
            }

            for (int i = 0; i < pixels.Length; i++)
            {
                byte inverted = (byte)(255 - pixels[i].Red);
                byte value = inverted > otsu ? (byte)255 : (byte)0;
                output[i] = new SKColor(value, value, value);
            }

            resized.Pixels = output;
            return resized;
        }

        static string Ocr(SKBitmap bitmap, PageSegMode mode, string? whitelist)
        {
            lock (SharedOcrEngine.Lock)
            {
                Engine engine = SharedOcrEngine.GetEngine();

                if (whitelist != null &&
                    !engine.SetVariable("tessedit_char_whitelist", whitelist))
                {
                    Serilog.Log.Warning("Card importer could not set an OCR whitelist - reading unrestricted.");
                }

                try
                {
                    byte[] pngBytes = ImageOps.EncodePng(bitmap);

                    using TesseractOCR.Pix.Image image = TesseractOCR.Pix.Image.LoadFromMemory(pngBytes);
                    using Page page = engine.Process(image, mode);

                    return (page.Text ?? string.Empty).Trim();
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Card importer: an OCR read failed.");
                    return string.Empty;
                }
                finally
                {
                    if (whitelist != null)
                        engine.SetVariable("tessedit_char_whitelist", "");
                }
            }
        }
    }
}
