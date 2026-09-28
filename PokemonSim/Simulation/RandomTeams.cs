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
        /// <summary>
        /// §327. <paramref name="tiers"/> narrows which published sets a
        /// team may be drawn from, and null means TierFilter.Standard -
        /// OU and UU.
        ///
        /// THIS CHANGES THE DEFAULT. Before §327 every imported format was
        /// one pool, so a random battle could be a UU wall against
        /// Rayquaza; 71 of the 565 species with sets have them in nothing
        /// but Ubers. That is not a position skill decides, and both the
        /// AI's training corpus and the win rate it is measured by were
        /// made of those battles. Pass TierFilter.All for exactly what
        /// this did before.
        ///
        /// A RESTRICTED POOL DOES NOT FALL BACK TO LEARNSETS. The fallback
        /// below builds a team out of a species' learnset when it has no
        /// published set, and under a filter that is precisely the back
        /// door an Ubers-only legendary would walk in through - it has no
        /// set in these tiers, so it would qualify for the fallback. Under
        /// a filter the pool IS the species with a qualifying set, and
        /// nothing else is drawn.
        /// </summary>
        public static TeamBuildResult Build(
            ISpeciesSource speciesSource, int count, int seed, int level = 100,
            TierFilter? tiers = null)
        {
            tiers ??= TierFilter.Standard;

            var rng = new Random(seed);

            // Under a filter the catalog is not the pool: only species with
            // a set in these tiers may be drawn, and only from the catalog
            // the caller actually has.
            IReadOnlyList<string> names = tiers.IsUnrestricted
                ? speciesSource.AllSpeciesNames
                : Eligible(speciesSource, tiers);

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
                if (CompetitiveSets.TryGet(info.Name, tiers, out IReadOnlyList<CompetitiveSet> sets))
                {
                    TeamSlotPlan real = CompetitiveSets.ToPlan(
                        info.Name, sets[rng.Next(sets.Count)], level, rng);

                    if (real.MoveNames.Count > 0)
                    {
                        plans.Add(real);
                        continue;
                    }
                }

                // §327: the learnset fallback belongs to the unrestricted
                // pool. Reaching it under a filter would mean building a
                // species that has no set in these tiers - the one thing
                // the filter exists to prevent.
                if (!tiers.IsUnrestricted)
                    continue;

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

        /// <summary>§327. The species this catalog and this filter agree
        /// on. Order is the sorted pool's, so a seed still reproduces its
        /// team.</summary>
        static IReadOnlyList<string> Eligible(ISpeciesSource speciesSource, TierFilter tiers)
        {
            var known = new HashSet<string>(speciesSource.AllSpeciesNames, StringComparer.OrdinalIgnoreCase);

            return CompetitiveSets.SpeciesIn(tiers).Where(known.Contains).ToList();
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