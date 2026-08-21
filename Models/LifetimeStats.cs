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

        public Dictionary<string, long> PokemonEncounters
        { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}