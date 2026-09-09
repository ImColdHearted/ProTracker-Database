namespace Foot_Tracker.Models
{
    public sealed class LifetimeStats
    {
        public long TotalEncounters { get; set; }

        public long SuccessfulCatches { get; set; }

        public long FailedCatches { get; set; }

        public long ShinyEncounters { get; set; }

        public long FormEncounters { get; set; }

        public TimeSpan TotalHuntingTime { get; set; }

        // Boss battles and PVP results detected by the trackers - see
        // Tracking/BossCooldownTracker + Tracking/PvpTracker and
        // MIGRATION_GUIDE.md §91. BossBattles can exceed BossVictories +
        // BossLosses on purpose: a manual boss cooldown card click counts a
        // fight whose outcome is unknown, and each NPC of a multi-NPC boss
        // is its own battle.
        public long BossBattles { get; set; }

        public long BossVictories { get; set; }

        public long BossLosses { get; set; }

        public long PvpWins { get; set; }

        public long PvpLosses { get; set; }

        public Dictionary<string, long> PokemonEncounters
        { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        // Lifetime PVP battle count per opponent name - never trimmed, unlike
        // PvpOpponentService's rolling battle log (capped at its
        // MaxSavedBattles most recent individual battles). This is what will
        // power a future "who have you battled most" PVP stats view, the same
        // way PokemonEncounters above already powers the wild-encounter
        // equivalent.
        public Dictionary<string, long> PvpOpponentBattleCounts
        { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}