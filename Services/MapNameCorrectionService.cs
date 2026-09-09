using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace Foot_Tracker.Services;

/// <summary>
/// Corrects known OCR misreads in RouteDetector's route/location name output -
/// a small, user-maintainable JSON dictionary at
/// DataFiles/MapNameCorrections.json rather than anything compiled into this
/// app, specifically so a fresh misread can be fixed by editing that file
/// directly, without needing a code change. Started after a real report plus
/// screenshot: RouteDetector consistently read "Vulcan Cove"/"Vulcan Island
/// Shore"/"Vulcan Forest" as "Yulcan Cove"/"Yulcan Island Shore"/"Yulcan
/// Forest" - a single "Yulcan" -&gt; "Vulcan" entry fixes all three at once,
/// since corrections match as a substring rather than a whole-name match.
///
/// Each entry is applied as a case-insensitive substring replacement, in the
/// order the file lists them, to whatever candidate RouteDetector's
/// EnumerateRouteNameCandidates (§101; formerly ExtractRouteName) already
/// picked out - this runs after that method's own "does this look
/// like a place name" filtering, not instead of it. A key starting with "_"
/// is skipped rather than treated as a correction - the seed file uses
/// "_comment" for exactly this reason, since plain JSON has no comment syntax
/// of its own.
///
/// Loaded once and cached for the process lifetime, same as every other
/// DataFiles-backed catalog in this app (BossCooldownService's boss list,
/// PokemonSpriteService's type/name data, and so on) - a correction added
/// while the app is already running needs a restart to take effect, the same
/// as editing any of those.
/// </summary>
public static class MapNameCorrectionService
{
    private static readonly string CorrectionsPath =
        Path.Combine(AppContext.BaseDirectory, "DataFiles", "MapNameCorrections.json");

    private static Dictionary<string, string>? corrections;

    private static Dictionary<string, string> GetCorrections()
    {
        if (corrections != null)
            return corrections;

        corrections = new Dictionary<string, string>();

        if (!File.Exists(CorrectionsPath))
            return corrections;

        try
        {
            string json = File.ReadAllText(CorrectionsPath);

            Dictionary<string, string>? loaded =
                JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            if (loaded != null)
            {
                foreach (KeyValuePair<string, string> pair in loaded)
                {
                    if (!pair.Key.StartsWith('_'))
                    {
                        corrections[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Same "keep going with an empty/partial set rather than break
            // route detection over a malformed file" reasoning every other
            // JSON-backed lookup in this app uses (see e.g.
            // HuntLogService.LoadFromDisk).
            Log.Warning(
                ex,
                "MapNameCorrectionService could not read {Path} - route names will be shown uncorrected",
                CorrectionsPath);
        }

        return corrections;
    }

    /// <summary>
    /// Applies every known correction to <paramref name="routeName"/> in turn,
    /// case-insensitively, as a substring replace. Returns the input unchanged
    /// if no correction's key appears in it - the overwhelmingly common case,
    /// since most route names already read correctly.
    /// </summary>
    public static string Apply(string routeName)
    {
        if (string.IsNullOrEmpty(routeName))
            return routeName;

        string result = routeName;

        foreach (KeyValuePair<string, string> pair in GetCorrections())
        {
            // Regex.Escape so a correction key with regex-special characters
            // (unlikely for a place name, but not guaranteed) is matched
            // literally; the replacement's own "$" is escaped to "$$" so a
            // corrected name containing a literal "$" can't be misread as a
            // regex backreference by Regex.Replace.
            result = Regex.Replace(
                result,
                Regex.Escape(pair.Key),
                pair.Value.Replace("$", "$$"),
                RegexOptions.IgnoreCase);
        }

        return result;
    }
}
