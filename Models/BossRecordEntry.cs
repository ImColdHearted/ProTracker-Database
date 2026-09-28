using System;

namespace Foot_Tracker.Models
{
    /// <summary>
    /// §277. One boss's win/loss record on this client profile - see
    /// Services/BossRecordService.cs.
    ///
    /// Keyed by the boss's FILE NAME, the same id BossRepository.Load opens and
    /// BossCooldownService.RegisterBossDefeat writes a cooldown for (§274),
    /// rather than the "bossId" field inside the file, which does not always
    /// agree with it.
    ///
    /// NOT split by difficulty. PRO's battle screen shows the trainer's name
    /// and nothing about which difficulty was picked, so the tracker has no way
    /// to tell an Easy clear from a Hard one - a per-difficulty record would be
    /// three columns of guesses. One record per boss, and the window says so.
    ///
    /// Unknown is a real third count, not padding: the manual "start the
    /// cooldown" click on a Boss Database card (§274) records a fight whose
    /// result nobody saw, exactly as it has always done for the lifetime
    /// tally. Counting it as a loss would invent a fact; leaving it out
    /// entirely would make the attempts not add up.
    /// </summary>
    public class BossRecordEntry
    {
        public string BossId { get; set; } = string.Empty;

        public int Wins { get; set; }

        public int Losses { get; set; }

        public int Unknown { get; set; }

        /// <summary>When this boss was last fought, as far as this record
        /// knows. Null only for a record written before this field existed.</summary>
        public DateTime? LastAtUtc { get; set; }

        /// <summary>Every attempt counted, however it went.</summary>
        public int Attempts => Wins + Losses + Unknown;
    }
}
