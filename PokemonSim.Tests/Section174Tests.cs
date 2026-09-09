using System.Collections.Generic;
using System.Linq;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>Section 174: EVs take any value per stat, not only 252.
    /// The file format always allowed it; these pin the round trip and
    /// the rule that decides which of the two forms gets written back.</summary>
    public class Section174Tests
    {
        [Fact]
        public void AnArbitrarySpreadSurvivesItsJson()
        {
            const string json = """
            {
              "name": "Bulky Rotom",
              "team": [
                {
                  "species": "Rotom",
                  "evs": [100, 0, 0, 152, 0, 252]
                }
              ]
            }
            """;

            CustomOpponentTeam team = CustomOpponents.Parse(json);

            Assert.Equal(new[] { 100, 0, 0, 152, 0, 252 }, CustomOpponents.ToPlans(team)[0].Evs);

            CustomOpponentTeam again = CustomOpponents.Parse(CustomOpponents.Serialize(team));

            Assert.Equal(new[] { 100, 0, 0, 152, 0, 252 }, CustomOpponents.ToPlans(again)[0].Evs);
        }

        [Fact]
        public void OddValuesAreKeptExactly_NotRoundedToTheGamesSteps()
        {
            var mon = new CustomOpponentPokemon
            {
                Species = "Rotom",
                Evs = new List<int> { 1, 7, 63, 99, 251, 5 }
            };

            Assert.Equal(new[] { 1, 7, 63, 99, 251, 5 }, CustomOpponents.ResolveEvs(mon));
        }

        [Fact]
        public void EvsClampToTheSection164Ceiling_NotTo252()
        {
            // PRO's hard bosses really do put 400 in a stat (§164), so 252
            // is not the ceiling here - 512 is, and it matches the factory.
            var mon = new CustomOpponentPokemon
            {
                Species = "Snorlax",
                Evs = new List<int> { 400, 600, -5, 0, 0, 0 }
            };

            Assert.Equal(new[] { 400, 512, 0, 0, 0, 0 }, CustomOpponents.ResolveEvs(mon));
        }

        [Fact]
        public void AsMaxEvNames_RecognisesTheTidyShorthand()
        {
            Assert.Equal(
                new[] { "hp", "spDefense" },
                CustomOpponents.AsMaxEvNames(new[] { 252, 0, 0, 0, 252, 0 }));

            Assert.Equal(new string[0], CustomOpponents.AsMaxEvNames(new[] { 0, 0, 0, 0, 0, 0 }));
        }

        [Fact]
        public void AsMaxEvNames_RefusesAnythingThatIsNotAll252sAndZeroes()
        {
            Assert.Null(CustomOpponents.AsMaxEvNames(new[] { 100, 0, 0, 152, 0, 252 }));
            Assert.Null(CustomOpponents.AsMaxEvNames(new[] { 4, 252, 0, 0, 0, 252 }));
            Assert.Null(CustomOpponents.AsMaxEvNames(new[] { 252, 0, 0 }));
        }

        [Fact]
        public void TheNamesComeBackInStatOrder()
        {
            // Not the order anything happened to be entered in - the file
            // should read the same however it was authored.
            Assert.Equal(
                new[] { "hp", "attack", "defense", "spAttack", "spDefense", "speed" },
                CustomOpponents.AsMaxEvNames(new[] { 252, 252, 252, 252, 252, 252 }));
        }

        [Fact]
        public void BothFormsProduceTheSamePlanWhenTheyMeanTheSameThing()
        {
            var shorthand = new CustomOpponentPokemon
            {
                Species = "Toxapex",
                MaxEvs = new List<string> { "hp", "spDefense" }
            };

            var explicitly = new CustomOpponentPokemon
            {
                Species = "Toxapex",
                Evs = new List<int> { 252, 0, 0, 0, 252, 0 }
            };

            Assert.Equal(CustomOpponents.ResolveEvs(shorthand), CustomOpponents.ResolveEvs(explicitly));
        }
    }
}