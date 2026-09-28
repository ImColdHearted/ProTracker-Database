using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>Display-friendly wrapper around Models.PvpOpponentEntry for
/// PreviouslyBattledUsersWindow's list - same idea as TargetDisplayItem/
/// EncounterCountRow (keep formatting out of the Model, done once when the
/// ViewModel builds the list rather than via XAML converters). One row per
/// individual battle - the same Name can appear on more than one row, since
/// PvpOpponentEntry is a battle log rather than a per-opponent summary.</summary>
public sealed class PvpOpponentDisplayItem
{
    public string Name { get; init; } = string.Empty;

    // Lifetime battle count against this opponent as of this specific battle -
    // see PvpOpponentEntry.TimesBattled's remarks.
    public int TimesBattled { get; init; }

    public string BattledAt { get; init; } = string.Empty;

    /// <summary>§279. Grouped, and the "x" applied here rather than by the
    /// binding's StringFormat - one place builds the cell, which is this
    /// type's whole reason for existing.</summary>
    public string TimesBattledDisplay => $"{DisplayNumber.Count(TimesBattled)}x";

    /// <summary>§276. "Won", "Lost", or a dash when the result was never read -
    /// see PvpOpponentEntry.Outcome for why that third state is real and is
    /// not a quiet loss.</summary>
    public string Result { get; init; } = "-";

    /// <summary>§276. Which of the three the row draws - a green Won, a red
    /// Lost, or a plain dash. Three flags rather than a colour key and a
    /// converter, because this codebase negates and switches visibility in
    /// XAML and owns no brush converter to add one for.</summary>
    public bool IsWin { get; init; }

    public bool IsLoss { get; init; }

    public bool IsUnknownResult => !IsWin && !IsLoss;
}
