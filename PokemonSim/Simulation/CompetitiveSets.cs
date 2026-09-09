using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PokemonSim.Data;
using PokemonSim.Models;

namespace PokemonSim.Simulation
{
    /// <summary>Section 178. One published competitive set, as
    /// CompetitiveSets.json carries it. Move slots are arrays because a
    /// Smogon set often offers a choice for a slot ("Fire Fang / Stone
    /// Edge"); a single-move slot is simply an array of one.</summary>
    public sealed class CompetitiveSet
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";

        /// <summary>The Smogon format it came from - gen9ou, gen7uu and so
        /// on. Kept so a run can be narrowed to a generation later without
        /// re-importing.</summary>
        [JsonPropertyName("format")] public string Format { get; set; } = "";

        [JsonPropertyName("moves")] public List<List<string>> Moves { get; set; } = new();

        [JsonPropertyName("ability")] public string? Ability { get; set; }
        [JsonPropertyName("item")] public string? Item { get; set; }
        [JsonPropertyName("nature")] public string? Nature { get; set; }

        /// <summary>EVs and IVs in the project's usual HP, Attack, Defense,
        /// SpAttack, SpDefense, Speed order. Null keeps the defaults.</summary>
        [JsonPropertyName("evs")] public List<int>? Evs { get; set; }
        [JsonPropertyName("ivs")] public List<int>? Ivs { get; set; }
    }

    /// <summary>
    /// Section 178. The competitive sets the Battle Lab builds its random
    /// opponents from, imported from the community's published movesets
    /// for generations 7 to 9.
    ///
    /// This exists because of what section 177 turned up. Until then, 767
    /// of the 803 species in pokedex.json shared one placeholder learnset -
    /// literally ["Flamethrower", "Earthquake"], for Blissey and Ferrothorn
    /// alike - so 95 percent of every lab battle was two Pokemon choosing
    /// between the same two moves, neither of them ever getting STAB. The
    /// model was being trained on a two-choice game and then measured
    /// against bosses carrying four real moves each. Section 178 filled the
    /// learnsets from real data AND added this: rather than four moves
    /// sampled at random out of a hundred, a lab opponent is now an actual
    /// competitive set, with the spread, item, ability and nature that go
    /// with it.
    ///
    /// Move names are stored VERBATIM, including moves this engine has not
    /// implemented yet. Those slots are skipped at build time, so
    /// implementing a move in moves.json upgrades every set that uses it
    /// with no re-import and no code change. ai-training/missing-moves.md
    /// is the worklist, ordered by how many sets each move would unlock.
    ///
    /// Missing or unreadable data costs exactly this feature: TryGet
    /// reports nothing and RandomTeams falls back to sampling the
    /// learnset, which is what it always did.
    /// </summary>
    public static class CompetitiveSets
    {
        static readonly object gate = new();
        static Dictionary<string, List<CompetitiveSet>>? bySpecies;
        static string status = "not loaded";

        /// <summary>What happened the last time the file was read - the
        /// admin console shows it rather than a silence.</summary>
        public static string Status
        {
            get { lock (gate) { return status; } }
        }

        /// <summary>How many species have at least one set.</summary>
        public static int SpeciesCount
        {
            get { EnsureLoaded(); lock (gate) { return bySpecies?.Count ?? 0; } }
        }

        public static void EnsureLoaded()
        {
            lock (gate)
            {
                if (bySpecies != null)
                    return;

                bySpecies = new Dictionary<string, List<CompetitiveSet>>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    string path = SimDataFiles.CompetitiveSetsPath;

                    if (!File.Exists(path))
                    {
                        status = "no CompetitiveSets.json - random opponents use their learnsets";
                        return;
                    }

                    var loaded = JsonSerializer.Deserialize<Dictionary<string, List<CompetitiveSet>>>(
                        File.ReadAllText(path),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    int sets = 0;

                    foreach ((string species, List<CompetitiveSet> rows) in loaded ?? new())
                    {
                        if (rows == null || rows.Count == 0)
                            continue;

                        bySpecies[species] = rows;
                        sets += rows.Count;
                    }

                    status = $"{sets} competitive set(s) across {bySpecies.Count} species";
                }
                catch (Exception ex)
                {
                    bySpecies.Clear();
                    status = "CompetitiveSets.json could not be read: " + FirstLine(ex.Message);
                }
            }
        }

        public static bool TryGet(string species, out IReadOnlyList<CompetitiveSet> sets)
        {
            EnsureLoaded();

            lock (gate)
            {
                if (bySpecies != null &&
                    !string.IsNullOrWhiteSpace(species) &&
                    bySpecies.TryGetValue(species.Trim(), out List<CompetitiveSet>? rows) &&
                    rows.Count > 0)
                {
                    sets = rows;
                    return true;
                }
            }

            sets = Array.Empty<CompetitiveSet>();
            return false;
        }

        /// <summary>
        /// A set turned into something the team builder can build. Each
        /// move slot contributes the first of its options this engine
        /// actually knows, chosen at random among those that qualify so a
        /// slot offering a real choice stays a real choice; a slot with no
        /// implemented option contributes nothing rather than failing the
        /// Pokemon. An item or ability the engine does not know is left
        /// off the same way - the engine's own defaults then apply.
        /// </summary>
        public static TeamSlotPlan ToPlan(string species, CompetitiveSet set, int level, Random rng)
        {
            var plan = new TeamSlotPlan
            {
                SpeciesName = species,
                Level = level,

                // A published set is authoritative about what the Pokemon
                // runs, in the same sense section 162's imported card is
                // authoritative about the player's own - so the learnset
                // and ability-ownership gates are skipped. That is not a
                // convenience: 643 of these move slots sit outside even the
                // section 178 learnsets (Hidden Power variants, Pursuit,
                // Volt Switch, Wish), and 1,088 of the 2,574 sets name an
                // ability for a species whose catalog entry lists none at
                // all. Gated, those sets would not merely lose a move -
                // TeamBuilder would drop the whole Pokemon.
                Trusted = true,
                AbilityName = Blank(set.Ability),
                ItemName = Blank(set.Item),
                Ivs = Spread(set.Ivs, 31),
                Evs = Spread(set.Evs, 0)
            };

            if (!string.IsNullOrWhiteSpace(set.Nature) &&
                Enum.TryParse(set.Nature, ignoreCase: true, out Nature nature))
            {
                plan.Nature = nature;
            }

            foreach (List<string> slot in set.Moves)
            {
                if (slot == null || slot.Count == 0)
                    continue;

                List<string> known = slot
                    .Where(m => !string.IsNullOrWhiteSpace(m) && MoveDex.TryGet(m, out _))
                    .Where(m => !plan.MoveNames.Contains(m, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (known.Count == 0)
                    continue;               // not implemented yet - see missing-moves.md

                plan.MoveNames.Add(known[rng.Next(known.Count)]);

                if (plan.MoveNames.Count >= TeamBuilder.MaxMoves)
                    break;
            }

            return plan;
        }

        static int[]? Spread(List<int>? values, int fallback)
        {
            if (values == null || values.Count == 0)
                return null;

            var spread = new int[6];

            for (int i = 0; i < 6; i++)
                spread[i] = i < values.Count ? values[i] : fallback;

            return spread;
        }

        static string? Blank(string? value) =>
            string.IsNullOrWhiteSpace(value) || value.Trim() == "None" ? null : value.Trim();

        static string FirstLine(string text)
        {
            int cut = text.IndexOfAny(new[] { '\r', '\n' });
            return cut < 0 ? text : text.Substring(0, cut);
        }
    }
}