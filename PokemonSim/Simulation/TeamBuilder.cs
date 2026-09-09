using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Abilities;
using PokemonSim.Models;

namespace PokemonSim.Simulation
{
    /// <summary>One planned team slot, as the setup screen edits it.</summary>
    public sealed class TeamSlotPlan
    {
        public string SpeciesName = "";
        public int Level = 50;
        public Nature Nature = Nature.Hardy;
        public string? AbilityName;

        /// <summary>Section 159: the slot's held item (a display name the
        /// engine normalizes; null or "None" = nothing held).</summary>
        public string? ItemName;

        public List<string> MoveNames = new();

        // ---- Section 162: screenshot imports. ----

        /// <summary>IVs and EVs in HP, Attack, Defense, SpAttack,
        /// SpDefense, Speed order - null keeps the 31/0 defaults.</summary>
        public int[]? Ivs;
        public int[]? Evs;

        /// <summary>True for a Pokemon read off the player's own game
        /// (§162): the card is authoritative, so the learnset gate and the
        /// ability-ownership gate are skipped - an unknown move is dropped
        /// with a warning instead of failing the build, exactly like the
        /// trusted Boss Database path.</summary>
        public bool Trusted;

        /// <summary>Section 164: the card's golden S badge - cosmetic, the
        /// battle views show the shiny artwork.</summary>
        public bool IsShiny;
    }

    public sealed class TeamBuildResult
    {
        public List<PokemonState> Team = new();
        public List<string> Errors = new();
        public List<string> Warnings = new();

        public bool Ok => Errors.Count == 0 && Team.Count > 0;
    }

    /// <summary>
    /// Section 154. Turns slot plans into a validated battle team: 1-6
    /// Pokemon, level 1-100, 1-4 distinct moves each - every move known to
    /// the engine's MoveDex and present in the species' learnset - and an
    /// ability that is either one of the species' own or empty. An ability
    /// the species has but the engine does not implement builds fine with a
    /// warning ("not simulated yet") rather than pretending. Section 159:
    /// held items attach the same way - a supported item works for real,
    /// an unknown one warns and is left off.
    /// </summary>
    public static class TeamBuilder
    {
        public const int MaxTeamSize = 6;
        public const int MaxMoves = 4;

        /// <summary>The species' learnset moves the engine can actually
        /// battle with (its MoveDex intersection), alphabetical.</summary>
        public static List<string> UsableMoves(SpeciesInfo species)
        {
            var usable = new List<string>();

            foreach (string moveName in species.LearnsetMoveNames)
            {
                if (MoveDex.TryGet(moveName, out _))
                    usable.Add(moveName);
            }

            usable.Sort(StringComparer.OrdinalIgnoreCase);
            return usable;
        }

        public static TeamBuildResult Build(IReadOnlyList<TeamSlotPlan> plans, ISpeciesSource source)
        {
            var result = new TeamBuildResult();

            if (plans.Count == 0)
            {
                result.Errors.Add("Add at least one Pokemon to the team.");
                return result;
            }

            if (plans.Count > MaxTeamSize)
            {
                result.Errors.Add($"A team holds at most {MaxTeamSize} Pokemon.");
                return result;
            }

            foreach (var plan in plans)
            {
                string slotName = string.IsNullOrWhiteSpace(plan.SpeciesName) ? "(empty slot)" : plan.SpeciesName;

                SpeciesInfo? info = string.IsNullOrWhiteSpace(plan.SpeciesName) ? null : source.Find(plan.SpeciesName);

                if (info == null)
                {
                    result.Errors.Add($"{slotName}: pick a Pokemon.");
                    continue;
                }

                if (plan.Level < 1 || plan.Level > 100)
                {
                    result.Errors.Add($"{info.Name}: level must be 1-100.");
                    continue;
                }

                var distinctMoves = plan.MoveNames
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (distinctMoves.Count == 0)
                {
                    result.Errors.Add($"{info.Name}: pick at least one move.");
                    continue;
                }

                if (distinctMoves.Count > MaxMoves)
                {
                    result.Errors.Add($"{info.Name}: at most {MaxMoves} moves.");
                    continue;
                }

                var moves = new List<MoveState>();
                bool slotFailed = false;

                foreach (string moveName in distinctMoves)
                {
                    // §162: an imported card is authoritative about what
                    // the Pokemon knows - no learnset gate.
                    if (!plan.Trusted &&
                        !info.LearnsetMoveNames.Contains(moveName, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Errors.Add($"{info.Name} does not learn {moveName}.");
                        slotFailed = true;
                        continue;
                    }

                    if (!MoveDex.TryGet(moveName, out MoveState move))
                    {
                        if (plan.Trusted)
                        {
                            result.Warnings.Add($"{info.Name}: {moveName} is not in the battle engine's move data yet - left off.");
                            continue;
                        }

                        result.Errors.Add($"{moveName} is not in the battle engine's move data yet.");
                        slotFailed = true;
                        continue;
                    }

                    moves.Add(move);
                }

                if (slotFailed)
                    continue;

                if (moves.Count == 0)
                {
                    result.Errors.Add($"{info.Name}: none of its moves are in the battle engine's move data yet.");
                    continue;
                }

                string? abilityId = null;

                if (!string.IsNullOrWhiteSpace(plan.AbilityName))
                {
                    // §162: the card is authoritative about the ability too
                    // (the species catalog only carries ability lists for a
                    // fraction of the dex).
                    bool ownAbility = plan.Trusted || info.AbilityNames.Any(a =>
                        AbilityFactory.Normalize(a) == AbilityFactory.Normalize(plan.AbilityName));

                    if (!ownAbility)
                    {
                        result.Errors.Add($"{info.Name} cannot have the ability {plan.AbilityName}.");
                        continue;
                    }

                    abilityId = plan.AbilityName;

                    if (!AbilityFactory.IsSupported(abilityId))
                        result.Warnings.Add($"{info.Name}: ability {plan.AbilityName} is not simulated yet - it will have no battle effect.");
                }

                // ---- held item (§159) ----

                string? heldItemId = null;

                if (!string.IsNullOrWhiteSpace(plan.ItemName) &&
                    !plan.ItemName.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    if (Engine.Items.HeldItems.IsSupported(plan.ItemName))
                    {
                        heldItemId = Engine.Items.HeldItems.Normalize(plan.ItemName);

                        // §161: a matching stone mega evolves for real now;
                        // only a mismatched one still just rides along.
                        if (Engine.Items.HeldItems.IsMegaStone(heldItemId) &&
                            !Engine.MegaEvolutions.StoneMatches(info.Name, heldItemId))
                        {
                            result.Warnings.Add($"{info.Name}: {plan.ItemName} belongs to a different Pokemon - it rides along with no effect.");
                        }

                        // §161: a crystal with no damaging move of its type
                        // will never fire.
                        PokemonType? crystalType = Engine.Items.HeldItems.ZCrystalType(heldItemId);

                        if (crystalType != null &&
                            !moves.Any(m => m.Category != MoveCategory.Status &&
                                            m.Power > 0 && !m.Typeless &&
                                            m.Type == crystalType.Value))
                        {
                            result.Warnings.Add($"{info.Name}: no damaging {crystalType.Value} move for {plan.ItemName} - it will sit idle.");
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

                var member = PokemonFactory.Create(species, plan.Level, plan.Nature, abilityId, moves,
                    plan.Ivs, plan.Evs);
                member.HeldItemId = heldItemId;
                member.IsShiny = plan.IsShiny;

                result.Team.Add(member);
            }

            if (result.Errors.Count > 0)
                result.Team.Clear();

            return result;
        }

        static List<PokemonType> ParseTypes(SpeciesInfo info, TeamBuildResult result)
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