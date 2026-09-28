using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace Foot_Tracker.Services;

/// <summary>
/// Validates RouteDetector's corner-HUD OCR against the real catalog of
/// Pokemon Revolution Online map names - DataFiles/pro-locations.json, 572
/// canonical locations merged from the PRO Wiki's Locations category and the
/// pokemap project's location data (both sources, retrieval dates, and the
/// merge rules are recorded in the file's own metadata and in
/// MIGRATION_GUIDE.md §100). RouteDetector's candidate-extraction doc
/// (EnumerateRouteNameCandidates since §101, ExtractRouteName before it)
/// carried "no catalog exists yet to validate against" since the day it was
/// written; this is that catalog.
///
/// TryMatch is a controlled, bounded matcher - not nearest-neighbor
/// autocorrect. Layers, in order:
///   1. EXACT match on a normalized form (casefold, accents folded, all
///      punctuation/whitespace dropped) of every canonical name and verified
///      alias - covers capitalization, spacing, "Mt"/"Mt.", missing
///      apostrophes, "Pokemon"/"Pokémon".
///   2. CONFUSION-FOLDED match: the same normalized form with the classic
///      OCR confusions collapsed identically on both sides (0/O, 1/I/l, 5/S,
///      rn/m, vv/w). Folded keys that collide between two different maps are
///      dropped at build time, so this layer can never pick between them.
///   3. Bounded Levenshtein fallback, and only for raw text at least 6
///      normalized characters long: allowed distance 1 (under 10 chars),
///      2 (under 16), 3 (16 or more); the candidate's digits must equal the
///      raw text's digits exactly (a misread "Route 15" can never fuzzy-match
///      "Route 18" at any distance); and the best candidate must beat every
///      other map's distance outright - a tie is rejected ("Vulcan Cvve"
///      sits one edit from both Vulcan Cove and Vulcan Cave, so it matches
///      neither rather than guessing).
///   4. §356 INTERIOR FLOORS: the catalog lists "Mt. Summer", not
///      "Mt. Summer 2F 2", and PRO's corner HUD shows the floor. Of 578
///      entries exactly five carry a floor token (the Iron Island set), so
///      every multi-floor interior in the game read as Unknown. A trailing
///      interior suffix - a REQUIRED floor token (1F, 2F, B1F), then an
///      optional instance number and an optional L/R side - is split off,
///      the head goes through layers 1-3, and a confirmed parent is joined
///      back to the tidied suffix. The floor token is what keeps this away
///      from "Route 11" and every other bare-numbered name: no floor token,
///      no layer 4. A result from this layer is therefore NOT itself a
///      catalog entry - it is a catalog entry plus a floor - and it is the
///      one case where TryMatch returns a name the dictionary does not
///      contain. Callers record it as the location; nothing downstream
///      looks the result back up.
/// Anything that fails all four returns null, and the caller's existing
/// "null means keep showing the previous reading" contract does the rest -
/// garbage like "FEFE" or "VilkaRCor" (real reads from the §99 history
/// windows) no longer replaces a good "Vulcan Cove".
///
/// Loaded and indexed once, lazily, on first use (a few milliseconds for
/// ~640 keys - measured and logged at Debug level); the file is never read
/// again after that, no network is ever touched, and a missing or damaged
/// file just means TryMatch returns the input's exact-normalized match
/// against nothing - i.e. null - while route OCR keeps running (the same
/// fail-soft rule every DataFiles-backed catalog in this app follows).
/// TryMatch runs on EncounterTracker's scan thread (never the UI thread) at
/// most once per corner-OCR tick, and layer 3 is only reached when the read
/// missed both dictionaries - a full scan is ~640 short-string comparisons
/// with early cutoffs, far under a millisecond.
/// </summary>
public static class LocationDictionaryService
{
    private static readonly string DictionaryPath =
        Path.Combine(AppContext.BaseDirectory, "DataFiles", "pro-locations.json");

    private static readonly object buildLock = new();

    // normalized name/alias -> canonical display name
    private static Dictionary<string, string>? exactLookup;

    // confusion-folded -> canonical, minus build-time collisions
    private static Dictionary<string, string>? foldedLookup;

    // (normalized key, canonical) pairs for the bounded fuzzy layer
    private static List<KeyValuePair<string, string>>? fuzzyCandidates;

    // Last raw text layer 3 rejected, so the diagnostic log line fires once
    // per distinct unmatched reading instead of once per scan tick.
    private static string? lastLoggedMiss;

    private sealed class LocationFile
    {
        public List<LocationEntry> Locations { get; set; } = new();
    }

    private sealed class LocationEntry
    {
        public string Name { get; set; } = string.Empty;
        public List<string> Aliases { get; set; } = new();
    }

    /// <summary>
    /// The canonical PRO map name for <paramref name="rawText"/>, or null when
    /// no sufficiently close, unambiguous match exists - see the class doc
    /// comment for the exact layering. Callers treat null as "not a usable
    /// reading", never as "clear the display".
    /// </summary>
    public static string? TryMatch(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return null;

        EnsureBuilt();

        if (exactLookup is null || exactLookup.Count == 0)
            return null;

        string normalized = Normalize(rawText);

        if (normalized.Length < 3)
            return null;

        if (exactLookup.TryGetValue(normalized, out string? exact))
            return exact;

        if (foldedLookup!.TryGetValue(FoldConfusions(normalized), out string? folded))
            return folded;

        // Layer 3 - bounded fuzzy (its own method since §431, so the
        // building-prefix layer can run it on a remainder). Deliberately
        // refused for short strings: one or two edits are a large fraction
        // of a short name, and short names ("Moon", route numbers) are
        // exactly where a weak match picks the wrong map.
        if (normalized.Length < 6)
            return null;

        string? fuzzy = TryMatchFuzzy(normalized);

        if (fuzzy is not null)
            return fuzzy;

        // §356 layer 4 - see the class doc. Only reached when the whole
        // reading matched nothing, and only ever fires on a reading that
        // ends in a floor token.
        string? withFloor = TryMatchInteriorFloor(rawText);

        if (withFloor is not null)
            return withFloor;

        // §431 layer 5. Inside a Pokecenter or a Pokemart the HUD prints
        // the building before the town - "Pokecenter Lilycove City" - and
        // the catalog knows the town. The town IS where the player is, and
        // no wild Pokemon spawns indoors, so answering with it is right and
        // costs nothing: the next encounter, on the route outside, brings
        // its own reading. Only the building words are stripped, and only
        // from the front; the remainder still has to be a confirmed map.
        string? withoutBuilding = TryMatchWithoutBuilding(rawText);

        if (withoutBuilding is not null)
            return withoutBuilding;

        // One Debug line per distinct rejected reading - the breadcrumb for
        // extending the dictionary if a real map ever goes missing from it.
        if (rawText != lastLoggedMiss)
        {
            lastLoggedMiss = rawText;
            Log.Debug("LocationDictionary: no confident match for {RawText}", rawText);
        }

        return null;
    }

    /// <summary>Layer 3: the one catalog name within a bounded edit
    /// distance of a normalised reading, with the same digits, and with no
    /// second name as close - or null. The cap grows with the length:
    /// one edit under ten characters, two under sixteen, three beyond.</summary>
    private static string? TryMatchFuzzy(string normalized)
    {
        if (normalized.Length < 6 || fuzzyCandidates is null)
            return null;

        int cap = normalized.Length < 10 ? 1 : normalized.Length < 16 ? 2 : 3;
        string rawDigits = DigitsOf(normalized);

        string? best = null;
        int bestDistance = cap + 1;
        int secondDistance = cap + 1;

        foreach (KeyValuePair<string, string> candidate in fuzzyCandidates)
        {
            if (DigitsOf(candidate.Key) != rawDigits)
                continue;

            int distance = BoundedLevenshtein(normalized, candidate.Key, cap);

            if (distance < bestDistance)
            {
                if (candidate.Value != best)
                    secondDistance = bestDistance;

                best = candidate.Value;
                bestDistance = distance;
            }
            else if (distance < secondDistance && candidate.Value != best)
            {
                secondDistance = distance;
            }
        }

        return best is not null && bestDistance <= cap && bestDistance < secondDistance ? best : null;
    }

    /// <summary>§431. The reading with a leading "Pokecenter " or
    /// "Pokemart " (spelt as the OCR spells them, accent or not) taken off
    /// and the remainder put through layers 1-3; null when the reading has
    /// no such prefix or the remainder is not a confirmed map. Never
    /// recurses into layer 4 or itself, so "Pokecenter Pokecenter X" and a
    /// building with a floor stay unmatched rather than guessed.</summary>
    private static string? TryMatchWithoutBuilding(string rawText)
    {
        string trimmed = rawText.Trim();

        foreach (string building in BuildingPrefixes)
        {
            if (trimmed.Length <= building.Length + 3
                || !trimmed.StartsWith(building, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string remainder = trimmed[building.Length..].Trim();

            if (remainder.Length < 3)
                continue;

            string normalized = Normalize(remainder);

            if (normalized.Length < 3 || exactLookup is null)
                continue;

            if (exactLookup.TryGetValue(normalized, out string? exact))
                return exact;

            if (foldedLookup is not null && foldedLookup.TryGetValue(FoldConfusions(normalized), out string? folded))
                return folded;

            // Layer 3 again, on the remainder alone.
            string? fuzzy = TryMatchFuzzy(normalized);

            if (fuzzy is not null)
                return fuzzy;
        }

        return null;
    }

    private static readonly string[] BuildingPrefixes =
    {
        "Pokecenter ", "Pok\u00e9center ", "Pokemon Center ", "Pok\u00e9mon Center ",
        "Pokemart ", "Pok\u00e9mart ", "Poke Mart ", "Pok\u00e9 Mart ",
    };

    /// <summary>
    /// §356. "Mt. Summer 2F 2" when the catalog only knows "Mt. Summer":
    /// split off a trailing interior suffix, confirm the HEAD through layers
    /// 1-3, and rejoin. Returns null unless the head is a confirmed map, so
    /// "Summer 2F 2" (head not in the catalog) and "2F 2" (no head at all)
    /// both stay unmatched - this adds floors to known maps, it does not
    /// invent maps.
    ///
    /// The suffix must contain a floor token. That single requirement is
    /// what makes the layer safe on a catalog full of bare-numbered names:
    /// "Route 11" has no floor token, so it is never split, and layers 1-3
    /// have already answered it anyway.
    /// </summary>
    private static string? TryMatchInteriorFloor(string rawText)
    {
        Match split = InteriorSuffix.Match(rawText.Trim());

        if (!split.Success)
            return null;

        string head = split.Groups["head"].Value.Trim();

        if (head.Length == 0)
            return null;

        string? parent = TryMatch(head);

        if (parent is null)
            return null;

        return parent + " " + TidyInteriorSuffix(split.Groups["suffix"].Value);
    }

    /// <summary>The §356 interior suffix: a floor token (optionally with a
    /// space the OCR put inside it, "2 F"), then an optional instance number
    /// and an optional side letter. Anchored to the END of the reading, and
    /// the separator before it must be real whitespace or punctuation so a
    /// name cannot be cut mid-word.</summary>
    private static readonly Regex InteriorSuffix = new(
        @"^(?<head>.*?)[\s,\.]+(?<suffix>B?\d{1,2}\s?F(?:\s+\d{1,2})?(?:\s+[LR])?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Upper-cases the suffix and squeezes the OCR's stray spaces
    /// out of the floor token itself, so "2 f 2" and "2F 2" record the same
    /// location rather than two.</summary>
    private static string TidyInteriorSuffix(string suffix)
    {
        string[] parts = suffix.ToUpperInvariant().Split(
            ' ', StringSplitOptions.RemoveEmptyEntries);

        var rebuilt = new List<string>(parts.Length);

        foreach (string part in parts)
        {
            if (rebuilt.Count > 0
                && rebuilt[^1].Length <= 2
                && rebuilt[^1].All(char.IsAsciiDigit)
                && part == "F")
            {
                rebuilt[^1] += "F";
                continue;
            }

            if (rebuilt.Count > 0 && rebuilt[^1] == "B" && part.EndsWith("F", StringComparison.Ordinal))
            {
                rebuilt[^1] += part;
                continue;
            }

            rebuilt.Add(part);
        }

        return string.Join(' ', rebuilt);
    }

    /// <summary>
    /// §356. Whether two OCR readings are the same place name, used by
    /// RouteDetector to decide that the spawn panel's "Pokemon in X" header
    /// is talking about the map the corner HUD is showing - that panel has a
    /// search box, so its header can name a map the player is not standing
    /// on, and only agreement with the HUD makes it usable.
    ///
    /// Digits must match EXACTLY, so "2F 2" and "2F 3" are never the same
    /// reading however similar they look; beyond that one or two edits of
    /// slack, which is the size of the damage OCR does to these strings
    /// (the report this came from lost the "t" out of "Mt.").
    /// </summary>
    public static bool LooksLikeSameName(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        string left = Normalize(a);
        string right = Normalize(b);

        if (left.Length < 3 || right.Length < 3)
            return false;

        if (left == right)
            return true;

        if (DigitsOf(left) != DigitsOf(right))
            return false;

        int cap = Math.Min(left.Length, right.Length) < 6 ? 1 : 2;

        return BoundedLevenshtein(left, right, cap) <= cap;
    }

    /// <summary>Admin Console's "Reload Location Dictionary" (§101): drops
    /// the built lookups so the next TryMatch lazily rebuilds them from the
    /// current file - lets an edited pro-locations.json take effect without
    /// an app restart. Thread-safe against a concurrent TryMatch via the
    /// same build lock (TryMatch reads the three fields it needs after
    /// EnsureBuilt, which re-populates them under this lock).</summary>
    public static void Reload()
    {
        lock (buildLock)
        {
            exactLookup = null;
            foldedLookup = null;
            fuzzyCandidates = null;
            lastLoggedMiss = null;
        }
    }

    private static void EnsureBuilt()
    {
        if (exactLookup is not null)
            return;

        lock (buildLock)
        {
            if (exactLookup is not null)
                return;

            var timer = Stopwatch.StartNew();

            var exact = new Dictionary<string, string>(StringComparer.Ordinal);
            var fold = new Dictionary<string, string>(StringComparer.Ordinal);
            var collided = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                if (File.Exists(DictionaryPath))
                {
                    LocationFile? file = JsonSerializer.Deserialize<LocationFile>(
                        File.ReadAllText(DictionaryPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (file is not null)
                    {
                        foreach (LocationEntry entry in file.Locations)
                        {
                            if (string.IsNullOrWhiteSpace(entry.Name))
                                continue;

                            AddKey(exact, Normalize(entry.Name), entry.Name);

                            foreach (string alias in entry.Aliases)
                            {
                                if (!string.IsNullOrWhiteSpace(alias))
                                    AddKey(exact, Normalize(alias), entry.Name);
                            }
                        }
                    }
                }
                else
                {
                    Log.Warning(
                        "LocationDictionary file not found at {Path} - route names will pass through unvalidated as null matches",
                        DictionaryPath);
                }
            }
            catch (Exception ex)
            {
                // Fail soft: an empty dictionary just means TryMatch always
                // returns null and the corner display keeps its previous
                // value - route OCR itself is unaffected.
                Log.Warning(ex, "LocationDictionary could not be loaded from {Path}", DictionaryPath);
                exact.Clear();
            }

            foreach (KeyValuePair<string, string> pair in exact)
            {
                string foldedKey = FoldConfusions(pair.Key);

                if (fold.TryGetValue(foldedKey, out string? existing))
                {
                    // Two DIFFERENT maps collapse to one folded key - neither
                    // may claim it (see the class doc's layer 2 rule).
                    if (existing != pair.Value)
                        collided.Add(foldedKey);
                }
                else
                {
                    fold[foldedKey] = pair.Value;
                }
            }

            foreach (string key in collided)
                fold.Remove(key);

            fuzzyCandidates = new List<KeyValuePair<string, string>>(exact);
            foldedLookup = fold;
            exactLookup = exact;

            Log.Debug(
                "LocationDictionary built: {Keys} keys ({Folded} folded, {Collisions} fold collisions dropped) in {Ms}ms",
                exact.Count,
                fold.Count,
                collided.Count,
                timer.ElapsedMilliseconds);
        }
    }

    private static void AddKey(Dictionary<string, string> lookup, string key, string canonical)
    {
        if (key.Length == 0)
            return;

        // First writer wins - the data file is validated to have no duplicate
        // canonicals, so a collision here is an alias overlapping a name;
        // preferring the earlier (name) entry keeps aliases from hijacking.
        lookup.TryAdd(key, canonical);
    }

    /// <summary>Casefold, fold the accented characters PRO map names actually
    /// use (é), drop everything that is not a letter or digit. "S.S. Anne",
    /// "Diglett's Cave" and "MT PYRE" all normalize to forms their OCR reads
    /// reach without any fuzzy step.</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (char raw in text)
        {
            char c = char.ToLowerInvariant(raw);

            if (c == 'é')
                c = 'e';

            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>The classic OCR confusions, collapsed identically on both the
    /// dictionary side and the read side: rn/m, vv/w, 0/o, 1/i, l/i, 5/s.
    /// Only ever applied to already-normalized text.</summary>
    private static string FoldConfusions(string normalized)
    {
        string sequenceFolded = normalized.Replace("rn", "m").Replace("vv", "w");

        var builder = new StringBuilder(sequenceFolded.Length);

        foreach (char c in sequenceFolded)
        {
            builder.Append(c switch
            {
                '0' => 'o',
                '1' => 'i',
                'l' => 'i',
                '5' => 's',
                _ => c
            });
        }

        return builder.ToString();
    }

    private static string DigitsOf(string text)
    {
        var builder = new StringBuilder(4);

        foreach (char c in text)
        {
            if (char.IsAsciiDigit(c))
                builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Plain two-row Levenshtein with an early cutoff once a whole
    /// row exceeds <paramref name="cap"/> - the usual small-string variant,
    /// nothing clever, sized for ~640 candidates of at most a few dozen
    /// characters.</summary>
    private static int BoundedLevenshtein(string a, string b, int cap)
    {
        if (Math.Abs(a.Length - b.Length) > cap)
            return cap + 1;

        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            int rowMinimum = i;

            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;

                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + cost);

                rowMinimum = Math.Min(rowMinimum, current[j]);
            }

            if (rowMinimum > cap)
                return cap + 1;

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
