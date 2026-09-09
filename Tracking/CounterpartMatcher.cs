using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Serilog;
using SkiaSharp;

namespace Foot_Tracker.Tracking
{
    /// <summary>
    /// §138. Which counterpart (event form) is the wild Pokemon on screen?
    ///
    /// The game already tells the tracker THAT a form was met (the "rare
    /// form" dialogue RareEncounterDetector reads); it never says which one.
    /// This answers that by matching the wild sprite pixels in the frame
    /// against the sprite library on disk: the species' normal sprite
    /// (Assets/Sprites) and every counterpart image the catalog lists for it
    /// (Assets/Counterparts via CounterpartSpriteService), and reporting the
    /// event whose image fits best - or saying that none does.
    ///
    /// What makes this work is a measurement, not a hope: on eighteen real
    /// frames at eight window sizes, the species' normal sprite scored 0.96
    /// to 1.00 at GUI scale 1 (pixel-identical - PRO draws the very same art
    /// the library holds, at 1:1), while the wrong species scored at most
    /// 0.50 and the SAME species' other forms 0.51 to 0.72. A counterpart on
    /// screen therefore separates from the normal sprite and from its
    /// sibling forms by a wide margin, on colour alone where the pose is
    /// shared (Pinkan, Summer) and on both where it is not.
    ///
    /// Two things keep it honest. The score is a zero-mean normalized
    /// cross-correlation over the sprite's OPAQUE pixels only, so the grass,
    /// the platform shadow and whatever else lies behind the sprite never
    /// enter the comparison; and a form is claimed only when the best image
    /// is a counterpart, scores at least AcceptScore, and beats the runner-up
    /// by AcceptMargin. At GUI scales other than 1 the game's own upscaling
    /// costs correlation (0.83 at 1.10, 0.90 at 1.29, 0.66 to 0.73 at 1.40 on
    /// the frames measured), and the thresholds let the 1.40 case fall to
    /// "not identified" rather than guess - which the fixture confirms is the
    /// right answer there, because at that scale a wrong form outscored the
    /// right sprite.
    ///
    /// §207. The admin can publish which events are actually running, and
    /// when they have, the events that are NOT running stop being candidates
    /// here at all. That is a real narrowing and it is meant to be: matching
    /// a Summer Wingull against thirty Valentines and Christmas skins is how
    /// a near-miss on the wrong form wins by a hair. The species' normal
    /// sprite always stays in the running, because it is what tells the
    /// difference between "this is a form the library does not have" and
    /// "this is an ordinary Pokemon". With nothing published, nothing is
    /// excluded and this behaves exactly as it did before §207.
    ///
    /// The cost is worth stating plainly: Pinkan is a permanent area rather
    /// than a seasonal event, so if the published list ever leaves it out, a
    /// Pinkan encounter cannot be identified while that list stands. Keeping
    /// Pinkan in one of the three slots permanently is the answer.
    ///
    /// When nothing is identified, the sprite region is saved as a PNG under
    /// the app's data folder (FormSprites) so the library can grow from real
    /// encounters: the soccer forms, the newest Summer sets and the Easter
    /// Togekiss line (whose only image shows all three stages at once) have
    /// no usable singular sprite today, and a real capture is exactly the
    /// raw material for one.
    ///
    /// Where to look: the wild sprite's bottom-centre sits at a fixed point
    /// of the battle window (AnchorX/AnchorY, measured within three pixels
    /// across species and sizes), so the search covers a generous box around
    /// it rather than the whole frame - about 24,000 positions at scale 1.
    /// Exhaustive within that box, deliberately: pixel art has a needle-sharp
    /// correlation peak, and a coarse-to-fine search that skipped rows walked
    /// straight past a Rattata it later matched at 1.000 when it looked at
    /// every position.
    /// </summary>
    public static class CounterpartMatcher
    {
        public sealed class MatchResult
        {
            public string Species { get; init; } = string.Empty;
            public bool Identified { get; init; }
            public string? EventName { get; init; }
            public string? ImageFile { get; init; }
            /// <summary>§139. The winning counterpart image as the catalog
            /// spells it ("SharedPokemonLibrary/Assets/Counterparts/Summer/
            /// Wingull.png") - what the encounter cards show and the session
            /// file keeps. Null unless Identified.</summary>
            public string? ImagePath { get; init; }
            public double BestScore { get; init; }
            public double RunnerUpScore { get; init; }
            public string BestLabel { get; init; } = string.Empty;
            public string? RunnerUpLabel { get; init; }
            public string? SavedCropPath { get; init; }
            public string Summary { get; init; } = string.Empty;
        }

        /// <summary>The battle window's width at GUI scale 1, where the
        /// library's sprites are 1:1 with the screen (§120/§135).</summary>
        public const double ReferenceBattleWidth = 782.0;

        public const double AcceptScore = 0.80;
        public const double AcceptMargin = 0.08;

        // The wild sprite's opaque-box bottom-centre, as fractions of the
        // battle window. Measured: Wingull (58x41) and Rattata (36x43) both
        // land within 3 px of (0.563, 0.446) at 1152x768 and 1366x768.
        private const double AnchorX = 0.563;
        private const double AnchorY = 0.446;

        // Search slack around the anchor, in scale-1 pixels: the observed
        // variation is a few pixels; this covers a sprite drawn on a larger
        // canvas with a different internal offset many times over.
        private const int SearchHalfWidth = 90;
        private const int SearchAbove = 80;
        private const int SearchBelow = 50;

        private const int MinOpaquePixels = 60;

        private static readonly string CropFolder =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProTracker",
                "FormSprites");

        private sealed class Template
        {
            public string Label = string.Empty;
            public bool IsNormal;
            public string? EventName;
            public string ImageFile = string.Empty;
            public int Width;
            public int Height;
            public int[] Xs = Array.Empty<int>();
            public int[] Ys = Array.Empty<int>();
            public float[] R = Array.Empty<float>();
            public float[] G = Array.Empty<float>();
            public float[] B = Array.Empty<float>();
            public double Norm;
        }

        private sealed class Candidate
        {
            public string Label = string.Empty;
            public bool IsNormal;
            public string? EventName;
            public string Path = string.Empty;
            public string RelativePath = string.Empty;
        }

        /// <summary>Runs the match. Returns null when the library holds no
        /// counterpart image for this species (there is nothing to identify
        /// against, and the caller should say so rather than "not
        /// identified"). Never throws for a bad asset - a file that will not
        /// decode is skipped and named in the log.</summary>
        public static MatchResult? Identify(SKBitmap screenshot, SKRectI battleBounds, string species)
        {
            if (screenshot is null || string.IsNullOrWhiteSpace(species))
                return null;

            List<Candidate> candidates = CollectCandidates(species, out int excludedByEvent);

            if (!candidates.Any(c => !c.IsNormal))
            {
                if (excludedByEvent > 0)
                {
                    // Not the same thing as an empty library, and saying so
                    // saves somebody an hour wondering where their sprites
                    // went after an admin published a list.
                    Log.Information(
                        "Counterpart match: all {Excluded} counterpart images for {Pokemon} belong to events that are not running ({Active}) - nothing to identify against",
                        excludedByEvent, species, string.Join(", ", ActiveEventService.Current.Events));
                }
                else
                {
                    Log.Information(
                        "Counterpart match: no counterpart images in the library for {Pokemon} - nothing to identify against",
                        species);
                }

                return null;
            }

            double f = battleBounds.Width / ReferenceBattleWidth;
            bool unitScale = Math.Abs(f - 1.0) < 0.02;
            double[] scales = unitScale ? new[] { 1.0 } : new[] { f * 0.97, f, f * 1.03 };

            SKColor[] pixels = screenshot.Pixels;
            int frameWidth = screenshot.Width;
            int frameHeight = screenshot.Height;

            double anchorX = battleBounds.Left + battleBounds.Width * AnchorX;
            double anchorY = battleBounds.Top + battleBounds.Height * AnchorY;

            var scored = new List<(Candidate candidate, double score)>();

            foreach (Candidate candidate in candidates)
            {
                double best = -1;

                using SKBitmap? asset = SKBitmap.Decode(candidate.Path);

                if (asset is null)
                {
                    Log.Warning("Counterpart match: could not decode {File}", candidate.Path);
                    continue;
                }

                foreach (double scale in scales)
                {
                    Template? template = BuildTemplate(asset, scale, candidate);

                    if (template is null)
                        continue;

                    double score = BestScore(pixels, frameWidth, frameHeight, anchorX, anchorY, f, template);
                    best = Math.Max(best, score);
                }

                if (best >= 0)
                    scored.Add((candidate, best));
            }

            if (scored.Count == 0)
                return null;

            scored.Sort((a, b) => b.score.CompareTo(a.score));

            Candidate bestCandidate = scored[0].candidate;
            double bestScore = scored[0].score;
            Candidate? runnerUp = scored.Count > 1 ? scored[1].candidate : null;
            double runnerUpScore = scored.Count > 1 ? scored[1].score : -1.0;

            bool identified =
                !bestCandidate.IsNormal &&
                bestScore >= AcceptScore &&
                (runnerUp is null || bestScore - runnerUpScore >= AcceptMargin);

            string table = string.Join(", ", scored.Select(s => $"{s.candidate.Label}={s.score:0.000}"));

            if (excludedByEvent > 0)
                table += $" (+{excludedByEvent} skipped, event not running)";

            string? savedCrop = null;
            string summary;

            if (identified)
            {
                summary = $"{bestCandidate.EventName} form ({bestScore:0.00})";
                Log.Information(
                    "Counterpart match for {Pokemon}: {Event} form from {File} (score {Score:0.000}, runner-up {RunnerUp:0.000}, scale {Scale:0.00}) - all: {Table}",
                    species, bestCandidate.EventName, bestCandidate.Label, bestScore, runnerUpScore, f, table);
            }
            else
            {
                savedCrop = TrySaveCrop(screenshot, battleBounds, anchorX, anchorY, f, species);

                string why = bestCandidate.IsNormal
                    ? $"the normal sprite fits best ({bestScore:0.00}) - the form's sprite is probably not in the library"
                    : bestScore < AcceptScore
                        ? $"best fit {bestCandidate.Label} only {bestScore:0.00}"
                        : $"{bestCandidate.Label} {bestScore:0.00} too close to {runnerUp?.Label} {runnerUpScore:0.00}";

                summary = "form not identified - " + why;

                Log.Information(
                    "Counterpart match for {Pokemon}: not identified ({Why}; scale {Scale:0.00}) - all: {Table}{Saved}",
                    species, why, f, table,
                    savedCrop is null ? string.Empty : $" - sprite region saved to {savedCrop}");
            }

            return new MatchResult
            {
                Species = species,
                Identified = identified,
                EventName = identified ? bestCandidate.EventName : null,
                ImageFile = identified ? Path.GetFileName(bestCandidate.Path) : null,
                ImagePath = identified ? bestCandidate.RelativePath : null,
                BestScore = bestScore,
                RunnerUpScore = runnerUpScore,
                BestLabel = bestCandidate.Label,
                RunnerUpLabel = runnerUp?.Label,
                SavedCropPath = savedCrop,
                Summary = summary
            };
        }

        private static List<Candidate> CollectCandidates(string species, out int excludedByEvent)
        {
            var list = new List<Candidate>();

            excludedByEvent = 0;

            // §207. One snapshot for the whole loop. Asking the service per
            // variant would be correct almost always and wrong occasionally:
            // a heartbeat refresh landing mid-loop would let one candidate
            // list be built from two different published lists.
            ActiveEvents active = ActiveEventService.Current;

            // The tracker's name is the OCR alias ("Farfetchd"); the sprite
            // table may only know the resolved species name. Try both.
            string resolved = PokemonSpriteService.ResolveEncounterName(species);

            if ((PokemonSpriteService.TryGetSpritePath(species, out string? normalPath) ||
                 PokemonSpriteService.TryGetSpritePath(resolved, out normalPath)) &&
                normalPath is not null)
            {
                list.Add(new Candidate { Label = "Normal", IsNormal = true, EventName = null, Path = normalPath });
            }

            foreach (CounterpartVariant variant in CounterpartSpriteService.GetForPokemon(species))
            {
                // §207. Only events the admin says are running are candidates.
                // Allows() is true for everything while nothing is published,
                // so this is a no-op on an untouched install.
                if (!active.Allows(variant.Event))
                {
                    excludedByEvent++;
                    continue;
                }

                string full = Path.Combine(
                    AppContext.BaseDirectory,
                    variant.ImagePath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(full))
                    continue;

                // Several images can share one event (Christmas Bisharp,
                // Bisharp2; Easter Togekiss 1-6): the label carries the file
                // so the log says which one won, the event is the answer.
                string label = variant.Event + " (" + Path.GetFileNameWithoutExtension(full) + ")";

                list.Add(new Candidate
                {
                    Label = label,
                    IsNormal = false,
                    EventName = variant.Event,
                    Path = full,
                    RelativePath = variant.ImagePath
                });
            }

            return list;
        }

        /// <summary>Crops the asset to its opaque box, scales it (bilinear,
        /// alpha included) when the battle window is not at GUI scale 1, and
        /// flattens the opaque pixels to zero-mean colour arrays.</summary>
        private static Template? BuildTemplate(SKBitmap asset, double scale, Candidate candidate)
        {
            SKColor[] src = asset.Pixels;
            int w = asset.Width;
            int h = asset.Height;

            int minX = w, minY = h, maxX = -1, maxY = -1;

            for (int y = 0; y < h; y++)
            {
                int row = y * w;

                for (int x = 0; x < w; x++)
                {
                    if (src[row + x].Alpha > 128)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (maxX < 0)
                return null;

            int bw = maxX - minX + 1;
            int bh = maxY - minY + 1;

            // Opaque box as RGBA floats.
            float[] r = new float[bw * bh], g = new float[bw * bh], b = new float[bw * bh], a = new float[bw * bh];

            for (int y = 0; y < bh; y++)
            {
                for (int x = 0; x < bw; x++)
                {
                    SKColor c = src[(minY + y) * w + (minX + x)];
                    int i = y * bw + x;
                    r[i] = c.Red; g[i] = c.Green; b[i] = c.Blue; a[i] = c.Alpha;
                }
            }

            int tw = bw, th = bh;

            if (Math.Abs(scale - 1.0) >= 0.005)
            {
                tw = Math.Max(1, (int)Math.Round(bw * scale));
                th = Math.Max(1, (int)Math.Round(bh * scale));

                r = Resample(r, bw, bh, tw, th);
                g = Resample(g, bw, bh, tw, th);
                b = Resample(b, bw, bh, tw, th);
                a = Resample(a, bw, bh, tw, th);
            }

            var xs = new List<int>();
            var ys = new List<int>();

            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                    if (a[y * tw + x] > 128f)
                    {
                        xs.Add(x);
                        ys.Add(y);
                    }

            if (xs.Count < MinOpaquePixels)
                return null;

            int n = xs.Count;
            var tr = new float[n]; var tg = new float[n]; var tb = new float[n];
            double sum = 0;

            for (int i = 0; i < n; i++)
            {
                int idx = ys[i] * tw + xs[i];
                tr[i] = r[idx]; tg[i] = g[idx]; tb[i] = b[idx];
                sum += tr[i] + tg[i] + tb[i];
            }

            // Pooled mean over all three channels, as the fixture measured.
            float mean = (float)(sum / (3.0 * n));
            double norm2 = 0;

            for (int i = 0; i < n; i++)
            {
                tr[i] -= mean; tg[i] -= mean; tb[i] -= mean;
                norm2 += tr[i] * tr[i] + tg[i] * tg[i] + tb[i] * tb[i];
            }

            if (norm2 <= 0)
                return null;

            return new Template
            {
                Label = candidate.Label,
                IsNormal = candidate.IsNormal,
                EventName = candidate.EventName,
                ImageFile = Path.GetFileName(candidate.Path),
                Width = tw,
                Height = th,
                Xs = xs.ToArray(),
                Ys = ys.ToArray(),
                R = tr,
                G = tg,
                B = tb,
                Norm = Math.Sqrt(norm2)
            };
        }

        /// <summary>Bilinear resample of one float plane (same sampling as
        /// ImageOps.ResizeBilinear, kept here because that one drops alpha).</summary>
        private static float[] Resample(float[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new float[dw * dh];

            for (int y = 0; y < dh; y++)
            {
                float sy = (y + 0.5f) * sh / dh - 0.5f;
                int y0 = Math.Clamp((int)Math.Floor(sy), 0, sh - 1);
                int y1 = Math.Min(y0 + 1, sh - 1);
                float wy = Math.Clamp(sy - y0, 0f, 1f);

                for (int x = 0; x < dw; x++)
                {
                    float sx = (x + 0.5f) * sw / dw - 0.5f;
                    int x0 = Math.Clamp((int)Math.Floor(sx), 0, sw - 1);
                    int x1 = Math.Min(x0 + 1, sw - 1);
                    float wx = Math.Clamp(sx - x0, 0f, 1f);

                    float top = src[y0 * sw + x0] + (src[y0 * sw + x1] - src[y0 * sw + x0]) * wx;
                    float bottom = src[y1 * sw + x0] + (src[y1 * sw + x1] - src[y1 * sw + x0]) * wx;

                    dst[y * dw + x] = top + (bottom - top) * wy;
                }
            }

            return dst;
        }

        /// <summary>Exhaustive masked zero-mean NCC over the anchored search
        /// box; returns the best score, or -1 when the box does not fit.</summary>
        private static double BestScore(
            SKColor[] pixels, int frameWidth, int frameHeight,
            double anchorX, double anchorY, double f, Template t)
        {
            int tw = t.Width, th = t.Height;

            int x0 = (int)Math.Round(anchorX - tw / 2.0 - SearchHalfWidth * f);
            int x1 = (int)Math.Round(anchorX - tw / 2.0 + SearchHalfWidth * f);
            int y0 = (int)Math.Round(anchorY - th - SearchAbove * f);
            int y1 = (int)Math.Round(anchorY - th + SearchBelow * f);

            x0 = Math.Max(0, x0);
            y0 = Math.Max(0, y0);
            x1 = Math.Min(frameWidth - tw, x1);
            y1 = Math.Min(frameHeight - th, y1);

            if (x1 < x0 || y1 < y0)
                return -1;

            int n = t.Xs.Length;
            double count = 3.0 * n;
            double best = -1;

            for (int py = y0; py <= y1; py++)
            {
                for (int px = x0; px <= x1; px++)
                {
                    double sum = 0, sum2 = 0, dot = 0;

                    for (int i = 0; i < n; i++)
                    {
                        SKColor c = pixels[(py + t.Ys[i]) * frameWidth + (px + t.Xs[i])];

                        float r = c.Red, g = c.Green, b = c.Blue;

                        sum += r + g + b;
                        sum2 += r * r + g * g + b * b;
                        dot += r * t.R[i] + g * t.G[i] + b * t.B[i];
                    }

                    double variance = sum2 - sum * sum / count;

                    if (variance <= 1e-3)
                        continue;

                    double score = dot / (Math.Sqrt(variance) * t.Norm);

                    if (score > best)
                        best = score;
                }
            }

            return best;
        }

        /// <summary>The raw material for a missing library sprite: the
        /// anchored search box, with its background, as a PNG in the app's
        /// data folder. Best effort - a failure here is logged and changes
        /// nothing else.</summary>
        private static string? TrySaveCrop(
            SKBitmap screenshot, SKRectI battleBounds, double anchorX, double anchorY, double f, string species)
        {
            try
            {
                int half = (int)Math.Round((SearchHalfWidth + 70) * f);
                int above = (int)Math.Round((SearchAbove + 130) * f);
                int below = (int)Math.Round((SearchBelow + 10) * f);

                SKRectI region = ImageOps.Intersect(
                    ImageOps.MakeRect((int)anchorX - half, (int)anchorY - above, half * 2, above + below),
                    ImageOps.MakeRect(0, 0, screenshot.Width, screenshot.Height));

                if (ImageOps.IsEmpty(region))
                    return null;

                Directory.CreateDirectory(CropFolder);

                string safeSpecies = string.Concat(species.Split(Path.GetInvalidFileNameChars()));
                string path = Path.Combine(
                    CropFolder,
                    $"{safeSpecies}-{DateTime.Now:yyyyMMdd-HHmmss}-w{battleBounds.Width}.png");

                using SKBitmap crop = ImageOps.Crop(screenshot, region);
                File.WriteAllBytes(path, ImageOps.EncodePng(crop));

                return path;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Counterpart match: could not save the sprite region for {Pokemon}", species);
                return null;
            }
        }
    }
}
