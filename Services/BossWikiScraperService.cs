using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Fetches a boss page from the PRO wiki (wiki.pokemonrevolution.net) and turns
    /// it into the same JSON shape DataFiles/Bosses/*.json already uses, so the
    /// ~50 boss files that only exist as near-empty stubs (bossId/name/NPCPicture/
    /// BossCooldown only - see MIGRATION_GUIDE.md) can be filled in without typing
    /// every team/moveset/reward table out by hand.
    ///
    /// Reads the wiki's raw WIKITEXT (MediaWiki's own template markup - {{BossNPC|
    /// ...}}, {{BossPokemon|...}}, etc.) via the wiki's public API, not the
    /// rendered HTML page. The templates are far more reliable to parse than
    /// scraping rendered HTML tables would be, since they're already effectively
    /// key=value data - but the wiki has been edited by many different people over
    /// many years, so not every boss page uses the exact same templates for the
    /// same information (confirmed directly: Lorelei's page states reward
    /// percentages as free-form prose, "'''Easy''': 85% chance for tier 1...",
    /// while Misty's page uses a clean {{BossModes|...}} template for the exact
    /// same information). This class tries the reliable template-based parse
    /// first and only falls back to regex-matching the prose when the template
    /// isn't there - and it reports what it could NOT confidently parse via
    /// Warnings rather than silently guessing, since a wrong guess written
    /// straight to a boss file would be worse than no data at all.
    ///
    /// Deliberately does not touch fields it didn't scrape (NPCPicture, an
    /// already-set locationPicture, any already-set BossCooldown) when merging
    /// into an existing file - see MergeIntoExisting. And deliberately works at
    /// the raw JsonObject level rather than round-tripping through the BossData
    /// class for that merge - BossData.cs does not model every key the on-disk
    /// files actually contain (NPCPicture and BossCooldown both exist in every
    /// real boss file but neither has a matching BossData property - they're
    /// clearly read by something else, e.g. BossCooldownService). Deserializing
    /// into BossData and serializing back out would silently drop both.
    /// </summary>
    public static class BossWikiScraperService
    {
        private const string ApiBaseUrl = "https://wiki.pokemonrevolution.net/api.php";

        public static async Task<BossScrapeResult> ScrapeAsync(
            string bossId,
            string wikiPageTitle,
            HttpClient httpClient)
        {
            if (string.IsNullOrWhiteSpace(bossId))
                throw new ArgumentException("A boss ID must be provided.", nameof(bossId));
            if (string.IsNullOrWhiteSpace(wikiPageTitle))
                throw new ArgumentException("A wiki page title must be provided.", nameof(wikiPageTitle));

            var warnings = new List<string>();

            string wikitext = await FetchWikitextAsync(wikiPageTitle, httpClient, warnings);
            ParsedBossPage parsed = ParseBossPage(wikitext, warnings);

            string existingPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "DataFiles", "Bosses", $"{bossId}.json");

            JsonObject existingRoot;
            bool existingFileHadRealData = false;

            if (System.IO.File.Exists(existingPath))
            {
                string existingText = System.IO.File.ReadAllText(existingPath);
                existingRoot = JsonNode.Parse(existingText) as JsonObject ?? new JsonObject();

                // "Real data" = it already has at least one difficulty populated -
                // a stub only ever has bossId/name/NPCPicture/BossCooldown.
                existingFileHadRealData =
                    existingRoot["difficulties"] is JsonObject existingDifficulties &&
                    existingDifficulties.Count > 0;
            }
            else
            {
                existingRoot = new JsonObject();
            }

            JsonObject merged = MergeIntoExisting(existingRoot, bossId, parsed, warnings);

            string previewJson = merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            return new BossScrapeResult
            {
                BossId = bossId,
                PreviewJson = previewJson,
                Warnings = warnings,
                ExistingFileHadRealData = existingFileHadRealData,
                MergedJson = merged,
            };
        }

        // ====================================================================
        // Fetching
        // ====================================================================

        private static async Task<string> FetchWikitextAsync(
            string pageTitle, HttpClient httpClient, List<string> warnings)
        {
            string encodedTitle = Uri.EscapeDataString(pageTitle.Replace(' ', '_'));
            string url = $"{ApiBaseUrl}?action=parse&page={encodedTitle}&prop=wikitext&format=json";

            using HttpResponseMessage response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            string body = await response.Content.ReadAsStringAsync();

            using JsonDocument doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("error", out JsonElement errorElement))
            {
                string info = errorElement.TryGetProperty("info", out var infoEl)
                    ? infoEl.GetString() ?? "unknown error"
                    : "unknown error";
                throw new InvalidOperationException(
                    $"The wiki API returned an error for page '{pageTitle}': {info}. " +
                    "Double-check the page title (it usually needs the exact \"(boss)\" suffix, " +
                    "e.g. \"Lorelei (boss)\").");
            }

            return doc.RootElement
                .GetProperty("parse")
                .GetProperty("wikitext")
                .GetProperty("*")
                .GetString() ?? string.Empty;
        }

        // ====================================================================
        // Wikitext template parsing
        // ====================================================================

        /// <summary>
        /// Finds "{{Name ... }}" starting at or after <paramref name="start"/>, matching
        /// nested "{{"/"}}" pairs so a template that itself contains other templates
        /// (e.g. {{BossNPCLineup}} contains many {{BossPokemon}}, which each contain
        /// several {{NPCMove}}) is extracted whole rather than stopping at the first "}}".
        /// Returns the content between the template name and its closing "}}" (still
        /// starting with the leading "|", e.g. "|Boss=Lorelei|Lineup=..."), and the index
        /// just past the closing "}}" - or (null, -1) if the template isn't found.
        /// </summary>
        private static (string? Inner, int End) FindTemplate(string text, string name, int start = 0)
        {
            string marker = "{{" + name;
            int idx = text.IndexOf(marker, start, StringComparison.Ordinal);
            if (idx == -1)
                return (null, -1);

            int pos = idx + marker.Length;
            int depth = 1;
            int i = pos;

            while (i < text.Length && depth > 0)
            {
                if (i + 1 < text.Length && text[i] == '{' && text[i + 1] == '{')
                {
                    depth++;
                    i += 2;
                }
                else if (i + 1 < text.Length && text[i] == '}' && text[i + 1] == '}')
                {
                    depth--;
                    i += 2;
                }
                else
                {
                    i++;
                }
            }

            string inner = text.Substring(pos, Math.Max(0, i - 2 - pos));
            return (inner, i);
        }

        private static List<string> FindAllTemplates(string text, string name)
        {
            var results = new List<string>();
            int pos = 0;
            while (true)
            {
                (string? inner, int end) = FindTemplate(text, name, pos);
                if (inner is null)
                    break;
                results.Add(inner);
                pos = end;
            }
            return results;
        }

        private static readonly Regex WikilinkRegex =
            new(@"\[\[(?:[^|\]]*\|)?([^\]]*)\]\]", RegexOptions.Compiled);

        private static string StripWikilinks(string s)
        {
            s = WikilinkRegex.Replace(s, "$1");
            return s.Replace("'''", "").Replace("''", "").Trim();
        }

        /// <summary>
        /// Splits a template's inner "|key=value|key=value" content into a dictionary
        /// (keys lowercased), treating a "|" as a separator only when it isn't inside a
        /// "[[...]]" wikilink or a nested "{{...}}" template - both appear in real boss
        /// pages (e.g. Requirements=[[Elite Four (Johto)|Johto Elite Four]] completed,
        /// Pokemoney={{Pdollar}}5,000-{{Pdollar}}10,000) and a naive Split('|') would
        /// cut those apart in the wrong place.
        /// </summary>
        private static Dictionary<string, string> SplitParams(string inner)
        {
            var parts = new List<string>();
            int bracketDepth = 0;
            int braceDepth = 0;
            var current = new System.Text.StringBuilder();

            int i = 0;
            while (i < inner.Length)
            {
                if (i + 1 < inner.Length && inner[i] == '[' && inner[i + 1] == '[')
                {
                    bracketDepth++; current.Append("[["); i += 2; continue;
                }
                if (i + 1 < inner.Length && inner[i] == ']' && inner[i + 1] == ']')
                {
                    bracketDepth--; current.Append("]]"); i += 2; continue;
                }
                if (i + 1 < inner.Length && inner[i] == '{' && inner[i + 1] == '{')
                {
                    braceDepth++; current.Append("{{"); i += 2; continue;
                }
                if (i + 1 < inner.Length && inner[i] == '}' && inner[i + 1] == '}')
                {
                    braceDepth--; current.Append("}}"); i += 2; continue;
                }
                if (inner[i] == '|' && bracketDepth == 0 && braceDepth == 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    i++;
                    continue;
                }
                current.Append(inner[i]);
                i++;
            }
            if (current.Length > 0)
                parts.Add(current.ToString());

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in parts)
            {
                int eq = part.IndexOf('=');
                if (eq < 0)
                    continue;

                string key = part[..eq].Trim().ToLowerInvariant();
                string value = part[(eq + 1)..].Trim();
                result[key] = value;
            }
            return result;
        }

        private static readonly Regex MoveRegex =
            new(@"\{\{NPCMove\|([^}]+)\}\}", RegexOptions.Compiled);

        private static List<string> ParseMoveset(string pokemonInner) =>
            MoveRegex.Matches(pokemonInner).Select(m => m.Groups[1].Value.Trim()).ToList();

        private static List<ParsedTeamMember> ParseLineupBlock(string lineupInner)
        {
            var team = new List<ParsedTeamMember>();
            int pos = 0;
            while (true)
            {
                (string? inner, int end) = FindTemplate(lineupInner, "BossPokemon", pos);
                if (inner is null)
                    break;

                Dictionary<string, string> p = SplitParams(inner);
                team.Add(new ParsedTeamMember
                {
                    Form = p.GetValueOrDefault("form", ""),
                    Name = p.GetValueOrDefault("name", ""),
                    Nature = p.GetValueOrDefault("nature", ""),
                    Ability = p.GetValueOrDefault("ability", ""),
                    Item = string.IsNullOrWhiteSpace(p.GetValueOrDefault("item", "")) ? "None" : p["item"],
                    Moves = ParseMoveset(inner),
                });
                pos = end;
            }
            return team;
        }

        /// <summary>
        /// Walks every {{BossNPCLineup}} block whose start position falls within
        /// [start, end), concatenating their teams into one list and recording
        /// each block's "Boss=" NPC name into <paramref name="npcNames"/>.
        ///
        /// Exists because a difficulty section - or, for a single-tier boss, the
        /// whole page - can hold more than one {{BossNPCLineup}}: some bosses are
        /// fought as a sequence of multiple NPCs in the same encounter (Shary &amp;
        /// Shaui and Medusa &amp; Eldir each have a "Boss=&lt;first&gt;"/"Boss=&lt;second&gt;"
        /// pair per difficulty, both battled back-to-back to clear it; Jessie &amp;
        /// James has "Boss=Jessie"/"Boss=James" as alternatives instead). The
        /// on-disk schema has no way to model "these Pokémon belong to a specific
        /// sub-NPC," so every Pokémon from every lineup in range is just combined
        /// into one flat team - confirmed against all three of those pages'
        /// real wikitext directly (the alternative, silently keeping only the
        /// first lineup, is what the original version of this method did, and
        /// it dropped the second NPC's whole team with no warning at all).
        /// </summary>
        private static List<ParsedTeamMember> CollectLineupTeams(
            string wikitext, int start, int end, List<string> npcNames)
        {
            var combined = new List<ParsedTeamMember>();
            int pos = start;

            while (true)
            {
                int nextIdx = wikitext.IndexOf("{{BossNPCLineup", pos, StringComparison.Ordinal);
                if (nextIdx < 0 || nextIdx >= end)
                    break;

                (string? inner, int lineupEnd) = FindTemplate(wikitext, "BossNPCLineup", pos);
                if (inner is null)
                    break;

                string npcName = SplitParams(inner).GetValueOrDefault("boss", "");
                if (!string.IsNullOrWhiteSpace(npcName))
                    npcNames.Add(npcName);

                combined.AddRange(ParseLineupBlock(inner));
                pos = lineupEnd;
            }

            return combined;
        }

        // Matches a MediaWiki heading of ANY level ("==Foo==", "===Foo===", etc.),
        // using a backreference so the opening/closing equals-run lengths must
        // match. Used as a general section-boundary marker (see
        // CollectLineupTeams/ParseBossPage) - not just for the "===Easy==="-style
        // difficulty headings, which are only a subset of what this matches.
        private static readonly Regex AnyHeadingRegex =
            new(@"(={2,6})\s*([^=\n]+?)\s*\1", RegexOptions.Compiled);

        private static readonly Regex CooldownDaysRegex =
            new(@"(\d+)\s*day", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CooldownHoursRegex =
            new(@"(\d+)\s*hour", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ProseRewardRegex = new(
            @"'''(Easy|Medium|Hard)'''\s*:?\s*(\d+)%.*?(\d+)%.*?(\d+)%.*?(\d+)\s*-\s*(\d+)k\s*money(?:.*?\+\s*(\d+)\s*PvE\s*Coins)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MoneyRangeRegex =
            new(@"(\d+)\s*-\s*(\d+)", RegexOptions.Compiled);

        private static ParsedBossPage ParseBossPage(string wikitext, List<string> warnings)
        {
            var result = new ParsedBossPage();

            // --- {{BossNPC|...}} infobox ---
            (string? infoboxInner, _) = FindTemplate(wikitext, "BossNPC");
            Dictionary<string, string> infobox = infoboxInner is not null
                ? SplitParams(infoboxInner)
                : new Dictionary<string, string>();

            if (infoboxInner is null)
                warnings.Add("Could not find the {{BossNPC}} infobox template - name/location/requirements/cooldown were not scraped.");

            result.Name = infobox.GetValueOrDefault("boss", "");
            result.Location = StripWikilinks(infobox.GetValueOrDefault("location", ""));
            result.Requirements = StripWikilinks(infobox.GetValueOrDefault("requirements", ""));

            string cooldownRaw = infobox.GetValueOrDefault("cooldown", "");
            Match daysMatch = CooldownDaysRegex.Match(cooldownRaw);
            Match hoursMatch = CooldownHoursRegex.Match(cooldownRaw);
            if (daysMatch.Success)
                result.BossCooldownHours = int.Parse(daysMatch.Groups[1].Value) * 24;
            else if (hoursMatch.Success)
                result.BossCooldownHours = int.Parse(hoursMatch.Groups[1].Value);
            else if (!string.IsNullOrWhiteSpace(cooldownRaw))
                warnings.Add($"Could not parse a cooldown duration from '{cooldownRaw}' - left BossCooldown unchanged.");

            // --- ===Easy=== / ===Medium=== / ===Hard=== / ===Medium/Hard=== lineups ---
            List<Match> allHeadings = AnyHeadingRegex.Matches(wikitext).Cast<Match>().ToList();
            bool foundDifficultyHeading = false;
            bool usedSingleTierFallback = false;

            for (int h = 0; h < allHeadings.Count; h++)
            {
                Match headingMatch = allHeadings[h];
                string headingText = headingMatch.Groups[2].Value.Trim();
                string heading = headingText.ToLowerInvariant();
                if (heading is not ("easy" or "medium" or "hard" or "medium/hard"))
                    continue;

                foundDifficultyHeading = true;

                int sectionStart = headingMatch.Index + headingMatch.Length;
                int sectionEnd = h + 1 < allHeadings.Count ? allHeadings[h + 1].Index : wikitext.Length;

                var npcNames = new List<string>();
                List<ParsedTeamMember> team = CollectLineupTeams(wikitext, sectionStart, sectionEnd, npcNames);

                if (team.Count == 0)
                {
                    warnings.Add($"Found a '{headingText}' heading but no {{{{BossNPCLineup}}}} block under it.");
                    continue;
                }

                if (npcNames.Count > 1)
                {
                    warnings.Add(
                        $"'{headingText}' section combined {npcNames.Count} NPC lineups ({string.Join(", ", npcNames)}) " +
                        $"into one team ({team.Count} Pokémon total) - this boss is fought as multiple NPCs in the " +
                        "same encounter; verify this is how you want it tracked.");
                }

                if (heading == "medium/hard")
                {
                    result.Teams["medium"] = team;
                    result.Teams["hard"] = team;
                }
                else
                {
                    result.Teams[heading] = team;
                }
            }

            if (!foundDifficultyHeading)
            {
                // No Easy/Medium/Hard/Medium-Hard heading anywhere on the page at
                // all - confirmed this happens for two different real reasons: a
                // boss with one single lineup and no difficulty split whatsoever
                // (The Pumpkin King's page has just a plain "==Lineup==" with one
                // {{BossNPCLineup}} under it), and a boss whose sections are
                // headed by NPC names instead of difficulty words because you
                // fight whichever one you choose (Jessie & James:
                // "===Jessie==="/"===James===", page text confirms "one
                // difficulty ... equivalent to medium"). Either way, every
                // {{BossNPCLineup}} on the page gets combined into one team and
                // used for all three difficulty keys, since the on-disk schema
                // has no way to represent "this boss has no difficulty split" -
                // only that all three keys got identical data, which the warning
                // below makes explicit rather than silent.
                var npcNames = new List<string>();
                List<ParsedTeamMember> combinedTeam = CollectLineupTeams(wikitext, 0, wikitext.Length, npcNames);

                if (combinedTeam.Count > 0)
                {
                    result.Teams["easy"] = combinedTeam;
                    result.Teams["medium"] = combinedTeam;
                    result.Teams["hard"] = combinedTeam;
                    usedSingleTierFallback = true;

                    string namesPart = npcNames.Count > 0 ? $" ({string.Join(", ", npcNames)})" : "";
                    warnings.Add(
                        $"No Easy/Medium/Hard sections found on this page at all - combined every {{{{BossNPCLineup}}}} " +
                        $"found{namesPart} into one {combinedTeam.Count}-Pokémon team and used it for all three " +
                        "difficulties. This usually means the boss has only one real lineup (no per-difficulty " +
                        "variation) or is fought as either-or of multiple NPCs - double-check this is the right call " +
                        "before saving.");
                }
            }

            if (result.Teams.Count == 0)
                warnings.Add("No team lineups were found at all - the page may use a different structure than expected.");

            // --- Rewards: {{BossModes}} template, falling back to prose ---
            (string? modesInner, _) = FindTemplate(wikitext, "BossModes");
            if (modesInner is not null)
            {
                foreach (string rowInner in FindAllTemplates(modesInner, "BossModesRow"))
                {
                    Dictionary<string, string> p = SplitParams(rowInner);
                    string mode = p.GetValueOrDefault("mode", "").ToLowerInvariant();
                    if (string.IsNullOrEmpty(mode))
                        continue;

                    string moneyClean = p.GetValueOrDefault("pokemoney", "")
                        .Replace("{{Pdollar}}", "", StringComparison.OrdinalIgnoreCase)
                        .Replace(",", "");
                    Match moneyMatch = MoneyRangeRegex.Match(moneyClean);

                    result.Modes[mode] = new ParsedModeRewards
                    {
                        Tier1 = ParseIntOrZero(p.GetValueOrDefault("tier1", "0")),
                        Tier2 = ParseIntOrZero(p.GetValueOrDefault("tier2", "0")),
                        Tier3 = ParseIntOrZero(p.GetValueOrDefault("tier3", "0")),
                        MoneyMin = moneyMatch.Success ? int.Parse(moneyMatch.Groups[1].Value) : 0,
                        MoneyMax = moneyMatch.Success ? int.Parse(moneyMatch.Groups[2].Value) : 0,
                        PveCoins = ParseIntOrZero(p.GetValueOrDefault("pvecoins", "0")),
                    };
                }
            }
            else
            {
                foreach (Match m in ProseRewardRegex.Matches(wikitext))
                {
                    string mode = m.Groups[1].Value.ToLowerInvariant();
                    result.Modes[mode] = new ParsedModeRewards
                    {
                        Tier1 = int.Parse(m.Groups[2].Value),
                        Tier2 = int.Parse(m.Groups[3].Value),
                        Tier3 = int.Parse(m.Groups[4].Value),
                        MoneyMin = int.Parse(m.Groups[5].Value) * 1000,
                        MoneyMax = int.Parse(m.Groups[6].Value) * 1000,
                        PveCoins = m.Groups[7].Success ? int.Parse(m.Groups[7].Value) : 0,
                    };
                }
            }

            if (result.Modes.Count == 0)
                warnings.Add("Could not find a {{BossModes}} template or matching reward-percentage prose - tierChances/pokedollars/pveCoins were not scraped.");

            // A single-tier boss (see usedSingleTierFallback above) can still have
            // its one real reward tier labeled with a difficulty name on the wiki
            // even though the boss itself has no actual difficulty split - Jessie
            // & James's page has exactly one {{BossModesRow|Mode=Hard|...}} despite
            // the boss having only one real fight. Rather than leave the other two
            // difficulty keys' rewards blank when a perfectly good reward figure
            // already exists, reuse that one entry for whichever keys are still
            // missing - matching the same "duplicate across all three, but warn"
            // treatment already applied to the team above.
            if (usedSingleTierFallback && result.Modes.Count > 0 && result.Modes.Count < 3)
            {
                string sourceModeLabel = result.Modes.Keys.First();
                ParsedModeRewards sourceMode = result.Modes[sourceModeLabel];

                foreach (string key in new[] { "easy", "medium", "hard" })
                    result.Modes.TryAdd(key, sourceMode);

                warnings.Add(
                    $"This boss's rewards were only labeled '{sourceModeLabel}' on the wiki (no separate " +
                    "easy/medium/hard breakdown) - used that same reward data for all three difficulties, " +
                    "matching the combined team above.");
            }

            // --- Randomized subset: Pokemon reward pool + item reward pool ---
            int subsetIdx = wikitext.IndexOf("Randomized subset", StringComparison.OrdinalIgnoreCase);
            int searchStart = subsetIdx >= 0 ? subsetIdx : 0;

            (string? poolInner, _) = FindTemplate(wikitext, "NBossRewardTable", searchStart);
            if (poolInner is not null)
            {
                foreach (string rowInner in FindAllTemplates(poolInner, "RewardRow"))
                {
                    Dictionary<string, string> p = SplitParams(rowInner);
                    result.PokemonPool.Add(new ParsedPokemonReward
                    {
                        Name = p.GetValueOrDefault("pokemon", ""),
                        Tier = p.GetValueOrDefault("reward tier", ""),
                    });
                }
            }
            else
            {
                warnings.Add("Could not find the randomized-subset Pokémon reward table.");
            }

            (string? itemsInner, _) = FindTemplate(wikitext, "BossItem", searchStart);
            if (itemsInner is not null)
            {
                foreach (string rowInner in FindAllTemplates(itemsInner, "BossItemRow"))
                {
                    Dictionary<string, string> p = SplitParams(rowInner);
                    bool isTm = p.ContainsKey("tm") && !p.ContainsKey("item");
                    string itemName = isTm ? p.GetValueOrDefault("tm", "") : p.GetValueOrDefault("item", "");

                    result.Items.Add(new ParsedItemReward
                    {
                        Name = isTm && !string.IsNullOrEmpty(itemName) ? $"TM: {itemName}" : itemName,
                        Quantity = p.GetValueOrDefault("quantity", ""),
                        Tier = p.GetValueOrDefault("reward tier", ""),
                    });
                }
            }
            else
            {
                warnings.Add("Could not find the item reward table.");
            }

            // --- Three-consecutive-wins bonus Pokemon (maps to winStreakRequired) ---
            int streakIdx = wikitext.IndexOf("Three-consecutive wins", StringComparison.OrdinalIgnoreCase);
            if (streakIdx >= 0)
            {
                (string? bonusInner, _) = FindTemplate(wikitext, "NBossRewardTable", streakIdx);
                if (bonusInner is not null)
                {
                    foreach (string rowInner in FindAllTemplates(bonusInner, "RewardRow"))
                    {
                        Dictionary<string, string> p = SplitParams(rowInner);
                        string name = p.GetValueOrDefault("pokemon", "");
                        if (!string.IsNullOrEmpty(name))
                            result.BonusPokemon.Add(name);
                    }
                }
            }

            return result;
        }

        private static int ParseIntOrZero(string s) => int.TryParse(s, out int v) ? v : 0;

        // ====================================================================
        // Dex number resolution (reuses the app's own Pokemon database rather
        // than hardcoding a second one here)
        // ====================================================================

        internal static (int DexNumber, string ResolvedName, bool Found) ResolveDexNumber(string wikiName, string form)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(form))
            {
                candidates.Add($"{form} {wikiName}");
                candidates.Add($"{wikiName}-{form}");
                candidates.Add($"{wikiName} ({form})");
            }
            candidates.Add(wikiName);

            foreach (string candidate in candidates)
            {
                var formMatch = PokemonSpriteService.AllForms.FirstOrDefault(f =>
                    string.Equals(f.Name, candidate, StringComparison.OrdinalIgnoreCase) ||
                    f.OcrAliases.Any(a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase)));
                if (formMatch is not null)
                    return (formMatch.DexNumber, formMatch.Name, true);

                var speciesMatch = PokemonSpriteService.AllPokemon.FirstOrDefault(p =>
                    string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase) ||
                    p.OcrAliases.Any(a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase)));
                if (speciesMatch is not null)
                    return (speciesMatch.DexNumber, speciesMatch.Name, true);
            }

            return (0, wikiName, false);
        }

        // ====================================================================
        // Merge into the existing (or new) JSON file
        // ====================================================================

        private static JsonObject MergeIntoExisting(
            JsonObject existing, string bossId, ParsedBossPage parsed, List<string> warnings)
        {
            var root = existing;

            // The boss-id key shows up as both "bossId" (every file the app itself has
            // ever written) and "BossID" (about half the stub files - confirmed by
            // inspecting every existing file directly; even one fully-populated file,
            // George.json, uses the capital-ID spelling). JsonObject property lookups
            // are case-sensitive, so root["bossId"] alone would miss "BossID" entirely,
            // and setting root["bossId"] afterwards without removing "BossID" would
            // leave both keys sitting in the file side by side. Collect whatever value
            // any variant already has, then drop every variant and write exactly one
            // canonical "bossId" key back.
            string? existingId = null;
            foreach (string idKey in new[] { "bossId", "BossID", "BossId", "Bossid" })
            {
                if (root.TryGetPropertyValue(idKey, out JsonNode? idNode) &&
                    idNode is JsonValue idValue &&
                    idValue.TryGetValue<string>(out string? idStr) &&
                    !string.IsNullOrWhiteSpace(idStr))
                {
                    existingId ??= idStr;
                }
                root.Remove(idKey);
            }
            root["bossId"] = !string.IsNullOrWhiteSpace(existingId) ? existingId : bossId;

            if (!string.IsNullOrWhiteSpace(parsed.Name))
                root["name"] = parsed.Name;

            if (!string.IsNullOrWhiteSpace(parsed.Location))
                root["location"] = parsed.Location;

            // locationPicture/NPCPicture: only guess a path if one isn't already
            // set - these point at real image files the scraper has no way to
            // know exist or not, so preserving whatever's already there (even if
            // it's blank) is safer than confidently overwriting it with a guess.
            string pascalName = ToPascalCase(string.IsNullOrWhiteSpace(parsed.Name) ? bossId : parsed.Name);

            if (root["locationPicture"] is null || string.IsNullOrWhiteSpace(root["locationPicture"]!.GetValue<string>()))
            {
                root["locationPicture"] = $"SharedPokemonLibrary/Assets/Locations/{pascalName}Loc.png";
                warnings.Add($"Guessed locationPicture as \"{pascalName}Loc.png\" - verify this file actually exists.");
            }

            if (root["NPCPicture"] is null || string.IsNullOrWhiteSpace(root["NPCPicture"]!.GetValue<string>()))
            {
                root["NPCPicture"] = $"SharedPokemonLibrary/Assets/Bosses/{pascalName}.png";
                warnings.Add($"Guessed NPCPicture as \"{pascalName}.png\" - verify this file actually exists.");
            }

            if (parsed.BossCooldownHours.HasValue)
                root["BossCooldown"] = parsed.BossCooldownHours.Value;

            if (!string.IsNullOrWhiteSpace(parsed.Requirements))
                root["requirements"] = parsed.Requirements;

            if (parsed.Teams.Count > 0)
            {
                var difficulties = new JsonObject();

                foreach (string difficultyKey in new[] { "easy", "medium", "hard" })
                {
                    if (!parsed.Teams.TryGetValue(difficultyKey, out List<ParsedTeamMember>? team))
                        continue;

                    var teamArray = new JsonArray();
                    foreach (ParsedTeamMember member in team)
                    {
                        (int dexNumber, string resolvedName, bool found) =
                            ResolveDexNumber(member.Name, member.Form);

                        if (!found)
                        {
                            warnings.Add(
                                $"Could not resolve a dex number for \"{(string.IsNullOrEmpty(member.Form) ? "" : member.Form + " ")}{member.Name}\" " +
                                $"({difficultyKey}) - set to 0, its sprite will not show until this is fixed by hand.");
                        }

                        teamArray.Add(new JsonObject
                        {
                            ["name"] = resolvedName,
                            ["dexNumber"] = dexNumber,
                            ["nature"] = member.Nature,
                            ["ability"] = member.Ability,
                            ["item"] = member.Item,
                            ["moves"] = new JsonArray(member.Moves.Select(mv => JsonValue.Create(mv)).ToArray()),
                        });
                    }

                    var rewardsObject = new JsonObject
                    {
                        ["pokedollars"] = new JsonObject(),
                        ["pveCoins"] = 0,
                        ["items"] = new JsonArray(),
                        ["pokemon"] = new JsonArray(),
                    };

                    if (parsed.Modes.TryGetValue(difficultyKey, out ParsedModeRewards? modeRewards))
                    {
                        rewardsObject["pokedollars"] = new JsonObject
                        {
                            ["minimum"] = modeRewards.MoneyMin,
                            ["maximum"] = modeRewards.MoneyMax,
                        };
                        rewardsObject["pveCoins"] = modeRewards.PveCoins;
                    }

                    var itemsArray = new JsonArray();
                    foreach (ParsedItemReward item in parsed.Items)
                    {
                        itemsArray.Add(new JsonObject
                        {
                            ["name"] = item.Name,
                            ["quantity"] = item.Quantity,
                            ["tier"] = int.TryParse(item.Tier, out int tierNum) ? tierNum : 0,
                            ["picture"] = "SharedPokemonLibrary/Assets/Items/MissingIcon.png",
                        });
                    }
                    rewardsObject["items"] = itemsArray;

                    var pokemonArray = new JsonArray();
                    foreach (ParsedPokemonReward reward in parsed.PokemonPool)
                    {
                        (int dexNumber, string resolvedName, bool found) = ResolveDexNumber(reward.Name, "");
                        if (!found)
                            warnings.Add($"Could not resolve a dex number for reward Pokémon \"{reward.Name}\".");

                        pokemonArray.Add(new JsonObject
                        {
                            ["name"] = resolvedName,
                            ["dexNumber"] = dexNumber,
                            ["picture"] = "",
                            ["winStreakRequired"] = 0,
                        });
                    }

                    // Three-consecutive-win bonus picks use the same schema field
                    // with winStreakRequired=3 - BossPokemonReward already models
                    // this even though no existing file has populated it yet (see
                    // MIGRATION_GUIDE.md).
                    foreach (string bonusName in parsed.BonusPokemon)
                    {
                        (int dexNumber, string resolvedName, bool found) = ResolveDexNumber(bonusName, "");
                        if (!found)
                            warnings.Add($"Could not resolve a dex number for three-win bonus Pokémon \"{bonusName}\".");

                        pokemonArray.Add(new JsonObject
                        {
                            ["name"] = resolvedName,
                            ["dexNumber"] = dexNumber,
                            ["picture"] = "",
                            ["winStreakRequired"] = 3,
                        });
                    }
                    rewardsObject["pokemon"] = pokemonArray;

                    var tierChances = new JsonObject();
                    if (parsed.Modes.TryGetValue(difficultyKey, out ParsedModeRewards? tierSource))
                    {
                        tierChances["tier1"] = tierSource.Tier1;
                        tierChances["tier2"] = tierSource.Tier2;
                        tierChances["tier3"] = tierSource.Tier3;
                    }

                    difficulties[difficultyKey] = new JsonObject
                    {
                        ["tierChances"] = tierChances,
                        ["rewards"] = rewardsObject,
                        ["team"] = teamArray,
                    };
                }

                root["difficulties"] = difficulties;
            }

            return root;
        }

        private static string ToPascalCase(string name)
        {
            var letters = name.Where(char.IsLetterOrDigit);
            var sb = new System.Text.StringBuilder();
            bool capitalizeNext = true;
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
                    capitalizeNext = false;
                }
                else
                {
                    capitalizeNext = true;
                }
            }
            return sb.ToString();
        }

        // ====================================================================
        // Internal parsing DTOs
        // ====================================================================

        private sealed class ParsedTeamMember
        {
            public string Form { get; init; } = "";
            public string Name { get; init; } = "";
            public string Nature { get; init; } = "";
            public string Ability { get; init; } = "";
            public string Item { get; init; } = "None";
            public List<string> Moves { get; init; } = new();
        }

        private sealed class ParsedModeRewards
        {
            public int Tier1 { get; init; }
            public int Tier2 { get; init; }
            public int Tier3 { get; init; }
            public int MoneyMin { get; init; }
            public int MoneyMax { get; init; }
            public int PveCoins { get; init; }
        }

        private sealed class ParsedPokemonReward
        {
            public string Name { get; init; } = "";
            public string Tier { get; init; } = "";
        }

        private sealed class ParsedItemReward
        {
            public string Name { get; init; } = "";
            public string Quantity { get; init; } = "";
            public string Tier { get; init; } = "";
        }

        private sealed class ParsedBossPage
        {
            public string Name { get; set; } = "";
            public string Location { get; set; } = "";
            public string Requirements { get; set; } = "";
            public int? BossCooldownHours { get; set; }
            public Dictionary<string, List<ParsedTeamMember>> Teams { get; } = new();
            public Dictionary<string, ParsedModeRewards> Modes { get; } = new();
            public List<ParsedPokemonReward> PokemonPool { get; } = new();
            public List<string> BonusPokemon { get; } = new();
            public List<ParsedItemReward> Items { get; } = new();
        }
    }

    /// <summary>Result of a scrape - holds a human-reviewable JSON preview and only
    /// actually touches disk when <see cref="Save"/> is called explicitly.</summary>
    public sealed class BossScrapeResult
    {
        public required string BossId { get; init; }
        public required string PreviewJson { get; init; }
        public required IReadOnlyList<string> Warnings { get; init; }

        /// <summary>True if DataFiles/Bosses/{BossId}.json already had at least one
        /// populated difficulty before this scrape - i.e. Save() would overwrite
        /// real, possibly hand-verified data rather than just filling in a stub.</summary>
        public required bool ExistingFileHadRealData { get; init; }

        // Not `required`: a required member can't be less visible than its
        // containing type (BossScrapeResult is public, this is deliberately
        // internal - CS9032 if both), and every construction site already
        // sets it unconditionally anyway (see ScrapeAsync). Currently unused
        // by any consumer (Save() writes from PreviewJson instead) - kept for
        // a future caller that wants the structured JsonObject directly
        // rather than re-parsing PreviewJson.
        internal JsonObject MergedJson { get; init; } = new();

        public void Save()
        {
            string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "DataFiles", "Bosses");
            System.IO.Directory.CreateDirectory(folder);
            string filePath = System.IO.Path.Combine(folder, $"{BossId}.json");
            System.IO.File.WriteAllText(filePath, PreviewJson);
        }
    }
}
