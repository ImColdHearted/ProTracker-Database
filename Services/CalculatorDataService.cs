using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Species data for the Calculators menu (Damage / IV): base stats, types,
    /// and learnsets for 802 species and forms, read from
    /// SharedPokemonLibrary/Data/Calculator/calc-pokedex.json. That file is a
    /// cleaned copy of the PokemonSim side project's pokedex.json (strict JSON
    /// - its source had line comments and trailing commas - plus 20 name/type
    /// typo fixes, see MIGRATION_GUIDE.md §89/§90), brought in because it's the
    /// one data set this app never had: the existing pokemon-species.json
    /// carries names/types/sprites but no base stats, and stats are what the
    /// stat and damage formulas run on.
    ///
    /// Same lazy-load-on-first-use shape as MoveLookupService (no startup
    /// wiring to remember), and deliberately never throws: a missing or
    /// damaged file just means empty lists, and each calculator window shows
    /// its own "data could not be loaded" status instead of the app failing.
    /// Move names in learnsets come from Pokémon Showdown's learnset data
    /// (§90 - the PokemonSim source carried a two-move placeholder for most
    /// species) and are spelled to match moves.json's names; callers still
    /// resolve them through MoveLookupService and skip anything unmatched.
    /// </summary>
    public static class CalculatorDataService
    {
        public sealed class CalcSpecies
        {
            public required string Name { get; init; }
            public required IReadOnlyList<string> Types { get; init; }
            public int BaseHp { get; init; }
            public int BaseAttack { get; init; }
            public int BaseDefense { get; init; }
            public int BaseSpAttack { get; init; }
            public int BaseSpDefense { get; init; }
            public int BaseSpeed { get; init; }
            public required IReadOnlyList<string> LearnsetMoveNames { get; init; }

            // §154: the fields §89 noted were "riding along unread" - the
            // Simulator's team builder reads them now.
            public IReadOnlyList<string> Abilities { get; init; } = new List<string>();
            public IReadOnlyList<string> PreferredNatures { get; init; } = new List<string>();
        }

        // JSON shapes - property names mirror calc-pokedex.json's fields.
        private sealed class SpeciesJson
        {
            [JsonPropertyName("types")] public List<string>? Types { get; set; }
            [JsonPropertyName("stats")] public StatsJson? Stats { get; set; }
            [JsonPropertyName("moves")] public List<string>? Moves { get; set; }
            [JsonPropertyName("abilities")] public List<string>? Abilities { get; set; }
            [JsonPropertyName("preferredNatures")] public List<string>? PreferredNatures { get; set; }
        }

        private sealed class StatsJson
        {
            [JsonPropertyName("hp")] public int Hp { get; set; }
            [JsonPropertyName("attack")] public int Attack { get; set; }
            [JsonPropertyName("defense")] public int Defense { get; set; }
            [JsonPropertyName("spAttack")] public int SpAttack { get; set; }
            [JsonPropertyName("spDefense")] public int SpDefense { get; set; }
            [JsonPropertyName("speed")] public int Speed { get; set; }
        }

        private static readonly Dictionary<string, CalcSpecies> speciesByName =
            new(StringComparer.OrdinalIgnoreCase);

        private static List<string> sortedNames = new();
        private static bool loaded;

        /// <summary>Every species name, alphabetical - empty if the data file
        /// couldn't be read (see the class remarks).</summary>
        public static IReadOnlyList<string> AllSpeciesNames
        {
            get
            {
                EnsureLoaded();
                return sortedNames;
            }
        }

        public static CalcSpecies? Find(string? speciesName)
        {
            if (string.IsNullOrWhiteSpace(speciesName))
                return null;

            EnsureLoaded();

            return speciesByName.TryGetValue(speciesName.Trim(), out CalcSpecies? species)
                ? species
                : null;
        }

        private static void EnsureLoaded()
        {
            if (loaded)
                return;

            loaded = true;

            try
            {
                string path = Path.Combine(
                    AppContext.BaseDirectory,
                    "SharedPokemonLibrary",
                    "Data",
                    "Calculator",
                    "calc-pokedex.json"
                );

                if (!File.Exists(path))
                    return;

                string json = File.ReadAllText(path);

                // The shipped file is strict JSON, but the lenient options cost
                // nothing and mean a future hand-edit with a stray comma or a
                // // note (both present in this file's original source) degrades
                // to "still loads" instead of "every calculator goes empty".
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };

                Dictionary<string, SpeciesJson>? data =
                    JsonSerializer.Deserialize<Dictionary<string, SpeciesJson>>(json, options);

                if (data == null)
                    return;

                foreach (KeyValuePair<string, SpeciesJson> entry in data)
                {
                    if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value?.Stats == null)
                        continue;

                    StatsJson stats = entry.Value.Stats;

                    speciesByName[entry.Key] = new CalcSpecies
                    {
                        Name = entry.Key,
                        Types = entry.Value.Types ?? new List<string>(),
                        BaseHp = stats.Hp,
                        BaseAttack = stats.Attack,
                        BaseDefense = stats.Defense,
                        BaseSpAttack = stats.SpAttack,
                        BaseSpDefense = stats.SpDefense,
                        BaseSpeed = stats.Speed,
                        LearnsetMoveNames = entry.Value.Moves ?? new List<string>(),
                        Abilities = entry.Value.Abilities ?? new List<string>(),
                        PreferredNatures = entry.Value.PreferredNatures ?? new List<string>()
                    };
                }

                sortedNames = speciesByName.Keys
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                // See the class remarks - the calculators show their own
                // status message when this comes back empty.
                speciesByName.Clear();
                sortedNames = new List<string>();
            }
        }
    }
}
