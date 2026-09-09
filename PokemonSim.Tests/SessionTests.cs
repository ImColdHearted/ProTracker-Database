using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 154. The Simulator window is a thin binding layer over
    /// SimulatorSession, so opening/closing and isolation are tested here,
    /// headlessly, where they can actually run - a real windowed test
    /// would need the tracker's Avalonia shell booted inside the test
    /// host. The strongest isolation guarantee is structural and pinned
    /// below: the engine assembly does not reference the tracker (or any
    /// UI) at all, so simulator battles CANNOT touch encounter counts,
    /// hunting time, catch logs, boss cooldowns or any other live data.
    /// </summary>
    public class SessionTests
    {
        static SimulatorSession NewSession(int seed = 1234)
        {
            var playerTeam = new List<PokemonState>
            {
                Mon("Player Lead", moves: new[] { Move("Jab", power: 40), Move("Rest Day", category: MoveCategory.Status, power: 0) }),
                Mon("Player Backup", moves: Move("Jab", power: 40))
            };

            var opponentTeam = new List<PokemonState>
            {
                Mon("Rival Lead", moves: Move("Bite", power: 40)),
                Mon("Rival Backup", moves: Move("Bite", power: 40))
            };

            return new SimulatorSession("You", playerTeam, "Baseline AI", opponentTeam, seed: seed);
        }

        [Fact]
        public async Task Session_OpensPlaysAndCloses()
        {
            var session = NewSession();

            Assert.Equal(BattleOutcome.Unfinished, session.Outcome);
            Assert.NotEmpty(session.State.Log.Lines);   // "Battle started..."

            var legal = session.PlayerLegalActions();
            Assert.Contains(legal, a => a.Move != null);

            await session.PlayTurnAsync(legal.First(a => a.Move != null));

            Assert.True(session.State.TurnNumber >= 1);

            session.Cancel();

            Assert.Equal(BattleOutcome.Cancelled, session.Outcome);
        }

        [Fact]
        public async Task Session_CancellationTokenCancelsTheBattle()
        {
            var session = NewSession();

            using var source = new CancellationTokenSource();
            source.Cancel();

            var legal = session.PlayerLegalActions();
            await session.PlayTurnAsync(legal[0], source.Token);

            Assert.Equal(BattleOutcome.Cancelled, session.Outcome);
        }

        [Fact]
        public async Task Session_PlayerReplacementFlow()
        {
            var session = NewSession(7);

            // Flatten the player's lead so a replacement is demanded.
            session.Player.ActivePokemon.CurrentHP = 1;
            session.Opponent.ActivePokemon.Stats.Attack = 999;
            session.Opponent.ActivePokemon.Stats.Speed = 999;

            var legal = session.PlayerLegalActions();
            await session.PlayTurnAsync(legal.First(a => a.Move != null));

            Assert.True(session.PlayerMustReplace);

            var choices = session.PlayerReplacementChoices();
            Assert.NotEmpty(choices);

            session.ReplacePlayerPokemon(choices[0]);

            Assert.False(session.PlayerMustReplace);
            Assert.Equal("Player Backup", session.Player.ActivePokemon.Species);
        }

        [Fact]
        public async Task Session_RunsAWholeBattleToAnOutcome()
        {
            var session = NewSession(99);
            session.State.MaxTurns = 60;

            int guard = 0;

            while (session.Outcome == BattleOutcome.Unfinished && guard++ < 400)
            {
                if (session.PlayerMustReplace)
                {
                    session.ReplacePlayerPokemon(session.PlayerReplacementChoices()[0]);
                    continue;
                }

                var legal = session.PlayerLegalActions();
                var attack = legal.FirstOrDefault(a => a.Move != null) ?? legal[0];

                await session.PlayTurnAsync(attack);
            }

            Assert.NotEqual(BattleOutcome.Unfinished, session.Outcome);
            Assert.True(LogContains(session.State, "wins!") || session.Outcome == BattleOutcome.Draw);
        }

        [Fact]
        public void Isolation_TheEngineCannotReferenceTheTrackerOrAnyUi()
        {
            var referenced = typeof(SimulatorSession).Assembly
                .GetReferencedAssemblies()
                .Select(a => a.Name ?? "")
                .ToList();

            Assert.DoesNotContain(referenced, name =>
                name.Contains("ProTracker", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Foot_Tracker", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Avalonia", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Isolation_ABattleWritesNoFiles()
        {
            string probe = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "protracker-sim-isolation-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(probe);

            string before = System.IO.Directory.GetCurrentDirectory();

            try
            {
                System.IO.Directory.SetCurrentDirectory(probe);

                var session = NewSession(5);
                var legal = session.PlayerLegalActions();

                await session.PlayTurnAsync(legal.First(a => a.Move != null));
                session.Cancel();

                Assert.Empty(System.IO.Directory.GetFileSystemEntries(probe));
            }
            finally
            {
                System.IO.Directory.SetCurrentDirectory(before);
                System.IO.Directory.Delete(probe, recursive: true);
            }
        }
    }
}