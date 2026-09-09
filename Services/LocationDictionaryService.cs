using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
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
/// Anything that fails all three returns null, and the caller's existing
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

        // Layer 3 - bounded fuzzy. Deliberately refused for short strings:
        // one or two edits are a large fraction of a short name, and short
        // names ("Moon", route numbers) are exactly where a weak match picks
        // the wrong map.
        if (normalized.Length < 6)
            return null;

        int cap = normalized.Length < 10 ? 1 : normalized.Length < 16 ? 2 : 3;
        string rawDigits = DigitsOf(normalized);

        string? best = null;
        int bestDistance = cap + 1;
        int secondDistance = cap + 1;

        foreach (KeyValuePair<string, string> candidate in fuzzyCandidates!)
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

        if (best is not null && bestDistance <= cap && bestDistance < secondDistance)
            return best;

        // One Debug line per distinct rejected reading - the breadcrumb for
        // extending the dictionary if a real map ever goes missing from it.
        if (rawText != lastLoggedMiss)
        {
            lastLoggedMiss = rawText;
            Log.Debug("LocationDictionary: no confident match for {RawText}", rawText);
        }

        return null;
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
