using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Models;

namespace PokemonSim.Simulation
{
    /// <summary>
    /// §168 (extracted verbatim from SimulatorViewModel's §154/§157/§161
    /// BuildOpponentTeam so the admin Battle Lab can build the same random
    /// teams headlessly). One seeded random team from whatever species
    /// catalog the caller supplies: preferred natures when the catalog
    /// knows them, movesets drawn from the usable learnset, and §161's
    /// occasional matching Z-Crystal so the AI's Z-Move path stays in the
    /// training data. Deterministic per (catalog, count, seed).
    ///
    /// §178 put a better source in front of that. A species with a
    /// published competitive set now gets one - four real moves with the
    /// spread, item, ability and nature that belong to them - and only a
    /// species without one falls back to sampling its learnset. The
    /// fallback matters far less than it used to: until §178 the learnsets
    /// were a placeholder pair shared by 767 species, so this method was
    /// producing two-move Pokemon that no real battle would ever contain.
    /// Both paths stay deterministic per (catalog, count, seed).
    /// </summary>
    public static class RandomTeams
    {
        public static TeamBuildResult Build(ISpeciesSource speciesSource, int count, int seed, int level = 100)
        {
            var rng = new Random(seed);
            IReadOnlyList<string> names = speciesSource.AllSpeciesNames;

            var plans = new List<TeamSlotPlan>();
            int guard = 0;

            while (plans.Count < count && guard++ < 400 && names.Count > 0)
            {
                SpeciesInfo? info = speciesSource.Find(names[rng.Next(names.Count)]);

                if (info == null)
                    continue;

                // §178: a real published set when this species has one.
                // The rng is drawn from either way so the sequence - and
                // therefore the whole team - stays reproducible whichever
                // branch a species takes.
                if (CompetitiveSets.TryGet(info.Name, out IReadOnlyList<CompetitiveSet> sets))
                {
                    TeamSlotPlan real = CompetitiveSets.ToPlan(
                        info.Name, sets[rng.Next(sets.Count)], level, rng);

                    if (real.MoveNames.Count > 0)
                    {
                        plans.Add(real);
                        continue;
                    }
                }

                List<string> usable = TeamBuilder.UsableMoves(info);

                if (usable.Count == 0)
                    continue;

                var plan = new TeamSlotPlan
                {
                    SpeciesName = info.Name,
                    Level = level,
                    Nature = PickNature(info, rng),
                    AbilityName = info.AbilityNames.Count > 0 ? info.AbilityNames[rng.Next(info.AbilityNames.Count)] : null
                };

                foreach (string move in usable.OrderBy(_ => rng.Next()).Take(TeamBuilder.MaxMoves))
                    plan.MoveNames.Add(move);

                // §161: sometimes the random opponent packs the Z-Crystal
                // matching one of its damaging moves - the AI fires it the
                // first time the Z-Move looks lethal.
                if (rng.Next(4) == 0)
                {
                    var zTypes = new List<PokemonType>();

                    foreach (string moveName in plan.MoveNames)
                    {
                        if (MoveDex.TryGet(moveName, out MoveState known) &&
                            known.Category != MoveCategory.Status &&
                            known.Power > 0 && !known.Typeless)
                        {
                            zTypes.Add(known.Type);
                        }
                    }

                    if (zTypes.Count > 0)
                        plan.ItemName = ZMoves.CrystalNameFor(zTypes[rng.Next(zTypes.Count)]);
                }

                plans.Add(plan);
            }

            return TeamBuilder.Build(plans, speciesSource);
        }

        static Nature PickNature(SpeciesInfo info, Random rng)
        {
            if (info.PreferredNatures.Count > 0 &&
                Enum.TryParse(info.PreferredNatures[rng.Next(info.PreferredNatures.Count)], ignoreCase: true, out Nature preferred))
            {
                return preferred;
            }

            Nature[] all = Enum.GetValues<Nature>();
            return all[rng.Next(all.Length)];
        }
    }
}