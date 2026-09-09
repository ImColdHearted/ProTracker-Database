using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Foot_Tracker.Tracking
{
    /// <summary>
    /// §211. Cuts the Pokemon out of the game's own Pokedex panel, dropping the
    /// blue backdrop and the platform it stands on, so the counterpart library
    /// can be grown from the game itself instead of hunting for art.
    ///
    /// WHY THIS IS POSSIBLE AT ALL. §138's matcher needs a clean sprite on
    /// transparency, and the Pokedex draws exactly that on top of two things it
    /// is easy to be certain about: a smooth blue-purple gradient, and a bright
    /// elliptical platform. Everything else in the panel - the stat hexagon, the
    /// type chips, the description box - is outside the box this works in.
    ///
    /// FINDING THE PANEL, rather than being told where it is. Two anchors, in
    /// order, both measured off real captures:
    ///
    ///   1. The cyan rule that separates the species list from the detail pane.
    ///      It is the longest contiguous near-(0,210,255) column in the frame by
    ///      a wide margin - 425 pixels on both captures measured, against 72 for
    ///      the next best - and it spans the panel, so it gives the panel's
    ///      vertical extent for free.
    ///   2. The platform, as the longest contiguous run of bright, desaturated
    ///      pixels to the right of that rule and in the top 58% of the panel. A
    ///      RUN, not a count: the description box and the stat hexagon are bright
    ///      too, but neither is ever eighty pixels wide without a break.
    ///
    /// The sprite pane is then derived from the platform, because the sprite
    /// stands on it. Nothing here is a fixed screen coordinate, so moving the
    /// Pokedex window does not matter.
    ///
    /// REMOVING THE BACKDROP, in two stages that fail differently.
    ///
    /// Stage A floods in from the pane border comparing each candidate to the
    /// pixel it spread FROM, not to one global colour. That is what lets a
    /// smooth vertical gradient be a single region rather than fifty bands, and
    /// it stops dead at the sprite because a sprite edge is a step change.
    ///
    /// Stage B removes the platform, which stage A will not cross because its
    /// rim is a hard edge. This is the stage that could eat a pale Pokemon, so
    /// it is confined to the band the platform was FOUND in and is refused entry
    /// to anything dark. A white Pokemon standing above the band is not a
    /// candidate at all, whatever colour it is - which was checked by pasting a
    /// white disc over the sprite and confirming it survived whole.
    ///
    /// Whatever survives is reduced to its largest connected run, preferring one
    /// centred over the platform, which discards the title text and the hexagon
    /// if the pane happened to reach them.
    ///
    /// MEASURED ON: a 700x444 Charizard capture (soccer form - 74x80 out, both
    /// wings, flame and ball intact) and an 879x566 full-screen Mareanie capture
    /// (55x51 out, pale cream body and every blue spike intact). Two frames is
    /// two frames, not a guarantee: the failure mode to watch for is a Pokemon
    /// whose colour sits within StepTolerance of the backdrop blue, which would
    /// let stage A walk into it. The result is shown before anything is saved
    /// for exactly that reason.
    /// </summary>
    public static class PokedexSpriteScraper
    {
        /// <summary>The library's counterpart canvas (§139): sprite standing on
        /// the bottom edge of a transparent square.</summary>
        public const int CanvasSize = 120;

        // The cyan rule. Generous per-channel tolerance because the game
        // anti-aliases it against whatever is behind the panel.
        private static readonly SKColor RuleColor = new(0, 210, 255);
        private const int RuleTolR = 40, RuleTolG = 45, RuleTolB = 45;
        private const int MinRuleRun = 150;

        /// <summary>§216. How much of the rule column between its first and
        /// last cyan pixel must actually BE cyan for that full extent to be
        /// trusted as the panel's height.
        ///
        /// The divider is not one unbroken line: the Abilities and Moves bars
        /// cross it, cutting it into segments. TryFindRule used to return the
        /// longest SEGMENT, and which segment is longest changes with the
        /// Pokemon on screen - measured across nine captures, the answer
        /// flipped between a segment starting at y 460 and one starting at
        /// y 650, a 190 pixel difference in where the panel was believed to
        /// begin. That moved the platform search window off the platform
        /// entirely and produced "the platform under the sprite was not found"
        /// on five of the nine.
        ///
        /// The column's full extent has no such problem. Measured on all
        /// sixteen captures to hand it is 424 pixels every single time, the
        /// platform sits 124 rows below its top every single time, and the
        /// column is 415 to 425 cyan pixels out of those 424 - so the density
        /// test below passes comfortably while still catching the case this
        /// guards against, a stray cyan pixel elsewhere in the same column
        /// stretching the extent into nonsense. When it fails, the longest
        /// segment is used, which is exactly the old behaviour.</summary>
        private const double MinRuleDensity = 0.80;

        // The platform.
        private const int PlatformLumMin = 140;
        private const int PlatformSatMax = 70;
        private const int MinPlatformRun = 24;
        private const double PanelTopFraction = 0.58;
        private const int SearchRight = 270;

        // The pane, as offsets from the platform.
        private const int PaneHalfWidth = 135;

        /// <summary>§214. How far above the platform the pane starts.
        ///
        /// Was 100, with §212 layering a floor on top of it derived from the
        /// cyan rule's top. That floor was right about WHERE the genus line
        /// is and wrong about what to measure it from. TryFindRule returns
        /// the longest UNBROKEN run of rule, so anything crossing the divider
        /// shortens it and moves its top down - measured on a Pokedex in grid
        /// view, the run came back 384 instead of 425 and the top sat 41
        /// pixels low, which dragged the floor down with it and cut the pane
        /// to 59 rows. Charizard came out 69x47 with his head missing.
        ///
        /// The platform does not have that problem. Across six captures the
        /// platform sits exactly 124 rows below the rule's top whenever the
        /// rule is whole, so anchoring to the platform says the same thing
        /// without depending on the rule being unbroken: 86 clears the genus
        /// line by the same four rows §212 measured, and still leaves five
        /// rows above the tallest sprite seen.</summary>
        private const int PaneAbove = 86;

        private const int PaneBelow = 10;

        // Stage A: Manhattan distance from the pixel spread from.
        private const int StepTolerance = 26;

        // Stage B: the band around the platform, and what may be eaten in it.
        private const int BandSide = 10, BandAbove = 18, BandBelow = 8;
        private const int DarkGate = 42;
        private const int PlatformFillLumMin = 110;
        private const int PlatformFillSatMax = 80;

        // Stage C. An enclosed pocket of backdrop - the gap between Charizard's
        // wing and its body, say - is never reached by a flood that starts at
        // the border, so it survives as opaque backdrop-coloured pixels INSIDE
        // the sprite. Measured: 108 of them on the Charizard capture, none of
        // them touching transparency, which is what proves they are enclosed
        // rather than fringe. They matter because §138 correlates over opaque
        // pixels only, so a hundred wrong-coloured ones go straight into the
        // score.
        //
        // The reference is taken per ROW from that row's own background rather
        // than from one colour for the whole pane, because the backdrop is a
        // vertical gradient - the same reason stage A compares to its
        // neighbour. The tolerance is deliberately tighter than stage A's: this
        // pass can reach pixels in the middle of a sprite, so it should only
        // ever fire on something that really is the backdrop.
        private const int EnclosedTolerance = 16;

        /// <summary>§214. The smallest run that may be kept as a second piece
        /// of the same sprite.
        ///
        /// "Largest connected run" assumes a Pokemon is one blob, and Pignite
        /// proved it is not: its soccer ball is a 221 pixel run that does not
        /// touch it, so the ball was dropped. On Charizard and Delphox the same
        /// ball happened to touch a foot and survived, which is luck rather
        /// than a rule.
        ///
        /// What separates a real second piece from the stat hexagon and the
        /// title text is not size but POSITION: a ball at the feet, a detached
        /// flame, a floating orb all sit inside the figure's own bounding box,
        /// and the interface furniture does not. So the winner is kept, and so
        /// is any other run of at least this many pixels whose box overlaps the
        /// winner's. Measured across six captures that recovers Pignite's ball
        /// and changes nothing else - not one pixel on the other five.</summary>
        private const int MinCompanionPixels = 20;

        /// <summary>§215. How far a second piece may sit from the figure's box
        /// and still be part of the same sprite.
        ///
        /// §214 required the boxes to actually OVERLAP, and Tepig missed by a
        /// single pixel: its ball ends at x 58 and Tepig begins at x 59. One
        /// column. Charizard, Delphox and one Pignite capture all overlapped by
        /// a handful of columns, which is why five captures made an exact
        /// overlap look like a rule rather than a coincidence.
        ///
        /// A few pixels of slack is all that is needed, and the slack alone
        /// would be dangerous - at a gap of twelve it starts pulling in bits of
        /// platform rim. What makes it safe is the second condition below.</summary>
        private const int MinCompanionGap = 4;

        private const int MinSpritePixels = 200;

        public sealed class ScrapeResult
        {
            public bool Found { get; init; }
            public SKBitmap? Sprite { get; init; }
            public SKBitmap? Canvas { get; init; }
            public SKRectI Pane { get; init; }
            public SKRectI Platform { get; init; }
            public int KeptPixels { get; init; }
            public string Summary { get; init; } = string.Empty;
        }

        /// <summary>Runs the whole thing. Never throws for an unexpected frame -
        /// a frame with no Pokedex on it comes back Found=false with a sentence
        /// saying which anchor was missing.</summary>
        public static ScrapeResult Scrape(SKBitmap frame)
        {
            if (frame is null || frame.Width < 80 || frame.Height < 80)
                return new ScrapeResult { Summary = "There is no usable frame to read." };

            SKColor[] pixels = frame.Pixels;
            int w = frame.Width, h = frame.Height;

            if (!TryFindRule(pixels, w, h, out int ruleX, out int ruleTop, out int ruleBottom))
            {
                return new ScrapeResult
                {
                    Summary = "The Pokedex was not found on this frame - its cyan divider is not there. Open the Pokedex in game and try again."
                };
            }

            if (!TryFindPlatform(pixels, w, h, ruleX, ruleTop, ruleBottom, out SKRectI platform))
            {
                return new ScrapeResult
                {
                    Summary = "The Pokedex is open but the platform under the sprite was not found - is a Pokemon selected?"
                };
            }

            int centreX = (platform.Left + platform.Right) / 2;

            // §212/§214: start the pane below the genus line, measured from
            // the platform rather than from the rule - see PaneAbove. The
            // §212 version measured from the rule's top and inherited every
            // way the rule can be shortened.
            SKRectI pane = ImageOps.Intersect(
                new SKRectI(
                    Math.Max(ruleX + 4, centreX - PaneHalfWidth),
                    platform.Top - PaneAbove,
                    centreX + PaneHalfWidth,
                    platform.Top + PaneBelow),
                ImageOps.MakeRect(0, 0, w, h));

            if (ImageOps.IsEmpty(pane) || pane.Width < 32 || pane.Height < 32)
                return new ScrapeResult { Summary = "The sprite pane worked out too small to read.", Platform = platform };

            SKBitmap? sprite = Cut(pixels, w, pane, platform, out int kept);

            if (sprite is null || kept < MinSpritePixels)
            {
                sprite?.Dispose();
                return new ScrapeResult
                {
                    Pane = pane,
                    Platform = platform,
                    KeptPixels = kept,
                    Summary = $"Nothing sprite-shaped survived in the pane (only {kept} pixels). If a Pokemon is on screen, its colours may be too close to the backdrop."
                };
            }

            return new ScrapeResult
            {
                Found = true,
                Sprite = sprite,
                Canvas = ToCanvas(sprite),
                Pane = pane,
                Platform = platform,
                KeptPixels = kept,
                Summary = $"Cut {sprite.Width}x{sprite.Height} from a {pane.Width}x{pane.Height} pane ({kept} pixels kept)."
            };
        }

        /// <summary>§216. Which column is the rule, and how far the panel
        /// reaches.
        ///
        /// Two different questions, answered two different ways. WHICH column
        /// is best answered by the longest unbroken run, because that is what
        /// distinguishes a divider from scattered cyan text. HOW FAR is then
        /// answered by that column's full extent, because the divider is cut
        /// into segments by the Abilities and Moves bars and no single segment
        /// describes the panel - see MinRuleDensity.</summary>
        private static bool TryFindRule(SKColor[] px, int w, int h, out int ruleX, out int top, out int bottom)
        {
            ruleX = top = bottom = 0;
            int bestRun = 0;

            static bool OnRule(SKColor c) =>
                Math.Abs(c.Red - RuleColor.Red) <= RuleTolR &&
                Math.Abs(c.Green - RuleColor.Green) <= RuleTolG &&
                Math.Abs(c.Blue - RuleColor.Blue) <= RuleTolB;

            for (int x = 0; x < w; x++)
            {
                int run = 0, start = 0;

                for (int y = 0; y < h; y++)
                {
                    if (OnRule(px[y * w + x]))
                    {
                        if (run == 0) start = y;
                        run++;

                        if (run > bestRun)
                        {
                            bestRun = run;
                            ruleX = x;
                            top = start;
                            bottom = y;
                        }
                    }
                    else
                    {
                        run = 0;
                    }
                }
            }

            if (bestRun < MinRuleRun)
                return false;

            // The winning column's full extent, and how much of it is cyan.
            int first = -1, last = -1, count = 0;

            for (int y = 0; y < h; y++)
            {
                if (!OnRule(px[y * w + ruleX]))
                    continue;

                if (first < 0) first = y;

                last = y;
                count++;
            }

            if (first >= 0 && last > first &&
                count >= (last - first + 1) * MinRuleDensity)
            {
                top = first;
                bottom = last;
            }

            return true;
        }

        /// <summary>The widest unbroken run of bright, desaturated pixels in the
        /// detail pane's upper half.</summary>
        private static bool TryFindPlatform(
            SKColor[] px, int w, int h, int ruleX, int ruleTop, int ruleBottom, out SKRectI platform)
        {
            platform = default;

            int x0 = ruleX + 8;
            int x1 = Math.Min(w, ruleX + SearchRight);
            int y0 = Math.Max(0, ruleTop);
            int y1 = Math.Min(h, ruleTop + (int)((ruleBottom - ruleTop) * PanelTopFraction));

            if (x1 - x0 < MinPlatformRun || y1 <= y0)
                return false;

            int bestRun = 0, bestY = 0, bestStart = 0, bestEnd = 0;

            for (int y = y0; y < y1; y++)
            {
                int run = 0, start = 0;

                for (int x = x0; x < x1; x++)
                {
                    SKColor c = px[y * w + x];

                    if (Luminance(c) > PlatformLumMin && Saturation(c) < PlatformSatMax)
                    {
                        if (run == 0) start = x;
                        run++;

                        if (run > bestRun)
                        {
                            bestRun = run;
                            bestY = y;
                            bestStart = start;
                            bestEnd = x;
                        }
                    }
                    else
                    {
                        run = 0;
                    }
                }
            }

            if (bestRun < MinPlatformRun)
                return false;

            platform = new SKRectI(bestStart, bestY, bestEnd, bestY);
            return true;
        }

        /// <summary>The two flood stages and the component pick. Returns the
        /// trimmed cutout, or null when nothing survived.</summary>
        private static SKBitmap? Cut(SKColor[] frame, int frameWidth, SKRectI pane, SKRectI platform, out int kept)
        {
            kept = 0;

            int w = pane.Width, h = pane.Height;
            var px = new SKColor[w * h];

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = frame[(pane.Top + y) * frameWidth + (pane.Left + x)];

            var background = new bool[w * h];
            var queue = new Queue<int>();

            void Seed(int i)
            {
                if (background[i]) return;
                background[i] = true;
                queue.Enqueue(i);
            }

            for (int x = 0; x < w; x++)
            {
                Seed(x);
                Seed((h - 1) * w + x);
            }

            for (int y = 0; y < h; y++)
            {
                Seed(y * w);
                Seed(y * w + w - 1);
            }

            void Flood(Func<SKColor, SKColor, int, int, bool> accept)
            {
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    int x = i % w, y = i / w;
                    SKColor here = px[i];

                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                        int ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);

                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                            continue;

                        int j = ny * w + nx;

                        if (background[j])
                            continue;

                        if (accept(here, px[j], nx, ny))
                        {
                            background[j] = true;
                            queue.Enqueue(j);
                        }
                    }
                }
            }

            // Stage A - follow the gradient.
            Flood((here, c, _, _) => Manhattan(here, c) <= StepTolerance);

            // Stage B - the platform, and ONLY inside the band it was found in.
            int bx0 = platform.Left - BandSide - pane.Left;
            int bx1 = platform.Right + BandSide - pane.Left;
            int by0 = platform.Top - BandAbove - pane.Top;
            int by1 = platform.Bottom + BandBelow - pane.Top;

            for (int i = 0; i < background.Length; i++)
                if (background[i])
                    queue.Enqueue(i);

            Flood((_, c, nx, ny) =>
                nx >= bx0 && nx <= bx1 && ny >= by0 && ny <= by1 &&
                Luminance(c) >= DarkGate &&
                Luminance(c) > PlatformFillLumMin &&
                Saturation(c) < PlatformFillSatMax &&
                c.Blue >= c.Red - 10);

            // Stage C - enclosed backdrop pockets, per row. See EnclosedTolerance.
            for (int y = 0; y < h; y++)
            {
                int reference = -1;

                for (int x = 0; x < w; x++)
                {
                    if (background[y * w + x])
                    {
                        reference = y * w + x;
                        break;
                    }
                }

                if (reference < 0)
                    continue;

                SKColor rowBackground = px[reference];

                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;

                    if (!background[i] && Manhattan(px[i], rowBackground) <= EnclosedTolerance)
                        background[i] = true;
                }
            }

            // The largest surviving run, preferring one centred over the platform.
            int platformCentre = (platform.Left + platform.Right) / 2 - pane.Left;
            var visited = new bool[w * h];
            var components = new List<List<int>>();
            List<int>? best = null;
            double bestScore = -1;

            for (int start = 0; start < w * h; start++)
            {
                if (background[start] || visited[start])
                    continue;

                var component = new List<int>();
                var walk = new Queue<int>();

                visited[start] = true;
                walk.Enqueue(start);

                int minX = w, maxX = -1;

                while (walk.Count > 0)
                {
                    int i = walk.Dequeue();
                    component.Add(i);

                    int x = i % w, y = i / w;

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;

                            int nx = x + dx, ny = y + dy;

                            if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                                continue;

                            int j = ny * w + nx;

                            if (background[j] || visited[j])
                                continue;

                            visited[j] = true;
                            walk.Enqueue(j);
                        }
                    }
                }

                components.Add(component);

                double offset = Math.Abs((minX + maxX) / 2.0 - platformCentre);
                double score = component.Count / (1.0 + offset / 40.0);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = component;
                }
            }

            if (best is null || best.Count == 0)
                return null;

            int left = w, top = h, right = -1, bottom = -1;

            foreach (int i in best)
            {
                int x = i % w, y = i / w;

                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }

            var keepMask = new bool[w * h];

            foreach (int i in best)
                keepMask[i] = true;

            kept = best.Count;

            // §214/§215: a sprite can be more than one piece. Keep any other
            // run that sits at the figure's box, within a few pixels - see
            // MinCompanionPixels and MinCompanionGap. The test is against the
            // WINNER's box, not a growing union, so this cannot walk outwards
            // one component at a time.
            //
            // §215's second condition is what makes the slack safe: a piece of
            // the Pokemon STANDS on the platform, so it does not reach below
            // the platform's surface. Every stray run the slack would otherwise
            // admit - rim left behind by stage B, the shadow's edge - lies
            // below that line by definition, and the interface furniture is
            // tens of pixels away sideways in any case.
            int platformTopRow = platform.Top - pane.Top;

            foreach (List<int> other in components)
            {
                if (ReferenceEquals(other, best) || other.Count < MinCompanionPixels)
                    continue;

                int ox0 = w, oy0 = h, ox1 = -1, oy1 = -1;

                foreach (int i in other)
                {
                    int x = i % w, y = i / w;

                    if (x < ox0) ox0 = x;
                    if (x > ox1) ox1 = x;
                    if (y < oy0) oy0 = y;
                    if (y > oy1) oy1 = y;
                }

                if (oy1 > platformTopRow)
                    continue;

                int gapX = ox1 >= left && ox0 <= right
                    ? 0
                    : Math.Min(Math.Abs(left - ox1), Math.Abs(ox0 - right));

                int gapY = oy1 >= top && oy0 <= bottom
                    ? 0
                    : Math.Min(Math.Abs(top - oy1), Math.Abs(oy0 - bottom));

                if (gapX > MinCompanionGap || gapY > MinCompanionGap)
                    continue;

                foreach (int i in other)
                    keepMask[i] = true;

                kept += other.Count;

                if (ox0 < left) left = ox0;
                if (ox1 > right) right = ox1;
                if (oy0 < top) top = oy0;
                if (oy1 > bottom) bottom = oy1;
            }

            int outW = right - left + 1, outH = bottom - top + 1;
            var sprite = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var outPixels = new SKColor[outW * outH];

            for (int y = 0; y < outH; y++)
            {
                for (int x = 0; x < outW; x++)
                {
                    int src = (top + y) * w + (left + x);
                    outPixels[y * outW + x] = keepMask[src] ? px[src] : SKColors.Transparent;
                }
            }

            sprite.Pixels = outPixels;
            return sprite;
        }

        /// <summary>§139's counterpart canvas: centred left to right, feet on the
        /// bottom edge. A sprite bigger than the canvas is scaled down whole
        /// rather than cropped, which has not been needed yet but would be the
        /// day somebody scrapes a Wailord.</summary>
        public static SKBitmap ToCanvas(SKBitmap sprite)
        {
            SKBitmap scaled = sprite;
            bool disposeScaled = false;

            if (sprite.Width > CanvasSize || sprite.Height > CanvasSize)
            {
                double f = Math.Min(CanvasSize / (double)sprite.Width, CanvasSize / (double)sprite.Height);

                scaled = ImageOps.Resize(
                    sprite,
                    Math.Max(1, (int)Math.Round(sprite.Width * f)),
                    Math.Max(1, (int)Math.Round(sprite.Height * f)));

                disposeScaled = true;
            }

            var canvas = new SKBitmap(CanvasSize, CanvasSize, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var pixels = new SKColor[CanvasSize * CanvasSize];

            int offsetX = (CanvasSize - scaled.Width) / 2;
            int offsetY = CanvasSize - scaled.Height;

            SKColor[] src = scaled.Pixels;

            for (int y = 0; y < scaled.Height; y++)
            {
                for (int x = 0; x < scaled.Width; x++)
                {
                    int dx = offsetX + x, dy = offsetY + y;

                    if (dx >= 0 && dy >= 0 && dx < CanvasSize && dy < CanvasSize)
                        pixels[dy * CanvasSize + dx] = src[y * scaled.Width + x];
                }
            }

            canvas.Pixels = pixels;

            if (disposeScaled)
                scaled.Dispose();

            return canvas;
        }

        private static double Luminance(SKColor c) => 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;

        private static int Saturation(SKColor c) =>
            Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue));

        private static int Manhattan(SKColor a, SKColor b) =>
            Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
    }
}