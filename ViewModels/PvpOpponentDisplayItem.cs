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
}
