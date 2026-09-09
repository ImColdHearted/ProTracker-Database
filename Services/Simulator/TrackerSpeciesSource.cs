using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Models;
using PokemonSim.Simulation;

namespace Foot_Tracker.Services.Simulator
{
    /// <summary>
    /// §154. The Simulator's species catalog IS the tracker's: this adapter
    /// hands the battle engine's team builder the same cleaned
    /// calc-pokedex.json the Calculators menu already reads
    /// (CalculatorDataService, §89/§90) - names, types, base stats,
    /// Showdown-spelled learnsets, abilities and preferred natures - so the
    /// app keeps ONE species catalog rather than a second, typo'd copy. The
    /// engine's own DataFiles/pokedex.json still ships for its dev console
    /// and tests; the app never reads it.
    /// </summary>
    public sealed class TrackerSpeciesSource : ISpeciesSource
    {
        public IReadOnlyList<string> AllSpeciesNames => CalculatorDataService.AllSpeciesNames;

        public SpeciesInfo? Find(string name)
        {
            CalculatorDataService.CalcSpecies? species = CalculatorDataService.Find(name);

            if (species == null)
                return null;

            return new SpeciesInfo
            {
                Name = species.Name,
                Types = species.Types,
                BaseStats = new Stats
                {
                    HP = species.BaseHp,
                    Attack = species.BaseAttack,
                    Defense = species.BaseDefense,
                    SpAttack = species.BaseSpAttack,
                    SpDefense = species.BaseSpDefense,
                    Speed = species.BaseSpeed
                },
                LearnsetMoveNames = species.LearnsetMoveNames,
                AbilityNames = species.Abilities,
                PreferredNatures = species.PreferredNatures
            };
        }
    }
}
