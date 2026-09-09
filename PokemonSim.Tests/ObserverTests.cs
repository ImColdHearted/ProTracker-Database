using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>Section 156. The passive observer: it records real battles
    /// faithfully, records nothing it should not, changes nothing, and
    /// fails soft. Every test writes to its own throwaway temp directory -
    /// no test can touch the app's real observation folder because the
    /// engine does not even know where that is.</summary>
    public class ObserverTests : IDisposable
    {
        readonly string dir = Path.Combine(
            Path.GetTempPath(), "protracker-observer-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }

        static (BattleState State, BattleEngine Engine) SeededBattle(int seed)
        {
            var a1 = TestKit.Mon("Alpha", hp: 240, moves: TestKit.Move("Hit A", power: 60));
            var a2 = TestKit.Mon("Beta", hp: 240, moves: TestKit.Move("Hit B", power: 60));
            var b1 = TestKit.Mon("Gamma", hp: 240, moves: TestKit.Move("Hit C", power: 60));
            var b2 = TestKit.Mon("Delta", hp: 240, moves: TestKit.Move("Hit D", power: 60));

            return TestKit.Battle(seed,
                new List<PokemonState> { a1, a2 },
                new List<PokemonState> { b1, b2 });
        }

        static string Fingerprint(BattleState state)
        {
            var parts = new List<string>
            {
                "turn=" + state.TurnNumber,
                "outcome=" + state.Outcome,
                "rng=" + state.Rng.Clone().Next(1000000),
                "log=" + string.Join("|", state.Log.Lines)
            };

            foreach (var player in new[] { state.Player1, state.Player2 })
            {
                foreach (var p in player.Team)
                    parts.Add($"{p.Species}:{p.CurrentHP}:{p.Status}");
            }

            return string.Join(";", parts);
        }

        async Task<List<JsonElement>> ReadAllRecords()
        {
            var records = new List<JsonElement>();

            if (!Directory.Exists(dir))
                return records;

            foreach (string file in Directory.GetFiles(dir, "obs-*.jsonl"))
            {
                foreach (string line in await File.ReadAllLinesAsync(file))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        records.Add(JsonDocument.Parse(line).RootElement.Clone());
                }
            }

            return records;
        }

        [Fact]
        public async Task ObserverOnAndOff_ProduceTheSameSeededBattle()
        {
            var (plain, plainEngine) = SeededBattle(seed: 501);
            plainEngine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy());

            var (observed, observedEngine) = SeededBattle(seed: 501);
            var observer = new BattleObserver(dir, "Primary", 501);
            observedEngine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);
            await observer.DisposeAsync();

            Assert.Equal(Fingerprint(plain), Fingerprint(observed));
        }

        [Fact]
        public async Task Observer_NeverMutatesTheBattleItWatches()
        {
            var (state, engine) = SeededBattle(seed: 502);
            var observer = new BattleObserver(dir, "Primary", 502);

            var legal = engine.GetLegalActions(state.Player1);
            string before = Fingerprint(state);

            observer.OnDecision(state, state.Player1, "Test", legal, legal[0]);
            observer.OnTurnResolved(state);
            observer.OnFinished(state);

            Assert.Equal(before, Fingerprint(state));

            await observer.DisposeAsync();
        }

        [Fact]
        public async Task PrimaryBattle_ProducesExactlyTheExpectedRecords()
        {
            var playerTeam = new List<PokemonState>
            {
                TestKit.Mon("Mine", hp: 400, moves: TestKit.Move("My Hit", power: 30))
            };
            var opponentTeam = new List<PokemonState>
            {
                TestKit.Mon("Theirs", hp: 400, moves: TestKit.Move("Their Hit", power: 30))
            };

            var observer = new BattleObserver(dir, "Primary", 503);
            var session = new SimulatorSession("You", playerTeam, "Foe", opponentTeam,
                seed: 503, observer: observer);

            for (int i = 0; i < 4; i++)
            {
                var move = session.PlayerLegalActions().First(a => a.Move != null);
                await session.PlayTurnAsync(move);
            }

            session.Cancel();
            await observer.DisposeAsync();

            List<JsonElement> records = await ReadAllRecords();
            var decisions = records.Where(r => r.GetProperty("Record").GetString() == "decision").ToList();
            var finals = records.Where(r => r.GetProperty("Record").GetString() == "final").ToList();

            Assert.Equal(8, decisions.Count);          // two sides x four turns
            Assert.Single(finals);
            Assert.All(decisions, d => Assert.Equal("Primary", d.GetProperty("Kind").GetString()));
            Assert.Equal("Cancelled", finals[0].GetProperty("Outcome").GetString());
        }

        [Fact]
        public async Task MonteCarloRollouts_AreNeverRecorded()
        {
            // The opponent brain simulates dozens of hypothetical battles
            // per decision; the observer must see only the visible turns.
            var playerTeam = new List<PokemonState>
            {
                TestKit.Mon("Mine", hp: 500, moves: TestKit.Move("My Hit", power: 20))
            };
            var opponentTeam = new List<PokemonState>
            {
                // Two moves, so the brain really evaluates (a single legal
                // action would skip the rollouts entirely).
                TestKit.Mon("Boss Mon", hp: 500, moves: new[]
                {
                    TestKit.Move("Boss Hit", power: 20),
                    TestKit.Move("Boss Jab", power: 25)
                })
            };

            var brain = new MonteCarloStrategy(
                new MonteCarloConfig { SimulationsPerAction = 12, MaxRolloutTurns = 12 }, seed: 91);

            var observer = new BattleObserver(dir, "Primary", 504);
            var session = new SimulatorSession("You", playerTeam, "Boss", opponentTeam,
                brain, seed: 504, observer: observer);

            for (int i = 0; i < 3; i++)
            {
                var move = session.PlayerLegalActions().First(a => a.Move != null);
                await session.PlayTurnAsync(move);
            }

            session.Cancel();
            await observer.DisposeAsync();

            List<JsonElement> records = await ReadAllRecords();
            var decisions = records.Where(r => r.GetProperty("Record").GetString() == "decision").ToList();

            // 12 rollouts x multiple actions x up to 12 turns each ran
            // inside the brain - and exactly six visible decisions exist.
            Assert.Equal(6, decisions.Count);
            Assert.All(decisions, d => Assert.Equal("Primary", d.GetProperty("Kind").GetString()));
        }

        [Fact]
        public void NoObserverAttached_MeansNoFilesAnywhere()
        {
            var (state, engine) = SeededBattle(seed: 505);
            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy());

            var playerTeam = new List<PokemonState> { TestKit.Mon("Solo", moves: TestKit.Move("Hit", power: 50)) };
            var opponentTeam = new List<PokemonState> { TestKit.Mon("Other", moves: TestKit.Move("Hit 2", power: 50)) };
            _ = new SimulatorSession("You", playerTeam, "Foe", opponentTeam, seed: 505);

            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }

        [Fact]
        public async Task LabBattles_RecordOnlyWhenExplicitlyMarked()
        {
            var (template, _) = SeededBattle(seed: 506);

            BattleSimulator.Run(template, simulations: 3, baseSeed: 506);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);

            var observers = new List<BattleObserver>();

            BattleSimulator.Run(template.Clone(), simulations: 3, baseSeed: 506,
                observerFactory: i =>
                {
                    var o = new BattleObserver(dir, "Lab", 506 + i);
                    lock (observers) observers.Add(o);
                    return o;
                });

            foreach (BattleObserver o in observers)
                await o.DisposeAsync();

            List<JsonElement> records = await ReadAllRecords();

            Assert.Equal(3, observers.Count);
            Assert.Equal(3, records.Count(r => r.GetProperty("Record").GetString() == "final"));
            Assert.All(records, r => Assert.Equal("Lab", r.GetProperty("Kind").GetString()));
        }

        [Fact]
        public async Task Records_FollowTheDocumentedSchema()
        {
            var (state, engine) = SeededBattle(seed: 507);
            var observer = new BattleObserver(dir, "Primary", 507);
            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);
            await observer.DisposeAsync();

            List<JsonElement> records = await ReadAllRecords();
            Assert.NotEmpty(records);

            foreach (JsonElement record in records)
            {
                Assert.Equal(ObservationSchema.Version, record.GetProperty("Schema").GetInt32());
                Assert.Equal(ObservationSchema.MechanicsVersion, record.GetProperty("Mechanics").GetString());
                Assert.False(string.IsNullOrEmpty(record.GetProperty("BattleId").GetString()));

                if (record.GetProperty("Record").GetString() == "decision")
                {
                    Assert.Equal(ObservationSchema.FeatureCount, record.GetProperty("State").GetArrayLength());
                    Assert.Equal(ObservationSchema.MoveSlots, record.GetProperty("LegalMoves").GetArrayLength());
                    string? kind = record.GetProperty("Action").GetProperty("Kind").GetString();
                    Assert.Contains(kind, new[] { "Move", "Switch", "Struggle", "Replacement" });
                }
                else
                {
                    Assert.Contains(record.GetProperty("Outcome").GetString(),
                        new[] { "Player1Wins", "Player2Wins", "Draw", "Cancelled" });
                    Assert.InRange(record.GetProperty("Player1Score").GetInt32(), -1, 1);
                }
            }
        }

        [Fact]
        public async Task Replacements_AreRecorded()
        {
            var (state, engine) = SeededBattle(seed: 508);
            var observer = new BattleObserver(dir, "Primary", 508);
            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);
            await observer.DisposeAsync();

            List<JsonElement> records = await ReadAllRecords();

            // Two mons per side and one side lost, so at least one faint
            // forced a replacement mid-battle.
            Assert.Contains(records, r =>
                r.GetProperty("Record").GetString() == "decision" &&
                r.GetProperty("Action").GetProperty("Kind").GetString() == "Replacement");
        }

        [Fact]
        public async Task ThrowingShadowEvaluator_CostsItsOpinionNotTheBattle()
        {
            var observer = new BattleObserver(dir, "Primary", 509, new ThrowingEvaluator());
            var (state, engine) = SeededBattle(seed: 509);

            BattleOutcome outcome = engine.RunBattle(
                new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);
            await observer.DisposeAsync();

            Assert.NotEqual(BattleOutcome.Unfinished, outcome);

            List<JsonElement> records = await ReadAllRecords();
            var decisions = records.Where(r => r.GetProperty("Record").GetString() == "decision").ToList();

            Assert.NotEmpty(decisions);
            Assert.Contains("shadow inference failed", observer.LastStatus);
        }

        sealed class ThrowingEvaluator : IShadowEvaluator
        {
            public ShadowEvaluatorStatus Status { get; } =
                new() { Available = true, Description = "test stub" };

            public ShadowPrediction? Predict(float[] state, float[] legalMoveMask) =>
                throw new InvalidOperationException("boom");
        }

        [Fact]
        public async Task Cancel_AbandonsQueuedWritesWithoutHanging()
        {
            var observer = new BattleObserver(dir, "Primary", 510);
            var (state, engine) = SeededBattle(seed: 510);

            var legal = engine.GetLegalActions(state.Player1);
            observer.OnDecision(state, state.Player1, "Test", legal, legal[0]);
            observer.OnTurnResolved(state);

            var stopwatch = Stopwatch.StartNew();
            observer.Cancel();
            await observer.DisposeAsync();
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 3000, $"cancellation took {stopwatch.ElapsedMilliseconds} ms");
            Assert.Equal("observation cancelled", observer.LastStatus);
        }

        [Fact]
        public async Task Observation_DoesNotMateriallyDelayTurns()
        {
            var stopwatchOff = Stopwatch.StartNew();
            var (plain, plainEngine) = SeededBattle(seed: 511);
            plainEngine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy());
            stopwatchOff.Stop();

            var stopwatchOn = Stopwatch.StartNew();
            var (observed, observedEngine) = SeededBattle(seed: 511);
            var observer = new BattleObserver(dir, "Primary", 511);
            observedEngine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);
            stopwatchOn.Stop();

            await observer.DisposeAsync();

            // Generous, machine-independent bound: encoding + queueing a
            // few dozen records must stay within interactive noise.
            Assert.True(stopwatchOn.ElapsedMilliseconds <= stopwatchOff.ElapsedMilliseconds * 5 + 750,
                $"observed {stopwatchOn.ElapsedMilliseconds} ms vs plain {stopwatchOff.ElapsedMilliseconds} ms");
        }

        [Fact]
        public async Task FinalRecord_MatchesTheOutcome()
        {
            var (state, engine) = SeededBattle(seed: 512);
            var observer = new BattleObserver(dir, "Primary", 512);
            BattleOutcome outcome = engine.RunBattle(
                new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);
            await observer.DisposeAsync();

            List<JsonElement> records = await ReadAllRecords();
            JsonElement final = records.Single(r => r.GetProperty("Record").GetString() == "final");

            Assert.Equal(outcome.ToString(), final.GetProperty("Outcome").GetString());

            int score = final.GetProperty("Player1Score").GetInt32();
            Assert.Equal(outcome == BattleOutcome.Player1Wins ? 1 :
                         outcome == BattleOutcome.Player2Wins ? -1 : 0, score);
        }

        [Fact]
        public void Encoder_StillOpensWithTheAuthorsTenFloatLayout()
        {
            var (state, _) = SeededBattle(seed: 513);

            state.Player1.ActivePokemon.CurrentHP = 120;   // of 240
            state.Player1.ActivePokemon.AttackStage = 2;
            state.Player2.ActivePokemon.SpeedStage = -1;
            state.Environment.Weather = WeatherType.Rain;

            float[] p1View = ObserverEncoder.Encode(state, state.Player1);
            float[] p2View = ObserverEncoder.Encode(state, state.Player2);

            // Section 175 widened the vector, but its first ten entries
            // are still exactly section 156's, which is what lets a model
            // trained before 175 keep working on the prefix.
            Assert.Equal(ObservationSchema.FeatureCount, p1View.Length);
            Assert.Equal(0.5f, p1View[0]);                  // self hp fraction
            Assert.Equal(2f, p1View[2]);                    // self attack stage
            Assert.Equal((float)WeatherType.Rain, p1View[8]);

            // Perspective mirroring: my hp fraction is your opponent's.
            Assert.Equal(p1View[0], p2View[1]);
            Assert.Equal(p1View[1], p2View[0]);
            Assert.Equal(-1f, p2View[4]);                   // p2's own speed stage
        }
    }
}