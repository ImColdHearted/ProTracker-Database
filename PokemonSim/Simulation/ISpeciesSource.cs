using System.Collections.Generic;
using PokemonSim.Models;

namespace PokemonSim.Simulation
{
    /// <summary>
    /// Section 154. Where the team builder reads species from. The engine's
    /// own PokemonDex implements it for the dev console and the tests; the
    /// tracker implements it over its cleaned calc-pokedex.json (the
    /// section-89 copy of this project's data) so the app has ONE species
    /// catalog instead of two competing ones.
    /// </summary>
    public interface ISpeciesSource
    {
        IReadOnlyList<string> AllSpeciesNames { get; }

        SpeciesInfo? Find(string name);
    }

    /// <summary>One species as the team builder needs it: display name,
    /// types and base stats for the engine, learnset move names to
    /// intersect with the implemented MoveDex, ability names (possibly
    /// empty), preferred natures (possibly empty).</summary>
    public sealed class SpeciesInfo
    {
        public required string Name { get; init; }
        public required IReadOnlyList<string> Types { get; init; }
        public required Stats BaseStats { get; init; }
        public required IReadOnlyList<string> LearnsetMoveNames { get; init; }
        public IReadOnlyList<string> AbilityNames { get; init; } = new List<string>();
        public IReadOnlyList<string> PreferredNatures { get; init; } = new List<string>();
    }

    /// <summary>The engine-data implementation (DataFiles/pokedex.json).</summary>
    public sealed class PokemonDexSpeciesSource : ISpeciesSource
    {
        public IReadOnlyList<string> AllSpeciesNames
        {
            get
            {
                Data.PokemonDex.EnsureLoaded();
                return Data.PokemonDex.All().Select(s => s.Name).OrderBy(n => n).ToList();
            }
        }

        public SpeciesInfo? Find(string name)
        {
            if (!Data.PokemonDex.TryGet(name, out PokemonSpecies species))
                return null;

            return new SpeciesInfo
            {
                Name = species.Name,
                Types = species.Types.Select(t => t.ToString()).ToList(),
                BaseStats = species.BaseStats,
                LearnsetMoveNames = species.Learnset.Select(m => m.Name).ToList(),
                AbilityNames = species.Abilities,
                PreferredNatures = species.PreferredNatures.Select(n => n.ToString()).ToList()
            };
        }
    }
}