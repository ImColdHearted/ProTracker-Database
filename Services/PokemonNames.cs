using System;

namespace Foot_Tracker.Services;

/// <summary>
/// §405. The two species whose names carried a gender sign - Nidoran♀ and
/// Nidoran♂ - are now "Nidoran F" and "Nidoran M": the sign drew in a
/// fallback font, could not be typed into a search, and the game itself
/// prints the letter ("VS. Wild Nidoran M"). The library spells them the
/// new way; this turns the old spelling, wherever a record kept it, into
/// the new one, so a hunt logged last month and one logged today count
/// as the same species.
///
/// Every store that reads species names back from disk passes them
/// through <see cref="Modern"/> once on load - the hunt log, the map
/// tallies, the Pokédex scans, the saved session, the spawn pages - and
/// the encounter log rewrites its rows once with an UPDATE. A name with
/// no sign comes back as it went in, so this costs nothing anywhere else.
/// </summary>
public static class PokemonNames
{
    private const char Female = '\u2640';

    private const char Male = '\u2642';

    /// <summary>The name as the library spells it now: a trailing ♀ or ♂
    /// (with or without a space before it) becomes " F" or " M". Anything
    /// else is returned as given.</summary>
    public static string Modern(string? name)
    {
        if (string.IsNullOrEmpty(name) || !IsLegacy(name))
            return name ?? string.Empty;

        string trimmed = name.TrimEnd();
        char sign = trimmed[^1];
        string stem = trimmed[..^1].TrimEnd();

        if (stem.Length == 0)
            return name;

        return stem + (sign == Female ? " F" : " M");
    }

    /// <summary>Whether the name ends in one of the two signs.</summary>
    public static bool IsLegacy(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        string trimmed = name.TrimEnd();

        return trimmed.Length > 0 && (trimmed[^1] == Female || trimmed[^1] == Male);
    }

    /// <summary>The old spelling of a modern name - "Nidoran F" back to
    /// "Nidoran♀" - for finding a file that was saved under it; null for a
    /// name that never had one.</summary>
    public static string? Legacy(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        if (name.EndsWith(" F", StringComparison.Ordinal) && name.StartsWith("Nidoran", StringComparison.OrdinalIgnoreCase))
            return name[..^2] + Female;

        if (name.EndsWith(" M", StringComparison.Ordinal) && name.StartsWith("Nidoran", StringComparison.OrdinalIgnoreCase))
            return name[..^2] + Male;

        return null;
    }
}
