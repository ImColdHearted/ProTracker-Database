using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>Section 155. The computer opponents: the preserved random
    /// baseline and the completed Monte Carlo strategy. Everything here is
    /// hand-built TestKit data - no files - so each test pins exactly the
    /// behaviour it names.</summary>
    public class StrategyTests
    {
        static (BattleState State, BattleEngine Engine) TwoVsTwo(int seed)
        {
            var a1 = TestKit.Mon("Alpha", PokemonType.Normal, hp: 220, moves: TestKit.Move("Hit A", power: 60));
            var a2 = TestKit.Mon("Beta", PokemonType.Normal, hp: 220, moves: TestKit.Move("Hit B", power: 60));
            var b1 = TestKit.Mon("Gamma", PokemonType.Normal, hp: 220, moves: TestKit.Move("Hit C", power: 60));
            var b2 = TestKit.Mon("Delta", PokemonType.Normal, hp: 220, moves: TestKit.Move("Hit D", power: 60));

            return TestKit.Battle(seed,
                new List<PokemonState> { a1, a2 },
                new List<PokemonState> { b1, b2 });
        }

        /// <summary>Everything that matters about the visible battle, plus
        /// a peek at the rng position (via Clone, which does not draw).</summary>
        static string Fingerprint(BattleState state)
        {
            var parts = new List<string>
            {
                "turn=" + state.TurnNumber,
                "rng=" + state.Rng.Clone().Next(1000000),
                "log=" + state.Log.Lines.Count,
                "weather=" + state.Environment.Weather + "/" + state.Environment.WeatherTurns
            };

            foreach (var player in new[] { state.Player1, state.Player2 })
            {
                foreach (var p in player.Team)
                {
                    parts.Add($"{p.Species}|{p.CurrentHP}|{p.Status}|{p.AttackStage}|{p.SpeedStage}|" +
                              string.Join(",", p.Moves.Select(m => m.Name + ":" + m.CurrentPP)));
                }
            }

            return string.Join(";", parts);
        }

        [Fact]
        public void RandomStrategy_OnlyEverPicksLegalActions()
        {
            var (state, engine) = TwoVsTwo(seed: 41);
            var strategy = new RandomMoveStrategy();

            for (int i = 0; i < 60 && state.Outcome == BattleOutcome.Unfinished; i++)
            {
                var legal1 = engine.GetLegalActions(state.Player1);
                var legal2 = engine.GetLegalActions(state.Player2);

                var pick1 = strategy.ChooseAction(state, state.Player1, legal1);
                var pick2 = strategy.ChooseAction(state, state.Player2, legal2);

                Assert.Contains(pick1, legal1);
                Assert.Contains(pick2, legal2);

                engine.RunTurn(pick1, pick2);
                ReplaceEveryFaint(state, engine, strategy);
            }
        }

        [Fact]
        public void MonteCarlo_OnlyEverPicksLegalActions()
        {
            var (state, engine) = TwoVsTwo(seed: 42);
            var mc = new MonteCarloStrategy(new MonteCarloConfig { SimulationsPerAction = 4, MaxRolloutTurns = 8 }, seed: 7);
            var random = new RandomMoveStrategy();

            for (int i = 0; i < 12 && state.Outcome == BattleOutcome.Unfinished; i++)
            {
                var legal1 = engine.GetLegalActions(state.Player1);
                var legal2 = engine.GetLegalActions(state.Player2);

                var pick1 = random.ChooseAction(state, state.Player1, legal1);
                var pick2 = mc.ChooseAction(state, state.Player2, legal2);

                Assert.Contains(pick2, legal2);

                engine.RunTurn(pick1, pick2);
                ReplaceEveryFaint(state, engine, random);
            }
        }

        [Fact]
        public void MonteCarlo_PicksTheObviousKill()
        {
            // A one-shot kill against a nearly dead target: any rollout that
            // opens with the big move wins immediately, so it must dominate.
            for (int seed = 1; seed <= 3; seed++)
            {
                var me = TestKit.Mon("Chooser", hp: 300, speed: 200, moves: new[]
                {
                    TestKit.Move("Feeble", power: 1),
                    TestKit.Move("Finisher", power: 250)
                });
                var foe = TestKit.Mon("Target", hp: 40, speed: 1,
                    moves: TestKit.Move("Retaliate", power: 120));

                var (state, engine) = TestKit.Duel(seed, me, foe);

                var mc = new MonteCarloStrategy(
                    new MonteCarloConfig { SimulationsPerAction = 12, MaxRolloutTurns = 6 }, seed: 100 + seed);

                var legal = engine.GetLegalActions(state.Player1);
                var pick = mc.ChooseAction(state, state.Player1, legal);

                Assert.NotNull(pick.Move);
                Assert.Equal("Finisher", pick.Move!.Name);
            }
        }

        [Fact]
        public void MonteCarlo_NeverTouchesTheVisibleBattle()
        {
            var (state, engine) = TwoVsTwo(seed: 43);
            string before = Fingerprint(state);

            var mc = new MonteCarloStrategy(new MonteCarloConfig { SimulationsPerAction = 10, MaxRolloutTurns = 12 }, seed: 9);
            mc.ChooseAction(state, state.Player1, engine.GetLegalActions(state.Player1));

            Assert.Equal(before, Fingerprint(state));
        }

        [Fact]
        public void MonteCarlo_IsDeterministicForASeed()
        {
            var (stateA, engineA) = TwoVsTwo(seed: 44);
            var (stateB, engineB) = TwoVsTwo(seed: 44);

            var mcA = new MonteCarloStrategy(new MonteCarloConfig { SimulationsPerAction = 8 }, seed: 555);
            var mcB = new MonteCarloStrategy(new MonteCarloConfig { SimulationsPerAction = 8 }, seed: 555);

            var pickA = mcA.ChooseAction(stateA, stateA.Player1, engineA.GetLegalActions(stateA.Player1));
            var pickB = mcB.ChooseAction(stateB, stateB.Player1, engineB.GetLegalActions(stateB.Player1));

            Assert.Equal(Describe(pickA, stateA.Player1), Describe(pickB, stateB.Player1));
        }

        [Fact]
        public void MonteCarlo_HonorsCancellationImmediately()
        {
            var (state, engine) = TwoVsTwo(seed: 45);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var mc = new MonteCarloStrategy(new MonteCarloConfig { SimulationsPerAction = 1000000 }, seed: 5)
            {
                CancellationToken = cts.Token
            };

            var stopwatch = Stopwatch.StartNew();
            var legal = engine.GetLegalActions(state.Player1);
            var pick = mc.ChooseAction(state, state.Player1, legal);
            stopwatch.Stop();

            Assert.Contains(pick, legal);
            Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"took {stopwatch.ElapsedMilliseconds} ms after cancellation");
        }

        [Fact]
        public void MonteCarlo_RespectsItsTimeBudget()
        {
            var (state, engine) = TwoVsTwo(seed: 46);

            var mc = new MonteCarloStrategy(
                new MonteCarloConfig { SimulationsPerAction = 1000000, MaxDecisionMilliseconds = 60 }, seed: 6);

            var stopwatch = Stopwatch.StartNew();
            var legal = engine.GetLegalActions(state.Player1);
            var pick = mc.ChooseAction(state, state.Player1, legal);
            stopwatch.Stop();

            Assert.Contains(pick, legal);
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"took {stopwatch.ElapsedMilliseconds} ms against a 60 ms budget");
        }

        [Fact]
        public void MonteCarlo_RolloutsTerminateWhenNobodyCanWin()
        {
            // Zero-power moves: no rollout can ever end by knockout, so only
            // the rollout turn cap brings the evaluation home.
            var a = TestKit.Mon("Stall A", hp: 200, moves: TestKit.Move("Nothing A", power: 0));
            var b = TestKit.Mon("Stall B", hp: 200, moves: TestKit.Move("Nothing B", power: 0));

            var (state, engine) = TestKit.Duel(47, a, b);

            var mc = new MonteCarloStrategy(
                new MonteCarloConfig { SimulationsPerAction = 6, MaxRolloutTurns = 10 }, seed: 8);

            var stopwatch = Stopwatch.StartNew();
            var pick = mc.ChooseAction(state, state.Player1, engine.GetLegalActions(state.Player1));
            stopwatch.Stop();

            Assert.NotNull(pick);
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"rollouts did not terminate promptly ({stopwatch.ElapsedMilliseconds} ms)");
        }

        [Fact]
        public void MonteCarlo_DoesNotSwitchForever()
        {
            // Both sides could switch every turn; the switch penalties must
            // keep a Monte Carlo mirror match converging on real attacks so
            // the battle actually ends.
            var (state, engine) = TwoVsTwo(seed: 48);

            var outcome = engine.RunBattle(
                new MonteCarloStrategy(new MonteCarloConfig { SimulationsPerAction = 4, MaxRolloutTurns = 8 }, seed: 21),
                new MonteCarloStrategy(new MonteCarloConfig { SimulationsPerAction = 4, MaxRolloutTurns = 8 }, seed: 22));

            Assert.NotEqual(BattleOutcome.Unfinished, outcome);
            Assert.NotEqual(BattleOutcome.Draw, outcome);
            Assert.True(state.TurnNumber < 150, $"the mirror match dragged {state.TurnNumber} turns - overswitching");
        }

        [Fact]
        public void MonteCarlo_HandlesForcedReplacement()
        {
            var (state, engine) = TwoVsTwo(seed: 49);

            state.Player1.ActivePokemon.CurrentHP = 0;
            Assert.True(engine.NeedsReplacement(state.Player1));

            var mc = new MonteCarloStrategy(new MonteCarloConfig { SimulationsPerAction = 4, MaxRolloutTurns = 6 }, seed: 11);

            var candidates = new List<int>();
            for (int i = 0; i < state.Player1.Team.Count; i++)
            {
                if (!state.Player1.Team[i].Fainted)
                    candidates.Add(i);
            }

            int pick = mc.ChooseReplacement(state, state.Player1, candidates);

            Assert.Contains(pick, candidates);
            Assert.False(state.Player1.Team[pick].Fainted);
        }

        [Fact]
        public void MonteCarlo_RespectsPpAndStruggles()
        {
            var me = TestKit.Mon("Empty", moves: TestKit.Move("Last Gasp", power: 40, pp: 1));
            var foe = TestKit.Mon("Wall", hp: 500, moves: TestKit.Move("Wall Hit", power: 10));

            var (state, engine) = TestKit.Duel(50, me, foe);

            me.Moves[0].CurrentPP = 0;

            var legal = engine.GetLegalActions(state.Player1);
            Assert.All(legal.Where(a => a.Move != null), a => Assert.Equal("Struggle", a.Move!.Name));

            var mc = new MonteCarloStrategy(new MonteCarloConfig { SimulationsPerAction = 4 }, seed: 12);
            var pick = mc.ChooseAction(state, state.Player1, legal);

            Assert.Contains(pick, legal);
        }

        [Fact]
        public async Task MonteCarlo_ParallelDecisionsOnSeparateBattlesAreSafe()
        {
            var (stateA, engineA) = TwoVsTwo(seed: 51);
            var (stateB, engineB) = TwoVsTwo(seed: 52);

            string beforeA = Fingerprint(stateA);
            string beforeB = Fingerprint(stateB);

            var legalA = engineA.GetLegalActions(stateA.Player1);
            var legalB = engineB.GetLegalActions(stateB.Player1);

            var taskA = Task.Run(() =>
                new MonteCarloStrategy(new MonteCarloConfig { SimulationsPerAction = 8 }, seed: 31)
                    .ChooseAction(stateA, stateA.Player1, legalA));

            var taskB = Task.Run(() =>
                new MonteCarloStrategy(new MonteCarloConfig { SimulationsPerAction = 8 }, seed: 32)
                    .ChooseAction(stateB, stateB.Player1, legalB));

            var picks = await Task.WhenAll(taskA, taskB);

            Assert.Contains(picks[0], legalA);
            Assert.Contains(picks[1], legalB);
            Assert.Equal(beforeA, Fingerprint(stateA));
            Assert.Equal(beforeB, Fingerprint(stateB));
        }

        [Fact]
        public void MonteCarlo_ZeroBudgetStillReturnsALegalFallback()
        {
            var (state, engine) = TwoVsTwo(seed: 53);

            var mc = new MonteCarloStrategy(
                new MonteCarloConfig { SimulationsPerAction = 50, MaxDecisionMilliseconds = 0 }, seed: 13);

            var legal = engine.GetLegalActions(state.Player1);
            var pick = mc.ChooseAction(state, state.Player1, legal);

            Assert.Contains(pick, legal);
            Assert.Equal(BattleActionType.Move, pick.Type);
        }

        [Fact]
        public async Task Session_RunsAWholeBattleAgainstMonteCarlo()
        {
            var playerTeam = new List<PokemonState>
            {
                TestKit.Mon("Mine", hp: 260, moves: TestKit.Move("My Hit", power: 70))
            };
            var bossTeam = new List<PokemonState>
            {
                TestKit.Mon("Boss One", hp: 240, moves: TestKit.Move("Boss Hit A", power: 60)),
                TestKit.Mon("Boss Two", hp: 240, moves: TestKit.Move("Boss Hit B", power: 60))
            };

            var mc = new MonteCarloStrategy(new MonteCarloConfig { SimulationsPerAction = 3, MaxRolloutTurns = 6 }, seed: 61);
            var session = new SimulatorSession("You", playerTeam, "Boss", bossTeam, mc, seed: 99);

            for (int i = 0; i < 60 && session.Outcome == BattleOutcome.Unfinished; i++)
            {
                if (session.PlayerMustReplace)
                {
                    var choices = session.PlayerReplacementChoices();
                    if (choices.Count == 0) break;
                    session.ReplacePlayerPokemon(choices[0]);
                    continue;
                }

                var legal = session.PlayerLegalActions();
                var move = legal.FirstOrDefault(a => a.Move != null) ?? legal[0];
                await session.PlayTurnAsync(move);
            }

            Assert.NotEqual(BattleOutcome.Unfinished, session.Outcome);
        }

        [Fact]
        public void BattleSimulator_CountsTurnsAndAcceptsStrategies()
        {
            var (template, _) = TwoVsTwo(seed: 60);

            SimulationResult result = BattleSimulator.Run(
                template, simulations: 6, baseSeed: 60,
                player2Strategy: i => new MonteCarloStrategy(
                    new MonteCarloConfig { SimulationsPerAction = 2, MaxRolloutTurns = 5 }, seed: 70 + i));

            Assert.Equal(6, result.Simulations);
            Assert.Equal(6, result.Player1Wins + result.Player2Wins + result.Draws + result.Cancelled);
            Assert.True(result.TotalTurns > 0);
            Assert.True(result.AverageTurns > 0);
        }

        static void ReplaceEveryFaint(BattleState state, BattleEngine engine, IBattleStrategy strategy)
        {
            foreach (var player in new[] { state.Player1, state.Player2 })
            {
                while (engine.NeedsReplacement(player))
                {
                    var candidates = new List<int>();

                    for (int i = 0; i < player.Team.Count; i++)
                    {
                        if (!player.Team[i].Fainted)
                            candidates.Add(i);
                    }

                    if (candidates.Count == 0)
                        break;

                    engine.Replace(player, player.Team[strategy.ChooseReplacement(state, player, candidates)]);
                }
            }
        }

        static string Describe(BattleAction action, PlayerState owner) =>
            action.Type == BattleActionType.Move
                ? "move:" + action.Move?.Name
                : "switch:" + owner.Team.IndexOf(action.SwitchTarget!);
    }
}