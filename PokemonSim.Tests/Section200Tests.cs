using System.Collections.Generic;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 200. A custom opponent slot can name the exact FORM it
    /// fields, by the national-dex id the sprite library names its files
    /// with.
    ///
    /// A name is ambiguous where a number is not. "Typhlosion" resolves to
    /// the Johto one, and the Hisuian form has no spelling every roster
    /// agrees on, so a boss meant to field it quietly fielded the ordinary
    /// one. The Boss Database has carried dexNumber on every Pokemon since
    /// it was written - BossPokemonData has the field - and until section
    /// 200 it only ever reached the boss card's roster picture, never the
    /// battle.
    ///
    /// So the id rides the same road section 199's sprite path does: file,
    /// plan, built Pokemon, and out to whatever draws the battle. It loses
    /// to a sprite path, which is an author naming one exact file, and it
    /// beats the species name, which is a guess.
    ///
    /// The field is nullable on purpose. A plain int would stamp
    /// "dexNumber": 0 onto every Pokemon of every roster that was ever
    /// opened and saved, and these are files people hand-edit.
    /// </summary>
    public class Section200Tests
    {
        static Section200Tests()
        {
            MoveDex.EnsureLoaded();
        }

        const string HisuiJson = """
        {
          "name": "Hisui Team",
          "level": 100,
          "team": [
            {
              "species": "Typhlosion",
              "dexNumber": 10233,
              "moves": ["Flamethrower"]
            },
            {
              "species": "Snorlax",
              "moves": ["Body Slam"]
            }
          ]
        }
        """;

        static OpponentSlotPlan Slot(string species, int dex) => new()
        {
            SpeciesName = species,
            NatureName = "Adamant",
            AbilityName = "Intimidate",
            ItemName = "None",
            MoveNames = new List<string> { "Body Slam" },
            DexNumber = dex
        };

        // ---------------- the format ----------------

        [Fact]
        public void TheDexNumberRoundTripsThroughTheFormat()
        {
            CustomOpponentTeam team = CustomOpponents.Parse(HisuiJson);

            Assert.Equal(10233, team.Team[0].DexNumber);
            Assert.Null(team.Team[1].DexNumber);

            CustomOpponentTeam again = CustomOpponents.Parse(CustomOpponents.Serialize(team));

            Assert.Equal(10233, again.Team[0].DexNumber);
            Assert.Null(again.Team[1].DexNumber);
        }

        [Fact]
        public void ASlotThatNamesNoFormIsWrittenBackWithoutTheField()
        {
            // These are files people hand-edit. A roster opened and saved
            // must not grow a "dexNumber": 0 on every Pokemon in it.
            string json = CustomOpponents.Serialize(CustomOpponents.Parse(HisuiJson));

            Assert.Equal(1, json.Split("\"dexNumber\"").Length - 1);
            Assert.Contains("10233", json);
        }

        [Fact]
        public void AZeroOrNegativeIdIsTreatedAsUnset()
        {
            foreach (int nonsense in new[] { 0, -1, -10233 })
            {
                var team = new CustomOpponentTeam
                {
                    Name = "Nonsense",
                    Team = { new CustomOpponentPokemon { Species = "Snorlax", DexNumber = nonsense } }
                };

                Assert.Equal(0, CustomOpponents.ToPlans(team)[0].DexNumber);
            }
        }

        [Fact]
        public void ToPlansCarriesTheDexNumberAndLeavesTheOtherSlotAlone()
        {
            List<OpponentSlotPlan> plans = CustomOpponents.ToPlans(CustomOpponents.Parse(HisuiJson));

            Assert.Equal(2, plans.Count);
            Assert.Equal(10233, plans[0].DexNumber);
            Assert.Equal(0, plans[1].DexNumber);
        }

        // ---------------- onto the Pokemon ----------------

        [Fact]
        public void TheBuiltPokemonCarriesItsSlotsDexNumber()
        {
            OpponentTeamResult result = OpponentTeams.Build(
                new[] { Slot("Typhlosion", 10233), Slot("Snorlax", 0) },
                new StubSpeciesSource("Typhlosion", "Snorlax"),
                new BattleRng(200));

            Assert.True(result.Ok);
            Assert.Equal(10233, result.Team[0].DexNumber);
            Assert.Equal(0, result.Team[1].DexNumber);
        }

        [Fact]
        public void ASkippedSlotDoesNotHandItsNeighboursFormToTheWrongPokemon()
        {
            // The same hazard section 199's sprite path has: a species this
            // catalog does not have is dropped, and from there the roster's
            // slots and the built team have stopped lining up.
            OpponentTeamResult result = OpponentTeams.Build(
                new[] { Slot("Munchlax", 446), Slot("Typhlosion", 10233) },
                new StubSpeciesSource("Typhlosion"),
                new BattleRng(200));

            Assert.True(result.Ok);
            Assert.Single(result.Team);
            Assert.Equal("Typhlosion", result.Team[0].Species);
            Assert.Equal(10233, result.Team[0].DexNumber);
        }

        [Fact]
        public void TheDexNumberClones()
        {
            OpponentTeamResult result = OpponentTeams.Build(
                new[] { Slot("Typhlosion", 10233) },
                new StubSpeciesSource("Typhlosion"),
                new BattleRng(200));

            PokemonState copy = result.Team[0].Clone();

            Assert.Equal(10233, copy.DexNumber);
        }
    }
}