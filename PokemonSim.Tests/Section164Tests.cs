using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>Section 164: PRO's boss stat standards (the 400-EV hard
    /// tier included) and the cosmetic shiny flag's ride through the
    /// build and battle plumbing.</summary>
    public class Section164Tests
    {
        sealed class OneSpeciesSource : ISpeciesSource
        {
            readonly SpeciesInfo info = new()
            {
                Name = "Toxapex",
                Types = new List<string> { "Poison", "Water" },
                BaseStats = new Stats
                {
                    HP = 50, Attack = 63, Defense = 152,
                    SpAttack = 53, SpDefense = 142, Speed = 35
                },
                LearnsetMoveNames = new List<string> { "Recover" }
            };

            public IReadOnlyList<string> AllSpeciesNames => new List<string> { info.Name };

            public SpeciesInfo? Find(string name) =>
                name.Equals(info.Name, StringComparison.OrdinalIgnoreCase) ? info : null;
        }

        static PokemonSpecies ToxapexSpecies() => new()
        {
            Name = "Toxapex",
            Types = new List<PokemonType> { PokemonType.Poison, PokemonType.Water },
            BaseStats = new Stats
            {
                HP = 50, Attack = 63, Defense = 152,
                SpAttack = 53, SpDefense = 142, Speed = 35
            },
            Learnset = new List<MoveState>(),
            Abilities = new List<string>()
        };

        [Fact]
        public void Factory_TakesTheHardBosses400Evs()
        {
            PokemonState mon = PokemonFactory.Create(
                ToxapexSpecies(), 100, Nature.Hardy, null,
                new List<MoveState> { Move("Poke") },
                ivs: new[] { 31, 31, 31, 31, 31, 31 },
                evs: new[] { 400, 400, 400, 400, 400, 400 });

            Assert.Equal(400, mon.HPEV);
            Assert.Equal(400, mon.SpeedEV);

            // 400 EVs are a flat +100 per stat: HP (2*50+31+100)+110 = 341,
            // Speed ((2*35+31+100)*100/100)+5 = 206 at a neutral nature.
            Assert.Equal(341, mon.MaxHP);
            Assert.Equal(206, mon.Stats.Speed);
        }

        [Fact]
        public void Factory_ClampsRunawayEvsAtTheSanityCeiling()
        {
            PokemonState mon = PokemonFactory.Create(
                ToxapexSpecies(), 100, Nature.Hardy, null,
                new List<MoveState> { Move("Poke") },
                ivs: null,
                evs: new[] { 999, 0, 0, 0, 0, -5 });

            Assert.Equal(512, mon.HPEV);
            Assert.Equal(0, mon.SpeedEV);
        }

        [Fact]
        public void OpponentPlans_CarryTheirSpreadIntoTheBuild()
        {
            MoveDex.EnsureLoaded();

            var plan = new OpponentSlotPlan
            {
                SpeciesName = "Toxapex",
                Level = 100,
                NatureName = "Hardy",
                AbilityName = null,
                MoveNames = { "Recover" },
                Ivs = new[] { 31, 31, 31, 31, 31, 31 },
                Evs = new[] { 400, 400, 400, 400, 400, 400 }
            };

            OpponentTeamResult result = OpponentTeams.Build(
                new[] { plan }, new OneSpeciesSource(), new BattleRng(1));

            Assert.True(result.Ok, string.Join(" | ", result.Errors));
            Assert.Equal(341, result.Team[0].MaxHP);
            Assert.Equal(400, result.Team[0].SpDefenseEV);
        }

        [Fact]
        public void OpponentPlans_WithoutASpread_KeepTheOldDefaults()
        {
            MoveDex.EnsureLoaded();

            var plan = new OpponentSlotPlan
            {
                SpeciesName = "Toxapex",
                Level = 100,
                NatureName = "Hardy",
                MoveNames = { "Recover" }
            };

            OpponentTeamResult result = OpponentTeams.Build(
                new[] { plan }, new OneSpeciesSource(), new BattleRng(1));

            Assert.True(result.Ok);
            Assert.Equal(31, result.Team[0].HPIV);
            Assert.Equal(0, result.Team[0].HPEV);
        }

        [Fact]
        public void TrustedBuild_CarriesTheShinyFlag()
        {
            MoveDex.EnsureLoaded();

            var plan = new TeamSlotPlan
            {
                SpeciesName = "Toxapex",
                Level = 100,
                Nature = Nature.Hardy,
                Trusted = true,
                IsShiny = true
            };

            plan.MoveNames.Add("Recover");

            TeamBuildResult result = TeamBuilder.Build(new[] { plan }, new OneSpeciesSource());

            Assert.True(result.Ok, string.Join(" | ", result.Errors));
            Assert.True(result.Team[0].IsShiny);
        }

        [Fact]
        public void Shiny_SurvivesCloningAndMegaEvolution()
        {
            var gengar = Mon("Gengar", type: PokemonType.Ghost,
                moves: Move("Shade", category: MoveCategory.Status, power: 0));
            gengar.IsShiny = true;
            gengar.HeldItemId = "gengarite";

            Assert.True(gengar.Clone().IsShiny);

            var (state, engine) = Duel(180, gengar,
                Mon("Wall", hp: 400, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            BattleAction action = MoveAction(state, gengar, gengar.Moves[0]);
            action.MegaEvolve = true;

            engine.RunTurn(action,
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.Equal("Mega Gengar", gengar.Species);
            Assert.True(gengar.IsShiny);
        }
    }
}