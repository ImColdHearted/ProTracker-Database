using System;
using System.Collections.Generic;
using System.Linq;
using Foot_Tracker.Models;
using Serilog;
using SkiaSharp;
using TesseractOCR;
using TesseractOCR.Enums;
using DisplayNumber = Foot_Tracker.Services.DisplayNumber;
using LocationDictionaryService = Foot_Tracker.Services.LocationDictionaryService;

namespace Foot_Tracker.Tracking
{
    /// <summary>
    /// §281. What one scan of the Pokedex's Area list came to.
    ///
    /// §284: and what it SAW, which is a separate thing. A scan that finds no
    /// rows has several quite different causes and one symptom, so the reading
    /// carries a palette sweep of the whole frame and lets the caller tell
    /// them apart instead of guessing. Added because a real scan reported "no
    /// rows" on a panel that was plainly on screen, and no amount of reasoning
    /// about the screenshot the user PASTED could say why.
    /// </summary>
    public sealed record PokedexScanReading(
        IReadOnlyList<PokedexSpawn> Spawns,
        int RowsFound,
        int RowsUnread,
        int FrameWidth,
        int FrameHeight,
        int LitPalettePixels,
        int NamePalettePixels)
    {
        /// <summary>
        /// §284. Why this scan read nothing, in the user's words rather than
        /// the reader's. Told apart by what the sweep found:
        ///
        ///   - no palette pixels at all: the capture is not the Pokedex. The
        ///     wrong window is bound, or the panel was shut when the button
        ///     was pressed.
        ///   - icon pixels but no name pixels: the Pokedex is on screen but
        ///     the Area list is not the part of it showing.
        ///   - plenty of both and still no rows: the panel IS there and in the
        ///     right colours, and the reader could not make rows out of it.
        ///     That is a reader fault, not a user fault, and the only thing
        ///     that can settle it is the frame itself - which is why the
        ///     caller keeps it.
        /// </summary>
        public string Diagnosis
        {
            get
            {
                string size =
                    $"{DisplayNumber.Count(FrameWidth)}x{DisplayNumber.Count(FrameHeight)} captured";

                if (LitPalettePixels == 0 && NamePalettePixels == 0)
                {
                    return size + ", with not one pixel of the Pokedex's own colours in it. "
                        + "That is a capture of something other than the Pokedex - either the "
                        + "wrong window is bound, or the panel was closed when the button was pressed.";
                }

                if (NamePalettePixels == 0)
                {
                    return size + $", {DisplayNumber.Count(LitPalettePixels)} icon pixels and no "
                        + "area-name pixels. The Pokedex is on screen but the Area list is not the "
                        + "part of it showing.";
                }

                return size + $", {DisplayNumber.Count(LitPalettePixels)} icon pixels and "
                    + $"{DisplayNumber.Count(NamePalettePixels)} area-name pixels - so the Area "
                    + "list IS on screen and in the right colours, and the reader could not make "
                    + "rows out of it. That is this reader's fault rather than yours, and the "
                    + "saved frame is what fixes it.";
            }
        }
    }

    /// <summary>
    /// §281, rebuilt in §285. Reads PRO's Pokedex "Area" list off a screenshot.
    ///
    /// ALMOST NONE OF THIS IS OCR, and that is the point. The panel is drawn
    /// from a fixed sprite sheet, so every fact but the area's NAME is a
    /// palette lookup - exact where OCR is probabilistic, and needing no
    /// tuning per user, per resolution or per background. Only the map name
    /// goes through Tesseract.
    ///
    /// The colours, measured over six screenshots on deliberately different
    /// backgrounds (a dark cave, a lit Pokecenter, open grass) and identical
    /// in all six:
    ///
    ///   - A row is five icons, then the area name. Icons in order: grass,
    ///     surf, morning, day, night.
    ///   - A LIT icon is drawn in #86F6FA, an unlit one in #282B39.
    ///   - The area name is exactly #00D2FF, or exactly #FFAAFF when the area
    ///     needs membership. Both are single RGB values, not ranges.
    ///
    /// The panel is opaque, which is why the background made no difference.
    ///
    /// §285: HOW A ROW IS FOUND, and why the first answer was wrong.
    ///
    /// §281 found rows by matching the morning icon's 16x15 SHAPE MASK at a
    /// fixed 18-pixel pitch. Against the app's own window capture that found
    /// nothing at all, on every Pokemon tried. §284 made the scan keep the
    /// frame it failed on, and the frame settled it in one measurement: the
    /// icons are on a 16-pixel pitch, not 18. The mask was a real glyph
    /// measured off a real screenshot, but the pitch beside it was wrong, so
    /// the mask was being tested at offsets no icon ever sits at.
    ///
    /// The fix is not a corrected 18. Fixed distances measured by eye are what
    /// failed, and correcting one by hand would leave the next one to be found
    /// the same expensive way. So nothing here is a fixed distance any more.
    /// A row is found by its STRUCTURE:
    ///
    ///   1. Find every blob of area-name colour - the letters of one name join
    ///      into one blob, because the mask is widened sideways before the
    ///      blobs are traced.
    ///   2. A blob the size and shape of a line of text is a candidate name.
    ///   3. Walk LEFT from it. The icons butt up against the name, so a real
    ///      row has an unbroken band of icon-palette pixels ending within a
    ///      few pixels of the first letter.
    ///   4. That band is the five icons. Its width divided by five IS the
    ///      pitch - derived from this frame, never assumed.
    ///
    /// Two rules then throw out everything that is not a row, and both are
    /// facts about the game rather than thresholds:
    ///
    ///   - An area PRO lists is one the Pokemon spawns in, so at least one of
    ///     grass/surf must be lit AND at least one of morning/day/night. The
    ///     column headers above the list have five icon-coloured boxes and not
    ///     one lit pixel, and this is what rejects them.
    ///   - Icons are line art on a panel, not a filled slab: over the 84 real
    ///     rows in thirteen screenshots the band was 26.3%-39.5% icon-palette,
    ///     never more. The only survivor of the first rule anywhere in any of
    ///     those frames was a line of chat text at 84.4%. The ceiling below
    ///     sits in that void.
    ///
    /// Checked against every Pokedex screenshot on hand - six species, window
    /// widths from 1305 to 1919, panels at six different x offsets: 84 rows
    /// found, every one at a derived pitch of exactly 16, and one false
    /// positive rejected. The pitch coming out the same everywhere says the
    /// panel really is drawn 1:1; deriving it anyway costs one division and
    /// means a client update that changes it is read correctly instead of
    /// silently read as nothing.
    /// </summary>
    public static class PokedexAreaReader
    {
        // The two icon palettes, exact.
        private static readonly SKColor IconLit = new(134, 246, 250);
        private static readonly SKColor IconUnlit = new(40, 43, 57);

        // The two name colours, exact.
        private static readonly SKColor NameNormal = new(0, 210, 255);
        private static readonly SKColor NameMembers = new(255, 170, 255);

        /// <summary>Anti-aliasing puts a handful of near-palette pixels around
        /// every glyph edge, so the match allows a little room. Small enough
        /// that the two icon palettes cannot be confused: they are 200 apart
        /// on green and blue.</summary>
        private const int ColourSlack = 24;

        /// <summary>The lit palette needs more room than the unlit one: it is
        /// a bright colour with a soft edge, and the unlit one sits close
        /// enough to the panel's own background (about 44 per channel away)
        /// that a wide slack would match the panel itself.</summary>
        private const int LitSlack = 40;

        private const int UnlitSlack = 22;

        /// <summary>Grass, surf, morning, day, night.</summary>
        private const int IconCount = 5;

        /// <summary>How far sideways the name mask is widened before blobs are
        /// traced, so the letters of one name join up and two columns of text
        /// do not. Letters in this font are 1-3 pixels apart and the Moves
        /// column is over fifty from the Area column, so the choice is not a
        /// close one.</summary>
        private const int LetterGap = 4;

        /// <summary>A line of this text: at least this tall, at most this
        /// tall, and wider than a stray glyph. "Moon" is the shortest area
        /// name in the game and is well over the width floor.</summary>
        private const int TextMinHeight = 5;
        private const int TextMaxHeight = 16;
        private const int TextMinWidth = 20;

        /// <summary>The band searched for icons runs a few rows above and
        /// below the text, because the icons are taller than the letters.</summary>
        private const int BandPad = 3;

        /// <summary>The icons butt up against the name. This is how far left
        /// of the first letter the last icon pixel may be before the thing to
        /// the left stops being this row's icons.</summary>
        private const int IconToNameGap = 8;

        /// <summary>Gaps INSIDE the icon band - between two glyphs, or across
        /// a thin glyph's own hollow. Wider than this ends the band.</summary>
        private const int IconGapAllowed = 4;

        /// <summary>Five icons, so a band narrower than this could not hold
        /// them and one wider is not a row.</summary>
        private const int BlockMinWidth = 40;
        private const int BlockMaxWidth = 220;

        /// <summary>A slot with less than this in it is not a glyph at all,
        /// which is what a row half off the screen looks like.</summary>
        private const int SlotMinPixels = 3;

        /// <summary>Measured: a lit icon holds 39-78 pixels of the lit palette
        /// and an unlit one holds 0-3. Nothing lands between. The threshold is
        /// the middle of that void, not a tuned number.</summary>
        private const int SlotLitRequired = 8;

        /// <summary>The icon band's density in icon-palette pixels. Real rows:
        /// 26.3%-39.5% over 84 of them. The nearest thing that passed every
        /// other test anywhere: 84.4%, a line of chat. See the class remarks.</summary>
        private const double BlockDensityMin = 0.12;
        private const double BlockDensityMax = 0.60;

        /// <summary>
        /// §281. Every area row visible in this frame. An empty list means the
        /// Pokedex was not open, or its Area list was not on screen - not an
        /// error, and the caller says so rather than writing nothing and
        /// claiming success.
        /// </summary>
        public static PokedexScanReading Read(SKBitmap frame)
        {
            var spawns = new List<PokedexSpawn>();

            int unread = 0;
            int rows = 0;

            if (frame is null || frame.Width < 200 || frame.Height < 100)
            {
                return new PokedexScanReading(
                    spawns, 0, 0, frame?.Width ?? 0, frame?.Height ?? 0, 0, 0);
            }

            SKColor[] pixels = frame.Pixels;
            int width = frame.Width;
            int height = frame.Height;

            foreach (AreaRow row in FindRows(pixels, width, height))
            {
                rows++;

                PokedexSpawn? spawn = ReadRow(frame, pixels, width, height, row);

                if (spawn is null)
                {
                    unread++;
                    continue;
                }

                spawns.Add(spawn);
            }

            // §284. Swept whatever the outcome, not only on failure: a scan
            // that DID read rows and a scan that read none are only
            // comparable if both were measured the same way, and the numbers
            // cost one pass over a frame the locator has already walked.
            (int litPixels, int namePixels) = SweepPalettes(pixels);

            Log.Information(
                "Pokedex scan: {Rows} area rows found, {Read} read, {Unread} unreadable, "
                + "{Frame} frame, {Lit} icon px, {Name} name px.",
                rows, spawns.Count, unread, $"{width}x{height}", litPixels, namePixels);

            return new PokedexScanReading(
                spawns, rows, unread, width, height, litPixels, namePixels);
        }

        /// <summary>§285. One located row: where its name is, where its five
        /// icons are, and the pitch derived from them.</summary>
        private readonly record struct AreaRow(
            int Top, int Bottom, int NameLeft, int NameRight, int BlockLeft, int BlockRight)
        {
            public double Pitch => (BlockRight - BlockLeft + 1) / (double)IconCount;
        }

        /// <summary>
        /// §284. How much of each palette is anywhere in the frame at all.
        ///
        /// Deliberately structure-free - no blobs, no bands, no slots. That is
        /// the whole value of it: every other number this class produces
        /// depends on the locator working, so when the locator is the suspect
        /// they are all equally silent. A flat count of colours can still say
        /// "the panel is right there" when the locator says nothing, and that
        /// difference is what separates a wrong window from a wrong reader.
        ///
        /// Icons are counted in the LIT palette only. The unlit one sits about
        /// 44 per channel from the panel's own background, so at any usable
        /// slack a frame-wide count of it measures the panel's background
        /// rather than its icons - fine inside a slot known to be an icon,
        /// useless over a whole screen.
        /// </summary>
        private static (int Lit, int Name) SweepPalettes(SKColor[] pixels)
        {
            int lit = 0;
            int name = 0;

            foreach (SKColor colour in pixels)
            {
                if (Near(colour, IconLit, LitSlack))
                    lit++;
                else if (Near(colour, NameNormal) || Near(colour, NameMembers))
                    name++;
            }

            return (lit, name);
        }

        /// <summary>
        /// §285. Every Area row in the frame, found by structure rather than
        /// by any fixed distance. See the class remarks for why.
        /// </summary>
        private static IEnumerable<AreaRow> FindRows(
            SKColor[] pixels, int width, int height)
        {
            bool[] name = NameMask(pixels, width, height);
            bool[] icon = IconMask(pixels, width, height);
            bool[] widened = WidenSideways(name, width, height, LetterGap);

            var rows = new List<AreaRow>();

            foreach ((int left, int top, int right, int bottom) in Blobs(widened, width, height))
            {
                // Undo the widening, so the bounds are the letters again.
                int textLeft = left + LetterGap;
                int textRight = right - LetterGap;

                int textHeight = bottom - top + 1;

                if (textHeight < TextMinHeight || textHeight > TextMaxHeight)
                    continue;

                if (textRight - textLeft + 1 < TextMinWidth)
                    continue;

                int bandTop = Math.Max(0, top - BandPad);
                int bandBottom = Math.Min(height - 1, bottom + BandPad);

                if (!FindIconBand(icon, name, width, bandTop, bandBottom, textLeft,
                        out int blockLeft, out int blockRight))
                {
                    continue;
                }

                if (!BandIsIcons(icon, width, bandTop, bandBottom, blockLeft, blockRight))
                    continue;

                var row = new AreaRow(bandTop, bandBottom, textLeft, textRight, blockLeft, blockRight);

                if (!HasFiveGlyphs(pixels, width, row))
                    continue;

                rows.Add(row);
            }

            return rows.OrderBy(r => r.Top).ThenBy(r => r.NameLeft);
        }

        /// <summary>
        /// §285. The unbroken band of icon pixels that ends just left of the
        /// name. Walked right to left from the first letter: the first icon
        /// pixel has to be close to the text or this is not a row, and the
        /// band ends at the first gap wider than one glyph's hollow.
        ///
        /// §286: and at the list's own left border. The Area list has a thin
        /// vertical line down its left edge with a round scroll thumb on it,
        /// and the thumb is drawn in the LIT icon colour. On most rows it sits
        /// five pixels clear of the first icon and the gap rule leaves it
        /// alone; on a one-row list it sat two pixels clear, the walk took it
        /// for a sixth icon, the five slots shifted by a fifth of a glyph, and
        /// a surf-only Wishiwashi was filed as spawning in grass. What tells
        /// the thumb from an icon is the line it sits on: the line is drawn in
        /// the NAME colour and runs through every row's band, and no icon has
        /// a name-coloured pixel in it (measured: zero, over every real row
        /// on hand). So a name-coloured column is the border, whatever is
        /// contiguous with it is the thumb, and the band begins at the first
        /// icon after the gap that follows the thumb.
        /// </summary>
        private static bool FindIconBand(
            bool[] icon, bool[] name, int width, int bandTop, int bandBottom, int textLeft,
            out int blockLeft, out int blockRight)
        {
            blockLeft = 0;
            blockRight = 0;

            bool started = false;
            int gap = 0;

            for (int x = textLeft - 2; x >= 0; x--)
            {
                if (ColumnHas(name, width, x, bandTop, bandBottom))
                {
                    if (!started)
                        return false;

                    // The border. Whatever is contiguous with it is the
                    // thumb; the band begins at the first icon column after
                    // the first gap to its right.
                    int cursor = x + 1;

                    while (cursor < blockRight && ColumnHas(icon, width, cursor, bandTop, bandBottom))
                        cursor++;

                    while (cursor < blockRight && !ColumnHas(icon, width, cursor, bandTop, bandBottom))
                        cursor++;

                    if (cursor >= blockRight)
                        return false;

                    blockLeft = cursor;
                    break;
                }

                if (ColumnHas(icon, width, x, bandTop, bandBottom))
                {
                    if (!started)
                    {
                        if (textLeft - 2 - x > IconToNameGap)
                            return false;

                        blockRight = x;
                        started = true;
                    }

                    blockLeft = x;
                    gap = 0;
                }
                else if (started)
                {
                    gap++;

                    if (gap > IconGapAllowed)
                        break;
                }
            }

            if (!started)
                return false;

            int bandWidth = blockRight - blockLeft + 1;

            return bandWidth >= BlockMinWidth && bandWidth <= BlockMaxWidth;
        }

        private static bool ColumnHas(bool[] mask, int width, int x, int top, int bottom)
        {
            for (int y = top; y <= bottom; y++)
            {
                if (mask[y * width + x])
                    return true;
            }

            return false;
        }

        /// <summary>
        /// §285. Icons are line art on a panel. A band that is nearly solid
        /// icon-palette is not five glyphs, it is a slab - a table header, or
        /// a run of text that happens to sit in the palette's slack.
        /// </summary>
        private static bool BandIsIcons(
            bool[] icon, int width, int bandTop, int bandBottom, int blockLeft, int blockRight)
        {
            int total = (blockRight - blockLeft + 1) * (bandBottom - bandTop + 1);

            if (total <= 0)
                return false;

            int set = 0;

            for (int y = bandTop; y <= bandBottom; y++)
            {
                int rowStart = y * width;

                for (int x = blockLeft; x <= blockRight; x++)
                {
                    if (icon[rowStart + x])
                        set++;
                }
            }

            double density = set / (double)total;

            return density >= BlockDensityMin && density <= BlockDensityMax;
        }

        /// <summary>
        /// §287. Five glyphs, lit or unlit - that is all a row has to have.
        ///
        /// §285 required more: at least one METHOD icon lit (grass or surf)
        /// and at least one TIME, on the reasoning that PRO lists an area
        /// because the Pokemon spawns there. The reasoning was right and the
        /// rule was wrong. Poliwag's Area list has rows with grass AND surf
        /// unlit - Amazon Forest, Berry Forest, Ecruteak City - because the
        /// Pokemon is FISHED there, and the panel has no fishing icon: both
        /// dark IS the fishing icon. Five
        /// of Poliwag's seven rows on one screen were thrown away for being
        /// impossible. Every other water Pokemon would have gone the same
        /// way, which is what "it really dislikes blue Pokemon" turned out to
        /// mean. The rule had already stopped rejecting anything real in
        /// §286; now it rejects nothing at all. What keeps false rows out is
        /// the structure and the density, and they were doing that job alone
        /// already.
        /// </summary>
        private static bool HasFiveGlyphs(SKColor[] pixels, int width, AreaRow row)
        {
            LitSlots(pixels, width, row, out bool complete);

            return complete;
        }

        /// <summary>
        /// §285. Which of the five icons are lit, by splitting the band into
        /// five equal slots. <paramref name="complete"/> is false when a slot
        /// holds no glyph at all, which is what a row half off the screen
        /// looks like - reported rather than guessed at, so a clipped row is
        /// never filed as a species that spawns nowhere.
        /// </summary>
        private static bool[] LitSlots(
            SKColor[] pixels, int width, AreaRow row, out bool complete)
        {
            bool[] lit = new bool[IconCount];

            complete = true;

            double pitch = row.Pitch;

            for (int i = 0; i < IconCount; i++)
            {
                int from = (int)Math.Round(row.BlockLeft + (i * pitch));
                int to = (int)Math.Round(row.BlockLeft + ((i + 1) * pitch)) - 1;

                int litCount = 0;
                int anyCount = 0;

                for (int y = row.Top; y <= row.Bottom; y++)
                {
                    int rowStart = y * width;

                    for (int x = from; x <= to; x++)
                    {
                        SKColor colour = pixels[rowStart + x];

                        if (Near(colour, IconLit, LitSlack))
                        {
                            litCount++;
                            anyCount++;
                        }
                        else if (Near(colour, IconUnlit, UnlitSlack))
                        {
                            anyCount++;
                        }
                    }
                }

                if (anyCount < SlotMinPixels)
                    complete = false;

                lit[i] = litCount >= SlotLitRequired;
            }

            return lit;
        }

        private static PokedexSpawn? ReadRow(
            SKBitmap frame, SKColor[] pixels, int width, int height, AreaRow row)
        {
            bool[] lit = LitSlots(pixels, width, row, out bool complete);

            if (!complete)
                return null;

            (string name, bool members) = ReadName(frame, pixels, width, height, row);

            if (name.Length == 0)
                return null;

            DateTime now = DateTime.UtcNow;

            return new PokedexSpawn
            {
                MapName = name,
                Land = lit[0],
                Water = lit[1],
                Morning = lit[2],
                Day = lit[3],
                Night = lit[4],
                MembersOnly = members,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            };
        }

        /// <summary>
        /// §281. The area's name, and whether it was drawn in the members
        /// colour.
        ///
        /// The membership answer does NOT come from the OCR - it comes from
        /// which of two exact colours the glyph pixels are, decided before a
        /// single character is recognised. OCR only ever supplies the name.
        /// </summary>
        private static (string Name, bool Members) ReadName(
            SKBitmap frame, SKColor[] pixels, int width, int height, AreaRow row)
        {
            int left = row.NameLeft;
            int right = Math.Min(width - 1, row.NameRight);

            int normal = 0;
            int members = 0;

            for (int y = row.Top; y <= row.Bottom; y++)
            {
                int rowStart = y * width;

                for (int x = left; x <= right; x++)
                {
                    SKColor colour = pixels[rowStart + x];

                    if (Near(colour, NameNormal)) normal++;
                    else if (Near(colour, NameMembers)) members++;
                }
            }

            if (normal + members < SlotMinPixels)
                return (string.Empty, false);

            SKRectI region = ImageOps.Intersect(
                ImageOps.MakeRect(left - 1, row.Top, right - left + 3, row.Bottom - row.Top + 1),
                ImageOps.MakeRect(0, 0, width, height));

            if (ImageOps.IsEmpty(region))
                return (string.Empty, false);

            using SKBitmap crop = ImageOps.Crop(frame, region);
            using SKBitmap prepared = PrepareForOcr(crop);

            string text = ReadText(prepared);

            return (Canonical(Clean(text)), members > normal);
        }

        /// <summary>
        /// §286. The catalogue's spelling of a read name, where the catalogue
        /// has one. LocationDictionaryService is the 578-map list the corner
        /// HUD's route OCR is already checked against (§100), and it is a
        /// bounded matcher rather than autocorrect: exact, then the classic
        /// OCR confusions folded (vv/w, l/I/1, 0/O), then a Levenshtein
        /// distance small enough that "Route 8" can never become "Route 6",
        /// with ties refused. A name it does not know is kept as read - the
        /// Pokedex lists places the catalogue does not yet (the Diamond
        /// Empire rooms, the Safari sub-areas), and dropping them would throw
        /// away a correct read to protect against a wrong one.
        /// </summary>
        private static string Canonical(string read)
        {
            if (read.Length == 0)
                return read;

            return LocationDictionaryService.TryMatch(read) ?? read;
        }

        /// <summary>
        /// §286. The name, keyed out by its COLOUR and then upscaled for
        /// Tesseract - not thresholded by brightness.
        ///
        /// §281 and §285 binarised on brightness like every other detector:
        /// max channel at or above 110 is ink. The Area list's panel is
        /// (56,66,110) - blue channel exactly 110 - so on the app's own
        /// capture the background went to ink along with the letters, and
        /// what reached Tesseract was a black slab with a few antialiased
        /// edges in it. That is where "HOIMEY." for Pattern Bush, "Coursied
        /// sk" for Corsica Island and "Zyzyrazg skt Fruzza Paigg" came from.
        /// The pasted screenshots that §285 was measured on happened to read
        /// through a luminance conversion instead, which is why the fault did
        /// not show there. Replaying the brightness rule on those same frames
        /// reproduces the garbage: 14 of 24 names.
        ///
        /// There is no reason to guess at a threshold here at all. The two
        /// name colours are known exactly - that is what decides membership -
        /// so a pixel's ink is its distance from the nearer of them: full at
        /// the exact colour, fading to nothing by <see cref="InkFade"/> per
        /// channel, which is the antialiased edge of a glyph. The panel, the
        /// icons and anything else in the crop are far outside that and
        /// vanish. Over the 68 map names in the reference frames this reads
        /// 65 exactly and the other three ("Flake VVood", "llex Forest") are
        /// the vv/w and l/I confusions LocationDictionaryService's second
        /// layer exists to fold - so all 68 land.
        ///
        /// Bilinear on the way up, for §97's reason: Tesseract's LSTM reads
        /// antialiased strokes and misreads the same glyphs as hard blocks.
        /// The keyed image is drawn with ink BRIGHT so that ThresholdToBlackAndWhite,
        /// which makes bright pixels black, produces the dark-on-light page
        /// Tesseract wants.
        /// </summary>
        private static SKBitmap PrepareForOcr(SKBitmap source)
        {
            const int scale = 4;

            using SKBitmap keyed = KeyToNameColour(source);

            SKBitmap resized = ImageOps.ResizeBilinear(keyed, keyed.Width * scale, keyed.Height * scale);

            ImageOps.ThresholdToBlackAndWhite(resized, InkThreshold);

            return resized;
        }

        /// <summary>How far from an exact name colour, per channel, a pixel
        /// stops being ink. Four times ColourSlack: the slack decides whether
        /// a pixel IS the colour, this decides how much of a glyph's soft edge
        /// to keep so the strokes read as strokes.</summary>
        private const int InkFade = 96;

        /// <summary>Half-way. The keyed image is a soft mask; after the
        /// bilinear upscale a stroke's edge is wherever it crosses the middle.</summary>
        private const int InkThreshold = 128;

        /// <summary>§286. A grey copy of the crop where brightness is
        /// closeness to either name colour: white at the exact colour, black
        /// by <see cref="InkFade"/> away from both.</summary>
        private static SKBitmap KeyToNameColour(SKBitmap source)
        {
            SKColor[] pixels = source.Pixels;
            var keyed = new SKColor[pixels.Length];

            for (int i = 0; i < pixels.Length; i++)
            {
                int distance = Math.Min(
                    Distance(pixels[i], NameNormal),
                    Distance(pixels[i], NameMembers));

                int ink = 255 - Math.Min(255, distance * 255 / InkFade);

                byte level = (byte)ink;
                keyed[i] = new SKColor(level, level, level);
            }

            var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
            result.Pixels = keyed;
            return result;
        }

        /// <summary>The largest per-channel difference - the same measure
        /// <see cref="Near"/> tests against a slack.</summary>
        private static int Distance(SKColor colour, SKColor wanted) =>
            Math.Max(
                Math.Abs(colour.Red - wanted.Red),
                Math.Max(
                    Math.Abs(colour.Green - wanted.Green),
                    Math.Abs(colour.Blue - wanted.Blue)));

        private static string ReadText(SKBitmap bitmap)
        {
            lock (SharedOcrEngine.Lock)
            {
                Engine engine = SharedOcrEngine.GetEngine();

                byte[] pngBytes = ImageOps.EncodePng(bitmap);

                using TesseractOCR.Pix.Image image =
                    TesseractOCR.Pix.Image.LoadFromMemory(pngBytes);

                using TesseractOCR.Page page = engine.Process(image, PageSegMode.SingleLine);

                return page.Text ?? string.Empty;
            }
        }

        /// <summary>Map names are words, digits and spaces - "Safari Area 2",
        /// "Mt. Moon 1F". Everything else is OCR noise from the row's edges,
        /// and a name is trimmed rather than rejected for carrying some.</summary>
        internal static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var kept = new System.Text.StringBuilder(text.Length);

            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '\'' || c == '-' || c == '.')
                    kept.Append(c);
            }

            string cleaned = System.Text.RegularExpressions.Regex
                .Replace(kept.ToString(), @"\s+", " ")
                .Trim();

            // A single character is never a map and is what a row of noise
            // collapses to.
            return cleaned.Length < 2 ? string.Empty : cleaned;
        }

        private static bool[] NameMask(SKColor[] pixels, int width, int height)
        {
            bool[] mask = new bool[width * height];

            for (int i = 0; i < mask.Length; i++)
            {
                SKColor colour = pixels[i];
                mask[i] = Near(colour, NameNormal) || Near(colour, NameMembers);
            }

            return mask;
        }

        private static bool[] IconMask(SKColor[] pixels, int width, int height)
        {
            bool[] mask = new bool[width * height];

            for (int i = 0; i < mask.Length; i++)
            {
                SKColor colour = pixels[i];
                mask[i] = Near(colour, IconLit, LitSlack) || Near(colour, IconUnlit, UnlitSlack);
            }

            return mask;
        }

        /// <summary>§285. Widen a mask sideways, so the separate letters of
        /// one word become one blob and two columns of text do not.</summary>
        private static bool[] WidenSideways(bool[] mask, int width, int height, int by)
        {
            bool[] widened = new bool[mask.Length];

            for (int y = 0; y < height; y++)
            {
                int rowStart = y * width;
                int run = 0;

                // Left to right, then right to left: a pixel is set if a mask
                // pixel is within `by` columns either side of it.
                // `by + 1` so that a mask pixel sets itself AND the `by`
                // columns after it: the reach either side is exactly `by`,
                // which is what the caller undoes to get the letters back.
                for (int x = 0; x < width; x++)
                {
                    run = mask[rowStart + x] ? by + 1 : Math.Max(0, run - 1);

                    if (run > 0)
                        widened[rowStart + x] = true;
                }

                run = 0;

                for (int x = width - 1; x >= 0; x--)
                {
                    run = mask[rowStart + x] ? by + 1 : Math.Max(0, run - 1);

                    if (run > 0)
                        widened[rowStart + x] = true;
                }
            }

            return widened;
        }

        /// <summary>§285. The bounding box of every 8-connected blob in a
        /// mask. Iterative, with an explicit stack: a line of text in a
        /// full-screen capture is thousands of pixels and recursion here would
        /// be a stack overflow waiting for a wide enough monitor.</summary>
        private static IEnumerable<(int Left, int Top, int Right, int Bottom)> Blobs(
            bool[] mask, int width, int height)
        {
            bool[] seen = new bool[mask.Length];
            var stack = new Stack<int>();
            var boxes = new List<(int, int, int, int)>();

            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || seen[start])
                    continue;

                int left = int.MaxValue, right = int.MinValue;
                int top = int.MaxValue, bottom = int.MinValue;

                seen[start] = true;
                stack.Push(start);

                while (stack.Count > 0)
                {
                    int index = stack.Pop();
                    int x = index % width;
                    int y = index / width;

                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;

                        if (ny < 0 || ny >= height)
                            continue;

                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;

                            if (nx < 0 || nx >= width)
                                continue;

                            int next = ny * width + nx;

                            if (mask[next] && !seen[next])
                            {
                                seen[next] = true;
                                stack.Push(next);
                            }
                        }
                    }
                }

                boxes.Add((left, top, right, bottom));
            }

            return boxes;
        }

        private static bool Near(SKColor colour, SKColor wanted) =>
            Near(colour, wanted, ColourSlack);

        private static bool Near(SKColor colour, SKColor wanted, int slack) =>
            Math.Abs(colour.Red - wanted.Red) <= slack
            && Math.Abs(colour.Green - wanted.Green) <= slack
            && Math.Abs(colour.Blue - wanted.Blue) <= slack;
    }
}
