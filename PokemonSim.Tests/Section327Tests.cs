using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §327. Random teams come from a tier pool.
    ///
    /// §178 put every imported format into one pool. That pool holds 565
    /// species and 71 of them have sets in nothing but Ubers - Rayquaza,
    /// Arceus, Crowned Zacian, Dialga - so a random battle could be a UU
    /// wall against a box legendary. The AI learns from those battles and
    /// is measured on them, so the noise lands twice.
    ///
    /// The half of this file that matters is the BACK DOOR: under a filter,
    /// a species with no qualifying set must not reach the learnset
    /// fallback, because that fallback does not look at sets at all and
    /// would happily build the very legendary the filter excluded.
    /// </summary>
    public class Section327Tests
    {
        [Fact]
        public void ATierIsTheFormatWithItsGenerationStripped()
        {
            Assert.Equal("ou", TierFilter.TierOf("gen9ou"));
            Assert.Equal("uu", TierFilter.TierOf("gen7uu"));
            Assert.Equal("ubers", TierFilter.TierOf("gen8ubers"));
        }

        [Fact]
        public void AGenerationNobodyHasImportedYetStillParses()
        {
            // Written as "strip gen and its digits" rather than as a list
            // of known formats, so gen10ou works the day it arrives instead
            // of being silently excluded from every filter.
            Assert.Equal("ou", TierFilter.TierOf("gen10ou"));
            Assert.Equal("ou", TierFilter.TierOf("GEN10OU"));
        }

        [Fact]
        public void SomethingThatIsNotAFormatDoesNotBecomeATier()
        {
            Assert.Equal("", TierFilter.TierOf(null));
            Assert.Equal("", TierFilter.TierOf("   "));
            Assert.Equal("", TierFilter.TierOf("gen9"));
        }

        [Fact]
        public void TheStandardPoolIsOuAndUu()
        {
            Assert.True(TierFilter.Standard.Allows("gen9ou"));
            Assert.True(TierFilter.Standard.Allows("gen7uu"));

            Assert.False(TierFilter.Standard.Allows("gen9ubers"));
            Assert.False(TierFilter.Standard.Allows("gen8ru"));
            Assert.False(TierFilter.Standard.Allows("gen9nu"));
        }

        [Fact]
        public void AnEmptyFilterMeansNoRestrictionRatherThanNothingAllowed()
        {
            // The opposite reading would make every team empty, silently.
            Assert.True(TierFilter.All.IsUnrestricted);
            Assert.True(TierFilter.All.Allows("gen9ubers"));
            Assert.True(TierFilter.All.Allows("anything at all"));
            Assert.True(TierFilter.All.Allows(null));
        }

        [Fact]
        public void TheRestrictedFiltersAreRestricted()
        {
            Assert.False(TierFilter.Standard.IsUnrestricted);
            Assert.False(TierFilter.OverUsedOnly.IsUnrestricted);
            Assert.False(TierFilter.NoUbers.IsUnrestricted);
        }

        [Fact]
        public void NoUbersIsExactlyThat()
        {
            foreach (string tier in new[] { "ou", "uu", "ru", "nu" })
                Assert.True(TierFilter.NoUbers.Allows("gen9" + tier), tier);

            Assert.False(TierFilter.NoUbers.Allows("gen9ubers"));
        }

        [Fact]
        public void TheChoicesOfferTheDefaultFirstAndEverythingLast()
        {
            Assert.Same(TierFilter.Standard, TierFilter.Choices[0]);
            Assert.Same(TierFilter.All, TierFilter.Choices[^1]);

            // The panel binds a combo-box index straight to this, so the
            // round trip has to hold for every entry.
            for (int i = 0; i < TierFilter.Choices.Length; i++)
                Assert.Equal(i, TierFilter.IndexOf(TierFilter.At(i)));
        }

        [Fact]
        public void AnOutOfRangeIndexFallsBackToTheDefaultRatherThanThrowing()
        {
            Assert.Same(TierFilter.Standard, TierFilter.At(-1));
            Assert.Same(TierFilter.Standard, TierFilter.At(99));
        }

        [Fact]
        public void EveryChoiceHasItsOwnName()
        {
            string[] names = TierFilter.Choices.Select(t => t.Name).ToArray();

            Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        // ==============================================================
        // Against the real data file
        // ==============================================================

        [Fact]
        public void TheStandardPoolIsSmallerThanEverythingAndNotEmpty()
        {
            IReadOnlyList<string> standard = CompetitiveSets.SpeciesIn(TierFilter.Standard);
            IReadOnlyList<string> everything = CompetitiveSets.SpeciesIn(TierFilter.All);

            // Skipped rather than failed when the data file is absent - the
            // engine is meant to run without it (CompetitiveSets says so).
            if (everything.Count == 0)
                return;

            Assert.NotEmpty(standard);
            Assert.True(standard.Count < everything.Count,
                $"{standard.Count} standard vs {everything.Count} total");
        }

        [Fact]
        public void APoolIsSortedSoASeedStillReproducesItsTeam()
        {
            IReadOnlyList<string> pool = CompetitiveSets.SpeciesIn(TierFilter.Standard);

            if (pool.Count == 0)
                return;

            Assert.Equal(
                pool.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                pool.ToList());
        }

        [Fact]
        public void AnUbersOnlySpeciesIsNotInTheStandardPool()
        {
            // The whole point, by name.
            IReadOnlyList<string> everything = CompetitiveSets.SpeciesIn(TierFilter.All);

            if (!everything.Contains("Rayquaza", StringComparer.OrdinalIgnoreCase))
                return;

            Assert.DoesNotContain(
                CompetitiveSets.SpeciesIn(TierFilter.Standard),
                n => string.Equals(n, "Rayquaza", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void AskingForAnUbersOnlySpeciesUnderTheStandardFilterFindsNothing()
        {
            if (!CompetitiveSets.TryGet("Rayquaza", TierFilter.All, out _))
                return;

            Assert.False(CompetitiveSets.TryGet("Rayquaza", TierFilter.Standard, out _));
        }

        [Fact]
        public void EverySetHandedBackUnderAFilterBelongsToIt()
        {
            IReadOnlyList<string> pool = CompetitiveSets.SpeciesIn(TierFilter.Standard);

            if (pool.Count == 0)
                return;

            foreach (string species in pool.Take(40))
            {
                Assert.True(CompetitiveSets.TryGet(species, TierFilter.Standard,
                    out IReadOnlyList<CompetitiveSet> sets));

                foreach (CompetitiveSet set in sets)
                    Assert.True(TierFilter.Standard.Allows(set.Format), species + ": " + set.Format);
            }
        }
    }
}
