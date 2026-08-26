using System.Text.Json;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Looks up an ability's description by name for display in the boss
    /// detail panel - see BossDetailViewModel's BossTeamMemberItem.AbilityDisplayText.
    /// Reads SharedPokemonLibrary/Data/Abilities/abilities.json once, the first
    /// time a lookup is requested, and caches it - same lazy-load-on-first-use
    /// pattern as MoveLookupService.
    /// </summary>
    public static class AbilityLookupService
    {
        private static readonly Dictionary<string, AbilityData> abilitiesByName =
            new(StringComparer.OrdinalIgnoreCase);

        private static bool loaded;

        /// <summary>Returns null if abilities.json is missing, unreadable, or has
        /// no entry matching this name - callers should fall back to showing the
        /// plain ability name in that case rather than failing.</summary>
        public static AbilityData? Find(string? abilityName)
        {
            if (string.IsNullOrWhiteSpace(abilityName))
                return null;

            EnsureLoaded();

            return abilitiesByName.TryGetValue(abilityName.Trim(), out AbilityData? ability) ? ability : null;
        }

        private static void EnsureLoaded()
        {
            if (loaded)
                return;

            loaded = true;

            string path = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Data",
                "Abilities",
                "abilities.json"
            );

            if (!File.Exists(path))
                return;

            string json = File.ReadAllText(path);

            List<AbilityData>? data = JsonSerializer.Deserialize<List<AbilityData>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data == null)
                return;

            foreach (AbilityData ability in data)
            {
                if (string.IsNullOrWhiteSpace(ability.Name))
                    continue;

                // abilities.json has no duplicate names (164 unique, checked when
                // it was built) - a plain assignment is fine even if that changes.
                abilitiesByName[ability.Name] = ability;
            }
        }
    }
}
