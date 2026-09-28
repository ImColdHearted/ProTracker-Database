using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Imaging;
using Foot_Tracker.Models;
using Serilog;

namespace Foot_Tracker.Services;

/// <summary>
/// §271. Which sprite a hunting target is DRAWN as.
///
/// The "Currently Hunting" cards have always shown the species sprite and
/// nothing else, which is the one place in the app where the hunt's own
/// subject is not allowed to look like what is actually being hunted: a
/// player chasing the Halloween Charizard sees the ordinary one on the card
/// while the counterpart sprite sits in the catalog, already scraped, already
/// used by the encounter card next to it (§139) and by the matcher (§140).
/// This service is the small piece that was missing - a per-species override,
/// picked by the player, applied wherever the target is drawn.
///
/// WHAT IT DOES NOT DO. It changes no name, no count and no detection. The
/// target is still the species: the matcher, the stats and Since Form are
/// untouched, and a Halloween Charizard skin does not make an ordinary
/// Charizard encounter count for less or for more. It is the picture only,
/// which is why it lives in <see cref="UiPreferences"/> beside the other
/// display choices rather than in the hunt session.
///
/// KEYED BY SPECIES, NOT BY SLOT. Targets get reordered, swapped out and set
/// again; a slot index would hand the skin to whoever stood in that position
/// next. Normalised the way <see cref="CounterpartSpriteService"/> normalises
/// its own names (§138), so "Farfetch'd" and "Farfetchd" are one key rather
/// than two.
///
/// A SKIN THAT WILL NOT DRAW IS NOT AN ERROR. The catalog can be re-scraped
/// and an image can go; <see cref="SpriteFor"/> falls back to the species
/// sprite and logs it, because a target card that draws nothing is worse than
/// a target card that has quietly gone back to normal.
/// </summary>
public static class TargetSpriteService
{
    /// <summary>§359. The stored value that means "draw this one shiny".
    ///
    /// Every other skin is a counterpart's image path out of the catalog, and
    /// a shiny is not in that catalog - it is the same species from the shiny
    /// sprite folder (§139). Rather than invent a second setting beside this
    /// one, the shiny is a RESERVED value in the same map: one place decides
    /// what a target is drawn as, and the picker, the save file and the swap
    /// window all keep working the way they already did.
    ///
    /// Not a path, and it cannot be one - the colons make it something no
    /// catalog entry can collide with. An older build reading a preferences
    /// file that contains it asks the counterpart catalog for ":shiny:",
    /// gets nothing, and falls back to the ordinary sprite with a Debug line,
    /// which is exactly what SpriteFor already does for a skin that will not
    /// load.</summary>
    public const string ShinySkin = ":shiny:";

    private static readonly Dictionary<string, string> skins =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set once by MainWindowViewModel so a change can be written
    /// back through the same preferences object every other display choice is
    /// saved from - this service owns the rule, not the file.</summary>
    private static Action? save;

    /// <summary>Takes the loaded preferences at startup. Safe to call again;
    /// the map is replaced, not merged, so a reloaded preferences file cannot
    /// leave a stale skin behind.</summary>
    public static void Load(UiPreferences preferences, Action persist)
    {
        skins.Clear();

        foreach (KeyValuePair<string, string> pair in preferences.TargetSpriteSkins)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                continue;

            skins[Key(pair.Key)] = pair.Value;
        }

        save = () =>
        {
            preferences.TargetSpriteSkins = skins.ToDictionary(
                p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

            persist();
        };
    }

    /// <summary>§361. The catalog path THIS SLOT is drawn with, or null when
    /// it is drawn normally.
    ///
    /// <paramref name="occurrence"/> is how many slots of the same species
    /// come before this one - 0 for the first Mareanie, 1 for the second, 2
    /// for the third. Hunting one species three times to watch for its form
    /// and its shiny at once is the whole reason this argument exists.
    ///
    /// §271 refused to key this by slot, and was right: "a slot index would
    /// hand the skin to whoever stood in that position next." An OCCURRENCE
    /// is not a slot index. It counts only same-species neighbours, so
    /// dropping or reordering an unrelated target cannot move it, and the
    /// only thing it can be confused with is another slot of the same
    /// species - which differs from it in nothing but the picture.</summary>
    public static string? SkinFor(string? species, int occurrence = 0) =>
        !string.IsNullOrWhiteSpace(species) && skins.TryGetValue(Key(species, occurrence), out string? path)
            ? path
            : null;

    /// <summary>Draws this species as the player asked: the counterpart image
    /// on a card-sized canvas when one is set and readable, the ordinary
    /// encounter sprite otherwise. The same fallback shape as §139's encounter
    /// card, for the same reason - the two sit side by side.</summary>
    public static Bitmap? SpriteFor(string? species, int occurrence = 0)
    {
        if (string.IsNullOrWhiteSpace(species))
            return null;

        if (SkinFor(species, occurrence) is { } path)
        {
            // §359: checked before the catalog, so the reserved value never
            // reaches a lookup that would only ever miss on it.
            if (string.Equals(path, ShinySkin, StringComparison.OrdinalIgnoreCase))
            {
                return PokemonSpriteService.GetShinyEncounterSprite(species)
                    ?? PokemonSpriteService.GetEncounterSprite(species);
            }

            Bitmap? counterpart = CounterpartSpriteService.GetCardSprite(path);

            if (counterpart is not null)
                return counterpart;

            Log.Debug(
                "Target sprite: {Species} is set to {Path}, which will not load - drawing the ordinary sprite.",
                species, path);
        }

        return PokemonSpriteService.GetEncounterSprite(species);
    }

    /// <summary>Points this species at a counterpart image, or - with a null
    /// or blank path - puts it back to its ordinary sprite. Saves either way.
    /// Returns true when something actually changed, so the caller can stay
    /// quiet about a pick that was already in force.</summary>
    public static bool Set(string species, string? imagePath, int occurrence = 0)
    {
        if (string.IsNullOrWhiteSpace(species))
            return false;

        string key = Key(species, occurrence);
        string? existing = skins.TryGetValue(key, out string? current) ? current : null;

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            if (existing is null)
                return false;

            skins.Remove(key);
        }
        else
        {
            if (string.Equals(existing, imagePath, StringComparison.OrdinalIgnoreCase))
                return false;

            skins[key] = imagePath;
        }

        save?.Invoke();

        return true;
    }

    // §138's normalisation, the counterpart catalog's own: the same species
    // is spelt "Farfetch'd" in one event and "Farfetchd" in another, and the
    // tracker's OCR name is a third spelling again. One key for all of them.
    //
    // §361: the FIRST slot of a species keeps the bare key it has always had.
    // That is deliberate and it is the whole migration: every skin already in
    // a preferences file is an occurrence-0 skin, so it keeps working without
    // the file being touched, and a build that never heard of occurrences
    // reads the same key back.
    private static string Key(string species, int occurrence = 0)
    {
        string name = species
            .Trim()
            .Replace("-", " ")
            .Replace("_", " ")
            .Replace("'", string.Empty)
            .Replace(".", string.Empty)
            .ToLowerInvariant();

        return occurrence <= 0 ? name : $"{name}#{occurrence}";
    }
}
