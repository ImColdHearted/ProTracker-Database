using System.Collections.Generic;

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

        /// <summary>
        /// §276. How this battle ended, as PRO's own result line reported it:
        /// "Won", "Lost", or empty.
        ///
        /// Empty is a real third state, not a missing "Lost". The tracker
        /// clears a battle either because it read the result text OR because
        /// the battle window stayed unreadable long enough to give up
        /// (PvpTracker.MissedScansBeforeForceReset - a disconnect, or the
        /// client closed mid-match). Writing "Lost" for the second case would
        /// invent a fact; the window shows a dash and the record counts it
        /// neither way.
        ///
        /// The outcome was already being READ before this section - it has fed
        /// the lifetime PVP tallies since §91 - it just was not kept against
        /// the battle it belonged to.
        /// </summary>
        public string Outcome { get; set; } = string.Empty;

        /// <summary>
        /// §276. The distinct lines PRO printed in the battle's message box
        /// while this battle was tracked, in the order they appeared.
        ///
        /// Stored as PRO WROTE THEM, unparsed. What the opponent's Pokemon
        /// were, which moves they used and which items showed all live in
        /// these lines, but pulling them out needs PRO's exact wording - which
        /// side a line is about most of all - and guessing at wording is what
        /// §239's predecessor kept breaking on. So this section captures, and
        /// a later one parses: the lines are on disk from now on, so the
        /// parsing when it lands applies to battles already recorded rather
        /// than only to future ones.
        ///
        /// Capped per battle (PvpOpponentService.MaxLogLinesPerBattle) - a
        /// long match prints a lot, and 250 battles of it would be a large
        /// file for a rolling log.
        /// </summary>
        public List<string> BattleLog { get; set; } = new();

        /// <summary>§276. True when the outcome was actually read, rather
        /// than the battle simply having gone away.</summary>
        public bool HasOutcome => Outcome.Length > 0;

        /// <summary>§276. Won only when the result line said so.</summary>
        public bool Won => Outcome == "Won";
    }
}
