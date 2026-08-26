namespace Foot_Tracker.Models
{
    /// <summary>
    /// One individual PVP battle PvpTracker has automatically detected - see
    /// Tracking/PvpBattleDetector.cs and Tracking/PvpTracker.cs.
    ///
    /// Every battle gets its own entry, even against an opponent who already
    /// has other entries in the list - PvpOpponentService.RegisterBattle does
    /// NOT deduplicate by name, so rebattling the same person adds a new row
    /// rather than updating an existing one (this list is a battle log, not a
    /// per-opponent summary).
    ///
    /// TimesBattled is the LIFETIME battle count against this opponent as of
    /// this specific battle - pulled from LifetimeStats.PvpOpponentBattleCounts
    /// (which is never trimmed), NOT a count of how many entries for this name
    /// are still present in PvpOpponentService's capped list. That keeps this
    /// number meaningful even after this entry - or an older one for the same
    /// opponent - ages out of the list.
    /// </summary>
    public class PvpOpponentEntry
    {
        public string Name { get; set; } = string.Empty;

        public int TimesBattled { get; set; }

        public DateTime BattledAtUtc { get; set; }
    }
}
