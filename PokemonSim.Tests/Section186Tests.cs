using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 186. The flush that faulted seventy-eight thousand times.
    ///
    /// A day's log came back at fifty megabytes, 78,422 lines of it one
    /// exception: ObjectDisposedException from CancellationTokenSource.Token
    /// inside the observer's writer loop. Three separate mistakes stacked to
    /// produce it, and only the third made it loud.
    ///
    /// The loop read writerCts.Token on EVERY iteration. That property
    /// throws once its source is disposed - it does not hand back an
    /// already-cancelled token - so a loop still running when DisposeAsync
    /// finished did not stop, it faulted.
    ///
    /// DisposeAsync disposed that source without waiting for the loop it
    /// belongs to. Its two-second budget was chosen when observers were
    /// retired one at a time after a whole run; section 181 started retiring
    /// them as each battle ends, so at fifty thousand battles the flushes
    /// contend and two seconds is routinely not enough. The timeout stopped
    /// being the exceptional path and became the usual one.
    ///
    /// And nothing ever awaited the writer after that, so the fault reached
    /// the finalizer, which is what logged it - every single time.
    ///
    /// These tests pin the observable part: a disposed observer never faults,
    /// whatever the timing, and everything it accepted still reaches the disk.
    /// </summary>
    public class Section186Tests : IDisposable
    {
        readonly string dir = Path.Combine(
            Path.GetTempPath(), "protracker-186-tests", Guid.NewGuid().ToString("N"));

        public Section186Tests() => Directory.CreateDirectory(dir);

        public void Dispose()
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }

        static (BattleState State, BattleEngine Engine) Battle(int seed) => TestKit.Battle(seed,
            new List<PokemonState>
            {
                TestKit.Mon("A1", hp: 140, moves: TestKit.Move("A Hit", power: 60)),
                TestKit.Mon("A2", hp: 140, moves: TestKit.Move("A Jab", power: 60))
            },
            new List<PokemonState>
            {
                TestKit.Mon("B1", hp: 140, moves: TestKit.Move("B Hit", power: 60)),
                TestKit.Mon("B2", hp: 140, moves: TestKit.Move("B Jab", power: 60))
            });

        int DecisionsOnDisk() =>
            Directory.GetFiles(dir, "obs-*.jsonl")
                .SelectMany(f => File.ReadAllLines(f))
                .Count(l => l.Contains("\"Record\":\"decision\"", StringComparison.Ordinal));

        /// <summary>Any fault the finalizer would have reported, forced out
        /// now rather than whenever the GC gets round to it. This is the
        /// mechanism that turned the bug into fifty megabytes.</summary>
        static List<Exception> UnobservedAfter(Action work)
        {
            var seen = new List<Exception>();

            void Handler(object? _, UnobservedTaskExceptionEventArgs e)
            {
                lock (seen)
                    seen.Add(e.Exception);

                e.SetObserved();
            }

            TaskScheduler.UnobservedTaskException += Handler;

            try
            {
                work();

                for (int i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= Handler;
            }

            lock (seen)
                return seen.ToList();
        }

        [Fact]
        public async Task DisposingAnObserverLeavesNothingForTheFinalizerToReport()
        {
            List<Exception> faults = UnobservedAfter(() =>
            {
                for (int i = 0; i < 24; i++)
                {
                    var (_, engine) = Battle(1860 + i);
                    var observer = new BattleObserver(dir, "Lab", 1860 + i);

                    engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(),
                                     observer: observer);

                    observer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            });

            Assert.Empty(faults);

            await Task.CompletedTask;
        }

        [Fact]
        public async Task EverythingTheObserverAcceptedReachesTheDisk()
        {
            long recorded = 0;

            for (int i = 0; i < 12; i++)
            {
                var (_, engine) = Battle(1890 + i);
                var observer = new BattleObserver(dir, "Lab", 1890 + i);

                engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(),
                                 observer: observer);

                recorded += observer.RecordedCount;

                Assert.Equal(0, observer.DroppedCount);

                await observer.DisposeAsync();
            }

            Assert.True(recorded > 0);
            Assert.Equal(recorded, (long)DecisionsOnDisk());
        }

        [Fact]
        public async Task RetiringManyObserversAtOnceIsStillClean()
        {
            // The section 181 shape: every observer disposed as its battle
            // ends, all of them in flight together, awaited once at the end.
            // This is the arrangement that made a two second flush budget
            // the normal path rather than the exceptional one.
            var flushing = new List<Task>();
            long recorded = 0;

            List<Exception> faults = UnobservedAfter(() =>
            {
                Parallel.For(0, 32, i =>
                {
                    var (_, engine) = Battle(1910 + i);
                    var observer = new BattleObserver(dir, "Lab", 1910 + i);

                    engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(),
                                     observer: observer);

                    Interlocked.Add(ref recorded, observer.RecordedCount);

                    lock (flushing)
                        flushing.Add(observer.DisposeAsync().AsTask());
                });

                Task.WhenAll(flushing).GetAwaiter().GetResult();
            });

            Assert.Empty(faults);
            Assert.Equal(32, Directory.GetFiles(dir, "obs-*.jsonl").Length);
            Assert.Equal(recorded, (long)DecisionsOnDisk());

            await Task.CompletedTask;
        }

        [Fact]
        public async Task DisposingTwiceIsNotAFault()
        {
            var (_, engine) = Battle(1950);
            var observer = new BattleObserver(dir, "Lab", 1950);

            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);

            List<Exception> faults = UnobservedAfter(() =>
            {
                observer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                observer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            });

            Assert.Empty(faults);

            await Task.CompletedTask;
        }

        [Fact]
        public async Task CancellingAndThenDisposingIsNotAFault()
        {
            // Cancel is the abandon-everything path, and it cancels the very
            // source DisposeAsync then disposes.
            var (_, engine) = Battle(1951);
            var observer = new BattleObserver(dir, "Lab", 1951);

            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);

            List<Exception> faults = UnobservedAfter(() =>
            {
                observer.Cancel();
                observer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            });

            Assert.Empty(faults);

            await Task.CompletedTask;
        }

        [Fact]
        public async Task AnObserverThatRecordedNothingDisposesQuietly()
        {
            // No records means no writer task at all, which is its own path
            // through DisposeAsync.
            var observer = new BattleObserver(dir, "Lab", 1952);

            List<Exception> faults = UnobservedAfter(() =>
                observer.DisposeAsync().AsTask().GetAwaiter().GetResult());

            Assert.Empty(faults);
            Assert.Equal(0, observer.RecordedCount);

            await Task.CompletedTask;
        }

        [Fact]
        public void TheFlushBudgetIsNoLongerTwoSeconds()
        {
            // Pinned by behaviour rather than by reading the constant: a
            // battle's worth of records must flush well inside whatever the
            // budget is, and the disposal must not be what is slow.
            var (_, engine) = Battle(1953);
            var observer = new BattleObserver(dir, "Lab", 1953);

            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);

            var clock = System.Diagnostics.Stopwatch.StartNew();

            observer.DisposeAsync().AsTask().GetAwaiter().GetResult();

            clock.Stop();

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20),
                        $"a single battle's flush took {clock.Elapsed}");
            Assert.Equal(observer.RecordedCount, DecisionsOnDisk());
        }
    }
}