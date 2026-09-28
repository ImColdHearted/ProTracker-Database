using PokemonSim.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PokemonSim.Data
{
    /// <summary>
    /// DataFiles/pokedex.json -> species. Section 154: calls MoveDex by its
    /// real name (the old code called a "Moves" class that did not exist),
    /// reads the abilities arrays the file always carried, skips a
    /// learnset move the move file does not know WITH a warning per species
    /// (268 distinct names are referenced but not implemented - a crash per
    /// unknown name made the file unloadable), and never Console.WriteLines
    /// from a library.
    /// </summary>
    public static class PokemonDex
    {
        static readonly Dictionary<string, PokemonSpecies> dex = new(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> warnings = new();
        static bool loaded;

        public static IReadOnlyList<string> Warnings => warnings;

        public static int Count => dex.Count;

        public static void EnsureLoaded() => Load(SimDataFiles.PokedexPath);

        public static void Load(string path)
        {
            if (loaded)
                return;

            // §316: the flag is set at the END, on success. It used to be set
            // here, before the read - so a load that threw (a data file that
            // was never unpacked onto a phone, which is exactly what §316
            // found) left the dex EMPTY and marked loaded, and every later
            // EnsureLoaded returned in silence. A caller then saw a species
            // with no abilities rather than an error, which is the hardest
            // kind of failure to chase. A throw now leaves it unloaded, so the
            // next call tries again and the exception reaches somebody.
            MoveDex.EnsureLoaded();

            string json = File.ReadAllText(path);

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            var raw = JsonSerializer.Deserialize<Dictionary<string, PokemonSpeciesJson>>(json, options);

            if (raw == null)
                throw new InvalidDataException("pokedex.json did not deserialize to a species dictionary.");

            foreach (var entry in raw)
            {
                var name = entry.Key;
                var data = entry.Value;

                if (data == null)
                {
                    warnings.Add($"{name}: null entry skipped.");
                    continue;
                }

                try
                {
                    var learnset = new List<MoveState>();
                    var missing = new List<string>();

                    foreach (string moveName in data.moves ?? new List<string>())
                    {
                        if (MoveDex.TryGet(moveName, out MoveState move))
                            learnset.Add(move);
                        else
                            missing.Add(moveName);
                    }

                    if (missing.Count > 0)
                        warnings.Add($"{name}: {missing.Count} learnset move(s) not in moves.json ({string.Join(", ", missing.Take(4))}{(missing.Count > 4 ? ", ..." : "")}).");

                    var species = new PokemonSpecies
                    {
                        Name = name,

                        Types = (data.types ?? new List<string>())
                            .Select(t => Enum.Parse<PokemonType>(t, ignoreCase: true))
                            .ToList(),

                        BaseStats = new Stats
                        {
                            HP = data.stats.hp,
                            Attack = data.stats.attack,
                            Defense = data.stats.defense,
                            SpAttack = data.stats.spAttack,
                            SpDefense = data.stats.spDefense,
                            Speed = data.stats.speed
                        },

                        Abilities = data.abilities ?? new List<string>(),

                        WeightKg = data.weight,

                        PreferredNatures = (data.preferredNatures ?? new List<string>())
                            .Select(n => Enum.Parse<Nature>(n, ignoreCase: true))
                            .ToList(),

                        Learnset = learnset
                    };

                    dex[name] = species;
                }
                catch (Exception ex)
                {
                    warnings.Add($"{name}: skipped - {ex.Message}");
                }
            }

            loaded = true;
        }

        public static bool TryGet(string name, out PokemonSpecies species)
        {
            EnsureLoaded();
            return dex.TryGetValue(name, out species!);
        }

        public static PokemonSpecies Get(string name)
        {
            if (TryGet(name, out PokemonSpecies species))
                return species;

            throw new KeyNotFoundException($"pokedex.json has no species named \"{name}\".");
        }

        /// <summary>§303. Kilograms for a species, or 0 when the dex has
        /// none - which every weight-based move reads as "no scaling"
        /// rather than as a featherweight. Takes the species NAME because
        /// that is all a PokemonState carries.</summary>
        /// <summary>§307. The abilities this species can have, in the dex's
        /// own order (the first is the usual one, the last is the hidden one
        /// where there is one). Empty for a species the dex does not know.
        ///
        /// Every one of the 804 species carries these now: §307 filled in the
        /// 743 that had none, joined to Showdown's dex by the same species-key
        /// mapping §303 used for weight.</summary>
        public static IReadOnlyList<string> AbilitiesOf(string? species)
        {
            if (string.IsNullOrWhiteSpace(species))
                return Array.Empty<string>();

            return TryGet(species, out PokemonSpecies found)
                ? found.Abilities
                : Array.Empty<string>();
        }

        /// <summary>§307. Whether the engine has a rule for this ability, as
        /// opposed to merely knowing the name. The dex lists 308 across every
        /// species and AbilityFactory implements 124 of them, so a calculator
        /// offering one of the other 184 has to say that it changes
        /// nothing rather than silently changing nothing.</summary>
        public static bool AbilityIsSimulated(string? ability)
        {
            if (string.IsNullOrWhiteSpace(ability))
                return false;

            string id = Engine.Abilities.AbilityFactory.Normalize(ability);

            foreach (string supported in Engine.Abilities.AbilityFactory.SupportedIds)
            {
                if (supported == id)
                    return true;
            }

            return false;
        }

        public static double WeightOf(string? species)
        {
            if (string.IsNullOrWhiteSpace(species))
                return 0;

            return TryGet(species, out PokemonSpecies found) ? found.WeightKg : 0;
        }

        public static List<PokemonSpecies> All()
        {
            EnsureLoaded();
            return dex.Values.ToList();
        }
    }
}