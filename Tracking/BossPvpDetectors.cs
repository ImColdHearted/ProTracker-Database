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
    // This file groups the two OCR-based detectors used by the Boss Cooldowns
    // and PVP tracking loops, rather than the wild-hunting one -
    // BossBattleDetector (called from BossCooldownTracker.cs and
    // Services/BossCooldownService.cs) and PvpBattleDetector (called from
    // PvpTracker.cs) - moved here from two separate files of their own, as
    // part of consolidating nine of the Tracking folder's ten detector-style
    // files down to four (this file, WildEncounterDetectors.cs,
    // PixelDetectors.cs, and SharedOcrEngine.cs). See MIGRATION_GUIDE.md for
    // the full writeup.
    //
    // Both classes are unchanged from their own files except for how they
    // talk to Tesseract: both now call SharedOcrEngine.GetEngine() instead of
    // keeping a private Engine of their own - the same shared engine
    // WildEncounterDetectors.cs's five OCR detectors use, so all seven
    // OCR-based detectors in this folder now share exactly one. See
    // SharedOcrEngine.cs.

    /// <summary>Result of BossBattleDetector.DetectBattleEnd - whether a boss
    /// battle's win/loss message has appeared yet, and which outcome it showed.
    /// The distinction is a no-op for an ordinary single-NPC boss (either one
    /// starts its cooldown the same way), but matters for MultiNpcRequiresAll
    /// bosses - see BossCooldownTracker.ScanOnce.</summary>
    public enum BossBattleOutcome
    {
        None,
        Won,
        Lost
    }

    /// <summary>
    /// Detects boss battles (as opposed to wild encounters) and when they end, so
    /// EncounterTracking.cs can automatically start a boss's cooldown - see
    /// MIGRATION_GUIDE.md for the full feature writeup.
    ///
    /// Reuses BattleWindowLocator's title-region crop and CatchDetector's
    /// message-region crop rather than inventing new ones - both are already
    /// proven-working screen positions for a "PlayerName VS. OpponentName" title
    /// bar and a bottom-left battle-log message, which boss battles use the exact
    /// same UI elements for (just different text content) as wild encounters do.
    /// </summary>
    public static class BossBattleDetector
    {
        // Bosses whose battle title doesn't show their real name (e.g. The Pumpkin
        // King's battle title just says "VS. Trainer") - OCR-based name matching
        // can't identify these, so they're skipped here and stay manual-only via
        // the Boss Cooldowns window. Add more bossIds here if you find others like
        // this - matches DataFiles/Bosses/<bossId>.json's bossId field.
        private static readonly HashSet<string> ExcludedFromAutoDetection =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "thepumpking"
            };

        /// <summary>
        /// Bosses fought as 2+ SEPARATE 1-on-1 battles against different named
        /// NPCs, where the combined name stored as this bossId's Name in
        /// DataFiles/Bosses/&lt;bossId&gt;.json (e.g. "Shary &amp; Shaui") never
        /// appears in a single battle's title - only one NPC's own name does
        /// (e.g. "VS. Shary" or "VS. Shaui"). TryDetectBoss checks these names
        /// directly, ahead of the generic whole-name/last-word matching below,
        /// which would otherwise recognize at most one of the names (whichever
        /// happens to be the last word of the combined name) and never the rest.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string[]> MultiNpcSubNames =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["SharyAndShaui"] = new[] { "Shary", "Shaui" },
                ["MedusaAndEldir"] = new[] { "Medusa", "Eldir" },
                ["GamersPewdieAndDiepy"] = new[] { "Pewdie", "Diepy" },
                ["JessieAndJames"] = new[] { "Jessie", "James" },
            };

        /// <summary>
        /// Which of the MultiNpcSubNames bosses need EVERY listed name beaten -
        /// in whatever order the player fights them in - before
        /// BossCooldownTracker may start the cooldown (a real user confirmed
        /// they don't always fight the same one first, so this can't just be
        /// "whichever NPC is detected first"). Any MultiNpcSubNames bossId NOT
        /// listed here (Jessie &amp; James: per the wiki, "Either Jessie or
        /// James can be challenged") keeps the app's existing one-battle-is-
        /// enough rule - it only needed the dictionary above so both individual
        /// names get recognized at all, not this "wait for both" treatment too.
        ///
        /// A LOSS against any one of these bosses' required NPCs ends the whole
        /// attempt right away rather than waiting for the rest - confirmed by a
        /// real user: losing means the player doesn't get a chance to fight the
        /// other NPC(s) at all, unlike a win, which only means "this one is
        /// done." See BossCooldownTracker.ScanOnce for where that distinction is
        /// actually applied.
        /// </summary>
        public static readonly IReadOnlySet<string> MultiNpcRequiresAll =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SharyAndShaui",
                "MedusaAndEldir",
                "GamersPewdieAndDiepy",
            };

        // (bossId, Name) pairs, longest name first so "Elite Four Lorelei"-style
        // longer names (if any ever exist) match before a shorter substring would.
        private static List<(string BossId, string Name)>? bossCatalog;

        /// <summary>
        /// Checks the battle title for a known boss name. Returns false for wild
        /// encounters (their title says "VS. Wild &lt;Pokemon&gt;", which won't match
        /// any real boss name) and for excluded bosses (see ExcludedFromAutoDetection).
        ///
        /// <paramref name="confirmedWild"/> is set true only when the OCR text
        /// unambiguously read a wild-encounter title ("...VS. Wild ...") - the
        /// caller (BossCooldownTracker) uses this to stop retrying immediately
        /// instead of burning its whole detection-attempt budget on a battle that
        /// was never going to be a boss in the first place. Every other false
        /// case (empty/garbled OCR, no "VS" yet, a boss name that hasn't rendered
        /// clearly enough to match) leaves it false so the caller keeps retrying.
        ///
        /// <paramref name="matchedSubNpc"/> is set only when <paramref name="bossId"/>
        /// is one of MultiNpcSubNames (e.g. Shary &amp; Shaui) - it names exactly
        /// which individual NPC this battle was against, which BossCooldownTracker
        /// needs before it can tell whether every required NPC (see
        /// MultiNpcRequiresAll) has been beaten yet. Left null for every ordinary
        /// single-NPC boss.
        /// </summary>
        public static bool TryDetectBoss(
            SKBitmap screenshot,
            SKRectI battleBounds,
            out string? bossId,
            out string? bossName,
            out bool confirmedWild,
            out string? matchedSubNpc)
        {
            bossId = null;
            bossName = null;
            confirmedWild = false;
            matchedSubNpc = null;

            var catalog = GetBossCatalog();

            SKRectI titleRegion = BattleWindowLocator.GetBattleTitleRegion(battleBounds);

            using SKBitmap titleCrop = ImageOps.Crop(screenshot, titleRegion);
            using SKBitmap prepared = PrepareForOcr(titleCrop);

            string rawText = ReadText(prepared, PageSegMode.SingleLine);

            if (string.IsNullOrWhiteSpace(rawText))
            {
                LogOcrAttemptIfChanged("(empty)", catalog.Count, titleRegion, battleBounds);
                return false;
            }

            string normalized = rawText
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            LogOcrAttemptIfChanged(normalized, catalog.Count, titleRegion, battleBounds);

            // A real battle title (wild encounter or boss) always contains "VS" -
            // guards against BattleWindowLocator's generic dark-bar heuristic
            // false-positiving on other dark UI panels (e.g. NPC dialogue boxes -
            // confirmed via a real tester's screenshot/log) ever being mistaken
            // for a boss battle at all.
            if (!normalized.Contains("vs", StringComparison.OrdinalIgnoreCase))
                return false;

            // Defensive: never mistake a wild encounter's title for a boss battle.
            if (normalized.Contains("wild", StringComparison.OrdinalIgnoreCase))
            {
                confirmedWild = true;
                return false;
            }

            // Multi-NPC bosses (e.g. Shary & Shaui) are fought as separate 1-on-1
            // battles that each show only one NPC's own name - check those exact
            // names first, since the catalog's stored Name (checked further below)
            // is the combined name (e.g. "Shary & Shaui"), which never appears
            // verbatim in either individual battle's title.
            foreach (var (multiBossId, subNpcNames) in MultiNpcSubNames)
            {
                if (ExcludedFromAutoDetection.Contains(multiBossId))
                    continue;

                foreach (string subNpcName in subNpcNames)
                {
                    if (!normalized.Contains(subNpcName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var catalogEntry = catalog.FirstOrDefault(b =>
                        b.BossId.Equals(multiBossId, StringComparison.OrdinalIgnoreCase));

                    bossId = multiBossId;
                    bossName = string.IsNullOrEmpty(catalogEntry.Name) ? subNpcName : catalogEntry.Name;
                    matchedSubNpc = subNpcName;

                    Log.Information(
                        "BossBattleDetector matched multi-NPC boss '{BossId}' sub-NPC '{SubNpc}' from OCR text '{OcrText}'",
                        multiBossId, subNpcName, normalized);

                    return true;
                }
            }

            // §377: the passes over the catalog live in MatchCatalog, so the
            // rule can be read - and ported to a test - on its own.
            if (MatchCatalog(normalized, catalog, ExcludedFromAutoDetection, out bossId, out bossName, out string how))
            {
                Log.Information(
                    "BossBattleDetector matched boss '{BossName}' ({How}) from OCR text '{OcrText}'",
                    bossName, how, normalized);

                return true;
            }

            return false;
        }

        /// <summary>§377. The words a battle title may print ahead of a boss's
        /// name - or may not. The catalog says "Professor Rowan", "Prof. Elm"
        /// and "Officer Jenny"; a real title has read "VS. Oak" with no
        /// honorific at all and "VS. Professor Rowan" with the whole of it,
        /// so a match cannot depend on whether it is there, how it is
        /// abbreviated, or how many spaces OCR put after it. Longest first,
        /// so "Prof" is not taken off the front of "Professor". Each carries
        /// the compact forms a title might hold, for the gate in MatchCatalog's
        /// last pass.</summary>
        private static readonly (string Prefix, string[] CompactAliases)[] Honorifics =
        {
            ("Elite Four", new[] { "elitefour" }),
            ("Gym Leader", new[] { "gymleader", "leader" }),
            ("Professor", new[] { "professor", "prof" }),
            ("Guardian", new[] { "guardian" }),
            ("Champion", new[] { "champion" }),
            ("Officer", new[] { "officer" }),
            ("Leader", new[] { "leader" }),
            ("Prof.", new[] { "professor", "prof" }),
            ("Prof", new[] { "professor", "prof" }),
            ("Dr.", new[] { "doctor", "dr" }),
        };

        /// <summary>Letters and digits only, lower case: the shape two strings
        /// have once the spaces OCR gets wrong are out of the way.</summary>
        internal static string Compact(string text) =>
            new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        /// <summary>§377. The catalog name with its honorific taken off -
        /// "Rowan" for "Professor Rowan", "Jenny" for "Officer Jenny" - with
        /// the honorific's compact aliases; null when the name carries none.
        /// (The element is "Surname", not "Rest": C# reserves Rest as a tuple
        /// element name - CS8126 - §379.)</summary>
        internal static (string Surname, string[] Aliases)? WithoutHonorific(string cleanedName)
        {
            foreach ((string prefix, string[] aliases) in Honorifics)
            {
                if (!cleanedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    cleanedName.Length <= prefix.Length)
                {
                    continue;
                }

                // "Prof." ends in its own punctuation; "Professor" needs the
                // space, or "Professorial" would lose its front.
                if (!prefix.EndsWith('.') && !char.IsWhiteSpace(cleanedName[prefix.Length]))
                    continue;

                string surname = cleanedName.Substring(prefix.Length).Trim();

                return surname.Length > 0 ? (surname, aliases) : null;
            }

            return null;
        }

        /// <summary>
        /// §377. Which boss, if any, a battle title names. Five passes, each
        /// over the WHOLE catalog before the next starts (longest name first),
        /// so a loose match never preempts a stricter one that happens to sit
        /// later in the list:
        ///
        ///  1. the whole name, as written                 ("Professor Rowan")
        ///  2. the whole name, spaces aside               ("Professor  Rowan", "ProfessorRowan")
        ///  3. the name without its honorific, spaces aside ("Rowan"; "Prof. Rowan")
        ///  4. the last word, three letters or more        ("Oak", "Jones")
        ///  5. the honorific present and the surname within one letter of it
        ///     ("Professer Rowen") - only for a name that HAS an honorific,
        ///     and only when the title shows one, because a one-letter
        ///     tolerance on a bare surname would let an NPC called Erica
        ///     start Erika's cooldown.
        ///
        /// Pass 4 is the rule a real tester's log forced in the first place:
        /// OCR read "...VS. Oak", and "Professor Oak" never appears verbatim in
        /// a battle title. Three letters rather than four so that "Oak" and
        /// "Elm" count; short common words ("And" in "Jessie And James")
        /// still cannot match on their own.
        /// </summary>
        internal static bool MatchCatalog(
            string normalized,
            IReadOnlyList<(string BossId, string Name)> catalog,
            IReadOnlySet<string> excluded,
            out string? bossId,
            out string? bossName,
            out string how)
        {
            bossId = null;
            bossName = null;
            how = string.Empty;

            string compactTitle = Compact(normalized);

            // 1. as written
            foreach (var (id, name) in catalog)
            {
                if (excluded.Contains(id))
                    continue;

                if (normalized.Contains(StripQualifierSuffix(name), StringComparison.OrdinalIgnoreCase))
                    return Found(id, name, "full name", out bossId, out bossName, out how);
            }

            // 2. spaces aside
            foreach (var (id, name) in catalog)
            {
                if (excluded.Contains(id))
                    continue;

                string compactName = Compact(StripQualifierSuffix(name));

                if (compactName.Length >= 3 && compactTitle.Contains(compactName, StringComparison.Ordinal))
                    return Found(id, name, "full name, spaces aside", out bossId, out bossName, out how);
            }

            // 3. without the honorific
            foreach (var (id, name) in catalog)
            {
                if (excluded.Contains(id))
                    continue;

                var bare = WithoutHonorific(StripQualifierSuffix(name));

                if (bare is null)
                    continue;

                string compactRest = Compact(bare.Value.Surname);

                if (compactRest.Length >= 3 && compactTitle.Contains(compactRest, StringComparison.Ordinal))
                    return Found(id, name, $"without the honorific: '{bare.Value.Surname}'", out bossId, out bossName, out how);
            }

            // 4. the last word
            foreach (var (id, name) in catalog)
            {
                if (excluded.Contains(id))
                    continue;

                string lastWord = StripQualifierSuffix(name)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault() ?? string.Empty;

                if (lastWord.Length >= 3 &&
                    normalized.Contains(lastWord, StringComparison.OrdinalIgnoreCase))
                {
                    return Found(id, name, $"last word '{lastWord}'", out bossId, out bossName, out how);
                }
            }

            // 5. honorific present, surname within a letter
            foreach (var (id, name) in catalog)
            {
                if (excluded.Contains(id))
                    continue;

                var bare = WithoutHonorific(StripQualifierSuffix(name));

                if (bare is null)
                    continue;

                bool honorificShown = bare.Value.Aliases.Any(alias =>
                    ContainsFuzzyText(compactTitle, alias, alias.Length >= 7 ? 1 : 0));

                if (!honorificShown)
                    continue;

                string compactRest = Compact(bare.Value.Surname);

                if (compactRest.Length >= 5 && ContainsFuzzyText(compactTitle, compactRest, 1))
                    return Found(id, name, $"honorific shown, '{bare.Value.Surname}' within a letter", out bossId, out bossName, out how);
            }

            return false;

            static bool Found(string id, string name, string rule, out string? bossId, out string? bossName, out string how)
            {
                bossId = id;
                bossName = name;
                how = rule;
                return true;
            }
        }

        /// <summary>Strips a trailing parenthetical qualifier like "(Easy/Hard Only)"
        /// from a boss name before matching - the in-game battle title obviously
        /// never displays this, so leaving it in would make the full-name check
        /// always fail for any boss whose catalog name includes one.</summary>
        private static string StripQualifierSuffix(string name)
        {
            int parenIndex = name.IndexOf('(');
            return parenIndex > 0 ? name[..parenIndex].Trim() : name;
        }

        // Diagnostic logging - only logs when the OCR result actually changes, to
        // avoid spamming the log with identical lines every scan tick during a
        // long battle, while still capturing every distinct thing OCR actually
        // read. Temporary/diagnostic in nature - not meant to stay this verbose
        // forever once boss title detection is confirmed working reliably.
        private static string? lastLoggedOcrText;

        private static void LogOcrAttemptIfChanged(
            string ocrText, int catalogSize, SKRectI titleRegion, SKRectI battleBounds)
        {
            if (ocrText == lastLoggedOcrText)
                return;

            lastLoggedOcrText = ocrText;

            Log.Information(
                "BossBattleDetector OCR attempt: text='{OcrText}', catalogSize={CatalogSize}, " +
                "titleRegion=({TX},{TY},{TW}x{TH}), battleBounds=({BX},{BY},{BW}x{BH})",
                ocrText, catalogSize,
                titleRegion.Left, titleRegion.Top, titleRegion.Width, titleRegion.Height,
                battleBounds.Left, battleBounds.Top, battleBounds.Width, battleBounds.Height);
        }

        /// <summary>Whether the battle-log message shows a win or loss result yet,
        /// and which one. For an ordinary single-NPC boss either outcome starts
        /// the cooldown the same way (see BossCooldownTracker), so this
        /// distinction used to be irrelevant - but it isn't for
        /// MultiNpcRequiresAll bosses (e.g. Shary &amp; Shaui): a real user
        /// confirmed that losing to one of them ends the whole attempt right
        /// away (the player doesn't get a chance to fight the other NPC at
        /// all), while winning only means "this one is done - wait for the
        /// other."</summary>
        public static BossBattleOutcome DetectBattleEnd(SKBitmap screenshot, SKRectI battleBounds)
        {
            SKRectI messageRegion = CatchDetector.GetBattleMessageRegion(
                battleBounds,
                new SKSizeI(screenshot.Width, screenshot.Height));

            if (messageRegion.Width <= 0 || messageRegion.Height <= 0)
                return BossBattleOutcome.None;

            using SKBitmap crop = ImageOps.Crop(screenshot, messageRegion);
            using SKBitmap prepared = PrepareForOcr(crop);

            string rawText = ReadText(prepared, PageSegMode.SingleLine);

            if (string.IsNullOrWhiteSpace(rawText))
                return BossBattleOutcome.None;

            string compact = new string(
                rawText.ToLowerInvariant().Where(char.IsLetter).ToArray());

            if (compact.Contains("wonthebattle") || ContainsFuzzyText(compact, "wonthebattle", 2))
                return BossBattleOutcome.Won;

            if (compact.Contains("lostthebattle") || ContainsFuzzyText(compact, "lostthebattle", 2))
                return BossBattleOutcome.Lost;

            return BossBattleOutcome.None;
        }

        private static List<(string BossId, string Name)> GetBossCatalog()
        {
            if (bossCatalog is not null)
                return bossCatalog;

            bossCatalog = BossCooldownService.GetAllBossNames()
                .OrderByDescending(b => b.Name.Length)
                .ToList();

            Log.Information(
                "BossBattleDetector loaded {Count} boss name(s) from BossCooldownService",
                bossCatalog.Count);

            if (bossCatalog.Count > 0)
            {
                Log.Information(
                    "BossBattleDetector boss catalog sample: {Sample}",
                    string.Join(", ", bossCatalog.Take(5).Select(b => $"{b.Name} ({b.BossId})")));
            }

            return bossCatalog;
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

        // Same tolerant substring-Levenshtein approach as CatchDetector.cs, kept
        // self-contained here rather than shared, matching this codebase's existing
        // per-detector style.
        private static bool ContainsFuzzyText(string source, string target, int maximumDistance)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return false;

            if (source.Contains(target, StringComparison.OrdinalIgnoreCase))
                return true;

            int minimumLength = Math.Max(1, target.Length - maximumDistance);
            int maximumLength = Math.Min(source.Length, target.Length + maximumDistance);

            for (int length = minimumLength; length <= maximumLength; length++)
            {
                for (int start = 0; start + length <= source.Length; start++)
                {
                    string section = source.Substring(start, length);

                    if (LevenshteinDistance(section, target) <= maximumDistance)
                        return true;
                }
            }

            return false;
        }

        private static int LevenshteinDistance(string a, string b)
        {
            int[,] distance = new int[a.Length + 1, b.Length + 1];

            for (int i = 0; i <= a.Length; i++)
                distance[i, 0] = i;

            for (int j = 0; j <= b.Length; j++)
                distance[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;

                    distance[i, j] = Math.Min(
                        Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                        distance[i - 1, j - 1] + cost);
                }
            }

            return distance[a.Length, b.Length];
        }
    }

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
    /// a bright green bar that only shows up in a real PVP match's UI, for every
    /// other case where the NPC has an actual name and OCR alone truly can't tell
    /// the two apart. See HasPvpIndicatorBar's own doc comment for how confident
    /// each of its two checked regions actually is - one is now confirmed against
    /// a real missed-battle recording, the other is still a first-pass guess from
    /// a single reference screenshot, unlike BattleWindowLocator's
    /// proven-across-many-captures dimensions.
    /// </summary>
    public static class PvpBattleDetector
    {
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

            // More OCR noise cleanup, this time at the tail: a real PRO username is
            // always a single word, no spaces - but this crop's right edge overlaps
            // the PVP indicator bar's own points/timer readout (see
            // TitleBarIndicatorRegion's comment below - that region starts at 72% of
            // battleBounds' width, well inside this title crop's 30%-82% span), and
            // OCR sometimes picks up a stray character from it as an extra trailing
            // "word". Confirmed against real detections that got saved and reported
            // back: "Free3 I", "Zaone I", and "Rabina I" all logged with a spurious
            // trailing " I" (most likely a misread "1" from that readout) that isn't
            // part of the actual username. Keeping only the first whitespace-
            // separated token drops that artifact regardless of exactly which stray
            // character or digit it comes out as.
            candidate = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

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
            if (!HasPvpIndicatorBar(screenshot, battleBounds))
                return false;

            opponentName = candidate;

            Log.Information(
                "PvpBattleDetector detected PVP opponent '{OpponentName}' from OCR text '{OcrText}'",
                opponentName, normalized);

            return true;
        }

        // Sampled from a real PVP match screenshot the user provided: that battle's
        // UI shows a persistent bright-green bar (an ELO/points readout) near it -
        // a widget that was completely absent from both false-positive NPC
        // screenshots checked against this (zero matching pixels in either one).
        // See PvpIndicatorMinMatchFraction's remarks on why the sampled regions
        // below have to stay this tight, and TitleBarIndicatorRegionX's comment
        // for where the bar actually renders in an ordinary "Random PvP queue"
        // match - not where the constants directly below this comment originally
        // assumed.
        //
        // Two regions are checked (see HasPvpIndicatorBar) because two different
        // PVP layouts have been seen so far:
        //
        // - TopCornerIndicatorRegion* (below): calibrated from the very first
        //   reference screenshot, which showed a full stadium-arena view (not the
        //   floating box over the map that wild/boss/NPC/ordinary PVP battles use)
        //   with the bar rendered as persistent chrome near the top of the whole
        //   capture. Still unconfirmed whether BattleWindowLocator even
        //   successfully finds battleBounds for that layout at all - kept as a
        //   fallback rather than removed, since it costs almost nothing to also
        //   check.
        //
        // - TitleBarIndicatorRegion* (further below): added after a real report
        //   plus a 30-second recording of a MISSED ordinary "Random PvP queue"
        //   battle ("Blackfoot VS. Thanhcoibg") proved the top-corner region above
        //   never matches for this common layout - across every frame of that
        //   recording the top-corner region had 0 matching pixels, start to
        //   finish. The bar isn't separate screen chrome there at all - it's
        //   rendered INSIDE the battle window's own title bar, right after the
        //   "<You> VS. <Opponent>" text. Measured directly off that recording
        //   (1280x720 capture, battleBounds ~(294,133,775,456)): the bar's pixels
        //   landed at 73.8%-99.0% of battleBounds' width and 3.3%-7.0% of its
        //   height, appearing a few seconds into the battle (after the window
        //   itself but well within PvpTracker's 20-second detection budget). This
        //   is now the primary/expected match for ordinary PVP; the top-corner
        //   region is the one that's unproven.
        private const float TopCornerIndicatorRegionX = 0.70f;
        private const float TopCornerIndicatorRegionY = 0f;
        private const float TopCornerIndicatorRegionWidth = 0.30f;
        private const float TopCornerIndicatorRegionHeight = 0.10f;

        // Margin added on both sides of the measured 73.8%-99.0% width range, and
        // pinned to the same vertical band as
        // BattleWindowLocator.GetBattleTitleRegion's own title-bar height fraction
        // (0.09, clamped 45-75px) so this can't drift down into the battle scene's
        // HP-bar green underneath the title bar.
        private const float TitleBarIndicatorRegionX = 0.72f;
        private const float TitleBarIndicatorRegionWidth = 0.28f;
        private const float TitleBarIndicatorHeightFraction = 0.09f;
        private const int TitleBarIndicatorMinHeight = 45;
        private const int TitleBarIndicatorMaxHeight = 75;

        // Measured color from the reference screenshot: RGB(97, 226, 8) / #61E208,
        // a saturated lime green. +/-50 per channel is generous enough to survive
        // compression/anti-aliasing without drifting into unrelated colors -
        // nothing this bright and this green-dominant turned up anywhere in either
        // false-positive screenshot this was checked against. Confirmed to still
        // hold for the title-bar rendering too: the "Thanhcoibg" recording's bar
        // sampled at RGB(91-94, 220-225, 2-5), comfortably inside this tolerance.
        private const int PvpIndicatorTargetR = 97;
        private const int PvpIndicatorTargetG = 226;
        private const int PvpIndicatorTargetB = 8;
        private const int PvpIndicatorColorTolerance = 50;

        // Out of a sampled region's pixels, how many need to match before this
        // counts as "the bar is showing." Measured at roughly 30% in the original
        // top-corner reference screenshot, and 30-32% across the "Thanhcoibg"
        // recording's title-bar region once the bar had rendered - 2% leaves a lot
        // of margin either way while staying far above what stray noise could
        // produce.
        private const double PvpIndicatorMinMatchFraction = 0.02;

        /// <summary>
        /// True if the PVP indicator bar is visible in either of the two places
        /// it's been confirmed to render - see the constants above for where each
        /// region came from and what it's worth. The top-corner region is still a
        /// first-pass calibration from a single reference screenshot, not a
        /// confirmed-across-many-captures value the way BattleWindowLocator's
        /// dimensions are; the title-bar region is confirmed against a real missed
        /// battle's recording, but only for the ordinary "Random PvP queue" layout.
        /// If some other PVP layout starts getting missed entirely (no "PVP battle
        /// detected" log line where one should appear, despite a PvpBattleDetector
        /// OCR attempt line showing the right title text), these regions are the
        /// first place to adjust - ideally against a recording from that layout
        /// rather than guessed again.
        /// </summary>
        private static bool HasPvpIndicatorBar(SKBitmap screenshot, SKRectI battleBounds)
        {
            SKRectI titleBarRegion = GetTitleBarIndicatorRegion(battleBounds);
            bool foundInTitleBar = RegionMatchesIndicatorColor(
                screenshot, titleBarRegion, out int titleBarMatch, out int titleBarTotal);

            SKRectI topCornerRegion = GetTopCornerIndicatorRegion(screenshot);
            int cornerMatch = 0;
            int cornerTotal = 0;
            bool foundInTopCorner = false;

            // Only bother checking the fallback region if the (now primary)
            // title-bar region already missed - saves a redundant pixel scan on
            // the common case.
            if (!foundInTitleBar)
            {
                foundInTopCorner = RegionMatchesIndicatorColor(
                    screenshot, topCornerRegion, out cornerMatch, out cornerTotal);
            }

            bool found = foundInTitleBar || foundInTopCorner;

            LogPvpIndicatorCheckIfChanged(
                found, foundInTitleBar,
                titleBarRegion, titleBarMatch, titleBarTotal,
                topCornerRegion, cornerMatch, cornerTotal);

            return found;
        }

        private static SKRectI GetTitleBarIndicatorRegion(SKRectI battleBounds)
        {
            int x = battleBounds.Left + (int)(battleBounds.Width * TitleBarIndicatorRegionX);
            int y = battleBounds.Top;
            int width = (int)(battleBounds.Width * TitleBarIndicatorRegionWidth);

            int height = Math.Clamp(
                (int)Math.Round(battleBounds.Height * TitleBarIndicatorHeightFraction),
                TitleBarIndicatorMinHeight,
                TitleBarIndicatorMaxHeight);

            return ImageOps.MakeRect(x, y, width, height);
        }

        private static SKRectI GetTopCornerIndicatorRegion(SKBitmap screenshot)
        {
            int x = (int)(screenshot.Width * TopCornerIndicatorRegionX);
            int y = (int)(screenshot.Height * TopCornerIndicatorRegionY);
            int width = (int)(screenshot.Width * TopCornerIndicatorRegionWidth);
            int height = (int)(screenshot.Height * TopCornerIndicatorRegionHeight);

            return ImageOps.MakeRect(x, y, width, height);
        }

        // Shared pixel-scan used by both indicator regions - same color-matching
        // loop the original single-region check used, just parameterized on the
        // region and reporting its counts back via out params for logging.
        private static bool RegionMatchesIndicatorColor(
            SKBitmap screenshot, SKRectI region, out int matchingPixels, out int totalPixels)
        {
            matchingPixels = 0;
            totalPixels = 0;

            SKRectI bounds = ImageOps.MakeRect(0, 0, screenshot.Width, screenshot.Height);
            SKRectI clamped = ImageOps.Intersect(region, bounds);

            if (ImageOps.IsEmpty(clamped))
                return false;

            SKColor[] pixels = screenshot.Pixels;
            int screenshotWidth = screenshot.Width;
            totalPixels = clamped.Width * clamped.Height;

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

            return totalPixels > 0 &&
                matchingPixels / (double)totalPixels >= PvpIndicatorMinMatchFraction;
        }

        private static bool? lastLoggedPvpIndicatorResult;
        private static bool? lastLoggedFoundInTitleBar;

        private static void LogPvpIndicatorCheckIfChanged(
            bool found, bool foundInTitleBar,
            SKRectI titleBarRegion, int titleBarMatch, int titleBarTotal,
            SKRectI topCornerRegion, int cornerMatch, int cornerTotal)
        {
            if (found == lastLoggedPvpIndicatorResult && foundInTitleBar == lastLoggedFoundInTitleBar)
                return;

            lastLoggedPvpIndicatorResult = found;
            lastLoggedFoundInTitleBar = foundInTitleBar;

            Log.Information(
                "PvpBattleDetector indicator bar check: found={Found}, matchedIn={MatchedIn}, " +
                "titleBar=({TX},{TY},{TW}x{TH}) {TitleBarMatch}/{TitleBarTotal}, " +
                "topCorner=({CX},{CY},{CW}x{CH}) {CornerMatch}/{CornerTotal}",
                found, found ? (foundInTitleBar ? "titleBar" : "topCorner") : "none",
                titleBarRegion.Left, titleBarRegion.Top, titleBarRegion.Width, titleBarRegion.Height,
                titleBarMatch, titleBarTotal,
                topCornerRegion.Left, topCornerRegion.Top, topCornerRegion.Width, topCornerRegion.Height,
                cornerMatch, cornerTotal);
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
    }
}
