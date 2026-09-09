using System.Collections.Generic;
using System.Linq;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 199. A custom opponent slot can name the picture it battles
    /// with.
    ///
    /// A counterpart is a sprite SKIN: a Pinkan Gyarados is a Gyarados to
    /// every rule in this engine - same species, same stats, same
    /// learnset - so there is no species name that reaches the pink
    /// artwork. Section 173's custom opponents are the one place an author
    /// wants to say "that one specifically", and the only way to say it is
    /// to name the file, which is exactly what the counterparts catalog
    /// itself does.
    ///
    /// So "sprite" rides from the .json, through the plan, onto the built
    /// Pokemon, and out to whatever draws the battle - the same journey
    /// section 164's IsShiny makes, and for the same reason: the engine
    /// never reads it.
    ///
    /// The test that matters most is the last but one. A roster slot whose
    /// species this catalog does not have is skipped, so the roster's
    /// slots and the built team stop lining up at that point - which is
    /// why the path is carried on the plan rather than matched up by
    /// index afterwards.
    /// </summary>
    public class Section199Tests
    {
        static Section199Tests()
        {
            MoveDex.EnsureLoaded();
        }

        const string PinkanJson = """
        {
          "name": "Pinkan Island",
          "level": 100,
          "team": [
            {
              "species": "Gyarados",
              "moves": ["Waterfall"],
              "sprite": "SharedPokemonLibrary/Assets/Counterparts/Pinkan/Gyarados.png"
            },
            {
              "species": "Snorlax",
              "moves": ["Body Slam"]
            }
          ]
        }
        """;

        static OpponentSlotPlan Slot(string species, string? sprite) => new()
        {
            SpeciesName = species,
            NatureName = "Adamant",
            AbilityName = "Intimidate",
            ItemName = "None",
            MoveNames = new List<string> { "Body Slam" },
            SpritePath = sprite
        };

        // ---------------- the format ----------------

        [Fact]
        public void TheSpriteFieldRoundTripsThroughTheFormat()
        {
            CustomOpponentTeam team = CustomOpponents.Parse(PinkanJson);

            Assert.Equal(
                "SharedPokemonLibrary/Assets/Counterparts/Pinkan/Gyarados.png",
                team.Team[0].Sprite);

            CustomOpponentTeam again = CustomOpponents.Parse(CustomOpponents.Serialize(team));

            Assert.Equal(team.Team[0].Sprite, again.Team[0].Sprite);
        }

        [Fact]
        public void ASlotThatNamesNoSpriteSaysNothingAtAll()
        {
            CustomOpponentTeam team = CustomOpponents.Parse(PinkanJson);

            Assert.Null(team.Team[1].Sprite);

            // Every roster written before this section says "no sprite" by
            // saying nothing, and one written back has to keep saying it
            // that way rather than growing a null field.
            string json = CustomOpponents.Serialize(team);

            Assert.Equal(1, json.Split("\"sprite\"").Length - 1);
        }

        [Fact]
        public void NormalizeSpritePathTreatsNothingAsNothing()
        {
            Assert.Null(CustomOpponents.NormalizeSpritePath(null));
            Assert.Null(CustomOpponents.NormalizeSpritePath(""));
            Assert.Null(CustomOpponents.NormalizeSpritePath("   "));
            Assert.Equal("a/b.png", CustomOpponents.NormalizeSpritePath("  a/b.png  "));
        }

        [Fact]
        public void APathAuthoredOnWindowsResolvesOnLinux()
        {
            var mon = new CustomOpponentPokemon
            {
                Species = "Gyarados",
                Sprite = @"SharedPokemonLibrary\Assets\Counterparts\Pinkan\Gyarados.png"
            };

            var team = new CustomOpponentTeam { Name = "Backslashes", Team = { mon } };

            Assert.Equal(
                "SharedPokemonLibrary/Assets/Counterparts/Pinkan/Gyarados.png",
                CustomOpponents.ToPlans(team)[0].SpritePath);
        }

        [Fact]
        public void ToPlansCarriesTheSpritePathAndLeavesTheOtherSlotAlone()
        {
            List<OpponentSlotPlan> plans = CustomOpponents.ToPlans(CustomOpponents.Parse(PinkanJson));

            Assert.Equal(2, plans.Count);
            Assert.Equal(
                "SharedPokemonLibrary/Assets/Counterparts/Pinkan/Gyarados.png",
                plans[0].SpritePath);
            Assert.Null(plans[1].SpritePath);
        }

        // ---------------- onto the Pokemon ----------------

        [Fact]
        public void TheBuiltPokemonCarriesItsSlotsSprite()
        {
            OpponentTeamResult result = OpponentTeams.Build(
                new[] { Slot("Snorlax", "art/lax.png"), Slot("Gyarados", null) },
                new StubSpeciesSource("Snorlax", "Gyarados"),
                new BattleRng(199));

            Assert.True(result.Ok);
            Assert.Equal("art/lax.png", result.Team[0].SpritePath);
            Assert.Null(result.Team[1].SpritePath);
        }

        [Fact]
        public void ASkippedSlotDoesNotHandItsNeighboursSpriteToTheWrongPokemon()
        {
            // Munchlax is not in this catalog, so its slot is dropped and
            // the roster's slots and the built team part company. Carrying
            // the path on the plan is what keeps Gyarados's own art with
            // Gyarados instead of shifting up one.
            OpponentTeamResult result = OpponentTeams.Build(
                new[]
                {
                    Slot("Munchlax", "art/munchlax.png"),
                    Slot("Gyarados", "art/gyarados.png")
                },
                new StubSpeciesSource("Gyarados"),
                new BattleRng(199));

            Assert.True(result.Ok);
            Assert.Single(result.Team);
            Assert.Equal("Gyarados", result.Team[0].Species);
            Assert.Equal("art/gyarados.png", result.Team[0].SpritePath);
        }

        [Fact]
        public void AnOrdinaryRosterBuildsPokemonWithNoSpriteAtAll()
        {
            OpponentTeamResult result = OpponentTeams.Build(
                new[] { Slot("Snorlax", null) },
                new StubSpeciesSource("Snorlax"),
                new BattleRng(199));

            Assert.True(result.Ok);
            Assert.Null(result.Team[0].SpritePath);
        }

        [Fact]
        public void TheSpritePathClones()
        {
            OpponentTeamResult result = OpponentTeams.Build(
                new[] { Slot("Snorlax", "art/lax.png") },
                new StubSpeciesSource("Snorlax"),
                new BattleRng(199));

            PokemonState copy = result.Team[0].Clone();

            Assert.Equal("art/lax.png", copy.SpritePath);
        }
    }
}