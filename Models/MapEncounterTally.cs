using System;
using System.Collections.Generic;

namespace Foot_Tracker.Models
{
    /// <summary>
    /// §278. One map's own encounter table: which species turned up there, how
    /// many were caught, how many ran, and when each was last seen.
    ///
    /// The same four facts <see cref="HuntSession"/> keeps for the hunt as a
    /// whole, kept again per map - not moved. That is the shape of the whole
    /// section: the STATS stay whole (time hunting, Since Shiny, catch rate,
    /// the totals) because they are about the hunt and not about the ground
    /// you happen to be standing on, and only the ENCOUNTER TABLE partitions.
    /// Nothing on HuntSession changed, so every stat, the export and the
    /// import all behave exactly as they did.
    ///
    /// Written as its own file per map (see MapEncounterService) rather than
    /// as one growing document: a save costs the current map's couple of
    /// kilobytes however many maps have ever been hunted.
    /// </summary>
    public class MapEncounterTally
    {
        /// <summary>The map name exactly as RouteDetector confirmed it. The
        /// FILE is named from a sanitised form of this, but this is what the
        /// UI shows and what identity is decided on, so a sanitiser that
        /// mangles a name can never rename the map itself.</summary>
        public string MapName { get; set; } = string.Empty;

        public Dictionary<string, int> EncounterCounts { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> CaughtCounts { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> RanFromCounts { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, DateTime> LastEncounteredUtc { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>When this map was first hunted and last hunted on this
        /// profile - the two facts a list of maps wants to sort and label by.</summary>
        public DateTime? FirstSeenUtc { get; set; }

        public DateTime? LastSeenUtc { get; set; }

        public int TotalEncounters
        {
            get
            {
                int total = 0;

                foreach (int count in EncounterCounts.Values)
                    total += count;

                return total;
            }
        }

        /// <summary>The same body HuntSession.RegisterPokemonEncounter has,
        /// against this map's dictionaries. Deliberately a second copy rather
        /// than a shared helper on HuntSession: this type is persisted on its
        /// own and must be free to diverge, and three lines of counting is a
        /// cheaper duplicate than a coupling between a per-map file and the
        /// session save format.</summary>
        public void RegisterPokemonEncounter(string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return;

            EncounterCounts[pokemonName] =
                EncounterCounts.TryGetValue(pokemonName, out int existing) ? existing + 1 : 1;

            LastEncounteredUtc[pokemonName] = DateTime.UtcNow;
        }

        public void RegisterCatch(string pokemonName) => Increment(CaughtCounts, pokemonName);

        public void RegisterRunAway(string pokemonName) => Increment(RanFromCounts, pokemonName);

        private static void Increment(Dictionary<string, int> counts, string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return;

            counts[pokemonName] =
                counts.TryGetValue(pokemonName, out int existing) ? existing + 1 : 1;
        }
    }
}
