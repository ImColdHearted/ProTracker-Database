using Avalonia.Media.Imaging;

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
}