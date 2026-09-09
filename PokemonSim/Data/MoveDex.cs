using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PokemonSim.Models;

namespace PokemonSim.Data
{
    /// <summary>
    /// DataFiles/moves.json -> MoveState templates. Section 154: the
    /// accidental nested namespace is gone (the class was really
    /// PokemonSim.Data.PokemonSim.Engine.MoveDex, and PokemonDex called it
    /// by a name that no longer existed), every field the file carries is
    /// mapped (effects, stat changes, chances, multi-hit, crit stage,
    /// weather/terrain - the old loader dropped them all), a move that will
    /// not parse is skipped WITH a warning instead of failing the whole
    /// load, unknown lookups answer null through TryGet plus a warning
    /// rather than a KeyNotFoundException, and loading is idempotent.
    /// </summary>
    public static class MoveDex
    {
        static readonly Dictionary<string, MoveState> moves = new(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> warnings = new();
        static bool loaded;

        public static IReadOnlyList<string> Warnings => warnings;

        public static bool IsLoaded => loaded;

        public static int Count => moves.Count;

        public static void EnsureLoaded() => Load(SimDataFiles.MovesPath);

        public static void Load(string path)
        {
            if (loaded)
                return;

            loaded = true;

            string json = File.ReadAllText(path);

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            options.Converters.Add(new JsonStringEnumConverter());

            var data = JsonSerializer.Deserialize<Dictionary<string, MoveJson>>(json, options)
                ?? new Dictionary<string, MoveJson>();

            foreach (var entry in data)
            {
                try
                {
                    var value = entry.Value;

                    var move = new MoveState
                    {
                        Name = entry.Key,
                        Type = Enum.Parse<PokemonType>(value.type, ignoreCase: true),
                        Category = Enum.Parse<MoveCategory>(value.category, ignoreCase: true),
                        Power = value.power,
                        Accuracy = value.accuracy,
                        MaxPP = value.pp,
                        CurrentPP = value.pp,
                        Priority = value.priority,
                        StatusChance = value.statusChance,
                        SecondaryChance = value.secondaryChance,
                        FlinchChance = value.flinchChance,
                        MinHits = value.minHits > 0 ? value.minHits : 1,
                        MaxHits = value.maxHits > 0 ? value.maxHits : Math.Max(1, value.minHits),
                        CritStage = value.critStage,
                        InflictStatus = value.inflictStatus,
                        StatChanges = value.statChanges,
                        Effects = value.effects,
                        SetWeather = value.setWeather,
                        SetTerrain = value.setTerrain,
                        UsesTargetDefense = value.usesTargetDefense,
                        UsesDefenseAsOffense = value.usesDefenseAsOffense,
                        UsesTargetAttack = value.usesTargetAttack,
                        UsesHigherOffense = value.usesHigherOffense,
                        RandomStatus = value.randomStatus?
                            .Select(s => Enum.Parse<StatusCondition>(s, ignoreCase: true))
                            .ToList()
                    };

                    if (move.MaxPP <= 0)
                    {
                        move.MaxPP = 10;
                        move.CurrentPP = 10;
                        warnings.Add($"{entry.Key}: no PP in the data - defaulted to 10.");
                    }

                    if (value.target != null)
                        warnings.Add($"{entry.Key}: targeting \"{value.target}\" is not simulated yet.");

                    if (move.Effects != null)
                    {
                        foreach (string effect in move.Effects)
                        {
                            if (!Engine.Effects.MoveEffectRegistry.IsKnown(effect))
                                warnings.Add($"{entry.Key}: effect \"{effect}\" is not simulated yet.");
                        }
                    }

                    // Section 158: contact/pulse/spread classification is
                    // mechanics knowledge, not data - MoveFlags derives it.
                    MoveFlags.Annotate(move);

                    moves[entry.Key] = move;
                }
                catch (Exception ex)
                {
                    warnings.Add($"{entry.Key}: skipped - {ex.Message}");
                }
            }
        }

        public static bool TryGet(string name, out MoveState move)
        {
            EnsureLoaded();

            if (moves.TryGetValue(name, out MoveState? template))
            {
                move = template.Clone();
                return true;
            }

            move = null!;
            return false;
        }

        /// <summary>Kept for the old harness call shape - throws with the
        /// move's name (the raw KeyNotFoundException said nothing).</summary>
        public static MoveState Get(string name)
        {
            if (TryGet(name, out MoveState move))
                return move;

            throw new KeyNotFoundException($"moves.json has no move named \"{name}\".");
        }

        public static IReadOnlyList<string> AllNames()
        {
            EnsureLoaded();
            return moves.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}