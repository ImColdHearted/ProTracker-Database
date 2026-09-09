using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 179. Counting and clearing a corpus without reading it.
    ///
    /// The stored-count line used to open every battle file and count the
    /// lines carrying a decision marker. At eighty thousand battles that is
    /// four gigabytes read off the disk every time the Battle Lab panel is
    /// opened. Every battle's last line is its FinalRecord and already
    /// carries DecisionRecords, so the same exact number is available from
    /// the tail. These tests pin that the tail read agrees with the slow
    /// count it replaced, including on the awkward files - an interrupted
    /// battle with no final line, an empty file, a torn last line.
    /// </summary>
    public class Section179Tests : IDisposable
    {
        readonly string dir = Path.Combine(
            Path.GetTempPath(), "protracker-179-tests", Guid.NewGuid().ToString("N"));

        public Section179Tests() => Directory.CreateDirectory(dir);

        public void Dispose()
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }

        // The two readings this section claims are equivalent. Both are
        // reimplemented here rather than called: ObservationStore lives in
        // the tracker project, which the engine tests cannot reference, so
        // what is pinned is the TECHNIQUE and the record shape it leans on.

        static long SlowCount(string file) =>
            File.ReadLines(file).Count(line =>
                line.Contains("\"Record\":\"decision\"", StringComparison.Ordinal));

        static string? LastLine(string file)
        {
            const int window = 8192;

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (stream.Length == 0)
                return null;

            int take = (int)Math.Min(window, stream.Length);
            stream.Seek(-take, SeekOrigin.End);

            var buffer = new byte[take];
            int read = stream.Read(buffer, 0, take);

            string[] lines = Encoding.UTF8.GetString(buffer, 0, read).Split('\n');

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();

                if (line.Length > 0 && line[0] == '{')
                    return line;
            }

            return null;
        }

        static long FastCount(string file)
        {
            string? last = LastLine(file);

            if (last != null)
            {
                const string marker = "\"DecisionRecords\":";
                int at = last.IndexOf(marker, StringComparison.Ordinal);

                if (at >= 0)
                {
                    int start = at + marker.Length, end = at + marker.Length;

                    while (end < last.Length && (char.IsDigit(last[end]) || last[end] == ' '))
                        end++;

                    if (long.TryParse(last.AsSpan(start, end - start).Trim(), out long counted))
                        return counted;
                }
            }

            return SlowCount(file);
        }

        // ---------------- a real recorded battle ----------------

        async Task<string> RecordOneBattle(int seed)
        {
            var a = TestKit.Mon("Alpha", hp: 200, moves: new[]
            {
                TestKit.Move("Hit A", power: 60), TestKit.Move("Hit B", power: 40)
            });

            var spare = TestKit.Mon("Spare", hp: 200, moves: TestKit.Move("Hit C", power: 50));
            var b = TestKit.Mon("Beta", hp: 200, moves: TestKit.Move("Hit D", power: 50));

            (BattleState state, BattleEngine engine) = TestKit.Battle(seed,
                new List<PokemonState> { a, spare },
                new List<PokemonState> { b });

            var observer = new BattleObserver(dir, "Lab", seed);

            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);

            await observer.DisposeAsync();

            return Directory.GetFiles(dir, "obs-*.jsonl").Single();
        }

        [Fact]
        public async Task TheTailReadAgreesWithReadingTheWholeFile()
        {
            string file = await RecordOneBattle(179);

            Assert.True(SlowCount(file) > 0, "the battle recorded no decisions");
            Assert.Equal(SlowCount(file), FastCount(file));
        }

        [Fact]
        public async Task ItAgreesAcrossManyBattles()
        {
            long slow = 0, fast = 0;

            for (int seed = 200; seed < 208; seed++)
            {
                foreach (string stale in Directory.GetFiles(dir, "obs-*.jsonl"))
                    File.Delete(stale);

                string file = await RecordOneBattle(seed);
                slow += SlowCount(file);
                fast += FastCount(file);
            }

            Assert.True(slow > 0);
            Assert.Equal(slow, fast);
        }

        [Fact]
        public async Task TheFinalRecordReallyCarriesTheCount()
        {
            string file = await RecordOneBattle(181);

            string last = File.ReadLines(file).Last(l => !string.IsNullOrWhiteSpace(l));
            JsonElement final = JsonDocument.Parse(last).RootElement;

            Assert.Equal("final", final.GetProperty("Record").GetString());
            Assert.Equal(SlowCount(file), final.GetProperty("DecisionRecords").GetInt64());
        }

        // ---------------- the awkward files ----------------

        [Fact]
        public void AnInterruptedBattleFallsBackToCountingItsLines()
        {
            // No final record: the run was killed mid-battle.
            string file = Path.Combine(dir, "obs-interrupted.jsonl");

            File.WriteAllLines(file, new[]
            {
                "{\"Record\":\"decision\",\"Turn\":1}",
                "{\"Record\":\"decision\",\"Turn\":2}",
                "{\"Record\":\"decision\",\"Turn\":3}"
            });

            Assert.Equal(3, FastCount(file));
            Assert.Equal(SlowCount(file), FastCount(file));
        }

        [Fact]
        public void AnEmptyFileCountsZeroRatherThanThrowing()
        {
            string file = Path.Combine(dir, "obs-empty.jsonl");
            File.WriteAllText(file, string.Empty);

            Assert.Equal(0, FastCount(file));
        }

        [Fact]
        public void ATornLastLineDoesNotBecomeACount()
        {
            // The process died mid-write, so the file ends in a fragment.
            string file = Path.Combine(dir, "obs-torn.jsonl");

            File.WriteAllText(file,
                "{\"Record\":\"decision\",\"Turn\":1}\n" +
                "{\"Record\":\"decision\",\"Turn\":2}\n" +
                "{\"Record\":\"fin");

            // The fragment starts with '{' so it is considered, finds no
            // DecisionRecords, and the slow count takes over - which is the
            // honest answer for a file with no final record.
            Assert.Equal(2, FastCount(file));
        }

        [Fact]
        public void AFileLongerThanTheWindowStillFindsItsFinalLine()
        {
            string file = Path.Combine(dir, "obs-long.jsonl");
            var lines = new List<string>();

            // Well past the 8 KB tail window.
            for (int i = 0; i < 400; i++)
                lines.Add("{\"Record\":\"decision\",\"Turn\":" + i + ",\"Padding\":\"" + new string('x', 200) + "\"}");

            lines.Add("{\"Record\":\"final\",\"DecisionRecords\":400,\"DroppedRecords\":0}");
            File.WriteAllLines(file, lines);

            Assert.True(new FileInfo(file).Length > 8192, "the test file is not long enough to matter");
            Assert.Equal(400, FastCount(file));
            Assert.Equal(SlowCount(file), FastCount(file));
        }

        [Fact]
        public void ADecisionRecordWithNoFinalCountIsNeverMisreadFromAnotherField()
        {
            // "DroppedRecords" must not be mistaken for "DecisionRecords".
            string file = Path.Combine(dir, "obs-dropped.jsonl");

            File.WriteAllLines(file, new[]
            {
                "{\"Record\":\"decision\",\"Turn\":1}",
                "{\"Record\":\"final\",\"DroppedRecords\":99,\"DecisionRecords\":1}"
            });

            Assert.Equal(1, FastCount(file));
        }

        // ---------------- what clearing must leave behind ----------------

        [Fact]
        public void ClearingTakesOnlyTheBattleFiles()
        {
            // The pattern ObservationStore.ClearAll deletes by. The model
            // Install Trained Model drops here, and the settings file, live
            // in this same folder and must survive - deleting the folder
            // wholesale would take the trained model with it.
            File.WriteAllText(Path.Combine(dir, "obs-00001.jsonl"), "{}");
            File.WriteAllText(Path.Combine(dir, "obs-00002.jsonl"), "{}");
            File.WriteAllText(Path.Combine(dir, "pokemon_ai.onnx"), "model");
            File.WriteAllText(Path.Combine(dir, "observer-settings.json"), "{}");

            long bytes = 0;
            int cleared = 0;

            foreach (string file in Directory.GetFiles(dir, "obs-*.jsonl"))
            {
                bytes += new FileInfo(file).Length;
                File.Delete(file);
                cleared++;
            }

            Assert.Equal(2, cleared);
            Assert.True(bytes > 0);
            Assert.True(File.Exists(Path.Combine(dir, "pokemon_ai.onnx")), "the trained model was deleted");
            Assert.True(File.Exists(Path.Combine(dir, "observer-settings.json")), "the settings were deleted");
            Assert.Empty(Directory.GetFiles(dir, "obs-*.jsonl"));
        }

        [Fact]
        public void ClearingAnEmptyFolderIsNotAnError()
        {
            string empty = Path.Combine(dir, "nothing-here");
            Directory.CreateDirectory(empty);

            Assert.Empty(Directory.GetFiles(empty, "obs-*.jsonl"));
        }
    }
}