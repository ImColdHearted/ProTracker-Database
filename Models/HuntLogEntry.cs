namespace Foot_Tracker.Models
{
    /// <summary>
    /// One individual caught Pokemon - species, level (if OCR could read it -
    /// see Tracking/LevelDetector.cs), the route/location PRO's corner HUD
    /// showed at the moment of the catch (see Tracking/RouteDetector.cs), and
    /// when it happened. Recorded by MainWindowViewModel.OnCatchResultDetected
    /// every time EncounterTracker fires CatchResultDetected with
    /// CatchResult.Success - a persistent LOG of individual catches, distinct
    /// from HuntSession.EncounterCounts (a running per-species tally with no
    /// per-event detail, used by the existing Session Encounters table).
    /// Mirrors Models/PvpOpponentEntry.cs's shape and role for the "Previously
    /// Battled Users" PVP log. Logged only on a successful catch, not on every
    /// encounter, as of MIGRATION_GUIDE.md §76 - see that section for why.
    /// </summary>
    public class HuntLogEntry
    {
        public string PokemonName { get; set; } = string.Empty;

        /// <summary>Null when the level couldn't be read for this encounter -
        /// see LevelDetector's calibration caveats. Shown as "Lv. ?" rather than
        /// blank wherever this is displayed.</summary>
        public int? Level { get; set; }

        /// <summary>"Male", "Female", or null - null covers both a genderless
        /// species and an unrecognized/unreadable icon the same way, since
        /// GenderDetector.cs can't reliably tell those two apart from pixel
        /// color alone (see its own doc comment).</summary>
        public string? Gender { get; set; }

        /// <summary>"Shiny", "Form", or null - whichever RareEncounterType
        /// (see Tracking/RareEncounterDetector.cs) this catch's encounter was
        /// confirmed as before it got caught, if any. Set from
        /// MainWindowViewModel.currentEncounterRareType at catch time - see
        /// that field's declaration comment. Added by MIGRATION_GUIDE.md
        /// §77.</summary>
        public string? RareType { get; set; }

        /// <summary>The route/location text RouteDetector's corner OCR last read
        /// at the moment of this encounter - "Unknown" if tracking had just
        /// started and no corner reading had come in yet.</summary>
        public string Map { get; set; } = "Unknown";

        public DateTime EncounteredAtUtc { get; set; }
    }
}
