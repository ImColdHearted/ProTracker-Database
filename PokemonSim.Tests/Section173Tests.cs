using System.Collections.Generic;
using System.Linq;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>Section 173: the custom opponent format - the EV shorthand
    /// PRO players actually use, the IV defaults, and the round trip
    /// through JSON that the editor and the .json files both depend on.</summary>
    public class Section173Tests
    {
        const string SampleJson = """
        {
          "name": "Rival Team",
          "level": 100,
          "team": [
            {
              "species": "Toxapex",
              "nature": "Sassy",
              "ability": "Regenerator",
              "item": "Black Sludge",
              "moves": ["Recover", "Toxic", "Baneful Bunker", "Infestation"],
              "ivs": [31, 31, 31, 31, 31, 0],
              "maxEvs": ["hp", "spDefense"]
            }
          ]
        }
        """;

        [Fact]
        public void TheSampleFileParsesIntoOnePlayableSlot()
        {
            CustomOpponentTeam team = CustomOpponents.Parse(SampleJson);

            Assert.Equal("Rival Team", team.Name);
            Assert.Equal(100, team.Level);

            List<OpponentSlotPlan> plans = CustomOpponents.ToPlans(team);

            Assert.Single(plans);
            Assert.Equal("Toxapex", plans[0].SpeciesName);
            Assert.Equal("Sassy", plans[0].NatureName);
            Assert.Equal("Regenerator", plans[0].AbilityName);
            Assert.Equal("Black Sludge", plans[0].ItemName);
            Assert.Equal(4, plans[0].MoveNames.Count);
            Assert.Equal(100, plans[0].Level);
        }

        [Fact]
        public void MaxEvs_Puts252InTheNamedStatsAndNothingElsewhere()
        {
            CustomOpponentTeam team = CustomOpponents.Parse(SampleJson);

            // Order is HP, Attack, Defense, SpAttack, SpDefense, Speed.
            Assert.Equal(new[] { 252, 0, 0, 0, 252, 0 }, CustomOpponents.ToPlans(team)[0].Evs);
        }

        [Fact]
        public void Ivs_DefaultTo31AndAreClamped()
        {
            var mon = new CustomOpponentPokemon { Species = "Gengar" };
            Assert.Equal(new[] { 31, 31, 31, 31, 31, 31 }, CustomOpponents.ResolveIvs(mon));

            mon.Ivs = new List<int> { 40, -3, 5 };
            Assert.Equal(new[] { 31, 0, 5, 31, 31, 31 }, CustomOpponents.ResolveIvs(mon));
        }

        [Fact]
        public void SpdMeansSpeed_TheSameWayTheCardImporterReadsIt()
        {
            // The PRO summary card prints SPD for Speed and SPDEF for
            // special defense; the two readers must not disagree.
            Assert.Equal(5, CustomOpponents.StatIndex("spd"));
            Assert.Equal(5, CustomOpponents.StatIndex("Speed"));
            Assert.Equal(4, CustomOpponents.StatIndex("spDef"));
            Assert.Equal(4, CustomOpponents.StatIndex("spDefense"));
            Assert.Equal(3, CustomOpponents.StatIndex("SpA"));
            Assert.Equal(-1, CustomOpponents.StatIndex("wisdom"));

            var mon = new CustomOpponentPokemon
            {
                Species = "Jolteon",
                MaxEvs = new List<string> { "spd", "spa" }
            };

            Assert.Equal(new[] { 0, 0, 0, 252, 0, 252 }, CustomOpponents.ResolveEvs(mon));
        }

        [Fact]
        public void AnExplicitEvListWinsOverTheShorthand()
        {
            var mon = new CustomOpponentPokemon
            {
                Species = "Snorlax",
                MaxEvs = new List<string> { "attack" },
                Evs = new List<int> { 4, 252, 0, 0, 252, 0 }
            };

            Assert.Equal(new[] { 4, 252, 0, 0, 252, 0 }, CustomOpponents.ResolveEvs(mon));
        }

        [Fact]
        public void EmptySlotsAndBlankMovesAreSkipped()
        {
            var team = new CustomOpponentTeam
            {
                Name = "Half Team",
                Team = new List<CustomOpponentPokemon>
                {
                    new() { Species = "Gengar", Moves = new List<string> { "Shadow Ball", "  ", "" } },
                    new() { Species = "   " },
                    new() { Species = "" }
                }
            };

            List<OpponentSlotPlan> plans = CustomOpponents.ToPlans(team);

            Assert.Single(plans);
            Assert.Equal(new[] { "Shadow Ball" }, plans[0].MoveNames);
        }

        [Fact]
        public void ATeamRoundTripsThroughItsOwnJson()
        {
            CustomOpponentTeam original = CustomOpponents.Parse(SampleJson);
            CustomOpponentTeam again = CustomOpponents.Parse(CustomOpponents.Serialize(original));

            Assert.Equal(original.Name, again.Name);
            Assert.Equal(
                CustomOpponents.ToPlans(original)[0].Evs,
                CustomOpponents.ToPlans(again)[0].Evs);
            Assert.Equal(
                CustomOpponents.ToPlans(original)[0].Ivs,
                CustomOpponents.ToPlans(again)[0].Ivs);
        }

        [Fact]
        public void ValidationNamesWhatIsWrongWithoutBlockingTheBattle()
        {
            var team = new CustomOpponentTeam
            {
                Name = "",
                Team = new List<CustomOpponentPokemon>
                {
                    new() { Species = "Gengar", MaxEvs = new List<string> { "wisdom" } }
                }
            };

            List<string> problems = CustomOpponents.Validate(team);

            Assert.Contains(problems, p => p.Contains("no name"));
            Assert.Contains(problems, p => p.Contains("no moves"));
            Assert.Contains(problems, p => p.Contains("wisdom"));

            // Still playable - validation is advice, not a gate.
            Assert.Single(CustomOpponents.ToPlans(team));
        }

        [Fact]
        public void ATeamLongerThanSixIsTrimmed()
        {
            var team = new CustomOpponentTeam { Name = "Too Many" };

            for (int i = 0; i < 8; i++)
                team.Team.Add(new CustomOpponentPokemon { Species = "Rattata" });

            Assert.Equal(CustomOpponents.MaxTeamSize, CustomOpponents.ToPlans(team).Count);
            Assert.Contains(CustomOpponents.Validate(team), p => p.Contains("at most"));
        }
    }
}