using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using PokemonSim.Shadow;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 175. The move-aware observation vector, Monte Carlo's
    /// per-slot ranking, and the evaluator's two accepted input widths.
    ///
    /// The point of the section is measurable: a state that does not say
    /// what the four move slots CONTAIN cannot predict which slot to pick,
    /// because RandomTeams shuffles a team's moves into its slots. These
    /// tests pin the facts that make the new vector able to: the type
    /// matchup, the power, the damage estimate and the legality of each
    /// slot are all in there, in a fixed place, and the first ten entries
    /// are still exactly section 156's so no earlier model is orphaned.
    /// </summary>
    public class Section175Tests : IDisposable
    {
        readonly string dir = Path.Combine(
            Path.GetTempPath(), "protracker-175-tests", Guid.NewGuid().ToString("N"));

        readonly List<string> temporaryFiles = new();

        public void Dispose()
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }

            foreach (string file in temporaryFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); }
                catch { /* best effort */ }
            }
        }

        // ---------------- helpers ----------------

        static PokemonState Typed(string species, PokemonType[] types, int hp = 200,
                                  int attack = 100, int defense = 100, int speed = 100,
                                  params MoveState[] moves)
        {
            PokemonState mon = TestKit.Mon(species, types[0], hp, attack, defense, speed, moves);
            mon.Types = types.ToList();
            return mon;
        }

        static int Slot(int index) => ObserverEncoder.MoveBlockStart + index * ObserverEncoder.MoveBlockStride;

        static int Global(int offset) => ObserverEncoder.GlobalBlockStart + offset;

        // ---------------- layout ----------------

        [Fact]
        public void TheLayoutAddsUpToTheDeclaredWidth()
        {
            Assert.Equal(ObservationSchema.LegacyFeatureCount, ObserverEncoder.MoveBlockStart);

            Assert.Equal(
                ObserverEncoder.MoveBlockStart + ObservationSchema.MoveSlots * ObserverEncoder.MoveBlockStride,
                ObserverEncoder.GlobalBlockStart);

            // The global block is the 22 entries the encoder fills after
            // the move blocks. Section 176 appended more behind it, so what
            // this pins now is that the section 175 vector is still exactly
            // the front of the current one.
            Assert.Equal(22, ObserverEncoder.GlobalBlockSize);
            Assert.Equal(ObservationSchema.FeatureCountV2,
                         ObserverEncoder.GlobalBlockStart + ObserverEncoder.GlobalBlockSize);
            Assert.True(ObservationSchema.FeatureCount >= ObservationSchema.FeatureCountV2);

            Assert.Equal(4, ObservationSchema.Version);
            Assert.Equal(10, ObservationSchema.LegacyFeatureCount);
            Assert.Equal(6, ObserverEncoder.StatusFlags.Length);
            Assert.DoesNotContain(StatusCondition.None, ObserverEncoder.StatusFlags);
        }

        [Fact]
        public void TheFirstTenAreStillTheSection156Ten()
        {
            var a = TestKit.Mon("Alpha", hp: 240, moves: TestKit.Move("Hit"));
            var b = TestKit.Mon("Beta", hp: 240, moves: TestKit.Move("Hit"));
            (BattleState state, _) = TestKit.Duel(700, a, b);

            a.CurrentHP = 120;             // of 240
            a.AttackStage = 2;
            b.SpeedStage = -1;
            state.Environment.Weather = WeatherType.Rain;
            state.Environment.Terrain = TerrainType.Electric;

            float[] p1 = ObserverEncoder.Encode(state, state.Player1);
            float[] p2 = ObserverEncoder.Encode(state, state.Player2);

            Assert.Equal(ObservationSchema.FeatureCount, p1.Length);

            Assert.Equal(0.5f, p1[0]);
            Assert.Equal(1f, p1[1]);
            Assert.Equal(2f, p1[2]);
            Assert.Equal(0f, p1[3]);
            Assert.Equal(0f, p1[4]);
            Assert.Equal(0f, p1[5]);
            Assert.Equal(0f, p1[6]);
            Assert.Equal(-1f, p1[7]);
            Assert.Equal((float)WeatherType.Rain, p1[8]);
            Assert.Equal((float)TerrainType.Electric, p1[9]);

            // Perspective mirroring is unchanged from section 156.
            Assert.Equal(p1[0], p2[1]);
            Assert.Equal(p1[1], p2[0]);
            Assert.Equal(p1[2], p2[5]);
            Assert.Equal(p1[7], p2[4]);
        }

        // ---------------- the move block ----------------

        [Fact]
        public void EachSlotCarriesItsOwnTypeMatchup()
        {
            var attacker = Typed("Attacker", new[] { PokemonType.Fire },
                moves: new[]
                {
                    TestKit.Move("Ember", PokemonType.Fire, power: 60),
                    TestKit.Move("Splash Hit", PokemonType.Water, power: 60),
                    TestKit.Move("Quake", PokemonType.Ground, power: 60),
                    TestKit.Move("Tackle", PokemonType.Normal, power: 60)
                });

            var target = Typed("Target", new[] { PokemonType.Grass, PokemonType.Flying });

            (BattleState state, _) = TestKit.Duel(701, attacker, target);

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            // Fire into Grass/Flying: 2x from Grass, 1x from Flying.
            Assert.Equal(2f, f[Slot(0) + 1]);

            // Water into Grass/Flying: 0.5x from Grass, 1x from Flying.
            Assert.Equal(0.5f, f[Slot(1) + 1]);

            // Ground cannot touch a Flying type at all.
            Assert.Equal(0f, f[Slot(2) + 1]);

            // Normal into Grass/Flying is plain.
            Assert.Equal(1f, f[Slot(3) + 1]);
        }

        [Fact]
        public void StabIsTheUsersOwnType()
        {
            var attacker = Typed("Attacker", new[] { PokemonType.Fire },
                moves: new[]
                {
                    TestKit.Move("Ember", PokemonType.Fire, power: 60),
                    TestKit.Move("Tackle", PokemonType.Normal, power: 60)
                });

            var target = Typed("Target", new[] { PokemonType.Normal });

            (BattleState state, _) = TestKit.Duel(702, attacker, target);

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            Assert.Equal(1f, f[Slot(0) + 6]);
            Assert.Equal(0f, f[Slot(1) + 6]);
        }

        [Fact]
        public void PowerCategoryAccuracyAndPpAreAllInTheSlot()
        {
            MoveState strong = TestKit.Move("Strong", PokemonType.Normal, MoveCategory.Physical,
                power: 150, accuracy: 80, pp: 10);
            MoveState beam = TestKit.Move("Beam", PokemonType.Normal, MoveCategory.Special, power: 75);
            MoveState buff = TestKit.Move("Sharpen", PokemonType.Normal, MoveCategory.Status, power: 0);

            strong.CurrentPP = 5;          // half of ten

            var attacker = Typed("Attacker", new[] { PokemonType.Fighting },
                moves: new[] { strong, beam, buff });

            var target = Typed("Target", new[] { PokemonType.Normal });

            (BattleState state, _) = TestKit.Duel(703, attacker, target);

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            Assert.Equal(1f, f[Slot(0) + 2]);          // 150 / 150
            Assert.Equal(1f, f[Slot(0) + 3]);          // physical
            Assert.Equal(0f, f[Slot(0) + 4]);
            Assert.Equal(0f, f[Slot(0) + 5]);
            Assert.Equal(0.8f, f[Slot(0) + 7]);        // accuracy 80
            Assert.Equal(0.5f, f[Slot(0) + 8]);        // 5 of 10 pp

            Assert.Equal(0.5f, f[Slot(1) + 2]);        // 75 / 150
            Assert.Equal(1f, f[Slot(1) + 4]);          // special

            Assert.Equal(1f, f[Slot(2) + 5]);          // status
            Assert.Equal(0f, f[Slot(2) + 1]);          // no effectiveness on a status move
            Assert.Equal(0f, f[Slot(2) + 10]);         // and no damage

            // An accuracy of zero in the data means "cannot miss".
            Assert.Equal(1f, f[Slot(1) + 7]);
        }

        [Fact]
        public void LegalitySaysWhichSlotsWereActuallyOffered()
        {
            var attacker = TestKit.Mon("Attacker", moves: new[]
            {
                TestKit.Move("One"), TestKit.Move("Two")
            });

            var target = TestKit.Mon("Target", moves: TestKit.Move("Hit"));

            (BattleState state, BattleEngine engine) = TestKit.Duel(704, attacker, target);

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);
            float[] mask = ObserverEncoder.LegalMoveMask(legal);

            float[] with = ObserverEncoder.Encode(state, state.Player1, legal);
            float[] without = ObserverEncoder.Encode(state, state.Player1);

            for (int slot = 0; slot < ObservationSchema.MoveSlots; slot++)
            {
                Assert.Equal(mask[slot], with[Slot(slot)]);

                // No legal actions offered (a forced replacement) reads as
                // "nothing was on offer" rather than inventing legality.
                Assert.Equal(0f, without[Slot(slot)]);
            }

            Assert.Equal(1f, mask[0]);
            Assert.Equal(1f, mask[1]);
        }

        [Fact]
        public void SlotsFollowTheMovesOwnIndex()
        {
            var mon = TestKit.Mon("Mixed", moves: new[]
            {
                TestKit.Move("First"), TestKit.Move("Second"), TestKit.Move("Third")
            });

            // The mask reads MoveState.Index, so the encoder must too - a
            // reordered list would otherwise describe the wrong slot.
            mon.Moves[0].Index = 2;
            mon.Moves[2].Index = 0;

            MoveState?[] slots = ObserverEncoder.SlotMoves(mon);

            Assert.Equal("Third", slots[0]!.Name);
            Assert.Equal("Second", slots[1]!.Name);
            Assert.Equal("First", slots[2]!.Name);
            Assert.Null(slots[3]);
        }

        // ---------------- the damage estimate ----------------

        [Fact]
        public void TheDamageEstimateRanksTheBetterMoveHigher()
        {
            var attacker = Typed("Attacker", new[] { PokemonType.Fire }, attack: 200,
                moves: new[]
                {
                    TestKit.Move("Flamethrower", PokemonType.Fire, power: 120),
                    TestKit.Move("Weak Splash", PokemonType.Water, power: 40)
                });

            var target = Typed("Target", new[] { PokemonType.Grass }, hp: 300, defense: 100);

            (BattleState state, BattleEngine engine) = TestKit.Duel(705, attacker, target);

            float[] f = ObserverEncoder.Encode(state, state.Player1, engine.GetLegalActions(state.Player1));

            float strong = f[Slot(0) + 10];
            float weak = f[Slot(1) + 10];

            Assert.True(strong > weak, $"expected {strong} > {weak}");
            Assert.True(strong > 0f);

            // The summary is the best any offered slot could do.
            Assert.Equal(Math.Max(strong, weak), f[Global(19)]);

            // And the best effectiveness likewise - Fire into Grass is 2x.
            Assert.Equal(2f, f[Global(18)]);
        }

        [Fact]
        public void AnImmuneMoveEstimatesNothing()
        {
            var attacker = Typed("Attacker", new[] { PokemonType.Ground }, attack: 250,
                moves: new[] { TestKit.Move("Quake", PokemonType.Ground, power: 200) });

            var target = Typed("Target", new[] { PokemonType.Flying }, hp: 100, defense: 50);

            (BattleState state, BattleEngine engine) = TestKit.Duel(706, attacker, target);

            float[] f = ObserverEncoder.Encode(state, state.Player1, engine.GetLegalActions(state.Player1));

            Assert.Equal(0f, f[Slot(0) + 1]);
            Assert.Equal(0f, f[Slot(0) + 10]);
            Assert.Equal(0f, f[Global(19)]);
        }

        [Fact]
        public void EveryFeatureIsAFiniteNumber()
        {
            // The record writer uses System.Text.Json's default options,
            // which throw on NaN and infinity, inside a try/catch that
            // would turn the throw into a missing record. Nothing the
            // encoder produces may be non-finite - including the awkward
            // cases: a fainted active, a zero-speed opponent, a move with
            // no power and a target with no hp left.
            var a = Typed("Alpha", new[] { PokemonType.Ghost }, hp: 1, speed: 1,
                moves: new[]
                {
                    TestKit.Move("Nothing", PokemonType.Normal, MoveCategory.Status, power: 0, pp: 0),
                    TestKit.Move("Quake", PokemonType.Ground, power: 100)
                });

            var b = Typed("Beta", new[] { PokemonType.Flying }, hp: 1, defense: 1, speed: 1,
                moves: new[] { TestKit.Move("Hit") });

            (BattleState state, BattleEngine engine) = TestKit.Duel(713, a, b);

            b.CurrentHP = 0;
            a.CurrentHP = 0;

            foreach (PlayerState side in new[] { state.Player1, state.Player2 })
            {
                float[] f = ObserverEncoder.Encode(state, side, engine.GetLegalActions(side));

                Assert.Equal(ObservationSchema.FeatureCount, f.Length);
                Assert.All(f, value => Assert.True(float.IsFinite(value), $"non-finite feature {value}"));
            }
        }

        [Fact]
        public void EncodingNeverRollsTheBattlesDice()
        {
            var a = Typed("Alpha", new[] { PokemonType.Fire }, attack: 150,
                moves: new[]
                {
                    TestKit.Move("Ember", PokemonType.Fire, power: 90),
                    TestKit.Move("Body Press", PokemonType.Fighting, power: 80)
                });

            var b = Typed("Beta", new[] { PokemonType.Grass, PokemonType.Steel });

            (BattleState state, BattleEngine engine) = TestKit.Duel(707, a, b);

            a.Moves[1].UsesDefenseAsOffense = true;   // exercise the stat re-pointing too

            BattleRng before = state.Rng.Clone();

            ObserverEncoder.Encode(state, state.Player1, engine.GetLegalActions(state.Player1));
            ObserverEncoder.Encode(state, state.Player2, engine.GetLegalActions(state.Player2));

            BattleRng after = state.Rng.Clone();

            for (int i = 0; i < 25; i++)
                Assert.Equal(before.Next(1000000), after.Next(1000000));
        }

        // ---------------- the global block ----------------

        [Fact]
        public void TheGlobalBlockKnowsSpeedStatusAndWhatIsLeft()
        {
            var fast = TestKit.Mon("Fast", speed: 200, moves: TestKit.Move("Hit"));
            var spare = TestKit.Mon("Spare", moves: TestKit.Move("Hit"));
            var slow = TestKit.Mon("Slow", speed: 50, moves: TestKit.Move("Hit"));

            (BattleState state, _) = TestKit.Battle(708,
                new List<PokemonState> { fast, spare },
                new List<PokemonState> { slow });

            fast.Status = StatusCondition.Burn;
            slow.Status = StatusCondition.Sleep;

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            Assert.Equal(1f, f[Global(0)]);                       // outspeeds
            Assert.Equal(1f, f[Global(2)]);                       // self burned
            Assert.Equal(0f, f[Global(3)]);                       // not paralysed
            Assert.Equal(1f, f[Global(11)]);                      // opponent asleep

            Assert.Equal(2f / 6f, f[Global(14)]);                 // two alive
            Assert.Equal(1f / 6f, f[Global(15)]);                 // one alive

            Assert.Equal(0.5f, f[Global(20)]);                    // level 50
            Assert.Equal(0.5f, f[Global(21)]);

            // The slow side sees the mirror image.
            float[] g = ObserverEncoder.Encode(state, state.Player2);
            Assert.Equal(0f, g[Global(0)]);
        }

        [Fact]
        public void TeamStrengthFallsAsPokemonFaint()
        {
            var a1 = TestKit.Mon("A1", hp: 100, moves: TestKit.Move("Hit"));
            var a2 = TestKit.Mon("A2", hp: 100, moves: TestKit.Move("Hit"));
            var b1 = TestKit.Mon("B1", hp: 100, moves: TestKit.Move("Hit"));

            (BattleState state, _) = TestKit.Battle(709,
                new List<PokemonState> { a1, a2 },
                new List<PokemonState> { b1 });

            Assert.Equal(1f, ObserverEncoder.Encode(state, state.Player1)[Global(16)]);

            a2.CurrentHP = 0;

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            Assert.Equal(0.5f, f[Global(16)]);                    // half the team's hp gone
            Assert.Equal(1f / 6f, f[Global(14)]);                 // and one of two is down
        }

        // ---------------- the teacher ----------------

        [Fact]
        public void MonteCarloPublishesThePerSlotRankingBehindItsChoice()
        {
            var brain = TestKit.Mon("Brain", speed: 120, moves: new[]
            {
                TestKit.Move("Weak", power: 10),
                TestKit.Move("Strong", power: 120)
            });

            var dummy = TestKit.Mon("Dummy", hp: 150, moves: TestKit.Move("Hit", power: 10));

            (BattleState state, BattleEngine engine) = TestKit.Duel(710, brain, dummy);

            var strategy = new MonteCarloStrategy(
                new MonteCarloConfig { SimulationsPerAction = 4, MaxRolloutTurns = 6 }, seed: 175);

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);

            Assert.True(legal.Count(a => a.Type == BattleActionType.Move) >= 2);

            BattleAction chosen = strategy.ChooseAction(state, state.Player1, legal);

            IReadOnlyList<float>? scores = strategy.LastMoveScores;

            Assert.NotNull(scores);
            Assert.Equal(ObservationSchema.ActionSlots, scores!.Count);

            // Every slot the engine offered was scored; the two it did not
            // offer stay NaN rather than pretending to be worth nothing.
            for (int slot = 0; slot < ObservationSchema.MoveSlots; slot++)
            {
                bool offered = legal.Any(a => a.Type == BattleActionType.Move &&
                                              a.Move != null && a.Move.Index == slot);

                Assert.Equal(offered, !float.IsNaN(scores[slot]));
            }

            // The choice is the top of its own ranking.
            float best = scores.Where(s => !float.IsNaN(s)).Max();
            Assert.Equal(best, scores[chosen.Move!.Index]);
        }

        [Fact]
        public void MonteCarloReportsNoRankingWhenThereIsNothingToRank()
        {
            var only = TestKit.Mon("Only", moves: TestKit.Move("Sole"));
            var dummy = TestKit.Mon("Dummy", moves: TestKit.Move("Hit"));

            (BattleState state, BattleEngine engine) = TestKit.Duel(711, only, dummy);

            var strategy = new MonteCarloStrategy(
                new MonteCarloConfig { SimulationsPerAction = 2, MaxRolloutTurns = 4 }, seed: 175);

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);

            Assert.Single(legal);

            strategy.ChooseAction(state, state.Player1, legal);

            Assert.Null(strategy.LastMoveScores);
        }

        [Fact]
        public async Task TheObserverRecordsTheTeachersRankingAndOnlyTheTeachers()
        {
            var a = TestKit.Mon("Alpha", hp: 200, moves: new[]
            {
                TestKit.Move("Hit A", power: 60), TestKit.Move("Hit B", power: 40)
            });

            var b = TestKit.Mon("Beta", hp: 200, moves: new[]
            {
                TestKit.Move("Hit C", power: 60), TestKit.Move("Hit D", power: 40)
            });

            (BattleState state, BattleEngine engine) = TestKit.Duel(712, a, b);

            var observer = new BattleObserver(dir, "Lab", 712);

            engine.RunBattle(
                new MonteCarloStrategy(
                    new MonteCarloConfig { SimulationsPerAction = 2, MaxRolloutTurns = 5 }, seed: 175),
                new RandomMoveStrategy(),
                observer: observer);

            await observer.DisposeAsync();

            List<JsonElement> decisions = (await ReadAllRecords())
                .Where(r => r.GetProperty("Record").GetString() == "decision")
                .ToList();

            Assert.NotEmpty(decisions);

            var ranked = decisions
                .Where(r => r.GetProperty("Strategy").GetString() == "Monte Carlo")
                .Where(r => r.TryGetProperty("TeacherScores", out JsonElement s) &&
                            s.ValueKind == JsonValueKind.Array)
                .ToList();

            Assert.NotEmpty(ranked);

            Assert.All(ranked, r =>
            {
                JsonElement scores = r.GetProperty("TeacherScores");

                Assert.Equal(ObservationSchema.ActionSlots, scores.GetArrayLength());

                // A slot the brain never evaluated is null, NOT NaN -
                // System.Text.Json's default options refuse NaN, and this
                // class catches its own exceptions, so a NaN here would
                // mean records quietly stopped being written.
                Assert.All(scores.EnumerateArray().ToList(), value =>
                    Assert.True(value.ValueKind == JsonValueKind.Number ||
                                value.ValueKind == JsonValueKind.Null,
                                $"unexpected {value.ValueKind} in TeacherScores"));

                Assert.Contains(scores.EnumerateArray().ToList(),
                    value => value.ValueKind == JsonValueKind.Number);
            });

            // The baseline does not rank, so it never writes a ranking.
            Assert.All(decisions.Where(r => r.GetProperty("Strategy").GetString() == "Random moves"),
                r => Assert.False(r.TryGetProperty("TeacherScores", out JsonElement s) &&
                                  s.ValueKind == JsonValueKind.Array));
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

        // ---------------- the evaluator's two widths ----------------

        // Two hand-built ONNX graphs, one of each accepted width, whose
        // single Gemm copies the first four inputs straight to the four
        // outputs. That makes the model's answer a known function of the
        // state, so these tests can prove not merely that a width loads
        // but that a narrow model is fed the PREFIX of a wide state.
        const string NarrowProbe =
            "CAgSFnByb3RyYWNrZXItYWktdHJhaW5pbmc6swIKIAoBeAoCVzAKAkIwEgZvdXRwdXQaBWdlbW0wIgRHZW1tEgpwb2tlbW9uX2Fp" +
            "Kq0BCAoIBBABQgJXMEqgAQAAgD8AAAAAAAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAAAA" +
            "AAAAgD8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAqGggEEAFCAkIwShAAAAAAAAAAAAAAAAAAAAAAWhgKAXgSEwoRCAESDQoHEgViYXRj" +
            "aAoCCApiHQoGb3V0cHV0EhMKEQgBEg0KBxIFYmF0Y2gKAggEQgQKABAN";

        const string WideProbe =
            "CAgSFnByb3RyYWNrZXItYWktdHJhaW5pbmc60woKIAoBeAoCVzAKAkIwEgZvdXRwdXQaBWdlbW0wIgRHZW1tEgpwb2tlbW9uX2Fp" +
            "Ks0JCEwIBBABQgJXMErACQAAgD8AAAAAAAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAAAA" +
            "AAAAgD8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAqGggEEAFCAkIwShAAAAAAAAAAAAAAAAAAAAAAWhgKAXgSEwoRCAESDQoH" +
            "EgViYXRjaAoCCExiHQoGb3V0cHV0EhMKEQgBEg0KBxIFYmF0Y2gKAggEQgQKABAN";

        string WriteProbe(string base64)
        {
            string path = Path.Combine(Path.GetTempPath(), $"protracker-175-{Guid.NewGuid():N}.onnx");
            File.WriteAllBytes(path, Convert.FromBase64String(base64));
            temporaryFiles.Add(path);
            return path;
        }

        [Fact]
        public void TheEvaluatorAcceptsBothWidthsAndSaysWhichItGot()
        {
            using var narrow = new OnnxShadowEvaluator(WriteProbe(NarrowProbe));
            using var wide = new OnnxShadowEvaluator(WriteProbe(WideProbe));

            Assert.True(narrow.Status.Available, narrow.Status.Description);
            Assert.True(wide.Status.Available, wide.Status.Description);

            Assert.Equal(ObservationSchema.FeatureCountV1, narrow.Status.InputWidth);
            Assert.Equal(ObservationSchema.FeatureCountV2, wide.Status.InputWidth);

            // Both of these probes predate section 176, and the status line
            // says which generation each one is.
            Assert.Contains("pre-175", narrow.Status.Description);
            Assert.Contains("pre-176", wide.Status.Description);
            Assert.DoesNotContain("pre-175", wide.Status.Description);
        }

        [Fact]
        public void ANarrowModelIsFedThePrefixOfAWideState()
        {
            using var narrow = new OnnxShadowEvaluator(WriteProbe(NarrowProbe));
            using var wide = new OnnxShadowEvaluator(WriteProbe(WideProbe));

            if (!narrow.Status.Available || !wide.Status.Available)
                return;   // covered by the load test above

            var state = new float[ObservationSchema.FeatureCount];
            state[0] = 0.1f;
            state[1] = 0.9f;      // slot 1 is the best of the four
            state[2] = 0.2f;
            state[3] = 0.3f;

            var open = new float[] { 1, 1, 1, 1 };

            ShadowPrediction? narrowSaid = narrow.Predict(state, open);
            ShadowPrediction? wideSaid = wide.Predict(state, open);

            Assert.NotNull(narrowSaid);
            Assert.NotNull(wideSaid);

            // Both graphs copy inputs 0-3 to the outputs, so both must
            // agree - which is only true if the narrow one really was
            // handed the first ten features rather than refused.
            Assert.Equal(1, narrowSaid!.PreferredMoveIndex);
            Assert.Equal(1, wideSaid!.PreferredMoveIndex);

            for (int i = 0; i < ObservationSchema.MoveSlots; i++)
                Assert.Equal(state[i], narrowSaid.Scores[i], 5);

            // Masking still wins over the model's own preference.
            ShadowPrediction? masked = wide.Predict(state, new float[] { 0, 0, 1, 0 });

            Assert.NotNull(masked);
            Assert.Equal(2, masked!.PreferredMoveIndex);
        }

        [Fact]
        public void AStateNarrowerThanTheModelGetsNoOpinion()
        {
            using var wide = new OnnxShadowEvaluator(WriteProbe(WideProbe));

            if (!wide.Status.Available)
                return;

            Assert.Null(wide.Predict(new float[ObservationSchema.LegacyFeatureCount],
                                     new float[] { 1, 1, 1, 1 }));
        }
    }
}