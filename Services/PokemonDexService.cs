using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>§413. One of a Pokémon's abilities, and whether it is the
    /// hidden one (PRO's 5% ability, 25% with a Black Medallion).</summary>
    public sealed record DexAbility(string Name, bool Hidden);

    /// <summary>§413. What the dex knows of one Pokémon or form: its
    /// abilities in slot order (the hidden one last) and its base stats.</summary>
    public sealed class DexEntry
    {
        public required int Id { get; init; }

        public required string Name { get; init; }

        public required IReadOnlyList<DexAbility> Abilities { get; init; }

        public bool HasStats { get; init; }

        public int Hp { get; init; }

        public int Attack { get; init; }

        public int Defense { get; init; }

        public int SpAttack { get; init; }

        public int SpDefense { get; init; }

        public int Speed { get; init; }
    }

    /// <summary>
    /// §413. Abilities and base stats for every Pokémon and form the sprite
    /// library knows - SharedPokemonLibrary/Data/Pokemon/pokemon-dex.json.
    ///
    /// The calculator's pokedex (calc-pokedex.json, §89) covers the 804
    /// species and forms a competitive team is built from - no Bulbasaur, no
    /// Bidoof - and carries abilities for 61 of them, so the Maps window's
    /// card had nothing to say for most of what spawns. This file is built
    /// from PokeAPI's data (github.com/PokeAPI/pokeapi, data/v2/csv), joined
    /// on the library's own Pokémon ids, which are PokeAPI's: every entry of
    /// pokemon-species.json and pokemon-forms.json, with the ability set as
    /// of the generation the file names (PRO plays by generation 7 rules;
    /// an ability a later game changed is given as it was then).
    ///
    /// Read once, on first use, like the calculator's data; never throws -
    /// a missing file means every lookup answers null and the card falls
    /// back to the calculator's data.
    /// </summary>
    public static class PokemonDexService
    {
        private sealed class FileJson
        {
            [JsonPropertyName("generation")] public int Generation { get; set; }
            [JsonPropertyName("pokemon")] public List<EntryJson>? Pokemon { get; set; }
        }

        private sealed class EntryJson
        {
            [JsonPropertyName("id")] public int Id { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("aliases")] public List<string>? Aliases { get; set; }
            [JsonPropertyName("abilities")] public List<AbilityJson>? Abilities { get; set; }
            [JsonPropertyName("stats")] public StatsJson? Stats { get; set; }
        }

        private sealed class AbilityJson
        {
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("hidden")] public bool Hidden { get; set; }
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

        private static readonly Dictionary<string, DexEntry> byName = new(StringComparer.OrdinalIgnoreCase);

        private static bool loaded;

        /// <summary>The generation the ability sets are given as of; 0 when
        /// the file could not be read.</summary>
        public static int Generation { get; private set; }

        /// <summary>How many Pokémon and forms the file holds.</summary>
        public static int Count { get; private set; }

        /// <summary>A Pokémon by its library name, any of its aliases, or the
        /// old spelling of the two Nidoran (§405); null when the dex has no
        /// entry or the file is missing.</summary>
        public static DexEntry? Find(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            EnsureLoaded();

            string trimmed = name.Trim();

            if (byName.TryGetValue(trimmed, out DexEntry? entry))
                return entry;

            return byName.TryGetValue(PokemonNames.Modern(trimmed), out entry) ? entry : null;
        }

        private static void EnsureLoaded()
        {
            if (loaded)
                return;

            // Set only once the file was read, as the calculator's data does
            // (§316): a file that arrives later is still picked up.
            try
            {
                string path = Path.Combine(
                    AppContext.BaseDirectory,
                    "SharedPokemonLibrary",
                    "Data",
                    "Pokemon",
                    "pokemon-dex.json");

                if (!File.Exists(path))
                    return;

                FileJson? file = JsonSerializer.Deserialize<FileJson>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                    });

                if (file?.Pokemon is null)
                    return;

                foreach (EntryJson item in file.Pokemon)
                {
                    if (string.IsNullOrWhiteSpace(item.Name))
                        continue;

                    var entry = new DexEntry
                    {
                        Id = item.Id,
                        Name = item.Name.Trim(),
                        Abilities = (item.Abilities ?? new List<AbilityJson>())
                            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                            .Select(a => new DexAbility(a.Name!.Trim(), a.Hidden))
                            .ToList(),
                        HasStats = item.Stats is not null,
                        Hp = item.Stats?.Hp ?? 0,
                        Attack = item.Stats?.Attack ?? 0,
                        Defense = item.Stats?.Defense ?? 0,
                        SpAttack = item.Stats?.SpAttack ?? 0,
                        SpDefense = item.Stats?.SpDefense ?? 0,
                        Speed = item.Stats?.Speed ?? 0,
                    };

                    // The name wins over another entry's alias: names first,
                    // aliases only where nothing is filed yet.
                    byName[entry.Name] = entry;
                }

                foreach (EntryJson item in file.Pokemon)
                {
                    if (string.IsNullOrWhiteSpace(item.Name) || !byName.TryGetValue(item.Name.Trim(), out DexEntry? entry))
                        continue;

                    foreach (string alias in item.Aliases ?? new List<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(alias))
                            byName.TryAdd(alias.Trim(), entry);
                    }
                }

                Generation = file.Generation;
                Count = file.Pokemon.Count;
                loaded = true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Dex: pokemon-dex.json could not be read.");
                byName.Clear();
            }
        }
    }
}
