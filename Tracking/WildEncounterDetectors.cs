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
    // This file groups the five OCR-based detectors that EncounterTracking.cs's
    // wild-hunting scan loop calls directly - EncounterDetector (battle title ->
    // Pokemon name), LevelDetector, CatchDetector, RareEncounterDetector, and
    // RouteDetector (the world/corner HUD, on its own timer independent of any
    // battle) - moved here from five separate files of their own, as part of
    // consolidating nine of the Tracking folder's ten detector-style files down
    // to four (this file, BossPvpDetectors.cs, PixelDetectors.cs, and
    // SharedOcrEngine.cs). See MIGRATION_GUIDE.md for the full writeup,
    // including why ProWindowFinder.cs - a tenth file in this same folder, but a
    // different kind of thing (it locates the PRO game window itself, not
    // content inside a battle screenshot) - was left out of this pass.
    //
    // Each class below is unchanged from its own file except for how it talks
    // to Tesseract: all five now call SharedOcrEngine.GetEngine() instead of
    // keeping a private Engine of their own, so five independent tessdata
    // loads become one - see SharedOcrEngine.cs. BossBattleDetector and
    // PvpBattleDetector (BossPvpDetectors.cs) share that same engine too, so
    // all seven OCR-based detectors in this folder now share exactly one.

    public static class EncounterDetector
    {
        /// <param name="looksLikeBattleTitle">True if the OCR'd title text reads
        /// like a real battle title - it contains "VS" or "Wild". Every real
        /// battle has one or both: a wild encounter titles as "VS. Wild Rattata"
        /// and a boss as "VS. &lt;name&gt;", and the log evidence behind §111 shows
        /// both surviving OCR reliably even when the rest of the line is mangled
        /// ("- VS, wild Kecleon", "VS. wWild Mantyke", "t VS, Wild Rattata").
        ///
        /// §111 made this a GATE, not just a status hint. It has always existed
        /// because BattleWindowLocator's generic dark-bar heuristic
        /// false-positives on other dark UI panels, but the method went on to
        /// return a match anyway - so any screen holding a Pokemon name
        /// anywhere in the title crop registered as a wild encounter, since
        /// TryMatchPokemon is a substring scan over every species. The PC
        /// storage boxes are exactly that screen: a grid of names and levels
        /// in a dark panel. See MIGRATION_GUIDE.md §111 for the report and
        /// the log that proved it.</param>
        public static bool TryDetectEncounter(
            SKBitmap screenshot,
            out string pokemonName,
            out bool looksLikeBattleTitle)
        {
            pokemonName = string.Empty;
            looksLikeBattleTitle = false;

            if (screenshot == null)
                return false;

            if (!BattleWindowLocator.TryLocate(
                    screenshot,
                    out SKRectI battleBounds))
            {
                return false;
            }

            // §135: the read itself lives in TryReadBattleTitle so the Set
            // Screen Boundaries window can run it on a box of its own. Same
            // steps in the same order as before; only the method boundary
            // moved.
            if (!TryReadBattleTitle(
                    screenshot,
                    battleBounds,
                    out _,
                    out looksLikeBattleTitle,
                    out string? matchedPokemon))
            {
                return false;
            }

            // §111: refuse a name matched out of something that is not a
            // battle title. Without this the check above only suppressed a
            // status message while the match itself still counted, and a
            // player opening their PC boxes had a stored Pokemon's name
            // recorded as a wild encounter - permanently, in the species
            // table and the Since Shiny / Since Form counters.
            //
            // Two signals rather than one because a missed encounter is the
            // cost of being wrong here: OCR has to lose BOTH "VS" and "Wild"
            // from the same title before a real battle is dropped, and it
            // loses neither in the evidence. A screen that is not a battle
            // has neither to lose.
            if (!looksLikeBattleTitle)
                return false;

            if (matchedPokemon is null)
                return false;

            pokemonName = matchedPokemon;
            return true;
        }

        /// <summary>
        /// §135. Reads the battle title inside <paramref name="battleBounds"/>:
        /// crops the title region, prepares it, requires enough bright
        /// pixels, OCRs it and normalizes the text. Returns false when there
        /// is nothing readable at all. <paramref name="looksLikeBattleTitle"/>
        /// is the "VS"/"Wild" test described on TryDetectEncounter, and
        /// <paramref name="matchedPokemon"/> is the species matched out of
        /// the text when that test passes - null otherwise, or when no
        /// species matched. TryDetectEncounter is this plus the locator in
        /// front and the §111 gate behind; the Set Screen Boundaries window
        /// calls it directly on the box the player drew.
        /// </summary>
        public static bool TryReadBattleTitle(
            SKBitmap screenshot,
            SKRectI battleBounds,
            out string titleText,
            out bool looksLikeBattleTitle,
            out string? matchedPokemon)
        {
            titleText = string.Empty;
            looksLikeBattleTitle = false;
            matchedPokemon = null;

            SKRectI titleRegion =
                BattleWindowLocator.GetBattleTitleRegion(
                    battleBounds
                );

            using SKBitmap titleCrop =
                ImageOps.Crop(
                    screenshot,
                    titleRegion
                );

            using SKBitmap prepared =
                PrepareForOcr(titleCrop);

            if (!ContainsEnoughBrightPixels(titleCrop))
            {
                LogTitleOcrIfChanged(
                    "too-dark", string.Empty, titleRegion, battleBounds,
                    screenshot.Width, screenshot.Height, interesting: false);
                return false;
            }

            string rawText =
                ReadText(prepared);

            if (string.IsNullOrWhiteSpace(rawText))
            {
                LogTitleOcrIfChanged(
                    "no-text", string.Empty, titleRegion, battleBounds,
                    screenshot.Width, screenshot.Height, interesting: false);
                return false;
            }

            titleText =
                NormalizeOcrText(rawText);

            looksLikeBattleTitle =
                titleText.Contains("vs", StringComparison.OrdinalIgnoreCase) ||
                titleText.Contains("wild", StringComparison.OrdinalIgnoreCase);

            if (looksLikeBattleTitle &&
                TryMatchPokemon(titleText, out string pokemonName) &&
                !string.IsNullOrWhiteSpace(pokemonName))
            {
                matchedPokemon = pokemonName;
            }

            // §363: this is the line the report could not answer. "A 'VS'
            // title is readable but no Pokemon name matched it" was all a
            // Report a Problem could say, because this method logged
            // nothing. The outcome name says which of the four ways the
            // read ended, and the text says what Tesseract actually
            // produced - so a garbled species, a crop that cut the name
            // off, and a species genuinely missing from the table stop
            // looking identical from the outside.
            LogTitleOcrIfChanged(
                matchedPokemon is not null
                    ? "matched:" + matchedPokemon
                    : looksLikeBattleTitle
                        ? "title-but-no-species"
                        : "not-a-title",
                titleText, titleRegion, battleBounds,
                screenshot.Width, screenshot.Height,
                interesting: looksLikeBattleTitle);

            return true;
        }

        // §363. EncounterDetector runs on every scan tick - roughly ten a
        // second - whatever is on screen, so an unconditional log line would
        // bury the file. Two throttles: a line is written only when the
        // outcome or the text changes, and an uninteresting outcome (no
        // battle title in the crop) is written at most once every two
        // seconds on top of that. A title that reads as a battle is never
        // rate-limited, because that is the case worth having.
        private static string? lastLoggedTitleOutcome;
        private static long lastUninterestingTitleLogTicks;

        private static void LogTitleOcrIfChanged(
            string outcome,
            string text,
            SKRectI region,
            SKRectI battleBounds,
            int screenshotWidth,
            int screenshotHeight,
            bool interesting)
        {
            string normalized =
                string.IsNullOrWhiteSpace(text)
                    ? "(empty)"
                    : text.Trim();

            string key = outcome + "|" + normalized;

            if (key == lastLoggedTitleOutcome)
                return;

            if (!interesting)
            {
                long now = Environment.TickCount64;

                if (now - lastUninterestingTitleLogTicks < 2000)
                    return;

                lastUninterestingTitleLogTicks = now;
            }

            lastLoggedTitleOutcome = key;

            Log.Information(
                "EncounterDetector title OCR attempt: outcome={Outcome}, text='{OcrText}', " +
                "region=({RX},{RY},{RW}x{RH}), battleBounds=({BX},{BY},{BW}x{BH}), " +
                "screenshot={SW}x{SH}",
                outcome,
                normalized,
                region.Left, region.Top, region.Width, region.Height,
                battleBounds.Left, battleBounds.Top, battleBounds.Width, battleBounds.Height,
                screenshotWidth, screenshotHeight);
        }

        private static string ReadText(
            SKBitmap bitmap)
        {
            lock (SharedOcrEngine.Lock)
            {
                Engine engine = SharedOcrEngine.GetEngine();

                byte[] pngBytes =
                    ImageOps.EncodePng(bitmap);

                using TesseractOCR.Pix.Image image =
                    TesseractOCR.Pix.Image.LoadFromMemory(pngBytes);

                using TesseractOCR.Page page =
                    engine.Process(
                        image,
                        PageSegMode.SingleLine
                    );

                return page.Text ?? string.Empty;
            }
        }

        private static bool ContainsEnoughBrightPixels(
    SKBitmap source)
        {
            SKColor[] pixels = source.Pixels;

            int brightPixels = 0;
            int checkedPixels = pixels.Length;

            for (int i = 0; i < pixels.Length; i++)
            {
                SKColor color = pixels[i];

                int brightness =
                    (color.Red + color.Green + color.Blue) / 3;

                // Wild Pokémon title text is nearly white.
                if (brightness >= 210)
                {
                    brightPixels++;
                }
            }

            if (checkedPixels == 0)
                return false;

            double brightRatio =
                brightPixels / (double)checkedPixels;

            return brightRatio >= 0.01;
        }

        private static SKBitmap PrepareForOcr(
            SKBitmap source)
        {
            // Upscale because the title text is fairly small.
            const int scale = 3;

            SKBitmap resized =
                ImageOps.Resize(
                    source,
                    source.Width * scale,
                    source.Height * scale
                );

            // Convert to high-contrast black/white.
            // The battle-title text is bright.
            ImageOps.ThresholdToBlackAndWhite(resized, 150);

            return resized;
        }

        private static string NormalizeOcrText(
            string text)
        {
            return text
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("|", " ")
                .Trim();
        }

        private static bool TryMatchPokemon(
     string ocrText,
     out string pokemonName)
        {
            pokemonName = string.Empty;

            if (string.IsNullOrWhiteSpace(ocrText))
                return false;

            // =====================================================
            // 1. CHECK ALTERNATE / REGIONAL FORMS FIRST
            // =====================================================
            //
            // This MUST happen before normal species.
            //
            // Example:
            // "Wild Voltorb-Hisui"
            //
            // If we checked normal species first,
            // "Voltorb" would match before "Voltorb-Hisui".
            // =====================================================

            foreach (var form in
                     PokemonSpriteService.AllForms
                         .OrderByDescending(
                             f => f.Name.Length))
            {
                // Canonical form name
                if (ocrText.Contains(
                        form.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    pokemonName = form.Name;
                    return true;
                }

                // OCR aliases
                foreach (string alias in form.OcrAliases
                             .OrderByDescending(a => a.Length))
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        continue;

                    // Don't allow the base species alias to steal
                    // a normal encounter.
                    if (alias.Equals(
                            form.SpeciesName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (ocrText.Contains(
                            alias,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        pokemonName = form.Name;
                        return true;
                    }
                }
            }

            // =====================================================
            // 2. NORMAL SPECIES
            // =====================================================

            foreach (var pokemon in
                     PokemonSpriteService.AllPokemon
                         .OrderByDescending(
                             p => p.Name.Length))
            {
                if (ocrText.Contains(
                        pokemon.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    pokemonName = pokemon.Name;
                    return true;
                }
            }

            // =====================================================
            // 3. NORMAL SPECIES OCR ALIASES
            // =====================================================

            foreach (var pokemon in
                     PokemonSpriteService.AllPokemon)
            {
                foreach (string alias in pokemon.OcrAliases
                             .OrderByDescending(a => a.Length))
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        continue;

                    if (ocrText.Contains(
                            alias,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        pokemonName = pokemon.Name;
                        return true;
                    }
                }
            }

            return false;
        }

        // NOTE: kept for parity with the original (which also had this method
        // unused/dead - live callers go through BattleWindowLocator.GetBattleTitleRegion).
        public static SKRectI GetBattleTitleRegion(
            SKRectI battleBounds)
        {
            int titleHeight =
                (int)Math.Round(
                    battleBounds.Height * 0.09
                );

            titleHeight =
                Math.Clamp(
                    titleHeight,
                    45,
                    75
                );

            return ImageOps.MakeRect(
                battleBounds.Left,
                battleBounds.Top,
                battleBounds.Width,
                titleHeight
            );
        }
    }

    /// <summary>
    /// §396. How much a level read is worth, in the order the consensus
    /// trusts it: the tally in EncounterTracker keeps one count per value of
    /// this enum and the BEST quality that produced any read decides the
    /// battle's level - see EncounterTracker's level-consensus comment for
    /// why a count of agreeing reads is the weaker signal. Declared in
    /// trust order on purpose; the tracker indexes its tallies by the
    /// integer value.
    /// </summary>
    public enum LevelReadQuality
    {
        /// <summary>The SparseText read held the label and one number and
        /// nothing else - "Lv.23", "L.36": the tag exactly as PRO draws it,
        /// which every measured corpus reads right when it reads at all.</summary>
        Clean,

        /// <summary>The SparseText read carried more than the tag - a glint
        /// line under it, a status badge's letters, a split "3 0", a label
        /// letter digitised ("L5.39") - and ExtractLevel had to pick the
        /// number out from among other digit runs, or the label was gone
        /// altogether. Usually still right, but something else was in the
        /// crop.</summary>
        Cluttered,

        /// <summary>SparseText found nothing usable and the large-window
        /// SingleBlock retry supplied the value. Right most of the time, and
        /// when wrong, wrong in a clean-looking systematic way - §98 caught
        /// it reading a level-37 tag as "Lv.87" on every frame - and a
        /// systematic misread agrees with itself on every frame, so many
        /// rescued reads are no stronger than one.</summary>
        Rescued
    }

    /// <summary>
    /// Reads the wild Pokemon's own name/level tag - the small "&lt;Name&gt; &lt;gender
    /// symbol&gt; Lv. NN" label PRO draws directly above its HP bar during a battle,
    /// top-left of the battle panel (the player's own active Pokemon gets an
    /// identical tag, mirrored bottom-right - see the reference screenshot this was
    /// calibrated from). Same crop -> upscale -> threshold -> Tesseract pipeline as
    /// every other detector in this folder (see EncounterDetector.cs/RouteDetector.cs).
    ///
    /// LevelRegionX/Y/Width/Height below were recalibrated against six reference
    /// screenshots the user sent together - two different window resolutions and
    /// two different in-game GUI scale settings, all showing the same "Lv. NN" tag
    /// (Wingull/Rattata/Corsola, levels 30/38/39) - after the very first version of
    /// this crop (a guess from a single screenshot, positioned too far left, over
    /// the Pokemon's NAME rather than its level) turned out to read "Lv. ?" for
    /// 100% of the user's real logged encounters. The recalibration located the
    /// small male/female gender-symbol icon that always sits immediately before
    /// "Lv." by its color (see GenderDetector.cs, added at the same time) and
    /// measured the level text's own bounding box relative to it directly, rather
    /// than guessing a region by eye - both landed inside a strikingly tight
    /// fraction-of-battleBounds range across all six screenshots (level text x:
    /// 0.178-0.216, y: 0.139-0.162) despite the differing resolutions/scales/names,
    /// which is why a single fixed-fraction crop is trusted here at all. Still
    /// only confirmed against 2-digit levels (30/38/39) - a 3-digit "Lv. 100" has
    /// deliberately been given extra width margin below but hasn't been seen in a
    /// real screenshot yet.
    ///
    /// A user report of the level going unread on roughly 99% of real encounters
    /// (gender detection working fine on the same encounters, from GenderDetector.cs
    /// above) led to switching ReadText's Tesseract mode from SingleLine to
    /// SparseText - see ReadText's own comment for the reasoning. This is a
    /// best-reasoned fix, not one confirmed against a real screenshot the way the
    /// region fractions above were: GenderDetector's own pixel-color approach
    /// can't be corrupted by background art the way an OCR line-read can, which is
    /// the most likely explanation for why one detector was reliable and the
    /// other wasn't when both read the same on-screen tag. DebugRegionOverlay.cs
    /// now also draws a box for this region (previously it drew none for either
    /// Level or Gender) - the next "Report a Problem" will show directly whether
    /// this region is landing in the right place, which the raw OCR text logged
    /// by LogOcrAttemptIfChanged below will show for the read itself.
    ///
    /// §396: every read also reports a <see cref="LevelReadQuality"/> - the tag
    /// alone, the tag among other things, or the SingleBlock rescue - because
    /// a wrong read from this crop is systematic (the same frame content misreads
    /// the same way on every sample), so EncounterTracker's consensus ranks reads
    /// by how they were obtained before it counts them. A level-23 Hariyama that
    /// registered as 23 and was then "corrected" to 28 by later reads of the
    /// same, unchanged tag is the report behind it.
    /// </summary>
    public static class LevelDetector
    {
        // Fractions of battleBounds (not the full screenshot) - same crop origin as
        // EncounterDetector.GetBattleTitleRegion. See the class doc comment above
        // for how these were measured. Starts a little left of the gender symbol's
        // own left edge (0.1598 measured minimum) rather than right at the level
        // text - a stray icon pixel or two at the crop's left edge is harmless to
        // the regex-based extraction below, whereas starting too tight risked
        // clipping the "L" of "Lv." on a slightly wider name/symbol layout.
        private const float LevelRegionX = 0.15f;
        private const float LevelRegionY = 0.11f;
        private const float LevelRegionWidth = 0.17f;
        private const float LevelRegionHeight = 0.09f;

        /// <summary>
        /// Crops the wild Pokemon's name/level tag out of <paramref name="screenshot"/>
        /// (using <paramref name="battleBounds"/> as the crop origin, same as
        /// EncounterDetector's title region) and OCRs it for a level number. Returns
        /// null if nothing readable came back - callers should treat null as "level
        /// unknown for this encounter," not as an error; see the class doc comment's
        /// calibration caveat for why this is more likely to happen here than for
        /// the longer-tuned detectors elsewhere in this folder.
        /// </summary>
        public static int? TryDetectLevel(SKBitmap screenshot, SKRectI battleBounds) =>
            TryDetectLevel(screenshot, battleBounds, out _);

        /// <summary>
        /// §396. The same read, also saying how it was obtained - see
        /// <see cref="LevelReadQuality"/>. The quality is meaningful only
        /// when a level comes back; for a null read it is left at Clean and
        /// means nothing.
        /// </summary>
        public static int? TryDetectLevel(SKBitmap screenshot, SKRectI battleBounds, out LevelReadQuality quality)
        {
            quality = LevelReadQuality.Clean;
            SKRectI levelRegion = GetLevelRegion(battleBounds);

            using SKBitmap levelCrop = ImageOps.Crop(screenshot, levelRegion);
            using SKBitmap? prepared = PrepareForOcr(levelCrop);

            // No near-white pixels isolated at all - the tag isn't rendered
            // (yet), or the crop is pure background art. Report null without
            // running Tesseract on nothing. See MIGRATION_GUIDE.md §95.
            if (prepared is null)
            {
                LogOcrAttemptIfChanged("(no level text isolated)", levelRegion);
                return null;
            }

            string rawText = ReadText(prepared, PageSegMode.SparseText);

            // §101: when diagnostic recording is on, retain this read's small
            // prepared crop alongside the text - the encode runs ONLY behind
            // the volatile flag, so normal tracking never pays for it.
            if (TrackerDiagnostics.RecordingEnabled)
            {
                TrackerDiagnostics.RecordOcr(
                    "LevelCrop", rawText, ImageOps.EncodePng(prepared));
            }

            LogOcrAttemptIfChanged(rawText, levelRegion);

            int? level = ExtractLevel(rawText);

            if (level is not null)
            {
                // §396: the tag and nothing but the tag, or a read that had
                // company in the crop - the consensus ranks the two apart.
                quality = ReadsAsTagAlone(rawText)
                    ? LevelReadQuality.Clean
                    : LevelReadQuality.Cluttered;
            }

            // Large-GUI retry (MIGRATION_GUIDE.md §96): at bigger GUI scales
            // the tag's strokes render proportionally thinner after isolation,
            // and SparseText - measured against a real large-GUI clip - goes
            // blank on them at every upscale factor while SingleBlock reads
            // them fine. Gated to genuinely large battle windows (>= 60% of
            // the capture height) because on SMALL text SingleBlock is the
            // less reliable mode (it hallucinated 36 -> 37/38 on real small-
            // GUI frames); below the gate a failed sparse read stays null and
            // the consensus sampler simply tries again next tick.
            if (level is null &&
                battleBounds.Height >= screenshot.Height * 0.6)
            {
                string blockText = ReadText(prepared, PageSegMode.SingleBlock);

                LogOcrAttemptIfChanged("[block] " + blockText, levelRegion);

                level = ExtractLevel(blockText);

                // §396: a rescued value, whatever the block text looked like
                // - this mode's known failure is a clean-looking wrong digit,
                // not clutter, so its text cannot vouch for it.
                quality = LevelReadQuality.Rescued;
            }

            return level;
        }

        /// <summary>§396. Whether a SparseText read is the tag and nothing
        /// else: the label's "L" is present (the whitelist admits no other
        /// letter) and the text holds exactly one digit run - the level.
        /// "Lv.23" and "L.36" qualify; "Lv.3 0", "L5.39", "Lv.30" over a
        /// glint's "4", and a bare "23" with the label gone do not. Those
        /// still parse (ExtractLevel's cascade exists for them) but they are
        /// counted as Cluttered, below the reads that needed no picking.</summary>
        private static bool ReadsAsTagAlone(string rawText) =>
            rawText.Contains('L') && Regex.Matches(rawText, @"\d+").Count == 1;

        /// <summary>Same crop DebugRegionOverlay.cs draws a box for in a "Report a
        /// Problem" screenshot - see the class doc comment above for how these
        /// fractions were measured. Extracted out of TryDetectLevel so both share
        /// one calculation, the same reasoning GenderDetector.GetGenderRegion
        /// already follows for its own detector.</summary>
        public static SKRectI GetLevelRegion(SKRectI battleBounds)
        {
            int x = battleBounds.Left + (int)(battleBounds.Width * LevelRegionX);
            int y = battleBounds.Top + (int)(battleBounds.Height * LevelRegionY);
            int width = (int)(battleBounds.Width * LevelRegionWidth);
            int height = (int)(battleBounds.Height * LevelRegionHeight);

            return ImageOps.MakeRect(x, y, width, height);
        }

        /// <summary>
        /// Tolerant of OCR mangling the period/spacing after "Lv" (same reasoning as
        /// RouteDetector.ExtractTimeOfDay's tolerant "Po[ck]e Time" pattern) - matches
        /// "Lv. 30", "Lv 30", "Lv:30", etc. anywhere in the crop rather than at a
        /// fixed position, since the crop's left edge intentionally leaves a little
        /// slack before the "L" (see LevelRegionX's comment) that a stray
        /// recognized character could land in ahead of the real match.
        /// When the "Lv" label itself gets mangled - at the reference window size it
        /// reliably reads "L.36", dropping the v - digits are only trusted from a
        /// LINE that still carries an "L": real captures showed the isolated crop
        /// can also pick up a short glint from the HP bar under the tag, which the
        /// digit whitelist renders as a separate junk line ("L.30" then "4" on its
        /// own line), and the previous version's take-the-trailing-digits fallback
        /// reported that junk as the level - the "level 4" bug. A digit run on a
        /// line with no "L" is only accepted when it is the ONLY digit run in the
        /// whole text (the label vanished entirely, leaving just the number), never
        /// when a second run competes with it. Within an L-line, digits are read
        /// through the §98 anchor cascade (after the last ".", then after the
        /// last "v", then the trailing run - never straight after the "L").
        /// MIGRATION_GUIDE.md §96-§98 have the measured evidence behind each
        /// rule.
        /// Every candidate is rejected outside 1-100 (PRO's level cap) rather than
        /// trusting any 1-3 digit OCR match, so a stray misread number elsewhere in
        /// the crop doesn't get reported as a plausible-looking but wrong level.
        /// </summary>
        private static int? ExtractLevel(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return null;

            // §129 widened every digit pattern in this method from
            // \d{1,3} to \d+. See the trailing-run branch below for the
            // report that forced it - the short form does not limit what is
            // ACCEPTED, it silently truncates what is READ.
            Match match = Regex.Match(rawText, @"Lv\.?\s*[:\.]?\s*(\d+)", RegexOptions.IgnoreCase);

            if (match.Success && TryParseLevel(match.Groups[1].Value, out int level))
                return level;

            // Label mangled ("L.30", "L 30", bare "L36"): take the first digit
            // run from a line that still contains the label's "L" (the OCR
            // whitelist only permits uppercase L, so a plain char test is
            // exact), scanning lines in order so the tag's own line wins over
            // anything isolated below it.
            bool sawLabel = false;

            foreach (string line in rawText.Split('\n'))
            {
                if (!line.Contains('L'))
                    continue;

                sawLabel = true;

                // §98 anchor cascade, measured against the app's own log and
                // the Report-a-Problem captures: when the label survives only
                // partially, its LETTERS can read as digits (a real "Lv. 39"
                // came back "L5.39" - the v as a 5 - and registered level 5),
                // so a digit run straight after the "L" cannot be trusted.
                // The tag draws its number after the label's punctuation, so
                // digits are taken from after the last "." first, then after
                // the last "v", and only then as the line's TRAILING run -
                // never from directly after the L, where a digitized letter
                // sits. Runs that fail the 1-100 gate fall through to the
                // next anchor (a merged glint like "L5830" fails all three
                // and stays null for the SingleBlock retry to rescue).
                //
                // §129: that last sentence was only true by luck until
                // now. Every pattern here matched \d{1,3}, which does not
                // reject a longer run - it CHOPS it and hands on a piece.
                // On "L5830" the pieces are "583" and "0", and the trailing
                // one fails the gate, so the claim held by accident. On
                // "L3686" - a real Lv. 36 read back from a scaled window -
                // the pieces are "368" and "6", and 6 is a perfectly valid
                // level, so the tracker recorded a level 36 Krabby as level
                // 6 and wrote it to the permanent log as fact.
                //
                // The gate was never the problem; it was being handed a
                // fragment instead of the number. \d+ matches the whole run
                // and lets the 1-100 check reject it entire, which is what
                // the comment above always described.
                int dotIndex = line.LastIndexOf('.');

                if (dotIndex >= 0 &&
                    TryFirstRunAfter(line, dotIndex + 1, out int fromDot))
                {
                    return fromDot;
                }

                int vIndex = line.LastIndexOf('v');

                if (vIndex >= 0 &&
                    TryFirstRunAfter(line, vIndex + 1, out int fromV))
                {
                    return fromV;
                }

                MatchCollection lineRuns = Regex.Matches(line, @"\d+");

                if (lineRuns.Count > 0 &&
                    TryParseLevel(lineRuns[^1].Value, out level))
                {
                    return level;
                }
            }

            if (sawLabel)
                return null;

            // No trace of the label anywhere: only trust the digits when they
            // are the single digit run in the text, so two competing numbers
            // (level-sized junk plus the real level, with no "L" left to tell
            // them apart) yield null and leave the choice to the next
            // consensus sample instead of a coin flip.
            MatchCollection runs = Regex.Matches(rawText, @"\d+");

            if (runs.Count == 1 && TryParseLevel(runs[0].Value, out level))
                return level;

            return null;
        }

        /// <summary>First 1-3 digit run at or beyond <paramref name="start"/>,
        /// validated through TryParseLevel - the §98 anchor cascade's step, see
        /// ExtractLevel.</summary>
        private static bool TryFirstRunAfter(string line, int start, out int level)
        {
            // §129: \d+, not \d{1,3} - the whole run or nothing. See
            // ExtractLevel's trailing-run branch.
            Match match = Regex.Match(line[start..], @"(\d+)");

            if (match.Success)
                return TryParseLevel(match.Groups[1].Value, out level);

            level = 0;
            return false;
        }

        /// <summary>The 1-100 gate shared by every ExtractLevel path - PRO's level
        /// cap, per ExtractLevel's doc comment.</summary>
        private static bool TryParseLevel(string digits, out int level)
        {
            return int.TryParse(digits, out level) && level is >= 1 and <= 100;
        }

        private static string ReadText(SKBitmap bitmap, PageSegMode pageSegMode)
        {
            lock (SharedOcrEngine.Lock)
            {
                Engine engine = SharedOcrEngine.GetEngine();

                // This crop only ever contains "Lv." plus 1-3 digits - nothing else
                // legitimately appears in it. Restricting Tesseract's character set
                // to exactly that (rather than the unrestricted recognition every
                // other detector sharing this engine leaves it at) stops it from
                // ever misreading a digit as a similar-looking letter or vice versa,
                // which is exactly the kind of mistake ExtractLevel's regex has no
                // way to catch or correct after the fact. Narrowed and restored
                // immediately around this one read, both inside this same lock, so
                // no other detector sharing the engine can ever observe it mid-
                // whitelist - back when this had its own private engine the
                // whitelist could just be set once at Initialize() time and left
                // there forever; a shared engine can't be left permanently narrowed
                // without corrupting every other detector's own reads. SetVariable
                // returns false if the variable name is unrecognized by this
                // Tesseract build - logged rather than thrown, since a failed
                // whitelist should degrade to the engine's unrestricted default,
                // not break level detection outright.
                if (!engine.SetVariable("tessedit_char_whitelist", "0123456789Lv."))
                {
                    Log.Warning("LevelDetector could not set a digit whitelist on the Tesseract engine - OCR will use its unrestricted default character set instead.");
                }

                byte[] pngBytes = ImageOps.EncodePng(bitmap);

                using TesseractOCR.Pix.Image image = TesseractOCR.Pix.Image.LoadFromMemory(pngBytes);
                // SparseText, not SingleLine - see the class doc comment's "misread
                // 99% of the time" note. SingleLine forces the whole crop to be read
                // as one continuous line, with no tolerance for anything that isn't
                // part of it; the tag renders directly over battle-scene artwork
                // (grass/dirt/trees), not a solid panel, so thresholding often turns
                // that background into noise blobs the single-line assumption has no
                // way to set aside, corrupting the read even when "Lv. NN" itself is
                // rendered perfectly clearly. SparseText looks for text-like content
                // wherever it appears in the crop instead of assuming the entire
                // region is one line, which is far more tolerant of exactly this
                // kind of non-text visual noise around the actual target text.
                using TesseractOCR.Page page = engine.Process(image, pageSegMode);
                string text = page.Text ?? string.Empty;

                // Reset immediately, still inside the lock - see the comment above
                // where the whitelist was set. Empty string is Tesseract's
                // documented way to clear a whitelist back to unrestricted
                // recognition, restoring exactly what every other detector sharing
                // this engine expects to find before its own read.
                engine.SetVariable("tessedit_char_whitelist", "");

                return text;
            }
        }

        // Fewer isolated pixels than this means "no text here" - the crop is
        // returned as null and OCR is skipped entirely. "Lv. NN" at the
        // reference window size isolates ~150-300 pixels; background art
        // isolates a handful at most.
        private const int MinimumTextPixels = 25;

        // Water-scene rescue thresholds - see PrepareForOcr's rescue comment
        // and MIGRATION_GUIDE.md §100. A pale water background is bright and
        // even enough to enter the near-white text mask itself; where it
        // touches the glyphs it welds them into one giant component that
        // RemoveSolidBlobs then erases text and all, leaving only stroke
        // dust. That wreckage has a signature no healthy frame shares -
        // a large removed mass AND only tiny fragments left - measured
        // across every §95-§99 land corpus (which never trips it: land
        // frames either remove nothing, or remove clouds while keeping
        // big intact glyph components) and nine live water captures
        // (which always trip it: ~880 removed, largest survivor 14-18px).
        private const int RescueRemovedPixels = 300;
        private const int RescueLargestComponent = 40;

        // The rescue mask's extra gate: text is neutral (white/gray), water
        // is blue-heavy, so genuine glyph pixels keep Blue within this of
        // Red while pale water sits at +80 and above. Applied ONLY in the
        // rescue pass - the everyday mask stays exactly §96's, because
        // video-sourced regression clips carry chroma-subsampled color where
        // a global blue limit strips real glyph edges (measured: c1's clean
        // 48-vote phase degrades to 26 votes under a global limit, and is
        // untouched with the limit confined to the rescue).
        private const int BlueTintLimit = 50;

        // Glyph height (pre-border) the isolated text is scale-normalized to
        // before OCR - see PrepareForOcr's render comment and
        // MIGRATION_GUIDE.md §97. Roughly Tesseract's comfortable size band:
        // a fixed 4x upscale put large-GUI glyphs near 80px, where reads
        // degraded badly against the app's own traineddata.
        private const int TargetGlyphHeight = 36;

        /// <summary>
        /// Tag-text isolation, round two - §95 introduced it, §96 refined it
        /// against four fresh clips at two GUI scales (see MIGRATION_GUIDE.md
        /// §96 for the measured evidence behind every number here). One
        /// lenient gate instead of §95's strict-then-lenient pair: the strict
        /// pass could "succeed" with a starved, skeletal mask (247 of ~2000
        /// glyph pixels at the larger GUI scale) and thereby block the pass
        /// that would have read correctly. Near-white here means every channel
        /// at 150+, channel spread at most 80. One new distractor class needed
        /// handling: near-white BACKGROUND ART - clouds in the sky - which
        /// enters the mask as large SOLID masses, unlike text's thin strokes,
        /// so any connected component over 100 pixels whose bounding-box fill
        /// exceeds 0.55 is erased (each glyph is its own small component;
        /// digit clusters never come close to that fill at that size). The
        /// survivors are cropped to their bounding box plus margin and
        /// rendered dark-on-white from the crop's REAL luminance - the mask
        /// only selects pixels, the game's own antialiasing supplies the
        /// shapes (§97) - then scale-normalized to roughly TargetGlyphHeight
        /// glyphs with bilinear interpolation and given a clean border. Null
        /// when fewer than MinimumTextPixels remain - the caller reports
        /// "level unknown" rather than OCRing background art.
        /// </summary>
        private static SKBitmap? PrepareForOcr(SKBitmap source)
        {
            SKColor[] pixels = source.Pixels;
            int width = source.Width;
            int height = source.Height;

            bool[] isText = BuildTextMask(pixels, rejectBlueTint: false, out int count);

            int removedPixels = RemoveSolidBlobs(isText, width, height);
            count -= removedPixels;

            // Water rescue (§100): when the blob filter had to remove a large
            // near-white mass AND what survived is only fragment dust, the
            // "mass" was a pale water background that had welded itself to
            // the glyphs - the removal amputated the text (live water
            // encounters registered "Level null" for a whole battle, block
            // reads of just "L"). Rebuild the mask with the blue-tint gate,
            // which excludes the water while leaving the neutral glyph
            // pixels; if that yields something readable, use it, otherwise
            // keep the original attempt. Land never trips the trigger (no
            // removal, or cloud removal beside big intact glyphs), so this
            // path adds nothing to the frames that already read perfectly -
            // measured against every §95-§99 corpus in MIGRATION_GUIDE.md
            // §100.
            if ((removedPixels > RescueRemovedPixels &&
                 LargestComponentSize(isText, width, height) < RescueLargestComponent) ||
                count < MinimumTextPixels)
            {
                bool[] rescued = BuildTextMask(pixels, rejectBlueTint: true, out int rescuedCount);
                rescuedCount -= RemoveSolidBlobs(rescued, width, height);

                if (rescuedCount >= MinimumTextPixels)
                {
                    isText = rescued;
                    count = rescuedCount;
                }
            }

            if (count < MinimumTextPixels)
                return null;

            int minX = width, minY = height, maxX = -1, maxY = -1;

            for (int i = 0; i < isText.Length; i++)
            {
                if (!isText[i])
                    continue;

                int x = i % width;
                int y = i / width;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            minX = Math.Max(0, minX - 4);
            minY = Math.Max(0, minY - 4);
            maxX = Math.Min(width - 1, maxX + 4);
            maxY = Math.Min(height - 1, maxY + 4);

            int textWidth = maxX - minX + 1;
            int textHeight = maxY - minY + 1;

            // Grayscale render (§97): the mask only DECIDES which pixels belong
            // to the text - grown two pixels so each stroke keeps its
            // antialiased skirt (DilateMask) - and the output copies the real
            // luminance of those pixels, inverted to dark-on-white. The
            // previous hard black/white stamp fed Tesseract blocky staircase
            // edges, and measured against the app's own eng.traineddata that
            // misread glyph shapes outright: a real "Lv. 39" read "Lv.34" on
            // every frame (the 9's loop corner reads as a 4), and the
            // large-GUI scale went skeletal. The game's own antialiasing is
            // what the LSTM reads best. MIGRATION_GUIDE.md §97 has the
            // per-clip numbers.
            bool[] keep = DilateMask(isText, width, height);

            var textPixels = new SKColor[textWidth * textHeight];
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int index = y * width + x;
                    byte value = 255;

                    if (keep[index])
                    {
                        SKColor color = pixels[index];
                        int luminance = (color.Red + color.Green + color.Blue) / 3;
                        value = (byte)(255 - luminance);
                    }

                    textPixels[(y - minY) * textWidth + (x - minX)] =
                        new SKColor(value, value, value);
                }
            }

            var textOnly = new SKBitmap(textWidth, textHeight);
            textOnly.Pixels = textPixels;

            // Scale-normalize instead of a fixed 4x: glyphs land at roughly
            // TargetGlyphHeight pixels whatever the GUI scale (textHeight
            // carries 8px of bbox margin, hence the subtraction), clamped so
            // a tiny mask can't explode and text already at size isn't
            // shrunk.
            int glyphHeight = Math.Max(1, textHeight - 8);
            float scale = Math.Clamp((float)TargetGlyphHeight / glyphHeight, 1f, 6f);
            int scaledWidth = Math.Max(1, (int)Math.Round(textWidth * scale));
            int scaledHeight = Math.Max(1, (int)Math.Round(textHeight * scale));

            SKBitmap scaled = ImageOps.ResizeBilinear(textOnly, scaledWidth, scaledHeight);
            textOnly.Dispose();

            // A clean white border around the text - Tesseract reads glyphs
            // touching the image edge noticeably worse.
            const int border = 12;
            int borderedWidth = scaled.Width + border * 2;
            int borderedHeight = scaled.Height + border * 2;

            var borderedPixels = new SKColor[borderedWidth * borderedHeight];
            Array.Fill(borderedPixels, SKColors.White);

            SKColor[] scaledPixels = scaled.Pixels;
            for (int y = 0; y < scaled.Height; y++)
            {
                Array.Copy(
                    scaledPixels, y * scaled.Width,
                    borderedPixels, (y + border) * borderedWidth + border,
                    scaled.Width);
            }

            scaled.Dispose();

            var bordered = new SKBitmap(borderedWidth, borderedHeight);
            bordered.Pixels = borderedPixels;

            return bordered;
        }

        /// <summary>
        /// Erases connected components that look like solid image masses
        /// rather than glyph strokes (over 100 pixels AND bounding-box fill
        /// over 0.55) - in practice, near-white clouds in the battle scene's
        /// sky at larger GUI scales. Plain four-neighbor flood fill over the
        /// small crop (a few thousand pixels); returns how many pixels were
        /// removed so the caller can adjust its count.
        /// </summary>
        private static int RemoveSolidBlobs(bool[] isText, int width, int height)
        {
            int[] componentOf = new int[isText.Length];
            var stack = new Stack<int>();
            int removedTotal = 0;
            int componentId = 0;

            for (int seed = 0; seed < isText.Length; seed++)
            {
                if (!isText[seed] || componentOf[seed] != 0)
                    continue;

                componentId++;
                stack.Push(seed);
                componentOf[seed] = componentId;

                int size = 0;
                int minX = width, minY = height, maxX = -1, maxY = -1;
                var members = new List<int>();

                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    members.Add(i);
                    size++;

                    int x = i % width;
                    int y = i / width;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;

                    if (x > 0 && isText[i - 1] && componentOf[i - 1] == 0) { componentOf[i - 1] = componentId; stack.Push(i - 1); }
                    if (x < width - 1 && isText[i + 1] && componentOf[i + 1] == 0) { componentOf[i + 1] = componentId; stack.Push(i + 1); }
                    if (y > 0 && isText[i - width] && componentOf[i - width] == 0) { componentOf[i - width] = componentId; stack.Push(i - width); }
                    if (y < height - 1 && isText[i + width] && componentOf[i + width] == 0) { componentOf[i + width] = componentId; stack.Push(i + width); }
                }

                double fill = size /
                    (double)((maxX - minX + 1) * (maxY - minY + 1));

                if (size > 100 && fill > 0.55)
                {
                    foreach (int i in members)
                        isText[i] = false;

                    removedTotal += size;
                }
            }

            return removedTotal;
        }

        /// <summary>The §96 near-white gate (every channel at 150+, spread at
        /// most 80), with the §100 water-rescue variant behind
        /// <paramref name="rejectBlueTint"/>: additionally require Blue to
        /// exceed Red by no more than BlueTintLimit, which excludes pale
        /// water pixels while keeping the neutral white/gray tag text. See
        /// the rescue comment in PrepareForOcr for when the caller uses
        /// which.</summary>
        private static bool[] BuildTextMask(SKColor[] pixels, bool rejectBlueTint, out int count)
        {
            bool[] isText = new bool[pixels.Length];
            count = 0;

            for (int i = 0; i < pixels.Length; i++)
            {
                SKColor color = pixels[i];
                int low = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
                int high = Math.Max(color.Red, Math.Max(color.Green, color.Blue));

                if (low < 150 || high - low > 80)
                    continue;

                if (rejectBlueTint && color.Blue - color.Red > BlueTintLimit)
                    continue;

                isText[i] = true;
                count++;
            }

            return isText;
        }

        /// <summary>Size of the largest 4-neighbor connected component still
        /// set in <paramref name="isText"/> - the §100 rescue trigger's
        /// "is anything glyph-sized left?" half. Same flood-fill pattern as
        /// RemoveSolidBlobs, only ever run when that method just removed a
        /// large mass (the short-circuit in PrepareForOcr), so it costs
        /// nothing on ordinary frames.</summary>
        private static int LargestComponentSize(bool[] isText, int width, int height)
        {
            int[] componentOf = new int[isText.Length];
            var stack = new Stack<int>();
            int componentId = 0;
            int largest = 0;

            for (int seed = 0; seed < isText.Length; seed++)
            {
                if (!isText[seed] || componentOf[seed] != 0)
                    continue;

                componentId++;
                stack.Push(seed);
                componentOf[seed] = componentId;

                int size = 0;

                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    size++;

                    int x = i % width;
                    int y = i / width;
                    if (x > 0 && isText[i - 1] && componentOf[i - 1] == 0) { componentOf[i - 1] = componentId; stack.Push(i - 1); }
                    if (x < width - 1 && isText[i + 1] && componentOf[i + 1] == 0) { componentOf[i + 1] = componentId; stack.Push(i + 1); }
                    if (y > 0 && isText[i - width] && componentOf[i - width] == 0) { componentOf[i - width] = componentId; stack.Push(i - width); }
                    if (y < height - 1 && isText[i + width] && componentOf[i + width] == 0) { componentOf[i + width] = componentId; stack.Push(i + width); }
                }

                if (size > largest)
                    largest = size;
            }

            return largest;
        }

        /// <summary>Grows the text mask by ONE 4-neighbor dilation pass - just
        /// enough that the §97 grayscale render keeps each stroke's immediate
        /// antialiased skirt, without deciding any SHAPE itself (§96 measured
        /// what happens when dilation shapes the glyphs: the 6's counter closes
        /// and the whole regression corpus reads "38"; here the grown region only
        /// selects which real pixels are copied, and the copied luminance
        /// supplies the shape). §97 shipped with two passes; §98 measured that at
        /// the windowed-client scale the second pass gave the label's letters
        /// enough flesh to read as DIGITS ("L5.39" for a real "Lv. 39" - the v
        /// as a 5 - and "Lv.87" for a real 37 out of the SingleBlock retry),
        /// while one pass reads every Report-a-Problem capture cleanly in every
        /// PSM mode and keeps the full §96-§97 corpus results. Returns a new
        /// array - the caller's mask is left untouched.</summary>
        private static bool[] DilateMask(bool[] isText, int width, int height)
        {
            bool[] grown = new bool[isText.Length];

            for (int i = 0; i < isText.Length; i++)
            {
                if (!isText[i])
                    continue;

                grown[i] = true;

                int x = i % width;
                int y = i / width;
                if (x > 0) grown[i - 1] = true;
                if (x < width - 1) grown[i + 1] = true;
                if (y > 0) grown[i - width] = true;
                if (y < height - 1) grown[i + width] = true;
            }

            return grown;
        }

        private static string? lastLoggedOcrText;

        private static void LogOcrAttemptIfChanged(string rawText, SKRectI region)
        {
            string normalized = rawText.Replace("\r", " ").Trim();

            if (normalized == lastLoggedOcrText)
                return;

            lastLoggedOcrText = normalized;

            Log.Information(
                "LevelDetector OCR (region {Region}): {OcrText}",
                region,
                string.IsNullOrWhiteSpace(normalized) ? "(empty)" : normalized);

            // §101 Admin Console tap - heartbeat always, retained text only
            // while diagnostic recording is enabled (see TrackerDiagnostics).
            TrackerDiagnostics.RecordOcr("Level", normalized);
        }
    }

    public enum CatchResult
    {
        None,
        Success,
        Failed,

        /// <summary>The player ran, and the game said so: "You have run away
        /// from the wild Pokemon."</summary>
        RunAway,

        /// <summary>§123. The battle ended some other way with nothing
        /// caught - the wild Pokemon fainted, or the EXP line appeared.
        ///
        /// This used to be reported as RunAway too, and for the one thing
        /// that consumed it - releasing the encounter lock - that was
        /// harmless, because both mean "battle over, nothing caught". It
        /// stopped being harmless the moment a per-species Ran From counter
        /// needed the number: every knockout would have been counted as a
        /// run. So the two are separate results now, and every consumer
        /// that only cares "did the battle end" handles both together.</summary>
        BattleEnded
    }

    public static class CatchDetector
    {
        /// <summary>
        /// §276. The lower dialogue box, read as text - lifted out of
        /// <see cref="Detect"/> unchanged so the PVP battle-log reader
        /// (Tracking/PvpBattleLogReader.cs) can read the SAME box through the
        /// SAME crop and the SAME preparation, rather than growing a second
        /// opinion of where PRO prints its battle text and how to OCR it.
        ///
        /// Empty when the crop lands outside the screenshot or reads as
        /// nothing, which is not an error - the box is blank between messages.
        /// </summary>
        public static string ReadBattleMessage(
            SKBitmap screenshot,
            SKRectI battleBounds)
        {
            SKRectI region =
                GetBattleMessageRegion(
                    battleBounds,
                    new SKSizeI(screenshot.Width, screenshot.Height)
                );

            if (region.Width <= 0 ||
                region.Height <= 0)
            {
                return string.Empty;
            }

            using SKBitmap crop =
                ImageOps.Crop(
                    screenshot,
                    region
                );

            using SKBitmap prepared =
                PrepareForOcr(crop);

            string rawText =
                ReadText(prepared);

            // Diagnostic logging - see BossBattleDetector.cs/RareEncounterDetector.cs
            // for why this pattern is worth having here too: a fixed-percentage
            // crop tuned against one screenshot may not land correctly on every
            // user's GUI scale/resolution, even though it's confirmed working for
            // several users already.
            LogOcrAttemptIfChanged(rawText, region, battleBounds, screenshot.Width, screenshot.Height);

            return rawText;
        }

        public static CatchResult Detect(
            SKBitmap screenshot,
            SKRectI battleBounds)
        {
            string rawText =
                ReadBattleMessage(
                    screenshot,
                    battleBounds
                );

            if (string.IsNullOrWhiteSpace(rawText))
                return CatchResult.None;

            CatchResult result = Classify(rawText);

            if (result != CatchResult.None)
            {
                Log.Information(
                    "CatchDetector matched {Result} from OCR text '{OcrText}'",
                    result, rawText);
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
                "CatchDetector OCR attempt: text='{OcrText}', region=({RX},{RY},{RW}x{RH}), " +
                "battleBounds=({BX},{BY},{BW}x{BH}), screenshot={SW}x{SH}",
                normalized,
                region.Left, region.Top, region.Width, region.Height,
                battleBounds.Left, battleBounds.Top, battleBounds.Width, battleBounds.Height,
                screenshotWidth, screenshotHeight);
        }

        private static CatchResult Classify(
            string text)
        {
            string normalized =
                text
                    .Replace("\r", " ")
                    .Replace("\n", " ")
                    .Replace("|", " ")
                    .Trim();

            // Successful catch ends the battle.
            if (normalized.Contains(
                    "you caught",
                    StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(
                    "success",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CatchResult.Success;
            }

            // ============================================================
            // FAILED CATCH
            // ============================================================

            // Exact result first.
            if (normalized.Contains(
                    "broke free",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CatchResult.Failed;
            }

            // Remove punctuation/spaces so OCR fragmentation matters less.
            string compact =
                new string(
                    normalized
                        .ToLowerInvariant()
                        .Where(char.IsLetter)
                        .ToArray()
                );

            // Common OCR variations.
            if (compact.Contains("brokefree") ||
                compact.Contains("brokefre") ||
                compact.Contains("brokfree") ||
                compact.Contains("brokefre") ||
                compact.Contains("broxefree") ||
                compact.Contains("brokeftee"))
            {
                return CatchResult.Failed;
            }

            // Final tolerant comparison against "brokefree".
            if (ContainsFuzzyText(
                    compact,
                    "brokefree",
                    2))
            {
                return CatchResult.Failed;
            }

            // "You cannot run away!" - an Arena Trap/Mean Look REFUSAL. It
            // contains the words "run away", and §101's log evidence caught
            // the old check matching it and unlocking the tracker in the
            // middle of a battle that was still running (a trapped Diglett
            // battle at 16:17:49 in the evidence log). The battle has NOT
            // ended - report None so the encounter lock holds.
            if (normalized.Contains(
                    "cannot run away",
                    StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(
                    "can not run away",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CatchResult.None;
            }

            // Running successfully ends the battle.
            if (normalized.Contains(
                    "run away",
                    StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(
                    "ran away",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CatchResult.RunAway;
            }

            // The wild Pokemon fainting ends the battle exactly like running
            // does: battle over, nothing caught. §101's evidence log showed
            // this text on screen, readable ("The opposing Diglett
            // fainted!"), with no rule here to match it - so KO'd battles
            // never unlocked, and because the SECONDARY NEW-BATTLE RECOVERY
            // deliberately requires a DIFFERENT species, a chain of same-
            // species encounters (Dugtrio after Dugtrio in Digletts Cave,
            // where Arena Trap forces fights instead of runs) stayed
            // suppressed for minutes - the reported "missed Diglett/Dugtrio"
            // and the 91-second dead window. "opposing" is required so a
            // trainer's OWN Pokemon fainting (which switches, not ends) can
            // never match; the EXP line is a second chance for frames where
            // the fainted line was missed, and only ever appears once the
            // wild Pokemon is down.
            //
            // §123 changed what this returns. It used to report RunAway,
            // on the reasoning that downstream only meant "battle ended
            // without a catch" - true at the time, and no longer true now
            // that a Ran From column counts runs per species. A knockout is
            // not a run. BattleEnded carries the identical meaning to every
            // existing consumer while keeping the two countable apart.
            if (normalized.Contains("opposing", StringComparison.OrdinalIgnoreCase) &&
                normalized.Contains("fainted", StringComparison.OrdinalIgnoreCase))
            {
                return CatchResult.BattleEnded;
            }

            if (normalized.Contains("gained", StringComparison.OrdinalIgnoreCase) &&
                normalized.Contains("exp", StringComparison.OrdinalIgnoreCase))
            {
                return CatchResult.BattleEnded;
            }

            return CatchResult.None;
        }

        private static bool ContainsFuzzyText(
    string source,
    string target,
    int maximumDistance)
        {
            if (string.IsNullOrWhiteSpace(source) ||
                string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            if (source.Contains(
                    target,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            int minimumLength =
                Math.Max(
                    1,
                    target.Length - maximumDistance
                );

            int maximumLength =
                Math.Min(
                    source.Length,
                    target.Length + maximumDistance
                );

            for (int length = minimumLength;
                 length <= maximumLength;
                 length++)
            {
                for (int start = 0;
                     start + length <= source.Length;
                     start++)
                {
                    string section =
                        source.Substring(
                            start,
                            length
                        );

                    if (LevenshteinDistance(
                            section,
                            target) <= maximumDistance)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static int LevenshteinDistance(
    string a,
    string b)
        {
            int[,] distance =
                new int[
                    a.Length + 1,
                    b.Length + 1
                ];

            for (int i = 0;
                 i <= a.Length;
                 i++)
            {
                distance[i, 0] = i;
            }

            for (int j = 0;
                 j <= b.Length;
                 j++)
            {
                distance[0, j] = j;
            }

            for (int i = 1;
                 i <= a.Length;
                 i++)
            {
                for (int j = 1;
                     j <= b.Length;
                     j++)
                {
                    int cost =
                        a[i - 1] == b[j - 1]
                            ? 0
                            : 1;

                    distance[i, j] =
                        Math.Min(
                            Math.Min(
                                distance[i - 1, j] + 1,
                                distance[i, j - 1] + 1
                            ),
                            distance[i - 1, j - 1] + cost
                        );
                }
            }

            return distance[
                a.Length,
                b.Length
            ];
        }

        private static string ReadText(
            SKBitmap bitmap)
        {
            lock (SharedOcrEngine.Lock)
            {
                Engine engine = SharedOcrEngine.GetEngine();

                byte[] pngBytes =
                    ImageOps.EncodePng(bitmap);

                using TesseractOCR.Pix.Image image =
                    TesseractOCR.Pix.Image.LoadFromMemory(pngBytes);

                using TesseractOCR.Page page =
                    engine.Process(
                        image,
                        PageSegMode.SingleLine
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

            ImageOps.ThresholdToBlackAndWhite(resized, 150);

            return resized;
        }

        public static SKRectI GetBattleMessageRegion(
            SKRectI battleBounds,
            SKSizeI screenshotSize)
        {
            // The catch result text sits in the lower dialogue box.
            // Keep this deliberately tight so Tesseract sees primarily
            // the sentence rather than Kadabra / HP bars / battlefield.

            int x =
                battleBounds.Left +
                (int)(battleBounds.Width * 0.035);

            int y =
                battleBounds.Top +
                (int)(battleBounds.Height * 0.86);

            int width =
                (int)(battleBounds.Width * 0.62);

            int height =
                (int)(battleBounds.Height * 0.11);

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

    public enum RareEncounterType
    {
        None,
        Shiny,
        Form
    }

    public static class RareEncounterDetector
    {
        public static RareEncounterType Detect(
            SKBitmap screenshot,
            SKRectI battleBounds)
        {
            if (screenshot == null)
                return RareEncounterType.None;

            // §206: look for the popup before falling back to cropping where
            // it usually is. A 30-second recording of a real form popup
            // measured the percentage crop's right edge landing anywhere
            // between x 725 and x 793 against a sentence that runs to x 775 -
            // so whether the last word survives is luck, which is what
            // section 94's clipped-sentence fallbacks were papering over. The
            // same measurement found the crop's top edge sitting BELOW the
            // popup's header, so the shortest and highest-contrast string in
            // the whole popup was never read at all. See RarePopupLocator.
            if (RarePopupLocator.TryLocate(screenshot, out RarePopupBox popup))
            {
                string header = ReadRegion(screenshot, popup.Header);
                string sentence = ReadRegion(screenshot, popup.Body);
                string located = (header + " " + sentence).Trim();

                LogOcrAttemptIfChanged(located, popup.Body, battleBounds, screenshot.Width, screenshot.Height);

                RareEncounterType locatedResult = Classify(located);

                if (locatedResult != RareEncounterType.None)
                {
                    Log.Information(
                        "RareEncounterDetector matched {Result} from the located popup " +
                        "(width {Width}px, header '{Header}', body '{Body}')",
                        locatedResult, popup.Width, header.Trim(), sentence.Trim());

                    return locatedResult;
                }

                // A popup was on screen and neither line classified. That is
                // worth saying out loud - it means the locator is working and
                // the OCR or the phrase list is not - and it is not a reason
                // to skip the older path below.
                Log.Warning(
                    "RareEncounterDetector found a popup ({Width}px wide) but could not classify it: " +
                    "header '{Header}', body '{Body}'",
                    popup.Width, header.Trim(), sentence.Trim());
            }

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

            string text = ReadRegion(screenshot, region);

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

            // CLIPPED-SENTENCE FALLBACKS - see MIGRATION_GUIDE.md §94. A real
            // 30-second recording showed GetRareEncounterRegion's right edge
            // cutting the popup's sentence down to "You encountered a rare
            // form Pokem": the final word can be lost to the crop on some
            // window sizes/GUI scales, so requiring the full "... Pokemon"
            // phrase silently missed the whole popup (and with it the entire
            // caught-form record for that hunt). Instead of one exact
            // sentence, accept two independent anchors from the same popup
            // text - its verb plus the type-specific words. Nothing else that
            // appears inside this crop (battle-scene art, the popup itself)
            // produces both anchors, and ordinary battle messages render in
            // the bottom dialogue region, entirely outside this crop - so
            // this stays far from matching unrelated text.
            bool saysEncountered = normalized.Contains(
                "encountered",
                StringComparison.OrdinalIgnoreCase);

            if (saysEncountered &&
                normalized.Contains(
                    "rare form",
                    StringComparison.OrdinalIgnoreCase))
            {
                return RareEncounterType.Form;
            }

            if (saysEncountered &&
                normalized.Contains(
                    "shiny",
                    StringComparison.OrdinalIgnoreCase))
            {
                return RareEncounterType.Shiny;
            }

            return RareEncounterType.None;
        }

        /// <summary>§206. Crop, prepare and read one region - the three
        /// steps Detect used to do inline, now shared by the located popup's
        /// two regions and the fallback crop alike, so all three go through
        /// exactly the same preparation.</summary>
        private static string ReadRegion(SKBitmap screenshot, SKRectI region)
        {
            if (region.Width <= 0 || region.Height <= 0)
                return string.Empty;

            using SKBitmap crop = ImageOps.Crop(screenshot, region);
            using SKBitmap prepared = PrepareForOcr(crop);

            return ReadText(prepared);
        }

        private static string ReadText(
            SKBitmap bitmap)
        {
            lock (SharedOcrEngine.Lock)
            {
                Engine engine = SharedOcrEngine.GetEngine();

                byte[] pngBytes =
                    ImageOps.EncodePng(bitmap);

                using TesseractOCR.Pix.Image image =
                    TesseractOCR.Pix.Image.LoadFromMemory(pngBytes);

                using TesseractOCR.Page page =
                    engine.Process(
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
            // Fourth recalibration - this time for placement, not because OCR
            // came back garbled. The third recalibration (19% left, 34% top, 53%
            // width, 42% height, see MIGRATION_GUIDE.md for its own history)
            // still wasn't cropping the same spot the user wanted for the box
            // this region gets drawn as in a "Report a Problem" screenshot
            // (DebugRegionOverlay.cs) - a real screenshot with a hand-drawn box
            // marking the exact area the user felt this should occupy instead
            // (positioned over plain battle-scene artwork - grass/dirt/the wild
            // Pokemon's sprite - with neither Pokemon's name/level tag anywhere
            // inside it, and deliberately kept small).
            //
            // Measured that hand-drawn box's own pixel bounds against the same
            // screenshot's battle window bounds (the same conversion-to-percentage-
            // of-battleBounds approach every recalibration in this method has
            // used) rather than eyeballing new percentages directly: roughly 14%
            // left, 27% top, 45% width, 42% height. The height came out almost
            // identical to the third recalibration's own 42% - only left/top/width
            // actually needed to move. Height was trimmed slightly further, to
            // 39%, after checking the estimated box against the player's own
            // Pokemon's name/level tag position (bottom-right of the battle
            // scene) and finding the third recalibration's full 42% would have
            // just barely clipped the top edge of that tag - shrinking by 3
            // points keeps this region clear of it with margin, matching the "no
            // other words in the box" requirement directly.
            //
            // Still a percentage-of-battleBounds approximation from one
            // screenshot's hand-drawn annotation, not pixel-perfect detection of
            // anything - the same caveat every prior recalibration here has
            // carried, and the same fix: DebugRegionOverlay.cs draws this region
            // as a box on the next "Report a Problem," so how well it now lines
            // up with the intended spot can be confirmed directly instead of
            // guessed at again.

            int x =
                battleBounds.Left +
                (int)(battleBounds.Width * 0.14);

            int y =
                battleBounds.Top +
                (int)(battleBounds.Height * 0.27);

            int width =
                (int)(battleBounds.Width * 0.45);

            int height =
                (int)(battleBounds.Height * 0.39);

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

    /// <summary>
    /// Reads the game client's own top-right corner HUD - the map-name banner
    /// next to the minimap buttons - via the same crop -> upscale -> threshold
    /// -> Tesseract pipeline every other detector in this folder uses (see
    /// BossBattleDetector.cs for the original template). Raw OCR text is never
    /// shown to the user: every plausible line is validated against the §100
    /// map catalog (LocationDictionaryService, DataFiles/pro-locations.json)
    /// after the user-editable substitutions in MapNameCorrectionService.cs,
    /// and a read that confirms no catalog map leaves the previous reading in
    /// place.
    ///
    /// The crop rectangle (CornerRegionX/Y/Width/Height below) and
    /// PageSegMode.SparseText began as first-pass estimates from one reference
    /// screenshot; the §101 27-capture fixture and §102's full-day log replay
    /// have since exercised them at the windowed sizes in evidence without a
    /// geometry miss.
    ///
    /// The crop inevitably also contains the client's "Local Time"/"Poke Time"
    /// clock panel, so those lines appear in raw OCR logs and diagnostic
    /// recordings - that is simply what the screenshot region contains, not a
    /// tracked value. §102 removed the day/night parsing that used to ride
    /// along here (ExtractTimeOfDay and the event's second output): it fed a
    /// TimeOfDayText property no window ever bound, so it was dead plumbing,
    /// removed at the user's request.
    /// </summary>
    public static class RouteDetector
    {
        // Fractions of the full captured window - see the class doc comment above.
        private const float CornerRegionX = 0.74f;
        private const float CornerRegionY = 0f;
        private const float CornerRegionWidth = 0.26f;
        private const float CornerRegionHeight = 0.18f;

        /// <summary>
        /// Crops the top-right corner of <paramref name="screenshot"/> and OCRs
        /// it for a map name, returning true only when a candidate line was
        /// CONFIRMED against the map catalog. Callers treat false as "leave
        /// whatever was shown before," never "clear it."
        ///
        /// §356: a true result is a canonical map name OR a canonical map
        /// name plus an interior floor - "Mt. Summer 2F 2" when the catalog
        /// only lists "Mt. Summer". It is NOT itself a catalog entry in that
        /// case; see LocationDictionaryService's layer 4 for the rule and for
        /// why a floor token is required before anything is split.
        /// (Before §102 this also parsed a day/night bucket from the corner's
        /// "Poke Time" readout; that output fed nothing any window displayed
        /// and was removed - see MIGRATION_GUIDE.md §102.)
        /// </summary>
        public static bool TryDetectCorner(
            SKBitmap screenshot,
            out string? routeName)
        {
            routeName = null;

            int x = (int)(screenshot.Width * CornerRegionX);
            int y = (int)(screenshot.Height * CornerRegionY);
            int width = (int)(screenshot.Width * CornerRegionWidth);
            int height = (int)(screenshot.Height * CornerRegionHeight);

            SKRectI cornerRegion = ImageOps.MakeRect(x, y, width, height);

            using SKBitmap cornerCrop = ImageOps.Crop(screenshot, cornerRegion);
            using SKBitmap prepared = PrepareForOcr(cornerCrop);

            string rawText = ReadText(prepared, PageSegMode.SparseText);

            LogOcrAttemptIfChanged(rawText, cornerRegion);

            if (string.IsNullOrWhiteSpace(rawText))
                return false;

            // §101: every plausible line gets its shot at the map catalog,
            // longest first, instead of §100's pick-one-line-then-validate.
            // The corner crop's raw text is multi-line ("Poke Time: 20:08",
            // stray glyphs from the minimap buttons, and the location line),
            // and the evidence log showed "Route 11" sitting right there in
            // the raw text on read after read while the old single-line
            // selection never surfaced it - its letters-only ratio filter
            // scored "Route 11" at 5 letters of 8 characters and rejected
            // every numbered route with a two-digit number as "not a place
            // name" (Diglett's Cave passed the same filter easily, which is
            // exactly the reported asymmetry). Each candidate goes through
            // the user-maintainable corrections (MapNameCorrectionService,
            // e.g. "Yulcan" -> "Vulcan") and then the §100 catalog matcher;
            // the first candidate the catalog confirms wins, and a frame
            // with nothing confirmable stays null - callers already treat
            // that as "keep showing the previous reading."
            routeName = null;

            List<string> candidates = EnumerateRouteNameCandidates(rawText);

            // §356: PRO's spawn list, when it is open, prints the map name a
            // second time as "Pokemon in <map>" - on a solid blue panel, so
            // it OCRs far better than the HUD line over the game world. In
            // the report this came from the HUD line read "M. Summer 2F 2"
            // and the panel read "Mt. Summer 2F 2" in the same frame.
            //
            // It is NOT trusted on its own. That panel has a Map Name search
            // box, so its header can name a map the player is not standing
            // on, and a tracker that believed it would silently record the
            // wrong location. It is used only when a corner-HUD line agrees
            // that it is the same place - and "same place" requires the
            // digits to match exactly, so 2F 2 never stands in for 2F 3.
            string? panelName = ExtractSpawnPanelName(rawText);

            if (panelName != null
                && candidates.Any(c => LocationDictionaryService.LooksLikeSameName(c, panelName)))
            {
                string correctedPanel = MapNameCorrectionService.Apply(panelName);
                routeName = LocationDictionaryService.TryMatch(correctedPanel);
            }

            if (routeName == null)
            {
                foreach (string candidate in candidates)
                {
                    string corrected = MapNameCorrectionService.Apply(candidate);
                    string? matched = LocationDictionaryService.TryMatch(corrected);

                    if (matched != null)
                    {
                        routeName = matched;
                        break;
                    }
                }
            }

            return routeName != null;
        }

        /// <summary>
        /// §356. The map name out of the spawn list's "Pokemon in &lt;map&gt;"
        /// header, or null when that panel is not in the crop. Deliberately
        /// tolerant about the word "Pokemon" itself - it is the part the OCR
        /// mangles and the part that carries no information - and deliberately
        /// strict about there being an " in " after it.
        ///
        /// The caller checks this against the HUD line before using it; see
        /// TryDetectCorner for why it is never trusted alone.
        /// </summary>
        private static string? ExtractSpawnPanelName(string rawText)
        {
            foreach (string line in rawText.Split(
                         '\n',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Match header = SpawnPanelHeader.Match(line);

                if (!header.Success)
                    continue;

                string name = header.Groups["name"].Value.Trim();

                if (name.Length >= 3 && name.Count(char.IsLetter) >= 3)
                    return name;
            }

            return null;
        }

        private static readonly Regex SpawnPanelHeader = new(
            @"^Pok[^\s]{0,6}\s+in\s+(?<name>.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// Every OCR'd line that could plausibly be a place name, longest
        /// first - candidate ENUMERATION only; the §100 map catalog
        /// (LocationDictionaryService in TryDetectCorner above) is what
        /// decides whether any of them names an actual map. The filter here
        /// deliberately counts digits as name characters - §101 fixed the
        /// letters-only ratio that scored "Route 11" at 62% letters and
        /// threw away every numbered route before the catalog ever saw it -
        /// while still requiring at least three letters (a real word), so
        /// pure digit/punctuation junk (dash runs, "50", a stray "J") never
        /// reaches the matcher.
        /// </summary>
        private static List<string> EnumerateRouteNameCandidates(string rawText)
        {
            string[] lines = rawText.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var candidates = new List<string>();

            foreach (string line in lines)
            {
                if (line.Length < 3)
                    continue;

                if (line.Contains("Time", StringComparison.OrdinalIgnoreCase))
                    continue;

                // §356: the spawn panel's header is handled separately in
                // TryDetectCorner and must never be matched as if it were
                // the corner HUD - it can name a searched map.
                if (SpawnPanelHeader.IsMatch(line))
                    continue;

                int letterCount = line.Count(char.IsLetter);

                if (letterCount < 3)
                    continue;

                int nameCharacterCount = line.Count(c => char.IsLetterOrDigit(c));

                if (nameCharacterCount < line.Length * 0.7)
                    continue;

                candidates.Add(line);
            }

            candidates.Sort((a, b) => b.Length.CompareTo(a.Length));

            return candidates;
        }

        private static string ReadText(SKBitmap bitmap, PageSegMode pageSegMode)
        {
            lock (SharedOcrEngine.Lock)
            {
                Engine engine = SharedOcrEngine.GetEngine();

                byte[] pngBytes = ImageOps.EncodePng(bitmap);

                using TesseractOCR.Pix.Image image = TesseractOCR.Pix.Image.LoadFromMemory(pngBytes);
                using TesseractOCR.Page page = engine.Process(image, pageSegMode);

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

        private static string? lastLoggedOcrText;

        private static void LogOcrAttemptIfChanged(string rawText, SKRectI cornerRegion)
        {
            string normalized = rawText.Replace("\r", " ").Trim();

            if (normalized == lastLoggedOcrText)
                return;

            lastLoggedOcrText = normalized;

            Log.Information(
                "RouteDetector OCR (corner region {Region}): {OcrText}",
                cornerRegion,
                string.IsNullOrWhiteSpace(normalized) ? "(empty)" : normalized);

            // §101 Admin Console tap - see TrackerDiagnostics.
            TrackerDiagnostics.RecordOcr("Route", normalized);
        }
    }
}
