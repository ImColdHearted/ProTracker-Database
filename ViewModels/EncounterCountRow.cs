using Avalonia.Media.Imaging;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

// Replaces WinForms' ResetEncounterTable/UpdateSessionEncounters, which built
// TableLayoutPanel rows by hand at runtime. In Avalonia this is just a bound
// collection rendered by a DataGrid/ItemsControl - see MainWindow.axaml.
public sealed class EncounterCountRow
{
    public required string PokemonName { get; init; }
    public required int Count { get; init; }
    public required double RatePercent { get; init; }
    public Bitmap? Sprite { get; init; }

    /// <summary>§125. How many of this species were caught and run from
    /// this hunt - both tracked per species by §123. They deliberately do
    /// not sum to Count: a battle can also end in a knockout, or in one
    /// breaking free and the player walking away, or still be running. See
    /// HuntSession's note.</summary>
    public int CaughtCount { get; init; }

    public int RanFromCount { get; init; }

    /// <summary>When this species was last seen, or null if the hunt predates
    /// §123 recording it - an old restored session has counts but no
    /// timestamps, and that reads as a blank cell rather than a wrong one.</summary>
    public DateTime? LastEncounteredUtc { get; init; }

    /// <summary>Relative, because "how long since I saw one" is the question
    /// this column exists to answer and a wall clock time makes the reader do
    /// the subtraction. Rounded DOWN throughout: "2 hours ago" for anything
    /// from two hours to just under three is how people say it, and rounding
    /// up would show "1 hour ago" nine seconds after the fact.</summary>
    public string LastEncounteredDisplay
    {
        get
        {
            if (LastEncounteredUtc is not DateTime seen)
                return string.Empty;

            TimeSpan ago = DateTime.UtcNow - seen;

            // A clock change or a save restored from a machine with a
            // different time can hand us a negative span. "Just now" is the
            // honest reading; a negative duration is not.
            if (ago < TimeSpan.Zero)
                return "Just now";

            if (ago.TotalSeconds < 10)
                return "Just now";

            if (ago.TotalMinutes < 1)
                return Plural((int)ago.TotalSeconds, "Second");

            if (ago.TotalHours < 1)
                return Plural((int)ago.TotalMinutes, "Minute");

            if (ago.TotalDays < 1)
                return Plural((int)ago.TotalHours, "Hour");

            return Plural((int)ago.TotalDays, "Day");
        }
    }

    private static string Plural(int value, string unit) =>
        value == 1 ? $"1 {unit} Ago" : $"{value} {unit}s Ago";

    /// <summary>Backs a small type-icon row next to this row's Pokemon name.</summary>
    public IReadOnlyList<string> Types => PokemonSpriteService.GetTypes(PokemonName);
}