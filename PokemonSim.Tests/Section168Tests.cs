using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>Section 168: the admin Battle Lab's engine half - the
    /// random-team factory extracted from the Simulator, and the bulk
    /// harness's per-battle state factory with completion reporting.</summary>
    public class Section168Tests
    {
        static string Signature(TeamBuildResult team) =>
            string.Join(";", team.Team.Select(p =>
                $"{p.Species}|{p.Nature}|{string.Join(",", p.Moves.Select(m => m.Name))}"));

        [Fact]
        public void RandomTeams_AreDeterministicPerSeed()
        {
            MoveDex.EnsureLoaded();
            var source = new PokemonDexSpeciesSource();

            TeamBuildResult first = RandomTeams.Build(source, 3, seed: 168);
            TeamBuildResult again = RandomTeams.Build(source, 3, seed: 168);

            Assert.True(first.Ok, string.Join(" | ", first.Errors));
            Assert.Equal(Signature(first), Signature(again));
        }

        [Fact]
        public void RandomTeams_DifferentSeeds_RollDifferentTeams()
        {
            MoveDex.EnsureLoaded();
            var source = new PokemonDexSpeciesSource();

            string a = Signature(RandomTeams.Build(source, 6, seed: 1));
            string b = Signature(RandomTeams.Build(source, 6, seed: 2));

            Assert.NotEqual(a, b);
        }

        [Fact]
        public void RandomTeams_BuildFullBattleReadyTeams()
        {
            MoveDex.EnsureLoaded();
            var source = new PokemonDexSpeciesSource();

            TeamBuildResult team = RandomTeams.Build(source, 6, seed: 777);

            Assert.True(team.Ok, string.Join(" | ", team.Errors));
            Assert.Equal(6, team.Team.Count);
            Assert.All(team.Team, p =>
            {
                Assert.Equal(100, p.Level);
                Assert.InRange(p.Moves.Count, 1, TeamBuilder.MaxMoves);
                Assert.True(p.MaxHP > 0);
            });
        }

        static BattleState QuickState(int tag)
        {
            var a = Mon($"Alpha{tag}", hp: 120, moves: Move("Hit A", power: 60));
            var b = Mon($"Beta{tag}", hp: 120, moves: Move("Hit B", power: 60));

            return new BattleState
            {
                Player1 = new PlayerState { Name = "P1", Team = new List<PokemonState> { a }, ActivePokemon = a },
                Player2 = new PlayerState { Name = "P2", Team = new List<PokemonState> { b }, ActivePokemon = b },
                Rng = new BattleRng(tag)
            };
        }

        [Fact]
        public void FactoryOverload_BuildsAFreshStatePerBattle()
        {
            var seen = new ConcurrentBag<int>();

            SimulationResult result = BattleSimulator.Run(
                stateFactory: i => { seen.Add(i); return QuickState(i); },
                simulations: 4, baseSeed: 168);

            Assert.Equal(new[] { 0, 1, 2, 3 }, seen.OrderBy(i => i).ToArray());
            Assert.Equal(4, result.Player1Wins + result.Player2Wins + result.Draws + result.Cancelled);
            Assert.Equal(0, result.Cancelled);
        }

        [Fact]
        public void FactoryOverload_ReportsEveryCompletion()
        {
            var completions = new ConcurrentBag<(int Index, BattleOutcome Outcome)>();

            BattleSimulator.Run(
                stateFactory: QuickState,
                simulations: 5, baseSeed: 999,
                onBattleCompleted: (i, outcome) => completions.Add((i, outcome)));

            Assert.Equal(new[] { 0, 1, 2, 3, 4 },
                completions.Select(c => c.Index).OrderBy(i => i).ToArray());
            Assert.All(completions, c => Assert.NotEqual(BattleOutcome.Cancelled, c.Outcome));
        }
    }
}