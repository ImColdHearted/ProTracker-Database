using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Foot_Tracker.Services;

/// <summary>§374. One colour per Pokémon type, read off the reference chart
/// the request came with - the eighteen chips every competitive player
/// already knows, so a move name drawn in one of these says its type
/// without a word. The first use is a team card's Hidden Power, whose
/// bracketed type ("Hidden Power (Fighting)") is too long for the move
/// column at every width the card can have; the name loses the bracket and
/// wears the colour instead (see SimulatorSlotViewModel.Apply).
///
/// These are text colours on the Statistics panel, whatever colour the
/// player gave it, so they are mid-tones on purpose: none of them is the
/// panel's own text colour and none disappears entirely on a light or a
/// dark fill. They are not themed - a type's colour is the type's colour.</summary>
public static class PokemonTypeColors
{
    private static readonly IReadOnlyDictionary<string, Color> byType =
        new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            ["Normal"] = Color.Parse("#AAAA99"),
            ["Fire"] = Color.Parse("#FF4422"),
            ["Water"] = Color.Parse("#3399FF"),
            ["Electric"] = Color.Parse("#FFCC33"),
            ["Grass"] = Color.Parse("#77CC55"),
            ["Ice"] = Color.Parse("#66CCFF"),
            ["Fighting"] = Color.Parse("#BB5544"),
            ["Poison"] = Color.Parse("#AA5599"),
            ["Ground"] = Color.Parse("#DDBB55"),
            ["Flying"] = Color.Parse("#8899FF"),
            ["Psychic"] = Color.Parse("#FF5599"),
            ["Bug"] = Color.Parse("#AABB22"),
            ["Rock"] = Color.Parse("#BBAA66"),
            ["Ghost"] = Color.Parse("#6666BB"),
            ["Dragon"] = Color.Parse("#7766EE"),
            ["Dark"] = Color.Parse("#775544"),
            ["Steel"] = Color.Parse("#AAAABB"),
            ["Fairy"] = Color.Parse("#EE99EE"),
        };

    /// <summary>The colour that stands in for a type nothing here knows -
    /// a misread or a blank. Neutral, so a wrong name is not a wrong type.</summary>
    public static readonly Color Unknown = Colors.Gray;

    public static bool TryGet(string? typeName, out Color color)
    {
        if (!string.IsNullOrWhiteSpace(typeName) &&
            byType.TryGetValue(typeName.Trim(), out color))
        {
            return true;
        }

        color = Unknown;
        return false;
    }

    /// <summary>The type's colour, or Unknown for a name that is not a type.</summary>
    public static Color For(string? typeName) =>
        TryGet(typeName, out Color color) ? color : Unknown;
}
