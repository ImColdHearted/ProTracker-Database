using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 181. Raising the Battle Lab's ceiling from 10,000 battles to
    /// 100,000, and the change that makes that survivable.
    ///
    /// The lab used to collect every battle's observer into one bag and
    /// dispose the lot after the whole run. Each observer holds a bounded
    /// channel, a parked writer task and a StringBuilder still sitting on
    /// the capacity of the last batch it formatted, so holding a hundred
    /// thousand of them for the length of an overnight run is the run's
    /// memory ceiling rather than an untidiness. An observer's work is over
    /// the moment its battle is, so it is now tallied and disposed then.
    ///
    /// LabBattleRunner lives in the tracker project, which these tests
    /// cannot reference (SessionTests pins that isolation deliberately), so
    /// what is pinned here is the TECHNIQUE it now uses and the promises it
    /// depends on: that a battle's counters are final when its battle ends,
    /// that disposing an observer that early loses nothing, and that every
    /// finished battle is already a complete file - which is what makes
    /// cancelling a long run safe.
    /// </summary>
    public class Section181Tests : IDisposable
    {
        readonly string dir = Path.Combine(
            Path.GetTempPath(), "protracker-181-tests", Guid.NewGuid().ToString("N"));

        public Section181Tests() => Directory.CreateDirectory(dir);

        public void Dispose()
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }

        static BattleState LabLikeBattle(int index)
        {
            List<PokemonState> Team(string prefix) => new List<PokemonState>
            {
                TestKit.Mon(prefix + " One",   hp: 120, moves: TestKit.Move(prefix + " Hit A", power: 70)),
                TestKit.Mon(prefix + " Two",   hp: 120, moves: TestKit.Move(prefix + " Hit B", power: 70)),
                TestKit.Mon(prefix + " Three", hp: 120, moves: TestKit.Move(prefix + " Hit C", power: 70))
            };

            var team1 = Team("Mine");
            var team2 = Team("Theirs");

            return new BattleState
            {
                Player1 = new PlayerState { Name = "P1", Team = team1, ActivePokemon = team1[0] },
                Player2 = new PlayerState { Name = "P2", Team = team2, ActivePokemon = team2[0] },
                Rng = new BattleRng(index)
            };
        }

        List<string> LinesOnDisk() =>
            Directory.GetFiles(dir, "obs-*.jsonl")
                .SelectMany(file => File.ReadAllLines(file))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

        /// <summary>The shape LabBattleRunner now uses: create in the
        /// observer factory, tally and dispose in the completion callback,
        /// await the flushes once at the end.</summary>
        async Task<(int Files, long Recorded, long Dropped, long Ranked)> RunRetiringAsYouGo(int battles)
        {
            var live = new ConcurrentDictionary<int, BattleObserver>();
            var flushing = new ConcurrentBag<Task>();

            long recorded = 0, dropped = 0, ranked = 0;
            int files = 0;

            await Task.Run(() => BattleSimulator.Run(
                stateFactory: LabLikeBattle,
                simulations: battles,
                baseSeed: 1810,
                observerFactory: i =>
                {
                    var observer = new BattleObserver(dir, "Lab", 1810 + i);
                    live[i] = observer;
                    return observer;
                },
                onBattleCompleted: (i, _) =>
                {
                    if (!live.TryRemove(i, out BattleObserver? observer))
                        return;

                    Interlocked.Add(ref recorded, observer.RecordedCount);
                    Interlocked.Add(ref dropped, observer.DroppedCount);
                    Interlocked.Add(ref ranked, observer.RankedCount);
                    Interlocked.Increment(ref files);

                    flushing.Add(observer.DisposeAsync().AsTask());
                }));

            Assert.Empty(live);

            await Task.WhenAll(flushing);

            return (files, recorded, dropped, ranked);
        }

        [Fact]
        public async Task RetiringEachObserverAsItsBattleEnds_LosesNoRecords()
        {
            var summary = await RunRetiringAsYouGo(battles: 12);

            List<string> lines = LinesOnDisk();
            int decisions = lines.Count(l => l.Contains("\"Record\":\"decision\"", StringComparison.Ordinal));

            Assert.Equal(12, summary.Files);
            Assert.Equal(0L, summary.Dropped);

            // Every record the observers counted reached the disk. Disposal
            // right after the battle is early, but it is not premature.
            Assert.Equal(summary.Recorded, (long)decisions);
            Assert.True(decisions > 0, "the run recorded nothing at all");
        }

        [Fact]
        public async Task EveryFinishedBattleIsAlreadyACompleteFile()
        {
            // This is what makes cancelling a long run safe, and what makes
            // a hundred thousand battles a decision about disk rather than
            // a decision about risk.
            await RunRetiringAsYouGo(battles: 8);

            string[] files = Directory.GetFiles(dir, "obs-*.jsonl");

            Assert.Equal(8, files.Length);

            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file).Where(l => l.Length > 0).ToArray();

                Assert.NotEmpty(lines);

                JsonElement last = JsonDocument.Parse(lines[^1]).RootElement;

                Assert.Equal("final", last.GetProperty("Record").GetString());
                Assert.True(last.TryGetProperty("DecisionRecords", out _));
            }
        }

        [Fact]
        public async Task ObserverCountersAreFinalWhenItsBattleEnds()
        {
            // The lab reads the counters in the completion callback and then
            // throws the observer away, so a counter that was still moving
            // afterwards would be silently under-reported.
            var (state, engine) = TestKit.Battle(1811,
                new List<PokemonState>
                {
                    TestKit.Mon("A1", hp: 120, moves: TestKit.Move("A Hit", power: 70)),
                    TestKit.Mon("A2", hp: 120, moves: TestKit.Move("A Jab", power: 70))
                },
                new List<PokemonState>
                {
                    TestKit.Mon("B1", hp: 120, moves: TestKit.Move("B Hit", power: 70)),
                    TestKit.Mon("B2", hp: 120, moves: TestKit.Move("B Jab", power: 70))
                });

            var observer = new BattleObserver(dir, "Lab", 1811);

            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);

            int atEnd = observer.RecordedCount;
            int droppedAtEnd = observer.DroppedCount;

            await observer.DisposeAsync();

            Assert.Equal(atEnd, observer.RecordedCount);
            Assert.Equal(droppedAtEnd, observer.DroppedCount);
            Assert.True(atEnd > 0);
        }

        [Fact]
        public async Task DisposingImmediatelyAfterTheBattle_FlushesEveryQueuedLine()
        {
            var (_, engine) = TestKit.Battle(1812,
                new List<PokemonState>
                {
                    TestKit.Mon("Long One", hp: 600, moves: TestKit.Move("Chip A", power: 12)),
                    TestKit.Mon("Long Two", hp: 600, moves: TestKit.Move("Chip B", power: 12))
                },
                new List<PokemonState>
                {
                    TestKit.Mon("Long Three", hp: 600, moves: TestKit.Move("Chip C", power: 12)),
                    TestKit.Mon("Long Four", hp: 600, moves: TestKit.Move("Chip D", power: 12))
                });

            var observer = new BattleObserver(dir, "Lab", 1812);

            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);

            int recordedByObserver = observer.RecordedCount;

            // No pause, no drain loop: exactly what the lab does now.
            await observer.DisposeAsync();

            int onDisk = LinesOnDisk()
                .Count(l => l.Contains("\"Record\":\"decision\"", StringComparison.Ordinal));

            Assert.Equal(recordedByObserver, onDisk);
        }

        [Fact]
        public async Task TheCompletionCallbackFiresExactlyOncePerBattle()
        {
            // The retire step hangs off this callback, so a battle it skipped
            // would leak an observer and lose its counters.
            var seen = new ConcurrentBag<int>();
            var observers = new ConcurrentBag<BattleObserver>();

            await Task.Run(() => BattleSimulator.Run(
                stateFactory: LabLikeBattle,
                simulations: 16,
                baseSeed: 1813,
                observerFactory: i =>
                {
                    var observer = new BattleObserver(dir, "Lab", 1813 + i);
                    observers.Add(observer);
                    return observer;
                },
                onBattleCompleted: (i, _) => seen.Add(i)));

            foreach (BattleObserver observer in observers)
                await observer.DisposeAsync();

            Assert.Equal(16, seen.Count);
            Assert.Equal(Enumerable.Range(0, 16), seen.OrderBy(i => i));
        }

        [Fact]
        public async Task RetiringAsYouGoAgreesWithHoldingThemAll()
        {
            // The old shape and the new one must report the same numbers -
            // this is a refactor of when the counters are read, not of what
            // they mean.
            var newWay = await RunRetiringAsYouGo(battles: 10);

            string[] first = Directory.GetFiles(dir, "obs-*.jsonl");

            foreach (string file in first)
                File.Delete(file);

            var held = new ConcurrentBag<BattleObserver>();

            await Task.Run(() => BattleSimulator.Run(
                stateFactory: LabLikeBattle,
                simulations: 10,
                baseSeed: 1810,
                observerFactory: i =>
                {
                    var observer = new BattleObserver(dir, "Lab", 1810 + i);
                    held.Add(observer);
                    return observer;
                }));

            long recorded = 0;

            foreach (BattleObserver observer in held)
            {
                recorded += observer.RecordedCount;
                await observer.DisposeAsync();
            }

            Assert.Equal(10, held.Count);
            Assert.Equal(newWay.Files, held.Count);
            Assert.Equal(newWay.Recorded, recorded);
        }

        [Fact]
        public async Task NothingIsHeldOpenAfterTheRun()
        {
            // The point of retiring early is that the observer becomes
            // garbage while the run is still going. The observable proof
            // available to a test is that its file is closed and writable
            // the moment the run has awaited its flushes.
            await RunRetiringAsYouGo(battles: 6);

            foreach (string file in Directory.GetFiles(dir, "obs-*.jsonl"))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                Assert.True(stream.Length > 0);
            }
        }
    }
}