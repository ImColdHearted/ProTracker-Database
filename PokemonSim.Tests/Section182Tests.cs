using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Engine.Items;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 182. DAgger, and the risk dial the teacher now carries.
    ///
    /// Two problems, one section. The first is that imitation learns the
    /// wrong distribution: a corpus recorded while the Monte Carlo brain
    /// plays holds the positions the BRAIN reaches, so a model that agrees
    /// with it on nearly six decisions in ten still loses nearly every
    /// battle, because after one imperfect turn it is somewhere the
    /// training data never went. The fix is to record the positions the
    /// MODEL reaches and label them with what the brain would have played
    /// there, which is what TeachingStrategy does.
    ///
    /// The second is that a faithful copy of the brain is a worse opponent
    /// than intended - the brain maximises expected value, so it grinds,
    /// and a difficulty that grinds is predictable. The rollouts already
    /// held the information needed to do better and were throwing it away:
    /// keeping the individual scores rather than only their mean is what
    /// lets a brain rank an action by how good it is WHEN IT GOES WELL.
    ///
    /// The tests that matter most here are the ones about not disturbing
    /// the battle. A teacher is only a teacher if the battle it advises
    /// plays out exactly like the battle it does not.
    /// </summary>
    public class Section182Tests : IDisposable
    {
        readonly string dir = Path.Combine(
            Path.GetTempPath(), "protracker-182-tests", Guid.NewGuid().ToString("N"));

        public Section182Tests() => Directory.CreateDirectory(dir);

        public void Dispose()
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }

        static MonteCarloStrategy Brain(double risk, int seed = 1820) =>
            new MonteCarloStrategy(
                new MonteCarloConfig
                {
                    SimulationsPerAction = 8,
                    MaxRolloutTurns = 8,
                    MaxDecisionMilliseconds = 60000,
                    RiskAppetite = risk
                },
                seed: seed);

        // ---------------- the risk arithmetic ----------------------------

        [Fact]
        public void AtRiskZero_ScoringIsTheMeanItAlwaysWas()
        {
            var brain = Brain(risk: 0);
            var samples = new double[] { 0.0, 1.0, 0.0, 1.0, 0.5, 0.5 };

            double total = samples.Sum();

            Assert.Equal(total / samples.Length,
                         brain.RiskAdjusted(samples, samples.Length, total), 10);
        }

        [Fact]
        public void AtRiskOne_ScoringIsTheMeanOfTheBestSlice()
        {
            var brain = Brain(risk: 1);

            // Eight rollouts, top quarter is two of them: 1.0 and 0.9.
            var samples = new double[] { 0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.9, 1.0 };

            Assert.Equal(0.95, brain.UpperTailMean(samples, samples.Length), 10);
            Assert.Equal(0.95, brain.RiskAdjusted(samples, samples.Length, samples.Sum()), 10);
        }

        [Fact]
        public void RiskBlendsBetweenTheTwo()
        {
            var samples = new double[] { 0.0, 0.0, 0.0, 1.0 };
            double mean = 0.25;
            double upside = 1.0;      // top quarter of four samples is one

            Assert.Equal(mean, Brain(0).RiskAdjusted(samples, 4, 1.0), 10);
            Assert.Equal(upside, Brain(1).RiskAdjusted(samples, 4, 1.0), 10);
            Assert.Equal(0.5 * mean + 0.5 * upside,
                         Brain(0.5).RiskAdjusted(samples, 4, 1.0), 10);
        }

        [Fact]
        public void TheGamblerPrefersUpsideWhereTheMeanPrefersTheGrind()
        {
            // The whole point, in two numbers. An all-or-nothing line wins
            // outright three times in ten; a grinding line reaches the
            // rollout cap every time and is scored a shade over a draw.
            var gamble = new double[] { 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
            var grind = Enumerable.Repeat(0.55, 10).ToArray();

            var careful = Brain(0);
            var reckless = Brain(1);

            Assert.True(careful.RiskAdjusted(grind, 10, grind.Sum()) >
                        careful.RiskAdjusted(gamble, 10, gamble.Sum()),
                        "the expected-value brain should take the grind");

            Assert.True(reckless.RiskAdjusted(gamble, 10, gamble.Sum()) >
                        reckless.RiskAdjusted(grind, 10, grind.Sum()),
                        "the gambler should take the gamble");
        }

        [Fact]
        public void ASingleRolloutHasNoSpreadToJudge()
        {
            // One sample is its own upper tail, so the two agree and the
            // short-circuit is safe rather than merely quiet.
            Assert.Equal(0.7, Brain(1).RiskAdjusted(new[] { 0.7 }, 1, 0.7), 10);
        }

        // ---------------- the teacher must not touch the battle ----------

        [Fact]
        public void Advise_RanksWithoutMegaEvolvingTheStudentsPokemon()
        {
            // WithMega SETS MegaEvolve on the BattleAction objects the
            // engine handed out - the very objects the student is about to
            // choose from. A teacher that called ChooseAction merely to
            // read its opinion would therefore reach into the battle and
            // evolve a Pokemon it does not control.
            var gengar = TestKit.Mon("Gengar", hp: 200, moves: new[]
            {
                TestKit.Move("Hex", power: 65),
                TestKit.Move("Shadow Ball", power: 80)
            });

            gengar.HeldItemId = HeldItems.Normalize("gengarite");

            var (state, engine) = TestKit.Battle(1821,
                new List<PokemonState> { gengar },
                new List<PokemonState> { TestKit.Mon("Foe", hp: 200) });

            // If the data files ever stop knowing about this form the test
            // must fail loudly rather than pass without testing anything.
            Assert.True(MegaEvolutions.CanMegaEvolve(state, state.Player1, gengar),
                        "the fixture no longer sets up a legal mega evolution");

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);
            var brain = Brain(risk: 0);

            brain.Advise(state, state.Player1, legal);

            Assert.All(legal, a => Assert.False(a.MegaEvolve,
                "Advise flipped mega evolution on an action it was only looking at"));

            // And the ordinary path still does what section 161 wanted.
            BattleAction played = brain.ChooseAction(state, state.Player1, legal);

            Assert.True(played.MegaEvolve);
        }

        [Fact]
        public void Advise_ReportsWhatItWouldPlayWithoutRememberingHavingPlayed()
        {
            var (state, engine) = TestKit.Battle(1822,
                new List<PokemonState>
                {
                    TestKit.Mon("Mine", hp: 200, moves: new[]
                    {
                        TestKit.Move("Weak", power: 10),
                        TestKit.Move("Strong", power: 90)
                    }),
                    TestKit.Mon("Bench", hp: 200)
                },
                new List<PokemonState> { TestKit.Mon("Foe", hp: 200) });

            var brain = Brain(risk: 0);
            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);

            brain.Advise(state, state.Player1, legal);

            Assert.NotNull(brain.LastMoveScores);
            Assert.InRange(brain.LastTeacherAction, 0, ObservationSchema.ActionSlots - 1);
        }

        [Fact]
        public void ABattleWithATeacherWatchingIsTheBattleWithoutOne()
        {
            // The strongest form of "the teacher is passive": same seed,
            // same teams, one side wrapped, and every observable identical
            // including the battle's own random stream.
            static (BattleState, BattleEngine) Fixture(int seed) => TestKit.Battle(seed,
                new List<PokemonState>
                {
                    TestKit.Mon("A1", hp: 150, moves: TestKit.Move("A Hit", power: 60)),
                    TestKit.Mon("A2", hp: 150, moves: TestKit.Move("A Jab", power: 60))
                },
                new List<PokemonState>
                {
                    TestKit.Mon("B1", hp: 150, moves: TestKit.Move("B Hit", power: 60)),
                    TestKit.Mon("B2", hp: 150, moves: TestKit.Move("B Jab", power: 60))
                });

            var (plain, plainEngine) = Fixture(1823);
            plainEngine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy());

            var (taught, taughtEngine) = Fixture(1823);
            taughtEngine.RunBattle(
                new TeachingStrategy(new RandomMoveStrategy(), Brain(risk: 1, seed: 77)),
                new RandomMoveStrategy());

            Assert.Equal(plain.Outcome, taught.Outcome);
            Assert.Equal(plain.TurnNumber, taught.TurnNumber);
            Assert.Equal(plain.Rng.Clone().Next(1000000), taught.Rng.Clone().Next(1000000));
            Assert.Equal(
                string.Join("|", plain.Player1.Team.Select(p => p.Species + ":" + p.CurrentHP)),
                string.Join("|", taught.Player1.Team.Select(p => p.Species + ":" + p.CurrentHP)));
            Assert.Equal(
                string.Join("|", plain.Player2.Team.Select(p => p.Species + ":" + p.CurrentHP)),
                string.Join("|", taught.Player2.Team.Select(p => p.Species + ":" + p.CurrentHP)));
        }

        // ---------------- what a taught record contains ------------------

        [Fact]
        public async Task ATaughtRecordCarriesTheTeachersChoiceAndTheStudentsAction()
        {
            var (_, engine) = TestKit.Battle(1824,
                new List<PokemonState>
                {
                    TestKit.Mon("Mine", hp: 300, moves: new[]
                    {
                        TestKit.Move("Feeble", power: 5),
                        TestKit.Move("Crushing", power: 110)
                    }),
                    TestKit.Mon("Bench", hp: 300, moves: TestKit.Move("Bench Hit", power: 40))
                },
                new List<PokemonState>
                {
                    TestKit.Mon("Foe", hp: 300, moves: TestKit.Move("Foe Hit", power: 40))
                });

            var observer = new BattleObserver(dir, "Lab", 1824);

            engine.RunBattle(
                new TeachingStrategy(new RandomMoveStrategy(), Brain(risk: 0, seed: 1824)),
                new RandomMoveStrategy(),
                observer: observer);

            await observer.DisposeAsync();

            List<JsonElement> taught = (await ReadAllRecords())
                .Where(r => r.GetProperty("Record").GetString() == "decision" &&
                            r.GetProperty("Side").GetString() == "P1")
                .ToList();

            Assert.NotEmpty(taught);

            // Every taught decision names a teacher choice, and it is
            // always a slot the mask allowed.
            foreach (JsonElement record in taught)
            {
                int advised = record.GetProperty("TeacherActionIndex").GetInt32();

                Assert.InRange(advised, 0, ObservationSchema.ActionSlots - 1);

                float[] mask = record.GetProperty("LegalActions")
                    .EnumerateArray().Select(v => v.GetSingle()).ToArray();

                Assert.Equal(1f, mask[advised]);
            }

            // The name marks them, which is how a reader tells a label that
            // is advice from a label that is a log.
            Assert.All(taught, r =>
                Assert.EndsWith("(taught)", r.GetProperty("Strategy").GetString()));

            // The brain preferred the strong move; a random student did not
            // always take it. If those never differed there would be
            // nothing for DAgger to correct.
            List<JsonElement> moves = taught
                .Where(r => r.GetProperty("Action").GetProperty("Kind").GetString() == "Move")
                .ToList();

            Assert.NotEmpty(moves);
            Assert.Contains(moves, r =>
                r.GetProperty("TeacherActionIndex").GetInt32() !=
                r.GetProperty("Action").GetProperty("MoveIndex").GetInt32());
        }

        [Fact]
        public async Task AnUntaughtRecordStillSaysNobodyAdvisedIt()
        {
            var (_, engine) = TestKit.Battle(1825,
                new List<PokemonState> { TestKit.Mon("Mine", hp: 200) },
                new List<PokemonState> { TestKit.Mon("Theirs", hp: 200) });

            var observer = new BattleObserver(dir, "Lab", 1825);

            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);
            await observer.DisposeAsync();

            List<JsonElement> records = (await ReadAllRecords())
                .Where(r => r.GetProperty("Record").GetString() == "decision")
                .ToList();

            Assert.NotEmpty(records);
            Assert.All(records, r =>
                Assert.Equal(-1, r.GetProperty("TeacherActionIndex").GetInt32()));
        }

        // ---------------- the risk feature -------------------------------

        [Fact]
        public void TheRiskFeatureIsTheLastOneAndDisturbsNothingBeforeIt()
        {
            // §311: the dial is still last, but "last" is a block now rather
            // than "one past the previous width".
            Assert.Equal(ObserverEncoder.TotalFeatures - 1, ObserverEncoder.RiskBlockStart);

            // Section 184 appended Sticky Web after it, so the risk feature
            // now ends version 5 rather than the whole vector. What has to
            // stay true is that nothing before it moved.
            Assert.Equal(ObserverEncoder.RiskBlockStart + 1, ObservationSchema.FeatureCount);

            var (state, _) = TestKit.Battle(1826,
                new List<PokemonState> { TestKit.Mon("Mine", hp: 200) },
                new List<PokemonState> { TestKit.Mon("Theirs", hp: 200) });

            float[] careful = ObserverEncoder.Encode(state, state.Player1, null, 0f);
            float[] reckless = ObserverEncoder.Encode(state, state.Player1, null, 1f);

            Assert.Equal(0f, careful[ObserverEncoder.RiskBlockStart]);
            Assert.Equal(1f, reckless[ObserverEncoder.RiskBlockStart]);

            // Nothing else moved: every earlier width is still a prefix,
            // which is what lets a 202-input model read a 203-wide state.
            for (int i = 0; i < ObserverEncoder.RiskBlockStart; i++)
                Assert.Equal(careful[i], reckless[i]);
        }

        [Fact]
        public void TheRiskFeatureIsClampedAndDefaultsToNeutral()
        {
            var (state, _) = TestKit.Battle(1827,
                new List<PokemonState> { TestKit.Mon("Mine", hp: 200) },
                new List<PokemonState> { TestKit.Mon("Theirs", hp: 200) });

            Assert.Equal(0f, ObservationSchema.NeutralRisk);
            Assert.Equal(ObservationSchema.NeutralRisk,
                         ObserverEncoder.Encode(state, state.Player1)[ObserverEncoder.RiskBlockStart]);
            Assert.Equal(1f, ObserverEncoder.Encode(state, state.Player1, null, 9f)[ObserverEncoder.RiskBlockStart]);
            Assert.Equal(0f, ObserverEncoder.Encode(state, state.Player1, null, -9f)[ObserverEncoder.RiskBlockStart]);
        }

        [Fact]
        public async Task ARecordedStateCarriesTheTeachersRisk()
        {
            var (_, engine) = TestKit.Battle(1828,
                new List<PokemonState>
                {
                    TestKit.Mon("Mine", hp: 200, moves: new[]
                    {
                        TestKit.Move("One", power: 40),
                        TestKit.Move("Two", power: 60)
                    })
                },
                new List<PokemonState> { TestKit.Mon("Theirs", hp: 200) });

            var observer = new BattleObserver(dir, "Lab", 1828);

            engine.RunBattle(Brain(risk: 0.75, seed: 1828), new RandomMoveStrategy(), observer: observer);
            await observer.DisposeAsync();

            List<JsonElement> mine = (await ReadAllRecords())
                .Where(r => r.GetProperty("Record").GetString() == "decision" &&
                            r.GetProperty("Side").GetString() == "P1")
                .ToList();

            Assert.NotEmpty(mine);
            Assert.All(mine, r => Assert.Equal(0.75,
                (double)r.GetProperty("State").EnumerateArray()
                    .ElementAt(ObserverEncoder.RiskBlockStart).GetSingle(), 3));
        }

        // ---------------- the action space mapping -----------------------

        [Fact]
        public void ActionIndex_NamesMovesAndSwitchesAndRefusesStruggle()
        {
            var mine = new List<PokemonState>
            {
                TestKit.Mon("Mine", hp: 200, moves: new[]
                {
                    TestKit.Move("First", power: 40),
                    TestKit.Move("Second", power: 40)
                }),
                TestKit.Mon("Bench", hp: 200)
            };

            var (state, _) = TestKit.Battle(1829, mine,
                new List<PokemonState> { TestKit.Mon("Theirs", hp: 200) });

            PokemonState active = state.Player1.ActivePokemon;

            Assert.Equal(0, ObserverEncoder.ActionIndex(state.Player1,
                TestKit.MoveAction(state, active, active.Moves[0])));
            Assert.Equal(1, ObserverEncoder.ActionIndex(state.Player1,
                TestKit.MoveAction(state, active, active.Moves[1])));

            Assert.Equal(ObservationSchema.MoveSlots + 1,
                ObserverEncoder.ActionIndex(state.Player1,
                    TestKit.SwitchAction(state, active, mine[1])));

            // Struggle has no slot to name, which is why it has never been
            // trainable.
            Assert.Equal(-1, ObserverEncoder.ActionIndex(state.Player1,
                TestKit.MoveAction(state, active, TestKit.Move("Struggle", power: 50))));
        }

        async Task<List<JsonElement>> ReadAllRecords()
        {
            var records = new List<JsonElement>();

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
    }
}