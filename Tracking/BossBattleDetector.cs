using System;
using System.Linq;
using SkiaSharp;
using Serilog;
using TesseractOCR;
using TesseractOCR.Enums;
using Foot_Tracker.Services;

namespace Foot_Tracker.Tracking
{
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

        private static readonly object ocrLock = new();
        private static Engine? engine;

        // (bossId, Name) pairs, longest name first so "Elite Four Lorelei"-style
        // longer names (if any ever exist) match before a shorter substring would.
        private static List<(string BossId, string Name)>? bossCatalog;

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

            // Two passes over the whole catalog (not interleaved per-entry) so a
            // short last-word fallback match never preempts a real full-name match
            // that happens to sit later in the list.

            foreach (var (id, name) in catalog)
            {
                if (ExcludedFromAutoDetection.Contains(id))
                    continue;

                string cleanedName = StripQualifierSuffix(name);

                if (normalized.Contains(cleanedName, StringComparison.OrdinalIgnoreCase))
                {
                    bossId = id;
                    bossName = name;

                    Log.Information(
                        "BossBattleDetector matched boss '{BossName}' (full name) from OCR text '{OcrText}'",
                        name, normalized);

                    return true;
                }
            }

            // Some bosses' battle titles show only part of their full name - e.g.
            // "Oak" for "Professor Oak" (confirmed via a real tester's log: OCR
            // correctly read "...VS. Oak", but the catalog name "Professor Oak"
            // never appears verbatim in any battle title). Falls back to the last
            // word of the cleaned name, guarded by a minimum length so short/common
            // words (e.g. "And" in "Jessie And James") can't cause false matches.
            foreach (var (id, name) in catalog)
            {
                if (ExcludedFromAutoDetection.Contains(id))
                    continue;

                string cleanedName = StripQualifierSuffix(name);
                string lastWord = cleanedName
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault() ?? string.Empty;

                // Lowered from 4 to 3 - confirmed via a real tester's log that
                // "Professor Oak" and "Professor Elm" both show only their
                // 3-letter surname ("Oak", "Elm") in the actual battle title,
                // and a length-4 minimum silently excluded both.
                if (lastWord.Length >= 3 &&
                    normalized.Contains(lastWord, StringComparison.OrdinalIgnoreCase))
                {
                    bossId = id;
                    bossName = name;

                    Log.Information(
                        "BossBattleDetector matched boss '{BossName}' (last word '{LastWord}') from OCR text '{OcrText}'",
                        name, lastWord, normalized);

                    return true;
                }
            }

            return false;
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
}