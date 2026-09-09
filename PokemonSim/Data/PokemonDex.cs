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

            loaded = true;

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

        public static List<PokemonSpecies> All()
        {
            EnsureLoaded();
            return dex.Values.ToList();
        }
    }
}