using Serilog;
using SkiaSharp;
using TesseractOCR;
using TesseractOCR.Enums;

namespace Foot_Tracker.Tracking
{
    /// <summary>§233. One reading of a catch preview panel: the six IVs in
    /// the order the panel prints them (ATK, DEF, SPD, SPATK, SPDEF, HP),
    /// their sum - which is what a World Quest credits - and the species the
    /// header named.</summary>
    internal sealed record PreviewIvReading(
        IReadOnlyList<int> Ivs,
        int Total,
        string Species);

    /// <summary>
    /// §233. Reads the six IVs off PRO's catch preview panel - the summary the
    /// client shows after a catch when the party is full and the player has
    /// asked to be offered the choice.
    ///
    /// Why this exists: a World Quest credits the SUM of a submitted catch's
    /// six IVs, so a player tracking their own contribution otherwise has to
    /// read six numbers off the screen and type them in for every catch, for a
    /// weekend. This reads them instead, and the World Quest window adds the
    /// total to a local running count.
    ///
    /// Four gates, all of which must pass, because the cost of a wrong read is
    /// a contribution total the player then trusts:
    ///
    ///  1. SIX ORANGE ROWS AT A REGULAR PITCH. The IV column is the only thing
    ///     on screen rendered in the panel's orange (about rgb(195, 98, 0)) in
    ///     six evenly spaced rows. Measured against 548 frames of ordinary play
    ///     - battles, overworld, menus - drawn from four capture videos: not one
    ///     produced such a grid.
    ///  2. THE WORD "PREVIEW" IN THE HEADER. A player can also open ANOTHER
    ///     player's Pokemon from chat, and that panel is laid out identically -
    ///     same orange IV column, same six rows. It is not the player's catch
    ///     and must never be counted. The one reliable difference across every
    ///     capture examined is the word PREVIEW in the header's top right, which
    ///     a shared panel does not carry. Nothing else separates them: the
    ///     shared panel's brighter title bar can be a hover state, and a catch
    ///     made with the first ball leaves the preview's HP bar full too.
    ///  3. THE SPECIES MATCHES THE QUEST. The header's name has to be the
    ///     species the running World Quest asks for, so a catch made between
    ///     quest catches is not credited.
    ///  4. ALL SIX IVS PARSE, EACH 0-31. Six or none - a panel that yields five
    ///     good numbers and one unreadable one is a miss, not a total that is
    ///     quietly short by up to 31.
    ///
    /// Every failure direction is a MISS, never a wrong number: the World Quest
    /// window's Submit Pokemon button is the manual fallback, and a missed catch
    /// costs one manual entry while a wrong read silently corrupts the count.
    ///
    /// The panel's size tracks the client's GUI Scale setting, NOT the window
    /// size - three captures at the same 1919-pixel width had row pitches of
    /// 20.1, 24 and 26.6 pixels. So nothing here may be a fraction of the client
    /// window: the detector finds the IV column first and measures everything
    /// else in multiples of the pitch it observed.
    /// </summary>
    internal static class PreviewIvDetector
    {
        // ------------------------------------------------------- the colour

        // The IV digits are the panel's orange, about rgb(195, 98, 0): red
        // high, blue almost absent, green a little under half the red. The
        // green RATIO is what separates them from the panel's other coloured
        // text - the stat values are white (green ~= red), the EV column is
        // blue, and the "shiny" gold used elsewhere runs greener than 0.65.
        // Stored as the comparison's own limits so the check reads as written
        // in the section that measured it (red > 110, blue < 55).
        private const int OrangeMinRed = 111;
        private const int OrangeMaxBlue = 54;
        private const double OrangeMinGreenRatio = 0.35;
        private const double OrangeMaxGreenRatio = 0.65;

        // ------------------------------------------------- finding the grid

        /// <summary>The panel is always in the right half of the client - and
        /// ignoring the left half also drops the party list, whose HP bars are
        /// the only other large orange thing on screen.</summary>
        private const double SearchFromWidthFraction = 0.50;

        /// <summary>Rows are grouped with a gap tolerance because a digit's own
        /// anti-aliasing can leave a one or two pixel hole across the whole row
        /// - without this, one capture's Rattata fragmented into seven pieces
        /// and the six-row test failed on a panel that was perfectly readable.</summary>
        private const int RowGapTolerance = 3;

        private const int RowMinimumHeight = 4;

        /// <summary>How far apart consecutive row spacings may be and still
        /// count as one evenly spaced block. Generous next to the 16-27 pixel
        /// pitches measured, because the rows are found from anti-aliased
        /// glyph tops and bottoms, not from panel furniture.</summary>
        private const double PitchSpreadLimit = 3.5;

        /// <summary>ATK, DEF, SPD, SPATK, SPDEF, HP. Exactly six - a block of
        /// seven or more is not this panel and is refused rather than
        /// trimmed.</summary>
        private const int StatRowCount = 6;

        // Inside the row band the mask has three or four x-clusters: the
        // nature-lowered stat LABELS (which are orange too, and were present in
        // five of the seven captures), then the IV column. The labels sit far
        // to the left, so the IV column is simply the rightmost cluster. The
        // gap here is wider than the row gap because it has to bridge the space
        // between a two-digit number's own digits.
        private const int ColumnGapTolerance = 6;
        private const int ColumnMinimumWidth = 5;

        /// <summary>§246. The fallback's shape. Projecting orange across the
        /// whole right half of the client to find the six rows assumes nothing
        /// else over there is orange, and PRO's own map breaks that: the
        /// wooden fencing and dirt on the Vulcan Cove beach read as roughly
        /// (130, 74, 35) and the chat's orange lines as (198, 108, 0), and both
        /// pass the colour test below. On the same scanlines as the stat block
        /// they merge the six thin bands into one fat run - one measured 91
        /// pixels tall, spanning half the window - and gate 1 fails.
        ///
        /// The fallback uses the one thing the scenery cannot imitate: the six
        /// IV values sit in a single narrow column. A cell is two digits wide;
        /// a band of beach is not. So only horizontal runs no wider than
        /// CellMaximumWidth are kept, each one seeds a column window, and the
        /// six rows are looked for inside that window alone.</summary>
        private const int CellMaximumWidth = 60;
        private const int SegmentGapTolerance = 6;
        private const int SegmentMinimumWidth = 5;
        private const int ColumnWindowPad = 4;

        /// <summary>Within one row, the two digits are separated at the
        /// column's midpoint - the font is fixed-width per GUI scale, so the
        /// tens digit is always left of centre and the units digit right of
        /// it. A one pixel gap is bridged so a digit with a hole in it
        /// (a "0" at the smallest scale) stays one glyph.</summary>
        private const int DigitGapTolerance = 1;

        // ------------------------------------------------- reading a digit

        // Every glyph is scaled into this box before it is compared, which is
        // what lets one set of templates read every GUI scale: the same "7"
        // occupies 3x9 pixels at the smallest scale and 8x15 at the largest.
        private const int GlyphWidth = 10;
        private const int GlyphHeight = 14;

        /// <summary>Sanity cap on the best template distance, not the primary
        /// gate - the structure above is. The worst genuine glyph across the
        /// seven captures scored 27.0 against the shipped templates and 32.7
        /// under leave-one-capture-out; 40 sits above both and still refuses a
        /// glyph that resembles no digit at all.</summary>
        private const double GlyphMaxDistance = 40.0;

        /// <summary>
        /// How far the best-matching digit has to beat the second best. This
        /// one IS load bearing, and it is here because of what happened when
        /// the captures were re-run through a resampler: at reduced sizes the
        /// glyphs blur, and five of them matched the WRONG digit at distances
        /// (20.8 to 28.8) the cap above happily allowed - a total quietly nine
        /// short rather than a miss, which is the one outcome this detector
        /// must never produce.
        ///
        /// The two populations separate cleanly on the margin instead of the
        /// distance. Every glyph in the seven real captures beat its runner-up
        /// by at least 5.68; every one of those five wrong reads beat it by at
        /// most 3.27. Four sits between them, so a digit that is nearly as
        /// much one shape as another is refused and the whole panel becomes a
        /// miss.
        /// </summary>
        private const double GlyphMinMargin = 4.0;

        /// <summary>An IV is 0-31. The panel prints it with a leading zero, so
        /// every cell is exactly two digits.</summary>
        private const int MaxIv = 31;

        // ------------------------------------------------------ the header

        // All four in multiples of the observed row pitch, measured off the top
        // of the IV column - see the class remark on GUI Scale. The band covers
        // the panel's title row only: species, gender, level, then PREVIEW and
        // the close button.
        private const double HeaderTopPitches = 13.0;
        private const double HeaderBottomPitches = 9.4;
        private const double HeaderLeftPitches = 19.0;
        private const double HeaderRightPitches = 3.5;

        // The title row is read as two separate crops rather than one line.
        // Read whole it comes back as "Rattata ° Lv = PREVIEW X" and the
        // species is mangled about a third of the time; split, with a
        // whitelist on each half, both halves read exactly on all seven
        // captures.
        private const double SpeciesFromHeaderFraction = 0.05;
        private const double SpeciesToHeaderFraction = 0.50;
        private const double PreviewFromHeaderFraction = 0.62;

        /// <summary>Tesseract does better on these crops enlarged; the factor
        /// is chosen per crop to bring it to about this height.</summary>
        private const int OcrTargetHeight = 140;

        private const int OcrMinimumScale = 2;

        private const string PreviewWord = "PREVIEW";

        /// <summary>How many characters a read of "PREVIEW" may gain, and
        /// how many it may lose, at the end of the word and still count (see
        /// MatchesWord - "slack" here is not an edit distance). Generous in
        /// both directions, because nothing else on this panel is anywhere
        /// near the word: a shared panel reads as empty in this crop, and the
        /// whitelist means only the letters of PREVIEW can come back at
        /// all.</summary>
        private const int PreviewExtraSlack = 2;
        private const int PreviewMissingSlack = 2;

        /// <summary>The species name is deliberately far tighter. There are
        /// over a thousand species to keep apart and several pairs that differ
        /// by one letter - Corsola and Cursola, Diglett and Wiglett, Latias
        /// and Latios - so a read may gain ONE trailing letter (which is what
        /// it takes to forgive the "Rattatao" the header read produces at some
        /// scales) and may lose nothing at all. Losing nothing is what keeps
        /// Porygon apart from Porygon2.
        ///
        /// Measured against the whole shipped species list at this setting,
        /// exactly two pairs remain indistinguishable, and neither is a matter
        /// of slack: Nidoran, whose gender the header shows as a symbol that
        /// no character rule can read, and a Pawmo quest against a caught
        /// Pawmot.</summary>
        private const int SpeciesExtraSlack = 1;
        private const int SpeciesMissingSlack = 0;

        /// <summary>A read shorter than the expected word has to be at least
        /// this long before a prefix counts - so a stray mark that recognises
        /// as one or two letters can never satisfy a short species name.</summary>
        private const int MinimumPrefixLength = 3;

        /// <summary>Tesseract's documented way to clear a whitelist back to
        /// unrestricted recognition - what every other detector sharing this
        /// engine expects to find before its own read.</summary>
        private const string UnrestrictedWhitelist = "";

        private const string SpeciesWhitelist =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        /// <summary>
        /// The ten digit templates, 14 rows of 10 hex nibbles each: the mean
        /// coverage of every sample of that digit, scaled into the glyph box.
        /// Written this way so they can be read - the shapes are visible in the
        /// source - and because a nibble per cell is all the precision the
        /// comparison needs (quantising to 16 levels left the classification of
        /// all 84 training glyphs unchanged).
        ///
        /// Built from 84 glyphs: the six IVs of each of seven catch previews
        /// spanning five GUI scales (row pitch 16.0, 20.1, 21.2, 24.0, 26.6),
        /// hand-checked against the screenshots. Holding each capture out in
        /// turn and rebuilding the templates from the other six classified all
        /// 84 correctly, so this reads scales it was not built from.
        /// </summary>
        private static readonly string[][] DigitTemplates =
        {
            // 0
            new[] { "028cffd810", "458aaaa963", "9a200004ca", "fd300005ee", "fd300005ef", "fd300005ef", "fd300005ef", "fd300005ef", "fd300005ed", "fd300005ed", "fd300005ed", "992000039b", "4345555433", "01aefff910" },
            // 1
            new[] { "0001179ddd", "00279bdfff", "3349cfffff", "bb9438bfff", "7762179eff", "0001179eff", "0001179eff", "0001179eff", "0001179eff", "0001179eff", "0001179eff", "0001179eff", "0001158dff", "0001125cff" },
            // 2
            new[] { "039effe810", "369aaa9974", "a6000006dd", "11000006ef", "00000006ef", "000000029d", "0000005622", "000002c910", "000002b810", "0000254200", "0001781000", "05b7311000", "abffdbb533", "cefffffedb" },
            // 3
            new[] { "007fffb400", "19eeeeeb51", "f7000008d9", "00000008ff", "00000008ff", "00000003a9", "00002aeb41", "0002deeb41", "00000003a9", "00000003a9", "00000008ff", "f7000003a9", "1122222211", "03cffff700" },
            // 4
            new[] { "000003ff70", "00009dff70", "0000cfff70", "0000cfff70", "002978ff70", "04bf33ff70", "07ff33ff70", "679923ff70", "fb5557ff95", "ffeeeeffee", "cccccdffdc", "000003df70", "0000003d70", "0000003d70" },
            // 5
            new[] { "02dfffffeb", "05ff999985", "05fc000000", "9c61000000", "ff84444300", "fffeeeea11", "ff955535aa", "a7000005eb", "00000005ff", "00000005ff", "32000005ff", "ea011114da", "3358888533", "00affffa00" },
            // 6
            new[] { "02ceffda20", "4467887456", "cb1111108a", "fd10000034", "fd11333300", "fd25cccb20", "ffc8333388", "ffd70001cb", "fe510001de", "fd100001dd", "fd100001db", "cb211112b9", "4567876654", "019dffc810" },
            // 7
            new[] { "bdffffffff", "9bccceffdc", "111114df71", "000001be50", "0000114720", "0003bb3000", "0003ef7100", "0003ef7100", "0003eb3000", "0023310000", "239b100000", "47dc100000", "45ac100000", "45ac100000" },
            // 8
            new[] { "00cffffc00", "579aaaa975", "ed111113ee", "fd000003ff", "fd000003ff", "ed111113fe", "25dddddd52", "35ccccc933", "ed111113ee", "fd000003ff", "fd000003ff", "fd000002ee", "8667777655", "00cffffc00" },
            // 9
            new[] { "018ced6510", "38bbccb533", "e9111114a7", "fd300005ef", "fd300005ef", "fd300006ef", "fd30018dff", "c72113ceff", "06deed57ef", "005ba206ef", "00021005ed", "a5000005b8", "5322222455", "018efec810" },
        };

        private static readonly Lazy<double[][]> Templates = new(BuildTemplates);

        private static double[][] BuildTemplates()
        {
            var built = new double[DigitTemplates.Length][];

            for (int digit = 0; digit < DigitTemplates.Length; digit++)
            {
                string[] rows = DigitTemplates[digit];
                var cells = new double[GlyphWidth * GlyphHeight];

                for (int y = 0; y < GlyphHeight; y++)
                {
                    string row = rows[y];

                    for (int x = 0; x < GlyphWidth; x++)
                        cells[(y * GlyphWidth) + x] = Convert.ToInt32(row.Substring(x, 1), 16) / 15.0;
                }

                built[digit] = cells;
            }

            return built;
        }

        /// <summary>
        /// The whole read. Returns null - a miss, never a guess - whenever any
        /// of the four gates fails.
        /// </summary>
        /// <param name="frame">A capture of the whole PRO client window.</param>
        /// <param name="questSpecies">The species the running World Quest asks
        /// for. A blank one skips gate 3, which is only wanted by the
        /// section's own tests; the window always passes the real name.</param>
        public static PreviewIvReading? Read(SKBitmap frame, string questSpecies)
        {
            if (frame.Width <= 0 || frame.Height <= 0)
                return null;

            bool[,] orange = BuildOrangeMask(frame);

            PreviewGrid? grid = FindGrid(orange, frame.Width, frame.Height);

            if (grid is null)
                return null;

            var ivs = new int[StatRowCount];

            for (int row = 0; row < StatRowCount; row++)
            {
                int? value = ReadCell(orange, grid, row);

                // Gate 4: six or none.
                if (value is null)
                    return null;

                ivs[row] = value.Value;
            }

            (string species, string preview) = ReadHeader(frame, grid);

            // Gate 2. A shared panel reads as empty here.
            if (!MatchesWord(preview, PreviewWord, PreviewExtraSlack, PreviewMissingSlack))
            {
                Log.Debug(
                    "Preview IV detector: a six-row IV grid was found but the header did not read PREVIEW - ignoring it as another player's Pokemon.");
                return null;
            }

            // Gate 3.
            if (!string.IsNullOrWhiteSpace(questSpecies) &&
                !MatchesWord(species, questSpecies, SpeciesExtraSlack, SpeciesMissingSlack))
            {
                Log.Debug("Preview IV detector: the preview is not the quest's species - ignoring it.");
                return null;
            }

            int total = 0;

            foreach (int iv in ivs)
                total += iv;

            return new PreviewIvReading(ivs, total, species);
        }

        // ------------------------------------------------------------ mask

        private static bool[,] BuildOrangeMask(SKBitmap frame)
        {
            int width = frame.Width;
            int height = frame.Height;
            var mask = new bool[height, width];

            // One bulk read - see ImageOps.Resize on why per-pixel GetPixel is
            // the expensive way to do this.
            SKColor[] pixels = frame.Pixels;

            int from = (int)(width * SearchFromWidthFraction);

            for (int y = 0; y < height; y++)
            {
                int rowStart = y * width;

                for (int x = from; x < width; x++)
                {
                    SKColor pixel = pixels[rowStart + x];

                    if (pixel.Red < OrangeMinRed || pixel.Blue > OrangeMaxBlue)
                        continue;

                    double ratio = pixel.Green / (double)pixel.Red;

                    if (ratio > OrangeMinGreenRatio && ratio < OrangeMaxGreenRatio)
                        mask[y, x] = true;
                }
            }

            return mask;
        }

        // ------------------------------------------------------------ grid

        /// <summary>Where the IV column sits and how big the panel is drawn.</summary>
        private sealed record PreviewGrid(
            IReadOnlyList<(int Start, int End)> Rows,
            int Left,
            int Right,
            double Pitch);

        /// <summary>§246. Try the row projection first, and only look again
        /// the narrow way if it found nothing. This is deliberately additive:
        /// the projection handles every frame it handled before - all 27
        /// recorded panel frames across three window sizes, all seven of the
        /// §233 captures - and the fallback exists solely for the frames it
        /// cannot. Measured across 856 frames, the fallback adds no readings
        /// the projection did not already make except on the frames where the
        /// projection returns nothing at all.</summary>
        private static PreviewGrid? FindGrid(bool[,] mask, int width, int height)
        {
            return FindGridByRowProjection(mask, width, height)
                ?? FindGridByColumn(mask, width, height);
        }

        private static PreviewGrid? FindGridByRowProjection(bool[,] mask, int width, int height)
        {
            var rowHasOrange = new bool[height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (mask[y, x])
                    {
                        rowHasOrange[y] = true;
                        break;
                    }
                }
            }

            List<(int Start, int End)> rows = FindRuns(rowHasOrange, RowGapTolerance, RowMinimumHeight);

            List<(int Start, int End)>? block = LongestEvenlySpaced(rows);

            // Gate 1.
            if (block is null || block.Count != StatRowCount)
                return null;

            int top = block[0].Start;
            int bottom = block[^1].End;

            var columnHasOrange = new bool[width];

            for (int x = 0; x < width; x++)
            {
                for (int y = top; y <= bottom; y++)
                {
                    if (mask[y, x])
                    {
                        columnHasOrange[x] = true;
                        break;
                    }
                }
            }

            List<(int Start, int End)> columns =
                FindRuns(columnHasOrange, ColumnGapTolerance, ColumnMinimumWidth);

            if (columns.Count == 0)
                return null;

            (int Start, int End) ivColumn = columns[^1];

            double first = (block[0].Start + block[0].End) / 2.0;
            double last = (block[^1].Start + block[^1].End) / 2.0;
            double pitch = (last - first) / (block.Count - 1);

            if (pitch <= 0)
                return null;

            return new PreviewGrid(block, ivColumn.Start, ivColumn.End, pitch);
        }

        /// <summary>§246. The same grid, found without trusting the whole
        /// width. Every row is reduced to its NARROW horizontal runs - a run
        /// wider than CellMaximumWidth is scenery, not a pair of digits - and
        /// each surviving run seeds a column window. Inside one window the six
        /// rows are located, the IV column is taken as the rightmost cluster
        /// there, and then the rows are MEASURED again using that column
        /// alone.
        ///
        /// The second measurement reads the same pixels ReadCell will: the
        /// row band has to bound the digits in the IV COLUMN, not in whatever
        /// window happened to find them. On every frame available here the two
        /// agree, so this is an argument about correctness rather than a
        /// measured fix - but the seed window can be wider than the column and
        /// a band clipped by a few pixels normalises to the wrong glyph, which
        /// is a silent wrong number rather than a refusal. The window says
        /// WHERE; the column says HOW TALL.</summary>
        private static PreviewGrid? FindGridByColumn(bool[,] mask, int width, int height)
        {
            int from = (int)(width * SearchFromWidthFraction);

            var perRow = new List<(int Start, int End)>?[height];
            var seeds = new List<(int Start, int End)>();
            var seen = new HashSet<(int, int)>();
            var rowFlags = new bool[width];

            for (int y = 0; y < height; y++)
            {
                Array.Clear(rowFlags, 0, width);
                bool any = false;

                for (int x = from; x < width; x++)
                {
                    if (!mask[y, x])
                        continue;

                    rowFlags[x] = true;
                    any = true;
                }

                if (!any)
                    continue;

                List<(int Start, int End)> segments =
                    FindRuns(rowFlags, SegmentGapTolerance, SegmentMinimumWidth);

                segments.RemoveAll(run => run.End - run.Start + 1 > CellMaximumWidth);

                if (segments.Count == 0)
                    continue;

                perRow[y] = segments;

                foreach ((int Start, int End) segment in segments)
                {
                    if (seen.Add((segment.Start, segment.End)))
                        seeds.Add(segment);
                }
            }

            seeds.Sort((a, b) => a.Start != b.Start ? a.Start - b.Start : a.End - b.End);

            PreviewGrid? best = null;

            foreach ((int seedStart, int seedEnd) in seeds)
            {
                int windowLeft = Math.Max(seedStart - ColumnWindowPad, 0);
                int windowRight = Math.Min(seedEnd + ColumnWindowPad, width - 1);

                var inWindow = new bool[height];

                for (int y = 0; y < height; y++)
                {
                    List<(int Start, int End)>? segments = perRow[y];

                    if (segments is null)
                        continue;

                    foreach ((int start, int end) in segments)
                    {
                        if (end < windowLeft || start > windowRight)
                            continue;

                        inWindow[y] = true;
                        break;
                    }
                }

                List<(int Start, int End)>? rough = LongestEvenlySpaced(
                    FindRuns(inWindow, RowGapTolerance, RowMinimumHeight));

                if (rough is null || rough.Count != StatRowCount)
                    continue;

                int top = rough[0].Start;
                int bottom = rough[^1].End;

                var columnHasOrange = new bool[width];

                for (int x = windowLeft; x <= windowRight; x++)
                {
                    for (int y = top; y <= bottom; y++)
                    {
                        if (!mask[y, x])
                            continue;

                        columnHasOrange[x] = true;
                        break;
                    }
                }

                List<(int Start, int End)> columns =
                    FindRuns(columnHasOrange, ColumnGapTolerance, ColumnMinimumWidth);

                if (columns.Count == 0)
                    continue;

                (int left, int right) = columns[^1];

                // Measure inside the IV column, and only near the block that
                // was just located - the same column further up or down the
                // frame can hold unrelated orange, and letting it join the run
                // would shift the six.
                int span = Math.Max(
                    (int)Math.Round((bottom - top) / (double)(StatRowCount - 1)), 1);

                int fromY = Math.Max(top - span, 0);
                int toY = Math.Min(bottom + span, height - 1);

                var inColumn = new bool[height];

                for (int y = fromY; y <= toY; y++)
                {
                    for (int x = left; x <= right; x++)
                    {
                        if (!mask[y, x])
                            continue;

                        inColumn[y] = true;
                        break;
                    }
                }

                List<(int Start, int End)>? block = LongestEvenlySpaced(
                    FindRuns(inColumn, RowGapTolerance, RowMinimumHeight));

                if (block is null || block.Count != StatRowCount)
                    continue;

                double first = (block[0].Start + block[0].End) / 2.0;
                double last = (block[^1].Start + block[^1].End) / 2.0;
                double pitch = (last - first) / (block.Count - 1);

                if (pitch <= 0)
                    continue;

                if (best is null || right - left > best.Right - best.Left)
                    best = new PreviewGrid(block, left, right, pitch);
            }

            return best;
        }

        /// <summary>
        /// Runs of true, allowing up to <paramref name="gap"/> false values
        /// inside one run, and keeping only runs at least
        /// <paramref name="minimumLength"/> long.
        /// </summary>
        private static List<(int Start, int End)> FindRuns(bool[] flags, int gap, int minimumLength)
        {
            var found = new List<(int Start, int End)>();
            int start = -1;
            int blank = 0;

            for (int i = 0; i < flags.Length; i++)
            {
                if (flags[i])
                {
                    if (start < 0)
                        start = i;

                    blank = 0;
                }
                else if (start >= 0)
                {
                    blank++;

                    if (blank > gap)
                    {
                        found.Add((start, i - blank));
                        start = -1;
                        blank = 0;
                    }
                }
            }

            if (start >= 0)
                found.Add((start, flags.Length - 1 - blank));

            found.RemoveAll(run => run.End - run.Start + 1 < minimumLength);

            return found;
        }

        /// <summary>The longest stretch of six or more consecutive runs whose
        /// spacings all agree to within <see cref="PitchSpreadLimit"/>.</summary>
        private static List<(int Start, int End)>? LongestEvenlySpaced(List<(int Start, int End)> runs)
        {
            List<(int Start, int End)>? best = null;

            for (int i = 0; i < runs.Count; i++)
            {
                for (int j = i + StatRowCount - 1; j < runs.Count; j++)
                {
                    int length = j - i + 1;

                    if (best is not null && length <= best.Count)
                        continue;

                    double smallest = double.MaxValue;
                    double largest = double.MinValue;

                    for (int k = i; k < j; k++)
                    {
                        double step = ((runs[k + 1].Start + runs[k + 1].End) / 2.0)
                                    - ((runs[k].Start + runs[k].End) / 2.0);

                        if (step < smallest)
                            smallest = step;

                        if (step > largest)
                            largest = step;
                    }

                    if (largest - smallest <= PitchSpreadLimit)
                        best = runs.GetRange(i, length);
                }
            }

            return best;
        }

        // ----------------------------------------------------------- digits

        private static int? ReadCell(bool[,] mask, PreviewGrid grid, int row)
        {
            (int top, int bottom) = grid.Rows[row];
            int width = grid.Right - grid.Left + 1;

            var columnHasOrange = new bool[width];

            for (int x = 0; x < width; x++)
            {
                for (int y = top; y <= bottom; y++)
                {
                    if (mask[y, grid.Left + x])
                    {
                        columnHasOrange[x] = true;
                        break;
                    }
                }
            }

            List<(int Start, int End)> pieces = FindRuns(columnHasOrange, DigitGapTolerance, 1);

            if (pieces.Count == 0)
                return null;

            double middle = width / 2.0;

            int? tens = ReadDigit(mask, grid, top, bottom, Extent(pieces, middle, left: true));
            int? units = ReadDigit(mask, grid, top, bottom, Extent(pieces, middle, left: false));

            if (tens is null || units is null)
                return null;

            int value = (tens.Value * 10) + units.Value;

            return value <= MaxIv ? value : null;
        }

        /// <summary>The span covered by every piece whose centre falls on one
        /// side of the column's midpoint - a digit broken into two pieces by
        /// its own hole is put back together here.</summary>
        private static (int Start, int End)? Extent(
            List<(int Start, int End)> pieces, double middle, bool left)
        {
            int start = int.MaxValue;
            int end = int.MinValue;

            foreach ((int pieceStart, int pieceEnd) in pieces)
            {
                double centre = (pieceStart + pieceEnd) / 2.0;

                if (left ? centre >= middle : centre < middle)
                    continue;

                if (pieceStart < start)
                    start = pieceStart;

                if (pieceEnd > end)
                    end = pieceEnd;
            }

            if (end < start)
                return null;

            return (start, end);
        }

        private static int? ReadDigit(
            bool[,] mask, PreviewGrid grid, int top, int bottom, (int Start, int End)? span)
        {
            if (span is null)
                return null;

            (int start, int end) = span.Value;

            // Tighten to the glyph's own ink before scaling, so the same digit
            // at two GUI scales lands on the same normalised shape.
            int inkTop = int.MaxValue;
            int inkBottom = int.MinValue;
            int inkLeft = int.MaxValue;
            int inkRight = int.MinValue;

            for (int y = top; y <= bottom; y++)
            {
                for (int x = start; x <= end; x++)
                {
                    if (!mask[y, grid.Left + x])
                        continue;

                    if (y < inkTop) inkTop = y;
                    if (y > inkBottom) inkBottom = y;
                    if (x < inkLeft) inkLeft = x;
                    if (x > inkRight) inkRight = x;
                }
            }

            if (inkBottom < inkTop || inkRight < inkLeft)
                return null;

            double[] glyph = Normalise(mask, grid.Left, inkLeft, inkRight, inkTop, inkBottom);

            double bestDistance = double.MaxValue;
            double runnerUp = double.MaxValue;
            int best = -1;

            for (int digit = 0; digit < Templates.Value.Length; digit++)
            {
                double[] template = Templates.Value[digit];
                double distance = 0;

                for (int cell = 0; cell < glyph.Length; cell++)
                {
                    double difference = glyph[cell] - template[cell];
                    distance += difference * difference;
                }

                if (distance < bestDistance)
                {
                    runnerUp = bestDistance;
                    bestDistance = distance;
                    best = digit;
                }
                else if (distance < runnerUp)
                {
                    runnerUp = distance;
                }
            }

            if (bestDistance > GlyphMaxDistance)
                return null;

            return runnerUp - bestDistance >= GlyphMinMargin ? best : null;
        }

        /// <summary>
        /// Scales one glyph's ink into the <see cref="GlyphWidth"/> x
        /// <see cref="GlyphHeight"/> box by area averaging: each cell is the
        /// mean of the source rectangle it covers, edge pixels counted by the
        /// fraction they contribute. Written out rather than handed to an
        /// imaging library because the templates above were built with exactly
        /// this rule - a different resampler shifts every distance.
        /// </summary>
        private static double[] Normalise(
            bool[,] mask, int columnLeft, int inkLeft, int inkRight, int inkTop, int inkBottom)
        {
            // inkLeft/inkRight are offsets inside the IV column; inkTop/
            // inkBottom are absolute rows of the frame.
            int sourceWidth = inkRight - inkLeft + 1;
            int sourceHeight = inkBottom - inkTop + 1;
            var cells = new double[GlyphWidth * GlyphHeight];

            for (int y = 0; y < GlyphHeight; y++)
            {
                double sourceTop = y * sourceHeight / (double)GlyphHeight;
                double sourceBottom = (y + 1) * sourceHeight / (double)GlyphHeight;

                for (int x = 0; x < GlyphWidth; x++)
                {
                    double sourceLeft = x * sourceWidth / (double)GlyphWidth;
                    double sourceRight = (x + 1) * sourceWidth / (double)GlyphWidth;

                    double sum = 0;
                    double weight = 0;

                    for (int sy = (int)Math.Floor(sourceTop);
                         sy < Math.Min(sourceHeight, Math.Ceiling(sourceBottom));
                         sy++)
                    {
                        double heightPart = Math.Min(sourceBottom, sy + 1) - Math.Max(sourceTop, sy);

                        if (heightPart <= 0)
                            continue;

                        for (int sx = (int)Math.Floor(sourceLeft);
                             sx < Math.Min(sourceWidth, Math.Ceiling(sourceRight));
                             sx++)
                        {
                            double widthPart = Math.Min(sourceRight, sx + 1) - Math.Max(sourceLeft, sx);

                            if (widthPart <= 0)
                                continue;

                            double area = heightPart * widthPart;

                            if (mask[inkTop + sy, columnLeft + inkLeft + sx])
                                sum += area;

                            weight += area;
                        }
                    }

                    cells[(y * GlyphWidth) + x] = weight > 0 ? sum / weight : 0;
                }
            }

            return cells;
        }

        // ----------------------------------------------------------- header

        private static (string Species, string Preview) ReadHeader(SKBitmap frame, PreviewGrid grid)
        {
            int top = (int)Math.Round(grid.Rows[0].Start - (HeaderTopPitches * grid.Pitch));
            int bottom = (int)Math.Round(grid.Rows[0].Start - (HeaderBottomPitches * grid.Pitch));
            int left = (int)Math.Round(grid.Left - (HeaderLeftPitches * grid.Pitch));
            int right = (int)Math.Round(grid.Right + (HeaderRightPitches * grid.Pitch));

            top = Math.Max(0, top);
            left = Math.Max(0, left);
            bottom = Math.Min(frame.Height, bottom);
            right = Math.Min(frame.Width, right);

            if (bottom - top < 2 || right - left < 2)
                return (string.Empty, string.Empty);

            int width = right - left;

            string species = ReadCrop(
                frame,
                ImageOps.MakeRect(
                    left + (int)(width * SpeciesFromHeaderFraction),
                    top,
                    (int)(width * (SpeciesToHeaderFraction - SpeciesFromHeaderFraction)),
                    bottom - top),
                SpeciesWhitelist);

            string preview = ReadCrop(
                frame,
                ImageOps.MakeRect(
                    left + (int)(width * PreviewFromHeaderFraction),
                    top,
                    right - left - (int)(width * PreviewFromHeaderFraction),
                    bottom - top),
                PreviewWord);

            return (species, preview);
        }

        private static string ReadCrop(SKBitmap frame, SKRectI region, string whitelist)
        {
            if (ImageOps.IsEmpty(region))
                return string.Empty;

            try
            {
                using SKBitmap crop = ImageOps.Crop(frame, region);

                if (crop.Width < 5 || crop.Height < 5)
                    return string.Empty;

                int scale = Math.Max(
                    OcrMinimumScale,
                    (int)Math.Round(OcrTargetHeight / (double)crop.Height));

                using SKBitmap enlarged = ImageOps.Resize(crop, crop.Width * scale, crop.Height * scale);

                return Recognise(enlarged, whitelist).Trim();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Preview IV detector: reading the panel header failed.");
                return string.Empty;
            }
        }

        /// <summary>Narrows the shared engine's character set for this one read
        /// and restores it immediately, both inside the same lock - the same
        /// arrangement, and for the same reason, as LevelDetector's own read in
        /// WildEncounterDetectors.cs.</summary>
        private static string Recognise(SKBitmap bitmap, string whitelist)
        {
            lock (SharedOcrEngine.Lock)
            {
                Engine engine = SharedOcrEngine.GetEngine();

                if (!engine.SetVariable("tessedit_char_whitelist", whitelist))
                {
                    Log.Warning(
                        "Preview IV detector could not set a character whitelist on the Tesseract engine - the header read will use its unrestricted default character set instead.");
                }

                try
                {
                    byte[] png = ImageOps.EncodePng(bitmap);

                    using TesseractOCR.Pix.Image image = TesseractOCR.Pix.Image.LoadFromMemory(png);

                    // SingleLine: unlike LevelDetector's tag, which renders over
                    // battle artwork, this crop is a strip of a solid panel with
                    // one line of text on it and nothing else.
                    using TesseractOCR.Page page = engine.Process(image, PageSegMode.SingleLine);

                    return page.Text ?? string.Empty;
                }
                finally
                {
                    engine.SetVariable("tessedit_char_whitelist", UnrestrictedWhitelist);
                }
            }
        }

        // ------------------------------------------------------- comparison

        /// <summary>
        /// Whether <paramref name="expected"/> appears in a read of the panel
        /// header.
        ///
        /// The rule is a PREFIX match on whole words, not an edit distance,
        /// and the difference matters. An edit-distance rule sized to forgive
        /// the junk these crops really produce ("ES Dugtrio", "Rattatao")
        /// also makes Corsola match Cursola, Diglett match Wiglett and Latias
        /// match Latios - measured across the shipped species list, a
        /// two-character allowance confuses 149 pairs of real species, and
        /// even one confuses 24. Since the species gate exists precisely to
        /// refuse a catch that is not the quest's species, that is the wrong
        /// mistake to make.
        ///
        /// So the read is split into words on anything that is not a letter or
        /// digit - which is what strips the gender symbol, the level and the
        /// close button, and what rescues a leading "ES " - and a word counts
        /// when it IS the expected word, when it is the expected word plus up
        /// to <paramref name="slack"/> characters, or when it is the expected
        /// word cut short by up to <paramref name="missingSlack"/>. The whole
        /// read joined together is tried as one word too, for a crop that ran
        /// two of them into each other. Nothing in the middle of a word can
        /// match, which is what stops a short name matching inside a long one
        /// (an earlier version matched "Abra" inside "Abomasnow").
        ///
        /// The gained characters have to be LETTERS. Both header crops are
        /// read with a letters-only whitelist, so a digit can never be OCR
        /// junk - it can only be part of the real name. That single rule is
        /// what keeps a Porygon quest from counting a Porygon2.
        /// </summary>
        internal static bool MatchesWord(string? text, string expected, int extraSlack, int missingSlack)
        {
            string wanted = Simplify(expected);

            if (wanted.Length == 0)
                return false;

            foreach (string word in Words(text))
            {
                if (word.Length == 0)
                    continue;

                if (string.Equals(word, wanted, StringComparison.Ordinal))
                    return true;

                // The read gained characters: "rattatao" for "rattata".
                if (word.Length > wanted.Length &&
                    word.Length - wanted.Length <= extraSlack &&
                    word.StartsWith(wanted, StringComparison.Ordinal) &&
                    AllLetters(word, wanted.Length))
                {
                    return true;
                }

                // The read lost the end of the word: "previe" for "preview".
                if (word.Length < wanted.Length &&
                    wanted.Length - word.Length <= missingSlack &&
                    word.Length >= MinimumPrefixLength &&
                    wanted.StartsWith(word, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether everything from <paramref name="from"/> onward is
        /// a letter - see MatchesWord on why a trailing digit is never
        /// forgiven.</summary>
        private static bool AllLetters(string word, int from)
        {
            for (int i = from; i < word.Length; i++)
            {
                if (!char.IsLetter(word[i]))
                    return false;
            }

            return true;
        }

        /// <summary>The read's words, lowercased, plus all of them run
        /// together as one more candidate.</summary>
        private static List<string> Words(string? text)
        {
            var words = new List<string>();
            var current = new System.Text.StringBuilder();
            var joined = new System.Text.StringBuilder();

            foreach (char c in text ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c))
                {
                    char lower = char.ToLowerInvariant(c);
                    current.Append(lower);
                    joined.Append(lower);
                }
                else if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }

            if (current.Length > 0)
                words.Add(current.ToString());

            if (joined.Length > 0 && words.Count != 1)
                words.Add(joined.ToString());

            return words;
        }

        private static string Simplify(string? text)
        {
            var builder = new System.Text.StringBuilder(text?.Length ?? 0);

            foreach (char c in text ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c))
                    builder.Append(char.ToLowerInvariant(c));
            }

            return builder.ToString();
        }
    }
}
