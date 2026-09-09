using Avalonia.Media.Imaging;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>Display-friendly wrapper around Models.HuntLogEntry for
/// HuntLogWindow/HuntLogSpeciesDetailWindow's tables - same idea as
/// PvpOpponentDisplayItem/EncounterCountRow (keep formatting out of the Model,
/// done once when the ViewModel builds the list rather than via XAML
/// converters). One row per individual encounter - the same species can appear
/// on more than one row, since HuntLogEntry is an encounter log rather than a
/// per-species summary (that's what the existing Session Encounters table
/// already provides).</summary>
public sealed class HuntLogDisplayItem
{
    public string PokemonName { get; init; } = string.Empty;

    public string LevelText { get; init; } = "Lv. ?";

    /// <summary>"♂", "♀", or "" - see Models.HuntLogEntry.Gender/
    /// GenderDetector.cs for what an empty result covers (genderless species
    /// and unrecognized/unreadable are indistinguishable here).</summary>
    public string GenderSymbol { get; init; } = string.Empty;

    /// <summary>"Shiny", "Form", or "" - see Models.HuntLogEntry.RareType.
    /// Backs the Shiny/Form column in HuntLogWindow - left blank rather than
    /// showing a placeholder like "-" or "None" for the common case of a
    /// perfectly ordinary catch. Added by MIGRATION_GUIDE.md §77.</summary>
    public string RareTypeText { get; init; } = string.Empty;

    public string Map { get; init; } = "Unknown";

    public string EncounteredAt { get; init; } = string.Empty;

    public Bitmap? Sprite { get; init; }

    /// <summary>Backs a small type-icon row next to this row's Pokemon name -
    /// same TypeIconConverter-driven pattern as every other sprite+name row in
    /// this app (see EncounterCountRow.Types).</summary>
    public IReadOnlyList<string> Types => PokemonSpriteService.GetTypes(PokemonName);
}
