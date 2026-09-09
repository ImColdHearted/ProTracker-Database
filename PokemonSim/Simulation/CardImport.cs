using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PokemonSim.Engine;
using PokemonSim.Models;

namespace PokemonSim.Simulation
{
    /// <summary>
    /// Section 162. One Pokemon as read off a PRO summary card - everything
    /// the simulator needs except the held item (the game does not show an
    /// item on this card; it is picked by hand in the builder). Exp and OT
    /// are deliberately not carried. IV and EV arrays run in the standard
    /// order: HP, Attack, Defense, SpAttack, SpDefense, Speed.
    /// </summary>
    public sealed class ImportedPokemon
    {
        public string SpeciesName = "";
        public int Level = 100;
        public string NatureName = "Hardy";
        public string? AbilityName;
        public List<string> MoveNames = new();
        public int[] Ivs = { 31, 31, 31, 31, 31, 31 };
        public int[] Evs = new int[6];

        /// <summary>The card's ID number, when it read cleanly - a storage
        /// dedupe key, nothing more.</summary>
        public string? GameId;

        /// <summary>Section 164: the golden S badge on the card's title
        /// ball. Set by the pixel side (CardOcrService), not the text
        /// parser; cosmetic - the views show shiny artwork.</summary>
        public bool IsShiny;

        /// <summary>Everything the reader was unsure about or corrected,
        /// in the order it happened - shown on the import preview.</summary>
        public List<string> Notes = new();

        /// <summary>Per stat row: clean / iv-fixed / ev-fixed / both-fixed /
        /// ev-capped / unchecked / unverified / rescued / derived /
        /// missing. Keys are the standard stat names (hp, attack, ...).</summary>
        public Dictionary<string, string> RowStatus = new();

        /// <summary>The identity the storage falls back to when two imports
        /// disagree about (or are missing) the card ID: the exact build.</summary>
        public string Fingerprint =>
            $"{SpeciesName}|{Level}|{NatureName}|{string.Join(",", Ivs)}|{string.Join(",", Evs)}";

        /// <summary>§167: the IVs and EVs in the ORDER THE CARD PRINTS its
        /// rows (Atk/Def/Spe/SpA/SpD/HP), so a preview reads against the
        /// game side by side. Only display text - everything internal
        /// stays in the standard HP-first order.</summary>
        public string CardOrderSpread
        {
            get
            {
                int[] order = { 1, 2, 5, 3, 4, 0 };

                return $"IVs {string.Join("/", order.Select(i => Ivs[i]))}   " +
                       $"EVs {string.Join("/", order.Select(i => Evs[i]))}   (Atk/Def/Spe/SpA/SpD/HP)";
            }
        }
    }

    /// <summary>The raw OCR strings the app-side reader hands over, one per
    /// card field. Hp and Id carry every preprocessing attempt (the parser
    /// takes the first plausible one), because those two fields read
    /// differently well under different thresholds.</summary>
    public sealed class CardOcrTexts
    {
        public string Title = "";
        public List<string> IdCandidates = new();
        public List<string> HpCandidates = new();
        public string Ability = "";
        public string Nature = "";
        public string MovesBlock = "";
        public string StatsBlock = "";

        /// <summary>§166: alternate reads of the title, the ability and
        /// the whole stats block (different scales and thresholds). The
        /// parser votes across every read instead of living or dying by
        /// the first one.</summary>
        public List<string> TitleCandidates = new();
        public List<string> AbilityCandidates = new();
        public List<string> StatsBlockCandidates = new();

        /// <summary>§165: single-line re-reads of just the HP stat row -
        /// the block read's shortest line and the first one a soft live
        /// capture loses. The parser only reaches for these when the
        /// block's own HP row is missing or impossible.</summary>
        public List<string> HpRowCandidates = new();
    }

    /// <summary>
    /// Section 162. Turns the raw OCR text of a PRO summary card into an
    /// ImportedPokemon. Everything here is deliberately redundant: names
    /// are snapped to the real dictionaries (species catalog, move data,
    /// ability list, the 25 natures) by edit distance, and every stat row
    /// is verified against the games' stat formula - the card shows the
    /// stat, the IV and the EV together, so a misread digit in any one of
    /// them is caught and repaired from the other two. The pixel side
    /// (finding the card, cropping, Tesseract) lives in the tracker; this
    /// class is pure text so the test suite can pin it to the exact OCR
    /// output of real screenshots.
    /// </summary>
    public static class CardImportParser
    {
        static readonly string[] StatKeys = { "hp", "attack", "defense", "spAttack", "spDefense", "speed" };

        static readonly Dictionary<string, int> LabelToIndex = new(StringComparer.OrdinalIgnoreCase)
        {
            ["HP"] = 0, ["ATK"] = 1, ["DEF"] = 2, ["SPATK"] = 3, ["SPDEF"] = 4, ["SPD"] = 5
        };

        /// <summary>§166: ability names PRO prints on summary cards that
        /// the tracker's ability catalog does not carry (legendary
        /// signatures and late-generation additions). Pure OCR dictionary
        /// entries - nothing engine-side changes; without them a clean
        /// read like "Full Metal Body" fuzzy-snaps onto a wrong nearby
        /// name, which is exactly what happened on a real Solgaleo card.</summary>
        static readonly string[] AbilityOcrSupplement =
        {
            "Full Metal Body", "Shadow Shield", "Prism Armor", "Beast Boost",
            "Battle Bond", "Power Construct", "Disguise", "Schooling",
            "Shields Down", "Comatose", "Steelworker", "Berserk", "Fluffy",
            "Dazzling", "Corrosion", "Stamina", "Dancer", "Battery",
            "Receiver", "Triage", "Galvanize", "Electric Surge", "Psychic Surge",
            "Grassy Surge", "Misty Surge", "Stakeout", "Water Compaction",
            "Queenly Majesty", "Innards Out", "Wimp Out", "Emergency Exit",
            "Long Reach", "Liquid Voice", "Slush Rush", "Surge Surfer",
        };

        /// <summary>OCR digit confusions worth trying when a stat row does
        /// not verify - mirrors the prototype that was tuned on real
        /// screenshots.</summary>
        static readonly Dictionary<char, string> DigitSubs = new()
        {
            ['0'] = "068", ['1'] = "17", ['2'] = "27", ['3'] = "389", ['4'] = "49",
            ['5'] = "568", ['6'] = "568", ['7'] = "127", ['8'] = "0368", ['9'] = "349"
        };

        // ---- the games' stat formula (integer, floor at every step) ----
        // Deliberately NOT StatCalculator: that builds the sim's own mons
        // and carries a fractional EV term; the card shows the game's
        // numbers, and verification must reproduce those exactly.

        public static int OfficialStat(int baseStat, int iv, int ev, int level, double natureMod) =>
            (int)((((2 * baseStat + iv + ev / 4) * level) / 100 + 5) * natureMod);

        public static int OfficialHp(int baseStat, int iv, int ev, int level) =>
            ((2 * baseStat + iv + ev / 4) * level) / 100 + level + 10;

        // ---- fuzzy matching ----

        public static int Distance(string a, string b)
        {
            a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
            if (a == b) return 0;

            int[] prev = new int[b.Length + 1];
            int[] cur = new int[b.Length + 1];

            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;

                for (int j = 1; j <= b.Length; j++)
                {
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1),
                        prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                }

                (prev, cur) = (cur, prev);
            }

            return prev[b.Length];
        }

        /// <summary>Nearest dictionary entry by edit distance (spaces also
        /// compared stripped, so "Toxa pex" finds Toxapex), or null when
        /// nothing lands within maxFraction of the text's length.</summary>
        public static string? Match(string text, IEnumerable<string> choices, double maxFraction = 0.4)
        {
            text = text.Trim();

            if (text.Length == 0)
                return null;

            string flat = text.Replace(" ", "");
            string? best = null;
            int bestDistance = int.MaxValue;

            foreach (string choice in choices)
            {
                int d = Math.Min(Distance(text, choice), Distance(flat, choice.Replace(" ", "")));

                if (d < bestDistance)
                {
                    best = choice;
                    bestDistance = d;
                }
            }

            return bestDistance <= Math.Max(1, (int)(text.Length * maxFraction)) ? best : null;
        }

        // ---- the parse ----

        public static ImportedPokemon Parse(
            CardOcrTexts texts,
            ISpeciesSource speciesSource,
            IReadOnlyCollection<string> moveNames,
            IReadOnlyCollection<string> abilityNames)
        {
            var result = new ImportedPokemon();

            List<string> speciesNames = speciesSource.AllSpeciesNames.ToList();

            ParseTitle(texts.Title, texts.TitleCandidates, speciesNames, result);

            // §166: resolved before the ability so the species' own legal
            // ability list can steer that read.
            SpeciesInfo? info = result.SpeciesName.Length > 0 ? speciesSource.Find(result.SpeciesName) : null;

            result.GameId = ParseId(texts.IdCandidates, texts.Title, texts.TitleCandidates);

            List<int> hpBars = ParseHpBars(texts.HpCandidates);

            ParseAbility(texts.Ability, texts.AbilityCandidates, abilityNames, info, result);
            ParseNature(texts.Nature, result);
            ParseMoves(texts.MovesBlock, moveNames, result);

            ParseStats(texts.StatsBlock, texts.StatsBlockCandidates,
                texts.HpRowCandidates, info, hpBars, result);

            return result;
        }

        static string? SpeciesFrom(string clean, IReadOnlyList<string> speciesNames)
        {
            List<string> words = Regex.Matches(clean, "[A-Za-z.'-]+")
                .Select(m => m.Value)
                .Where(w => w.Length > 1)
                .ToList();

            string? species = null;
            int bestDistance = int.MaxValue;

            for (int k = 1; k <= 3 && k <= words.Count; k++)
            {
                string candidate = string.Join(" ", words.Take(k));
                string? match = Match(candidate, speciesNames);

                if (match != null)
                {
                    int d = Math.Min(Distance(candidate, match),
                        Distance(candidate.Replace(" ", ""), match.Replace(" ", "")));

                    if (d < bestDistance)
                    {
                        species = match;
                        bestDistance = d;
                    }
                }
            }

            return species;
        }

        /// <summary>§166: the level marker first; without one, the LARGEST
        /// standalone one-to-three digit number that is a legal level -
        /// stray glyphs read as small numbers ("1)") otherwise beat the
        /// real level to the front of the string.</summary>
        static int TitleLevel(string clean)
        {
            System.Text.RegularExpressions.Match lv =
                Regex.Match(clean, @"(?:[Ll1It][vy][ .:]*\s*)(\d{1,3})");

            if (lv.Success && int.TryParse(lv.Groups[1].Value, out int marked))
                return marked >= 1 && marked <= 100 ? marked : 100;

            int best = 0;

            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(clean, @"\b(\d{1,3})\b"))
            {
                if (int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= 100 && n > best)
                    best = n;
            }

            return best > 0 ? best : 100;
        }

        static void ParseTitle(
            string title, IReadOnlyList<string> titleCandidates,
            IReadOnlyList<string> speciesNames, ImportedPokemon result)
        {
            // §166: any read attempt may carry the species; the first that
            // does also supplies the level.
            foreach (string text in new[] { title }.Concat(titleCandidates))
            {
                string clean = Regex.Replace(text, "[^A-Za-z0-9 .:/-]", " ");
                string? species = SpeciesFrom(clean, speciesNames);

                if (species != null)
                {
                    result.SpeciesName = species;
                    result.Level = TitleLevel(clean);
                    return;
                }
            }

            string primaryClean = Regex.Replace(title, "[^A-Za-z0-9 .:/-]", " ");
            result.Notes.Add($"species did not read - got \"{primaryClean.Trim()}\"");
            result.Level = TitleLevel(primaryClean);
        }

        static string? ParseId(
            IReadOnlyList<string> idCandidates, string title, IReadOnlyList<string> titleCandidates)
        {
            // §166: every digit run from every read votes. The real ids are
            // 8 digits; 'ID:' glyphs prepend junk digits (a longer run keeps
            // its tail) and soft reads drop the lead digit (a shorter run
            // supports whichever 8-digit run contains it).
            var runs = new List<string>();

            foreach (string raw in idCandidates.Concat(new[] { title }).Concat(titleCandidates))
            {
                string s = raw.Replace(" ", "");

                foreach (System.Text.RegularExpressions.Match m in Regex.Matches(s, @"\d{6,10}"))
                {
                    string run = m.Value;
                    runs.Add(run.Length > 8 ? run.Substring(run.Length - 8) : run);
                }
            }

            List<string> eights = runs.Where(r => r.Length == 8).ToList();

            if (eights.Count > 0)
            {
                string best = eights[0];
                (int Votes, int Support, int Order) bestScore = (int.MinValue, int.MinValue, int.MinValue);

                foreach (string candidate in eights.Distinct())
                {
                    (int Votes, int Support, int Order) score = (
                        eights.Count(e => e == candidate),
                        runs.Count(o => o != candidate && candidate.Contains(o)),
                        -eights.IndexOf(candidate));

                    if (score.CompareTo(bestScore) > 0)
                    {
                        best = candidate;
                        bestScore = score;
                    }
                }

                return best;
            }

            return runs.Count > 0 ? runs[0] : null;
        }

        /// <summary>§166: every distinct plausible reading of the HP bar,
        /// in read order - a single misread bar ("425/425" for 428) must
        /// not get to anchor the whole HP row alone.</summary>
        static List<int> ParseHpBars(IReadOnlyList<string> hpCandidates)
        {
            var bars = new List<int>();

            foreach (string candidate in hpCandidates)
            {
                string hp = Regex.Replace(candidate, "[^0-9/]", "");
                int value = -1;

                System.Text.RegularExpressions.Match m = Regex.Match(hp, @"^(\d{1,3})/(\d{1,3})$");

                if (m.Success &&
                    int.Parse(m.Groups[1].Value) <= int.Parse(m.Groups[2].Value))
                {
                    value = int.Parse(m.Groups[2].Value);
                }
                else if (Regex.IsMatch(hp, @"^\d+$"))
                {
                    int n = hp.Length;

                    // A missed slash on a full-health card reads the same
                    // number twice ("274274"); a slash read as one stray
                    // glyph leaves it either side of a junk character
                    // ("4281428" - the very read that pinned a real 428).
                    if ((n == 4 || n == 6) && hp.Substring(0, n / 2) == hp.Substring(n / 2))
                        value = int.Parse(hp.Substring(n / 2));
                    else if ((n == 5 || n == 7) && hp.Substring(0, n / 2) == hp.Substring(n / 2 + 1))
                        value = int.Parse(hp.Substring(n / 2 + 1));
                }

                if (value >= 0 && !bars.Contains(value))
                    bars.Add(value);
            }

            return bars;
        }

        static void ParseAbility(
            string raw, IReadOnlyList<string> abilityCandidates,
            IReadOnlyCollection<string> abilityNames, SpeciesInfo? info, ImportedPokemon result)
        {
            // §166: the dictionary is the catalog plus the supplement, the
            // species' own legal list is tried first when it is known, and
            // the best relative match across every read attempt wins.
            List<string> pool = abilityNames
                .Concat(AbilityOcrSupplement.Where(a => !abilityNames.Contains(a, StringComparer.OrdinalIgnoreCase)))
                .ToList();

            IReadOnlyList<string> speciesAbilities =
                info?.AbilityNames ?? (IReadOnlyList<string>)Array.Empty<string>();

            string? bestName = null;
            double bestRel = double.MaxValue;
            string firstClean = "";
            bool first = true;

            foreach (string text in new[] { raw }.Concat(abilityCandidates))
            {
                string clean = Regex.Replace(text, "[^A-Za-z '-]", "").Trim();

                if (first)
                {
                    firstClean = clean;
                    first = false;
                }

                if (clean.Length == 0)
                    continue;

                string? match = speciesAbilities.Count > 0
                    ? Match(clean, speciesAbilities, 0.5)
                    : null;

                match ??= Match(clean, pool);

                if (match == null)
                    continue;

                int d = Math.Min(Distance(clean, match),
                    Distance(clean.Replace(" ", ""), match.Replace(" ", "")));
                double rel = d / (double)Math.Max(clean.Length, match.Length);

                if (rel < bestRel)
                {
                    bestName = match;
                    bestRel = rel;
                }
            }

            if (bestName != null)
            {
                result.AbilityName = bestName;
                return;
            }

            result.AbilityName = firstClean.Length > 0 ? firstClean : null;

            if (firstClean.Length > 0)
                result.Notes.Add($"ability \"{firstClean}\" is not in the ability list - kept as read");
        }

        static void ParseNature(string raw, ImportedPokemon result)
        {
            string clean = Regex.Replace(raw, "[^A-Za-z]", "");
            string? match = Match(clean, Enum.GetNames<Nature>());

            if (match != null)
            {
                result.NatureName = match;
            }
            else
            {
                result.NatureName = "Hardy";
                result.Notes.Add($"nature did not read - got \"{raw.Trim()}\", using Hardy");
            }
        }

        static void ParseMoves(string movesBlock, IReadOnlyCollection<string> moveNames, ImportedPokemon result)
        {
            foreach (string rawLine in movesBlock.Split('\n'))
            {
                if (result.MoveNames.Count >= 4)
                    break;

                string line = Regex.Replace(rawLine, @"[^A-Za-z '()./-]", "").Trim();

                if (line.Length < 3)
                    continue;

                string? match = null;

                // "Hidden Power Ice" on the card is "Hidden Power (Ice)" in
                // the move data (§158 carries the typed variants bosses
                // use). Typed lookups are EXACT - fuzzing "(Ground)" onto
                // "(Ice)" would silently change the type - and fall back to
                // plain Hidden Power with a note.
                System.Text.RegularExpressions.Match hp =
                    Regex.Match(line, @"(hidden\s*power)\s*[( ]*([a-z]+)?", RegexOptions.IgnoreCase);

                if (hp.Success && hp.Groups[2].Success)
                {
                    string type = hp.Groups[2].Value;
                    string typed = $"Hidden Power ({char.ToUpperInvariant(type[0])}{type.Substring(1).ToLowerInvariant()})";

                    match = moveNames.FirstOrDefault(m => m.Equals(typed, StringComparison.OrdinalIgnoreCase));

                    if (match == null &&
                        moveNames.Any(m => m.Equals("Hidden Power", StringComparison.OrdinalIgnoreCase)))
                    {
                        match = "Hidden Power";
                        result.Notes.Add($"\"{typed}\" is not in the move data - using plain Hidden Power");
                    }
                }

                match ??= Match(line, moveNames, 0.34);

                if (match != null && !result.MoveNames.Contains(match))
                    result.MoveNames.Add(match);
                else if (match == null)
                    result.Notes.Add($"move \"{line}\" did not match any known move");
            }
        }

        // ---- §166: the stats block, read as a jury ----

        /// <summary>A row label with up to one OCR miss ("SPO", "AIK",
        /// leading junk stripped by the caller) still names its row - but
        /// only when exactly one label is that close.</summary>
        static int? FuzzyLabel(string word)
        {
            string w = word.ToUpperInvariant();

            if (LabelToIndex.TryGetValue(w, out int exact))
                return exact;

            string? best = null;
            int bestDistance = int.MaxValue;
            int ties = 0;

            foreach (string label in LabelToIndex.Keys)
            {
                int d = Distance(w, label);

                if (d < bestDistance)
                {
                    best = label;
                    bestDistance = d;
                    ties = 1;
                }
                else if (d == bestDistance)
                {
                    ties++;
                }
            }

            return best != null && bestDistance <= 1 && ties == 1 ? LabelToIndex[best] : null;
        }

        /// <summary>Digits of a row's number region, with the glyph
        /// confusions seen on real captures folded in (O for 0, ? for 7).
        /// Anything else non-numeric is dropped.</summary>
        static string DigitsOf(string rest)
        {
            var sb = new System.Text.StringBuilder();

            foreach (char raw in rest)
            {
                char c = raw switch { 'O' => '0', 'o' => '0', '?' => '7', _ => raw };

                if (c >= '0' && c <= '9')
                    sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>One stats line -> (index, stat, iv, ev), or null. The
        /// HP row carries no stat of its own (the bar is its stat), and a
        /// live capture may wrap the label in junk ("5PD; 34930 252").</summary>
        static (int Index, int Stat, int Iv, int Ev)? ParseRowLine(string rawLine)
        {
            System.Text.RegularExpressions.Match m =
                Regex.Match(rawLine.Trim(), @"^[^A-Za-z]*([A-Za-z]+)(.*)$");

            if (!m.Success)
                return null;

            int? index = FuzzyLabel(m.Groups[1].Value);

            if (index == null)
                return null;

            string digits = DigitsOf(m.Groups[2].Value);

            if (index.Value == 0)
            {
                if (digits.Length < 4 || digits.Length > 7)
                    return null;

                return (0, 0, int.Parse(digits.Substring(0, digits.Length - 3)),
                    int.Parse(digits.Substring(digits.Length - 3)));
            }

            if (digits.Length < 6 || digits.Length > 9)
                return null;

            return (index.Value, int.Parse(digits.Substring(0, digits.Length - 5)),
                int.Parse(digits.Substring(digits.Length - 5, 2)),
                int.Parse(digits.Substring(digits.Length - 3)));
        }

        /// <summary>A §165 band read as an HP row candidate: its own line
        /// when the label survived, else the bare digits (4 to 7 of them).</summary>
        static (int Iv, int Ev)? ParseBandCandidate(string text)
        {
            (int Index, int Stat, int Iv, int Ev)? row = ParseRowLine(text);

            if (row != null && row.Value.Index == 0)
                return (row.Value.Iv, row.Value.Ev);

            string digits = DigitsOf(text);

            if (digits.Length < 4 || digits.Length > 7)
                return null;

            return (int.Parse(digits.Substring(0, digits.Length - 3)),
                int.Parse(digits.Substring(digits.Length - 3)));
        }

        /// <summary>
        /// §166. Every read of the stats block - the primary, the alternate
        /// preprocessings, and the HP row's own §165 band - contributes row
        /// candidates, and each stat resolves by the strongest evidence
        /// available: (1) a candidate the games' formula confirms (against
        /// the row's own stat, or any reading of the HP bar), (2) two reads
        /// agreeing on a plausible row, (3) the primary block's plausible
        /// row exactly as §162 took it, (4) for HP, the bar pinning the
        /// total outright, (5) the honest labeled assumption. An impossible
        /// row (IV over 31) that nothing confirms is never shown as fact.
        /// </summary>
        static void ParseStats(
            string statsBlock, IReadOnlyList<string> statsBlockCandidates,
            IReadOnlyList<string> hpRowCandidates, SpeciesInfo? info,
            IReadOnlyList<int> hpBars, ImportedPokemon result)
        {
            var rows = new List<(bool Primary, int Stat, int Iv, int Ev)>[6];

            for (int i = 0; i < 6; i++)
                rows[i] = new List<(bool, int, int, int)>();

            void Collect(string block, bool primary)
            {
                foreach (string rawLine in block.Split('\n'))
                {
                    (int Index, int Stat, int Iv, int Ev)? row = ParseRowLine(rawLine);

                    if (row != null)
                        rows[row.Value.Index].Add((primary, row.Value.Stat, row.Value.Iv, row.Value.Ev));
                }
            }

            Collect(statsBlock, primary: true);

            foreach (string block in statsBlockCandidates)
                Collect(block, primary: false);

            foreach (string candidate in hpRowCandidates)
            {
                (int Iv, int Ev)? pair = ParseBandCandidate(candidate);

                if (pair != null)
                    rows[0].Add((false, 0, pair.Value.Iv, pair.Value.Ev));
            }

            if (info == null)
            {
                // No species to verify against: the §162 behavior verbatim -
                // the primary block alone, clamped, unchecked.
                for (int i = 0; i < 6; i++)
                {
                    string key = StatKeys[i];
                    (bool Primary, int Stat, int Iv, int Ev) row = rows[i].FirstOrDefault(r => r.Primary);

                    if (!row.Primary)
                    {
                        result.Ivs[i] = 31;
                        result.Evs[i] = 0;
                        result.RowStatus[key] = "missing";
                        result.Notes.Add($"{key} row did not read - assuming IV 31, EV 0");
                        continue;
                    }

                    result.Ivs[i] = Math.Clamp(row.Iv, 0, 31);
                    result.Evs[i] = Math.Clamp(row.Ev, 0, 252);
                    result.RowStatus[key] = "unchecked";
                }

                return;
            }

            for (int i = 0; i < 6; i++)
            {
                string key = StatKeys[i];

                int baseStat = i switch
                {
                    0 => info.BaseStats.HP,
                    1 => info.BaseStats.Attack,
                    2 => info.BaseStats.Defense,
                    3 => info.BaseStats.SpAttack,
                    4 => info.BaseStats.SpDefense,
                    _ => info.BaseStats.Speed
                };

                double mod = i == 0 ? 1.0 : NatureModifier(result.NatureName, key);
                List<(bool Primary, int Stat, int Iv, int Ev)> candidates = rows[i];
                bool hasBlockRow = candidates.Any(c => c.Primary);
                (bool Primary, int Stat, int Iv, int Ev) blockRow = candidates.FirstOrDefault(c => c.Primary);
                bool done = false;

                // 1. a candidate the equation confirms.
                foreach ((bool primarySource, int stat, int iv, int ev) in candidates)
                {
                    if (i == 0)
                    {
                        foreach (int bar in hpBars)
                        {
                            (int iv2, int ev2, string status) = VerifyRow(
                                baseStat, result.Level, 1.0, 0, iv, ev, true, bar);

                            if (status is "clean" or "iv-fixed" or "ev-fixed" or "both-fixed")
                            {
                                result.Ivs[0] = iv2;
                                result.Evs[0] = ev2;

                                if (primarySource)
                                {
                                    result.RowStatus[key] = status;

                                    if (status != "clean")
                                        result.Notes.Add($"{key}: {status} (read IV {iv}, EV {ev} -> IV {iv2}, EV {ev2})");
                                }
                                else
                                {
                                    result.RowStatus[key] = "rescued";
                                    result.Notes.Add(
                                        $"hp row was rescued by its own re-read - IV {iv2}, EV {ev2} (matches the {bar} HP bar)");
                                }

                                done = true;
                                break;
                            }
                        }
                    }
                    else
                    {
                        (int iv2, int ev2, string status) = VerifyRow(
                            baseStat, result.Level, mod, stat, iv, ev, false, null);

                        if (status is "clean" or "iv-fixed" or "ev-fixed" or "both-fixed")
                        {
                            result.Ivs[i] = iv2;
                            result.Evs[i] = ev2;
                            result.RowStatus[key] = status;

                            if (status != "clean")
                                result.Notes.Add($"{key}: {status} (read IV {iv}, EV {ev} -> IV {iv2}, EV {ev2})");

                            done = true;
                        }
                    }

                    if (done)
                        break;
                }

                if (done)
                    continue;

                // 2. two reads agreeing on a plausible row.
                List<(int Iv, int Ev)> plausible = candidates
                    .Where(c => c.Iv <= 31 && c.Ev <= 252)
                    .Select(c => (c.Iv, c.Ev))
                    .ToList();

                foreach ((int Iv, int Ev) pair in plausible)
                {
                    if (plausible.Count(p => p == pair) < 2)
                        continue;

                    result.Ivs[i] = pair.Iv;
                    result.Evs[i] = pair.Ev;

                    if (i == 0)
                    {
                        result.RowStatus[key] = "rescued";
                        result.Notes.Add(
                            $"hp row was rescued by its own re-read - IV {pair.Iv}, EV {pair.Ev} (two reads agree)");
                    }
                    else
                    {
                        result.RowStatus[key] = "unverified";
                        result.Notes.Add($"{key}: unverified (read IV {pair.Iv}, EV {pair.Ev} -> IV {pair.Iv}, EV {pair.Ev})");
                    }

                    done = true;
                    break;
                }

                if (done)
                    continue;

                // 3. the primary block's row, when plausible, keeps its §162 fate.
                if (hasBlockRow)
                {
                    int? bar0 = i == 0 && hpBars.Count > 0 ? hpBars[0] : (int?)null;

                    (int iv3, int ev3, string status3) = VerifyRow(
                        baseStat, result.Level, mod, blockRow.Stat, blockRow.Iv, blockRow.Ev, i == 0, bar0);

                    if (iv3 <= 31 && ev3 <= 252)
                    {
                        result.Ivs[i] = iv3;
                        result.Evs[i] = ev3;
                        result.RowStatus[key] = status3;

                        if (status3 != "clean" && status3 != "unchecked")
                            result.Notes.Add($"{key}: {status3} (read IV {blockRow.Iv}, EV {blockRow.Ev} -> IV {iv3}, EV {ev3})");

                        continue;
                    }
                }

                // 4. hp only: the bar pins the total outright.
                if (i == 0)
                {
                    bool derivedDone = false;

                    foreach (int bar in hpBars)
                    {
                        (int Iv, int Ev)? derived = DeriveHpFromBar(baseStat, result.Level, bar);

                        if (derived != null)
                        {
                            result.Ivs[0] = derived.Value.Iv;
                            result.Evs[0] = derived.Value.Ev;
                            result.RowStatus[key] = "derived";
                            result.Notes.Add(
                                $"hp row did not read - IV {derived.Value.Iv}, EV {derived.Value.Ev} derived from the {bar} HP bar (total HP exact)");
                            derivedDone = true;
                            break;
                        }
                    }

                    if (derivedDone)
                        continue;
                }

                // 5. the honest assumption.
                result.Ivs[i] = 31;
                result.Evs[i] = 0;
                result.RowStatus[key] = "missing";

                if (hasBlockRow)
                    result.Notes.Add($"{key} row read impossibly (IV {blockRow.Iv}, EV {blockRow.Ev}) - assuming IV 31, EV 0");
                else
                    result.Notes.Add($"{key} row did not read - assuming IV 31, EV 0");
            }
        }

        /// <summary>§165: the games' HP formula only ever sees iv + ev/4,
        /// so a full HP bar pins that total exactly. The split favors the
        /// IV (up to 31, remainder as EVs) and is cosmetic - every split
        /// with the same total builds the same battle stat. Null when no
        /// legal total (IV 0..31, EV 0..252) reproduces the bar.</summary>
        public static (int Iv, int Ev)? DeriveHpFromBar(int baseStat, int level, int hpMax)
        {
            for (int total = 0; total <= 94; total++)
            {
                if (OfficialHp(baseStat, total, 0, level) == hpMax)
                {
                    int iv = Math.Min(31, total);
                    return (iv, 4 * (total - iv));
                }
            }

            return null;
        }

        static double NatureModifier(string natureName, string statKey)
        {
            return Enum.TryParse(natureName, ignoreCase: true, out Nature nature)
                ? NatureCalculator.GetModifier(nature, char.ToUpperInvariant(statKey[0]) + statKey.Substring(1))
                : 1.0;
        }

        /// <summary>The self-check: the card shows stat, IV and EV together,
        /// so the three must satisfy the games' formula. When they do not,
        /// one misread value is recovered from the other two - first by
        /// solving for the IV, then by trying single-digit OCR confusions on
        /// the EV, then both.</summary>
        public static (int Iv, int Ev, string Status) VerifyRow(
            int baseStat, int level, double natureMod,
            int stat, int iv, int ev, bool isHp, int? hpMax)
        {
            bool Eq(int i, int e) => isHp
                ? OfficialHp(baseStat, i, e, level) == hpMax
                : OfficialStat(baseStat, i, e, level, natureMod) == stat;

            if (isHp && hpMax == null)
            {
                if (ev > 252 || iv > 31)
                {
                    foreach (string variant in DigitVariants($"{ev:000}").OrderBy(v => v, StringComparer.Ordinal))
                    {
                        int e = int.Parse(variant);

                        if (e <= 252 && iv <= 31)
                            return (iv, e, "ev-capped");
                    }
                }

                return (iv, ev, "unchecked");
            }

            if (iv >= 0 && iv <= 31 && ev >= 0 && ev <= 252 && Eq(iv, ev))
                return (iv, ev, "clean");

            for (int i = 0; i <= 31; i++)
            {
                if (ev >= 0 && ev <= 252 && Eq(i, ev))
                    return (i, ev, "iv-fixed");
            }

            foreach (string variant in DigitVariants($"{ev:000}").OrderBy(v => v, StringComparer.Ordinal))
            {
                int e = int.Parse(variant);

                if (e <= 252 && iv >= 0 && iv <= 31 && Eq(iv, e))
                    return (iv, e, "ev-fixed");
            }

            for (int i = 0; i <= 31; i++)
            {
                foreach (string variant in DigitVariants($"{ev:000}").OrderBy(v => v, StringComparer.Ordinal))
                {
                    int e = int.Parse(variant);

                    if (e <= 252 && Eq(i, e))
                        return (i, e, "both-fixed");
                }
            }

            return (iv, ev, "unverified");
        }

        static HashSet<string> DigitVariants(string s)
        {
            var variants = new HashSet<string> { s };

            for (int i = 0; i < s.Length; i++)
            {
                if (!DigitSubs.TryGetValue(s[i], out string? subs))
                    continue;

                foreach (char r in subs)
                    variants.Add(s.Substring(0, i) + r + s.Substring(i + 1));
            }

            return variants;
        }
    }
}