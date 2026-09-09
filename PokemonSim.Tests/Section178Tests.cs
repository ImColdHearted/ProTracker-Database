using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PokemonSim.Data;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 178. The imported learnsets and competitive sets.
    ///
    /// Section 177 found that 767 of the 803 species in pokedex.json shared
    /// one placeholder learnset - ["Flamethrower", "Earthquake"], for
    /// Blissey and Ferrothorn alike - so 95 percent of every lab battle was
    /// two Pokemon choosing between the same two moves. These tests pin
    /// that the data is genuinely fixed, that a lab opponent is now a real
    /// competitive set rather than a random pair, and that a set naming a
    /// move this engine has not implemented yet loses that move and not the
    /// whole Pokemon.
    /// </summary>
    public class Section178Tests
    {
        // ---------------- the data itself ----------------

        [Fact]
        public void ThePlaceholderLearnsetIsGone()
        {
            MoveDex.EnsureLoaded();
            var source = new PokemonDexSpeciesSource();

            var placeholder = new[] { "Flamethrower", "Earthquake" };
            int stubbed = 0, total = 0;

            foreach (string name in source.AllSpeciesNames)
            {
                SpeciesInfo? info = source.Find(name);

                if (info == null)
                    continue;

                total++;

                if (info.LearnsetMoveNames.Count == placeholder.Length &&
                    placeholder.All(m => info.LearnsetMoveNames.Contains(m, StringComparer.OrdinalIgnoreCase)))
                {
                    stubbed++;
                }
            }

            Assert.True(total > 700, $"only {total} species loaded");

            // It was 767 of 803. Nothing should carry it now.
            Assert.Equal(0, stubbed);
        }

        [Fact]
        public void LearnsetsAreDeepEnoughToChooseFrom()
        {
            MoveDex.EnsureLoaded();
            var source = new PokemonDexSpeciesSource();

            List<int> sizes = source.AllSpeciesNames
                .Select(source.Find)
                .Where(i => i != null)
                .Select(i => TeamBuilder.UsableMoves(i!).Count)
                .OrderBy(n => n)
                .ToList();

            int median = sizes[sizes.Count / 2];

            // The median was 2. Anything in this range means real data.
            Assert.InRange(median, 20, 300);

            // And almost everything can fill four slots.
            Assert.True(sizes.Count(n => n < TeamBuilder.MaxMoves) < 40,
                $"{sizes.Count(n => n < TeamBuilder.MaxMoves)} species still cannot fill four moves");
        }

        [Fact]
        public void TheMisspelledSpeciesAreSpelledCorrectlyNow()
        {
            var source = new PokemonDexSpeciesSource();
            var names = new HashSet<string>(source.AllSpeciesNames, StringComparer.OrdinalIgnoreCase);

            foreach (string fixedName in new[]
            {
                "Girafarig", "Chimecho", "Cherrim", "Hippowdon", "Eelektross",
                "Thundurus", "Palkia: Origin", "Palossand", "Pheromosa",
                "Copperajah", "Espathra"
            })
            {
                Assert.True(names.Contains(fixedName), $"{fixedName} is missing");
            }

            foreach (string typo in new[]
            {
                "Giragarig", "Chimeco", "Cherim", "Hippowdoon", "Elektross",
                "Thunderus", "PalkiaL Origin", "Palosand", "Peromosa",
                "Copperjah", "Espartha"
            })
            {
                Assert.False(names.Contains(typo), $"{typo} is still there");
            }
        }

        // ---------------- the sets ----------------

        [Fact]
        public void TheSetsFileLoadsAndCoversAGoodShareOfTheDex()
        {
            CompetitiveSets.EnsureLoaded();

            Assert.DoesNotContain("could not be read", CompetitiveSets.Status);
            Assert.True(CompetitiveSets.SpeciesCount > 400,
                $"only {CompetitiveSets.SpeciesCount} species have sets - {CompetitiveSets.Status}");
        }

        [Fact]
        public void AKnownSetCarriesItsWholeSpread()
        {
            CompetitiveSets.EnsureLoaded();

            Assert.True(CompetitiveSets.TryGet("Garchomp", out IReadOnlyList<CompetitiveSet> sets));
            Assert.NotEmpty(sets);

            CompetitiveSet set = sets[0];

            Assert.False(string.IsNullOrWhiteSpace(set.Name));
            Assert.Matches("^gen[789]", set.Format);
            Assert.InRange(set.Moves.Count, 1, TeamBuilder.MaxMoves);
            Assert.All(set.Moves, slot => Assert.NotEmpty(slot));

            // Somewhere in Garchomp's sets there is a spread, an item and
            // an ability - that is the whole point of importing them.
            Assert.Contains(sets, s => s.Evs != null && s.Evs.Count == 6);
            Assert.Contains(sets, s => !string.IsNullOrWhiteSpace(s.Item));
            Assert.Contains(sets, s => !string.IsNullOrWhiteSpace(s.Ability));
        }

        [Fact]
        public void SetsAreStoredVerbatimSoImplementingAMoveUpgradesThem()
        {
            CompetitiveSets.EnsureLoaded();
            MoveDex.EnsureLoaded();

            int unimplemented = 0;

            foreach (string species in new PokemonDexSpeciesSource().AllSpeciesNames)
            {
                if (!CompetitiveSets.TryGet(species, out IReadOnlyList<CompetitiveSet> sets))
                    continue;

                foreach (CompetitiveSet set in sets)
                {
                    foreach (List<string> slot in set.Moves)
                    {
                        foreach (string move in slot)
                        {
                            if (!MoveDex.TryGet(move, out _))
                                unimplemented++;
                        }
                    }
                }
            }

            // The names are kept even when the engine cannot run them yet -
            // that is what makes ai-training/missing-moves.md a worklist
            // rather than a record of what was thrown away.
            Assert.True(unimplemented > 0,
                "no unimplemented move names survived the import - the sets were filtered, not stored verbatim");
        }

        // ---------------- turning a set into a Pokemon ----------------

        static CompetitiveSet Set(params string[][] moves) => new()
        {
            Name = "Test",
            Format = "gen9ou",
            Moves = moves.Select(m => m.ToList()).ToList()
        };

        [Fact]
        public void AnUnimplementedMoveCostsItsSlotAndNothingMore()
        {
            MoveDex.EnsureLoaded();

            CompetitiveSet set = Set(
                new[] { "Earthquake" },
                new[] { "Definitely Not A Real Move" },
                new[] { "Stealth Rock" });

            TeamSlotPlan plan = CompetitiveSets.ToPlan("Garchomp", set, 100, new Random(178));

            Assert.Equal(new[] { "Earthquake", "Stealth Rock" }, plan.MoveNames);
            Assert.Equal("Garchomp", plan.SpeciesName);
            Assert.Equal(100, plan.Level);
        }

        [Fact]
        public void AChoiceSlotPicksSomethingTheEngineCanActuallyRun()
        {
            MoveDex.EnsureLoaded();

            CompetitiveSet set = Set(new[] { "Not A Move At All", "Earthquake" });

            for (int seed = 0; seed < 8; seed++)
            {
                TeamSlotPlan plan = CompetitiveSets.ToPlan("Garchomp", set, 100, new Random(seed));
                Assert.Equal(new[] { "Earthquake" }, plan.MoveNames);
            }
        }

        [Fact]
        public void TheSpreadNatureAbilityAndItemAllSurvive()
        {
            MoveDex.EnsureLoaded();

            var set = new CompetitiveSet
            {
                Name = "Swords Dance", Format = "gen9ou",
                Moves = new List<List<string>> { new() { "Earthquake" } },
                Ability = "Rough Skin", Item = "Life Orb", Nature = "Jolly",
                Evs = new List<int> { 0, 252, 0, 0, 4, 252 },
                Ivs = new List<int> { 31, 31, 31, 0, 31, 31 }
            };

            TeamSlotPlan plan = CompetitiveSets.ToPlan("Garchomp", set, 100, new Random(1));

            Assert.Equal("Rough Skin", plan.AbilityName);
            Assert.Equal("Life Orb", plan.ItemName);
            Assert.Equal(Nature.Jolly, plan.Nature);
            Assert.Equal(new[] { 0, 252, 0, 0, 4, 252 }, plan.Evs);
            Assert.Equal(new[] { 31, 31, 31, 0, 31, 31 }, plan.Ivs);

            // §178: authoritative, exactly as an imported card is - without
            // this the learnset and ability gates would drop the Pokemon.
            Assert.True(plan.Trusted);
        }

        [Fact]
        public void AnItemOrAbilityThatIsJustNoneIsLeftOff()
        {
            MoveDex.EnsureLoaded();

            var set = new CompetitiveSet
            {
                Name = "Bare", Format = "gen7ou",
                Moves = new List<List<string>> { new() { "Earthquake" } },
                Ability = "None", Item = "   "
            };

            TeamSlotPlan plan = CompetitiveSets.ToPlan("Garchomp", set, 100, new Random(2));

            Assert.Null(plan.AbilityName);
            Assert.Null(plan.ItemName);
            Assert.Null(plan.Evs);
            Assert.Null(plan.Ivs);
        }

        [Fact]
        public void ASetNeverFillsMoreThanFourSlotsOrRepeatsAMove()
        {
            MoveDex.EnsureLoaded();

            CompetitiveSet set = Set(
                new[] { "Earthquake" }, new[] { "Stealth Rock" }, new[] { "Protect" },
                new[] { "Earthquake" }, new[] { "Substitute" }, new[] { "Toxic" });

            TeamSlotPlan plan = CompetitiveSets.ToPlan("Garchomp", set, 100, new Random(3));

            Assert.InRange(plan.MoveNames.Count, 1, TeamBuilder.MaxMoves);
            Assert.Equal(plan.MoveNames.Count, plan.MoveNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        // ---------------- what the lab now builds ----------------

        [Fact]
        public void LabTeamsAreRealPokemonNowNotTwoMoveStubs()
        {
            MoveDex.EnsureLoaded();
            var source = new PokemonDexSpeciesSource();

            var counts = new List<int>();

            for (int seed = 0; seed < 12; seed++)
            {
                TeamBuildResult team = RandomTeams.Build(source, TeamBuilder.MaxTeamSize, seed);

                Assert.True(team.Ok, string.Join(" | ", team.Errors));
                Assert.Equal(TeamBuilder.MaxTeamSize, team.Team.Count);

                counts.AddRange(team.Team.Select(p => p.Moves.Count));
            }

            double average = counts.Average();

            // It was 2.09 - every lab Pokemon carrying the same two moves.
            Assert.True(average >= 3.0, $"lab Pokemon still average only {average:0.00} moves");
            Assert.True(counts.Count(n => n >= 3) > counts.Count / 2,
                "most lab Pokemon should now have three or more moves");
        }

        [Fact]
        public void LabTeamsAreStillDeterministicPerSeed()
        {
            MoveDex.EnsureLoaded();
            var source = new PokemonDexSpeciesSource();

            static string Signature(TeamBuildResult t) =>
                string.Join(";", t.Team.Select(p =>
                    $"{p.Species}|{p.Nature}|{p.HeldItemId}|{string.Join(",", p.Moves.Select(m => m.Name))}"));

            Assert.Equal(Signature(RandomTeams.Build(source, 6, 178)),
                         Signature(RandomTeams.Build(source, 6, 178)));

            Assert.NotEqual(Signature(RandomTeams.Build(source, 6, 1)),
                            Signature(RandomTeams.Build(source, 6, 2)));
        }

        [Fact]
        public void LabTeamsCarryRealItemsAndSpreadsNow()
        {
            MoveDex.EnsureLoaded();
            var source = new PokemonDexSpeciesSource();

            int withItem = 0, total = 0;

            for (int seed = 20; seed < 32; seed++)
            {
                TeamBuildResult team = RandomTeams.Build(source, TeamBuilder.MaxTeamSize, seed);

                foreach (PokemonState p in team.Team)
                {
                    total++;
                    if (!string.IsNullOrWhiteSpace(p.HeldItemId)) withItem++;
                }
            }

            // Before §178 an item only appeared on the §161 Z-Crystal roll,
            // about one in four. Real sets hold something far more often.
            Assert.True(withItem > total / 3,
                $"only {withItem} of {total} lab Pokemon hold an item");
        }

        [Fact]
        public void TheWorklistShipsBesideTheTrainer()
        {
            // ai-training/missing-moves.md is the ordered list of moves the
            // imported sets need. It is documentation, not build input, so
            // this only asserts it was not lost.
            string? found = null;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            for (int i = 0; i < 8 && dir != null && found == null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "ai-training", "missing-moves.md");

                if (File.Exists(candidate))
                    found = candidate;
            }

            if (found == null)
                return;      // not a source checkout - nothing to check

            string text = File.ReadAllText(found);

            Assert.Contains("missing-moves", found);
            Assert.Contains("| sets | species | move |", text);
        }
    }
}