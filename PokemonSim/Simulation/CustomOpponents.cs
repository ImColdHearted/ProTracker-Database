using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PokemonSim.Simulation
{
    /// <summary>§173. One Pokemon on a custom opponent's team, exactly as
    /// the .json carries it. Everything is optional except the species:
    /// a missing nature, ability or item leaves the engine's own default
    /// in place, so a half-filled slot still battles.</summary>
    public sealed class CustomOpponentPokemon
    {
        [JsonPropertyName("species")] public string Species { get; set; } = "";
        [JsonPropertyName("nature")] public string? Nature { get; set; }
        [JsonPropertyName("ability")] public string? Ability { get; set; }
        [JsonPropertyName("item")] public string? Item { get; set; }
        [JsonPropertyName("moves")] public List<string> Moves { get; set; } = new();

        /// <summary>§199. An explicit picture for this slot, instead of the
        /// one the species name resolves to. Spelled exactly as the
        /// counterparts catalog spells its own "image" field: relative to
        /// the application folder, forward slashes -
        /// "SharedPokemonLibrary/Assets/Counterparts/Pinkan/Gyarados.png".
        ///
        /// Absent means the ordinary artwork for the species, which is what
        /// every file written before §199 says by saying nothing.
        ///
        /// Nothing here checks that the file is there: this class does not
        /// touch a disk, by the rule at the top of it. The editor that
        /// writes the path is the thing that can see the filesystem, and it
        /// is where a missing file is reported.</summary>
        [JsonPropertyName("sprite")] public string? Sprite { get; set; }

        /// <summary>§200. The national-dex id of the exact form, spelled the
        /// same way the Boss Database spells it on its own Pokemon (see
        /// BossPokemonData.dexNumber) and named the same way the sprite
        /// library names its files - 25 for Pikachu, and 10000-and-up for
        /// the regional and alternate forms.
        ///
        /// Absent (or 0) means "work it out from the species name", which is
        /// what every roster written before §200 says by saying nothing, and
        /// what goes wrong the moment two forms share a name: a Hisuian
        /// Typhlosion asked for by name gets the Johto one. It changes only
        /// the picture - the battle still uses the species in "species".
        ///
        /// Nullable so a roster that does not name one is written back
        /// without the field at all, the same way Level is - a plain int
        /// would stamp "dexNumber": 0 onto every Pokemon of every roster
        /// that was ever opened and saved.</summary>
        [JsonPropertyName("dexNumber")] public int? DexNumber { get; set; }

        /// <summary>IVs in the standard HP, Attack, Defense, SpAttack,
        /// SpDefense, Speed order. Absent or short means 31s.</summary>
        [JsonPropertyName("ivs")] public List<int>? Ivs { get; set; }

        /// <summary>The simple way to spend EVs: name the stats that get
        /// 252 and every other stat gets none. Names are the standard
        /// keys - hp, attack, defense, spAttack, spDefense, speed -
        /// matched case-insensitively, and a few friendly spellings
        /// (atk, def, spa, spd, spe) are accepted too.</summary>
        [JsonPropertyName("maxEvs")] public List<string>? MaxEvs { get; set; }

        /// <summary>The exact way, when 252s are not what you want: six
        /// numbers in the same order as ivs. Overrides maxEvs.</summary>
        [JsonPropertyName("evs")] public List<int>? Evs { get; set; }
    }

    /// <summary>§173. A custom opponent: a name and up to six Pokemon,
    /// one .json file each.</summary>
    public sealed class CustomOpponentTeam
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("notes")] public string? Notes { get; set; }

        /// <summary>Absent means the simulator's own level default.</summary>
        [JsonPropertyName("level")] public int? Level { get; set; }

        [JsonPropertyName("team")] public List<CustomOpponentPokemon> Team { get; set; } = new();
    }

    /// <summary>
    /// §173. Custom opponents: the player's own boss teams, authored in
    /// the Simulator and stored one .json per team beside the Boss
    /// Database's own files. This class is the whole format - reading it,
    /// writing it, and turning it into the engine's opponent plans - kept
    /// engine-side so the shape is pinned by tests rather than by the UI
    /// that happens to edit it. File IO and folder choice belong to the
    /// app; nothing here touches a disk.
    ///
    /// The EV model is deliberately the one PRO players actually think
    /// in: name the stats that get 252 and leave the rest at zero. An
    /// explicit six-number list is there for anything else.
    /// </summary>
    public static class CustomOpponents
    {
        public const int MaxTeamSize = 6;

        /// <summary>The stat order every array here uses.</summary>
        public static readonly string[] StatKeys =
            { "hp", "attack", "defense", "spAttack", "spDefense", "speed" };

        // NOTE the SPD convention: this project has read "SPD" as SPEED
        // since §162, because that is what the PRO summary card prints
        // (its special-defense row is "SPDEF"). Mapping it the other way
        // would silently swap two stats between the card importer and
        // this reader, so it stays Speed here.
        static readonly Dictionary<string, int> StatAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hp"] = 0,
            ["attack"] = 1, ["atk"] = 1,
            ["defense"] = 2, ["def"] = 2, ["defence"] = 2,
            ["spattack"] = 3, ["spa"] = 3, ["spatk"] = 3, ["specialattack"] = 3,
            ["spdefense"] = 4, ["spdef"] = 4, ["specialdefense"] = 4, ["spdefence"] = 4,
            ["speed"] = 5, ["spe"] = 5, ["spd"] = 5
        };

        static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>The stat index a name refers to, or -1. Public so the
        /// editor can tell the author their spelling was understood.</summary>
        public static int StatIndex(string? name) =>
            !string.IsNullOrWhiteSpace(name) &&
            StatAliases.TryGetValue(name.Trim().Replace(" ", ""), out int index) ? index : -1;

        /// <summary>Parses one custom opponent file. Throws only on JSON
        /// that is not JSON; anything else missing is defaulted.</summary>
        public static CustomOpponentTeam Parse(string json) =>
            JsonSerializer.Deserialize<CustomOpponentTeam>(json, ReadOptions) ?? new CustomOpponentTeam();

        public static string Serialize(CustomOpponentTeam team) =>
            JsonSerializer.Serialize(team, WriteOptions);

        /// <summary>Everything wrong with a team, in plain words, or an
        /// empty list. A team with problems can still be battled - these
        /// are warnings for the author, not gates.</summary>
        public static List<string> Validate(CustomOpponentTeam team)
        {
            var problems = new List<string>();

            if (string.IsNullOrWhiteSpace(team.Name))
                problems.Add("This opponent has no name.");

            List<CustomOpponentPokemon> filled = team.Team
                .Where(p => !string.IsNullOrWhiteSpace(p.Species))
                .ToList();

            if (filled.Count == 0)
                problems.Add("No slot has a species, so there is nobody to battle.");

            if (team.Team.Count > MaxTeamSize)
                problems.Add($"A team holds at most {MaxTeamSize} Pokemon; the extra slots are ignored.");

            for (int i = 0; i < filled.Count; i++)
            {
                CustomOpponentPokemon mon = filled[i];
                string who = $"Slot {i + 1} ({mon.Species})";

                if (mon.Moves.Count(m => !string.IsNullOrWhiteSpace(m)) == 0)
                    problems.Add($"{who} has no moves - it will only be able to Struggle.");

                if (mon.Ivs != null && mon.Ivs.Any(v => v < 0 || v > 31))
                    problems.Add($"{who} has an IV outside 0-31; it will be clamped.");

                foreach (string name in mon.MaxEvs ?? new List<string>())
                {
                    if (StatIndex(name) < 0)
                        problems.Add($"{who} names a stat this reader does not know: \"{name}\".");
                }
            }

            return problems;
        }

        /// <summary>The engine-side plans for a custom opponent. Empty
        /// slots are skipped, blank moves are dropped, IVs are clamped to
        /// 0-31, and the EV spread is whichever of the two forms the file
        /// used.</summary>
        public static List<OpponentSlotPlan> ToPlans(CustomOpponentTeam team)
        {
            var plans = new List<OpponentSlotPlan>();

            foreach (CustomOpponentPokemon mon in team.Team.Take(MaxTeamSize))
            {
                if (string.IsNullOrWhiteSpace(mon.Species))
                    continue;

                plans.Add(new OpponentSlotPlan
                {
                    SpeciesName = mon.Species.Trim(),
                    Level = team.Level,
                    NatureName = Blank(mon.Nature),
                    AbilityName = Blank(mon.Ability),
                    ItemName = Blank(mon.Item),
                    MoveNames = mon.Moves
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Select(m => m.Trim())
                        .ToList(),
                    Ivs = ResolveIvs(mon),
                    Evs = ResolveEvs(mon),
                    // §199: forward slashes whatever the author typed, so
                    // one authored on Windows still resolves on Linux.
                    SpritePath = NormalizeSpritePath(mon.Sprite),
                    // §200: a zero or negative id in a hand-edited file is
                    // nonsense rather than a form, so it is dropped back to
                    // "unset" and the species name answers instead.
                    DexNumber = mon.DexNumber is int dex && dex > 0 ? dex : 0
                });
            }

            return plans;
        }

        /// <summary>§199. A sprite path as the format wants it: trimmed,
        /// forward-slashed, and null rather than empty. Backslashes are
        /// accepted on the way in because a Windows file browser hands them
        /// over that way, and a file authored on Windows has to keep
        /// working on a Linux user's machine.</summary>
        public static string? NormalizeSpritePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string trimmed = path.Trim().Replace('\\', '/');

            return trimmed.Length == 0 ? null : trimmed;
        }

        /// <summary>Six IVs, defaulting to 31 and clamped to 0-31.</summary>
        public static int[] ResolveIvs(CustomOpponentPokemon mon)
        {
            var ivs = new int[6];

            for (int i = 0; i < 6; i++)
            {
                int value = mon.Ivs != null && i < mon.Ivs.Count ? mon.Ivs[i] : 31;
                ivs[i] = Math.Clamp(value, 0, 31);
            }

            return ivs;
        }

        /// <summary>Six EVs: the explicit list when the file gives one,
        /// else 252 in each named stat and nothing anywhere else.</summary>
        public static int[] ResolveEvs(CustomOpponentPokemon mon)
        {
            var evs = new int[6];

            if (mon.Evs != null && mon.Evs.Count > 0)
            {
                for (int i = 0; i < 6; i++)
                {
                    int value = i < mon.Evs.Count ? mon.Evs[i] : 0;
                    evs[i] = Math.Clamp(value, 0, 512);
                }

                return evs;
            }

            foreach (string name in mon.MaxEvs ?? new List<string>())
            {
                int index = StatIndex(name);

                if (index >= 0)
                    evs[index] = 252;
            }

            return evs;
        }

        /// <summary>§174. The stat names for a spread that is nothing but
        /// 252s and zeroes, so a file written back keeps the tidy
        /// shorthand; null when any value needs writing out in full.
        /// Deliberately name-ordered by stat index, not by the order the
        /// author happened to tick things.</summary>
        public static List<string>? AsMaxEvNames(int[] evs)
        {
            if (evs.Length != 6 || evs.Any(v => v != 0 && v != 252))
                return null;

            var names = new List<string>();

            for (int i = 0; i < 6; i++)
            {
                if (evs[i] == 252)
                    names.Add(StatKeys[i]);
            }

            return names;
        }

        /// <summary>The one-line spread the editor and the opponent list
        /// show, in the card's own row order (§167).</summary>
        public static string DescribeSpread(CustomOpponentPokemon mon)
        {
            int[] ivs = ResolveIvs(mon);
            int[] evs = ResolveEvs(mon);
            int[] order = { 1, 2, 5, 3, 4, 0 };

            return $"IVs {string.Join("/", order.Select(i => ivs[i]))}   " +
                   $"EVs {string.Join("/", order.Select(i => evs[i]))}   (Atk/Def/Spe/SpA/SpD/HP)";
        }

        static string? Blank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}