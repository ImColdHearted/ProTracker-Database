using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Abilities;
using PokemonSim.Engine.Items;
using PokemonSim.Models;

namespace PokemonSim.Simulation
{
    /// <summary>One planned opponent slot as boss data describes it. Unlike
    /// TeamSlotPlan this is trusted data: the moves are NOT gated on a
    /// learnset (the boss file is authoritative for what a boss knows), the
    /// nature and ability may be the literal word "Random", the level may be
    /// missing (null), and a held item may be named even though this engine
    /// does not simulate items yet.</summary>
    public sealed class OpponentSlotPlan
    {
        public string SpeciesName = "";
        public int? Level;
        public string? NatureName;
        public string? AbilityName;
        public string? ItemName;
        public List<string> MoveNames = new();

        /// <summary>Section 164: IVs and EVs in HP, Attack, Defense,
        /// SpAttack, SpDefense, Speed order - null keeps the 31/0
        /// defaults. The Boss Database applies PRO's difficulty
        /// standards through these (Easy 31/0, Medium 31/252, Hard
        /// 31/400 per stat).</summary>
        public int[]? Ivs;
        public int[]? Evs;

        /// <summary>§199. An explicit picture for this slot, carried
        /// through to the built Pokemon and read by whatever draws the
        /// battle. Null for every roster that does not say otherwise,
        /// which is the Boss Database's entire catalog. See
        /// PokemonState.SpritePath.</summary>
        public string? SpritePath;

        /// <summary>§200. The national-dex id of the exact form to draw, or
        /// 0 to work it out from the species name. Both the Boss Database
        /// and a custom opponent fill this in; see
        /// PokemonState.DexNumber.</summary>
        public int DexNumber;
    }

    public sealed class OpponentTeamResult
    {
        public List<PokemonState> Team = new();
        public List<string> Errors = new();
        public List<string> Warnings = new();

        /// <summary>A fight can start when at least one slot built.</summary>
        public bool Ok => Team.Count > 0;
    }

    /// <summary>
    /// Section 155. Builds computer-opponent teams from trusted rosters -
    /// the tracker's boss files, in practice. Species names are resolved
    /// through the same ISpeciesSource the team builder uses, with the
    /// spelling differences between boss data and the species catalog
    /// bridged by NameCandidates ("Rotom-Wash" finds "Wash Rotom",
    /// "Ninetales-Alolan" finds "Alolan Ninetales", "Shaymin-Sky" finds
    /// "Shaymin: Sky"); a Mega form the catalog lacks falls back to the
    /// base species with a warning rather than losing the slot. Moves are
    /// taken on the boss file's authority: anything the engine's MoveDex
    /// implements is kept - no learnset veto - and anything it does not is
    /// reported by name and skipped. "Random" natures and abilities are
    /// rolled from the provided rng (seeded by the battle, so previews and
    /// fights agree). A malformed slot degrades to a named warning or
    /// error, never an exception, and a fight starts as long as one slot
    /// survived.
    /// </summary>
    public static class OpponentTeams
    {
        public const int DefaultLevel = 100;

        public static OpponentTeamResult Build(
            IReadOnlyList<OpponentSlotPlan> plans,
            ISpeciesSource source,
            BattleRng rng)
        {
            var result = new OpponentTeamResult();

            if (plans.Count == 0)
            {
                result.Errors.Add("The roster is empty.");
                return result;
            }


            foreach (var plan in plans.Take(TeamBuilder.MaxTeamSize))
            {
                BuildSlot(plan, source, rng, result);
            }

            if (plans.Count > TeamBuilder.MaxTeamSize)
                result.Warnings.Add($"The roster lists {plans.Count} Pokemon - only the first {TeamBuilder.MaxTeamSize} battle.");


            if (result.Team.Count == 0)
                result.Errors.Add("No Pokemon on this roster could be built - the fight cannot start.");

            return result;
        }

        static void BuildSlot(
            OpponentSlotPlan plan,
            ISpeciesSource source,
            BattleRng rng,
            OpponentTeamResult result)
        {
            if (string.IsNullOrWhiteSpace(plan.SpeciesName))
            {
                result.Errors.Add("A roster slot has no species name - skipped.");
                return;
            }

            (SpeciesInfo? info, string? resolveNote) = Resolve(plan.SpeciesName, source);

            if (info == null)
            {
                result.Errors.Add($"{plan.SpeciesName} is not in the species data - skipped.");
                return;
            }

            if (resolveNote != null)
                result.Warnings.Add(resolveNote);

            int level = plan.Level ?? DefaultLevel;

            if (plan.Level == null)
                result.Warnings.Add($"{info.Name}: no level in the data - simulated at level {DefaultLevel}.");
            else if (level < 1 || level > 100)
            {
                result.Warnings.Add($"{info.Name}: level {level} is out of range - clamped.");
                level = Math.Clamp(level, 1, 100);
            }

            // ---- moves: the roster is authoritative, the engine decides
            //      only what it can actually simulate ----

            var moves = new List<MoveState>();
            var unknown = new List<string>();

            foreach (string moveName in plan.MoveNames
                         .Where(m => !string.IsNullOrWhiteSpace(m))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Take(TeamBuilder.MaxMoves))
            {
                if (MoveDex.TryGet(moveName, out MoveState move))
                    moves.Add(move);
                else
                    unknown.Add(moveName);
            }

            if (unknown.Count > 0)
                result.Warnings.Add($"{info.Name}: not in the battle engine's move data yet - skipped: {string.Join(", ", unknown)}.");

            if (moves.Count == 0)
                result.Warnings.Add($"{info.Name}: none of its listed moves are implemented - it can only Struggle.");

            // ---- nature ----

            Nature nature;

            if (string.IsNullOrWhiteSpace(plan.NatureName) ||
                plan.NatureName.Equals("Random", StringComparison.OrdinalIgnoreCase))
            {
                Nature[] all = Enum.GetValues<Nature>();
                nature = all[rng.Next(all.Length)];
            }
            else if (!Enum.TryParse(plan.NatureName, ignoreCase: true, out nature))
            {
                result.Warnings.Add($"{info.Name}: nature \"{plan.NatureName}\" is unknown - using Hardy.");
                nature = Nature.Hardy;
            }

            // ---- ability ----

            string? abilityId = plan.AbilityName;

            if (abilityId != null &&
                (string.IsNullOrWhiteSpace(abilityId) || abilityId.Equals("Random", StringComparison.OrdinalIgnoreCase)))
            {
                abilityId = info.AbilityNames.Count > 0
                    ? info.AbilityNames[rng.Next(info.AbilityNames.Count)]
                    : null;
            }

            if (abilityId != null && !AbilityFactory.IsSupported(abilityId))
                result.Warnings.Add($"{info.Name}: ability {abilityId} is not simulated yet - it will have no battle effect.");

            // ---- held item (§159): attached for real ----

            string? heldItemId = null;

            if (!string.IsNullOrWhiteSpace(plan.ItemName) &&
                !plan.ItemName.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                if (HeldItems.IsSupported(plan.ItemName))
                {
                    heldItemId = HeldItems.Normalize(plan.ItemName);

                    // §161: a matching stone means this boss will mega
                    // evolve for real; only a mismatched stone is still
                    // dead weight worth a note.
                    if (HeldItems.IsMegaStone(heldItemId) &&
                        !MegaEvolutions.StoneMatches(info.Name, heldItemId))
                    {
                        result.Warnings.Add($"{info.Name}: {plan.ItemName} belongs to a different Pokemon - it rides along with no effect.");
                    }
                }
                else
                {
                    result.Warnings.Add($"{info.Name}: item {plan.ItemName} is not simulated yet - ignored.");
                }
            }

            var species = new PokemonSpecies
            {
                Name = info.Name,
                Types = ParseTypes(info, result),
                BaseStats = info.BaseStats,
                Learnset = new List<MoveState>(),
                Abilities = info.AbilityNames.ToList()
            };

            var pokemon = PokemonFactory.Create(species, level, nature, abilityId, moves,
                plan.Ivs, plan.Evs);
            pokemon.HeldItemId = heldItemId;

            // §199: rides along on the built Pokemon, so a slot that named
            // its own artwork keeps it even when the roster's slots and the
            // built team do not line up - a species this catalog does not
            // have is skipped above, and the indexes part company there.
            pokemon.SpritePath = plan.SpritePath;

            // §200: and the exact form, for the same reason and by the same
            // route - the roster's slots and the built team stop lining up
            // wherever a species was skipped.
            pokemon.DexNumber = plan.DexNumber;

            result.Team.Add(pokemon);
        }

        /// <summary>Exact match first, then the spelling bridges, then a
        /// Mega-to-base fallback. Returns the species plus an optional
        /// warning describing any compromise made.</summary>
        public static (SpeciesInfo? Info, string? Note) Resolve(string name, ISpeciesSource source)
        {
            foreach (string candidate in NameCandidates(name))
            {
                SpeciesInfo? info = source.Find(candidate);

                if (info != null)
                    return (info, null);
            }

            // A Mega form the catalog lacks: fight it as the base species
            // rather than dropping a boss's headline Pokemon entirely.
            string trimmed = name.Trim();
            string? baseName = null;

            if (trimmed.StartsWith("Mega ", StringComparison.OrdinalIgnoreCase))
                baseName = trimmed.Substring(5);
            else if (trimmed.EndsWith("-Mega", StringComparison.OrdinalIgnoreCase))
                baseName = trimmed.Substring(0, trimmed.Length - 5);

            if (baseName != null)
            {
                baseName = baseName.Split('-')[0].Trim();
                SpeciesInfo? baseInfo = source.Find(baseName);

                if (baseInfo != null)
                    return (baseInfo, $"{trimmed}: the Mega form is not in the species data - simulated as {baseInfo.Name}.");
            }

            return (null, null);
        }

        /// <summary>The spellings a roster name might correspond to in the
        /// species catalog, most specific first. Handles the punctuation
        /// styles the boss files and calc-pokedex actually use:
        /// "Rotom-Wash" / "Wash Rotom", "Ninetales-Alolan" / "Alolan
        /// Ninetales", "Arcanine-Hisui" / "Hisuian Arcanine",
        /// "Weezing-Galarian" / "Galarian Weezing", "Shaymin-Sky" /
        /// "Shaymin: Sky", "Gourgeist-Small" / "Gourgeist: Small",
        /// "Malamar-Mega" / "Mega Malamar", "Mega Charizard-X" / "Mega
        /// Charizard X", "Aegislash" / "Aegislash Shield Forme".</summary>
        public static IEnumerable<string> NameCandidates(string name)
        {
            string trimmed = name.Trim();

            yield return trimmed;

            yield return trimmed.Replace('-', ' ');

            int dash = trimmed.LastIndexOf('-');

            if (dash > 0 && dash < trimmed.Length - 1)
            {
                string head = trimmed.Substring(0, dash).Trim();
                string tail = trimmed.Substring(dash + 1).Trim();

                string tailAdjective = tail.Equals("Hisui", StringComparison.OrdinalIgnoreCase)
                    ? "Hisuian"
                    : tail.Equals("Alola", StringComparison.OrdinalIgnoreCase) ? "Alolan" : tail;

                yield return $"{tailAdjective} {head}";      // Alolan Ninetales, Mega Gyarados, Wash Rotom
                yield return $"{head}: {tail}";              // Shaymin: Sky, Gourgeist: Small
                yield return $"{head} {tail}";               // Mega Charizard X (from "Mega Charizard-X")
            }

            // A bare form-family name whose catalog entries all carry a
            // forme suffix (Aegislash -> Aegislash Shield Forme).
            yield return $"{trimmed} Shield Forme";
            yield return $"{trimmed} Forme";
        }

        static List<PokemonType> ParseTypes(SpeciesInfo info, OpponentTeamResult result)
        {
            var types = new List<PokemonType>();

            foreach (string typeName in info.Types)
            {
                if (Enum.TryParse(typeName, ignoreCase: true, out PokemonType type))
                    types.Add(type);
                else
                    result.Warnings.Add($"{info.Name}: type \"{typeName}\" is unknown - ignored.");
            }

            if (types.Count == 0)
                types.Add(PokemonType.Normal);

            return types;
        }
    }
}