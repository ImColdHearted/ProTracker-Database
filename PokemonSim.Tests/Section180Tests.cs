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
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 180. Post-faint replacements as training data.
    ///
    /// Section 176 gave the network a switch head and then starved it: a
    /// 9,990-battle lab corpus held 681 switch decisions in 339,129, or
    /// 0.2 percent, because the baseline AI never switches by choice and
    /// so almost nothing in the corpus was a switch at all. Meanwhile
    /// every faint forces a replacement - roughly five a battle - and
    /// those WERE being recorded, just uselessly: BattleEngine called the
    /// observer AFTER Replace, so the recorded state already had the
    /// chosen Pokemon standing in it. The answer was inside its own
    /// question, which is why section 176 wrote an empty mask on those
    /// records to keep the trainer away from them.
    ///
    /// Moving the call before the swap is the whole fix, and these tests
    /// pin the three things that makes true: the recorded state is the one
    /// the side decided FROM (its active still the fainted Pokemon), the
    /// mask names the candidates the engine offered, and the label is one
    /// of them. The rest pin the ranking that now rides along, and that
    /// records written the old way are still ignored.
    /// </summary>
    public class Section180Tests : IDisposable
    {
        readonly string dir = Path.Combine(
            Path.GetTempPath(), "protracker-180-tests", Guid.NewGuid().ToString("N"));

        public Section180Tests() => Directory.CreateDirectory(dir);

        public void Dispose()
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
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

        static List<JsonElement> Replacements(IEnumerable<JsonElement> records) =>
            records.Where(r =>
                r.GetProperty("Record").GetString() == "decision" &&
                r.GetProperty("Action").GetProperty("Kind").GetString() == "Replacement")
                .ToList();

        static float[] Floats(JsonElement row, string name) =>
            row.GetProperty(name).EnumerateArray().Select(v => v.GetSingle()).ToArray();

        /// <summary>Two full teams that will certainly faint each other
        /// several times over, so a battle produces replacements without
        /// depending on a particular roll.</summary>
        static (BattleState State, BattleEngine Engine) FaintingBattle(int seed)
        {
            List<PokemonState> Team(string prefix) => new List<PokemonState>
            {
                TestKit.Mon(prefix + " One",   hp: 60, moves: TestKit.Move(prefix + " Hit A", power: 120)),
                TestKit.Mon(prefix + " Two",   hp: 60, moves: TestKit.Move(prefix + " Hit B", power: 120)),
                TestKit.Mon(prefix + " Three", hp: 60, moves: TestKit.Move(prefix + " Hit C", power: 120))
            };

            return TestKit.Battle(seed, Team("Mine"), Team("Theirs"));
        }

        // ---------------- the ordering, which is the whole section -------

        [Fact]
        public async Task Replacement_IsRecordedBeforeTheSwap()
        {
            var (_, engine) = FaintingBattle(seed: 1801);
            var observer = new BattleObserver(dir, "Lab", 1801);

            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);
            await observer.DisposeAsync();

            List<JsonElement> replacements = Replacements(await ReadAllRecords());

            Assert.NotEmpty(replacements);

            // Feature 0 is the acting side's own active HP fraction. Before
            // the swap the active is the Pokemon that just fainted, so it
            // is zero on every one of these. Recorded after the swap - the
            // pre-180 behaviour - it would be the incoming Pokemon at full
            // health, and the state would be announcing its own answer.
            Assert.All(replacements, r => Assert.Equal(0f, Floats(r, "State")[0]));
        }

        [Fact]
        public async Task Replacement_MaskNamesTheCandidatesAndNoMove()
        {
            var (_, engine) = FaintingBattle(seed: 1802);
            var observer = new BattleObserver(dir, "Lab", 1802);

            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);
            await observer.DisposeAsync();

            List<JsonElement> replacements = Replacements(await ReadAllRecords());

            Assert.NotEmpty(replacements);

            foreach (JsonElement r in replacements)
            {
                float[] mask = Floats(r, "LegalActions");

                Assert.Equal(ObservationSchema.ActionSlots, mask.Length);

                // No move is a legal answer to "who comes in".
                for (int i = 0; i < ObservationSchema.MoveSlots; i++)
                    Assert.Equal(0f, mask[i]);

                // The four-wide legacy mask stays empty for the same reason.
                Assert.All(Floats(r, "LegalMoves"), v => Assert.Equal(0f, v));

                // Something on the bench was offered, and the label is one
                // of the things offered.
                Assert.Contains(mask.Skip(ObservationSchema.MoveSlots), v => v == 1f);

                int team = r.GetProperty("Action").GetProperty("SwitchTeamIndex").GetInt32();

                Assert.InRange(team, 0, ObservationSchema.TeamSlots - 1);
                Assert.Equal(1f, mask[ObservationSchema.MoveSlots + team]);
            }
        }

        [Fact]
        public async Task Replacement_NamesThePokemonComingIn_NotTheOneLeaving()
        {
            var mine = new List<PokemonState>
            {
                TestKit.Mon("Faints", hp: 1, moves: TestKit.Move("Feeble", power: 1)),
                TestKit.Mon("Answers", hp: 300, moves: TestKit.Move("Solid", power: 40))
            };
            var theirs = new List<PokemonState>
            {
                TestKit.Mon("Bruiser", hp: 300, moves: TestKit.Move("Crush", power: 150))
            };

            var (_, engine) = TestKit.Battle(1803, mine, theirs);
            var observer = new BattleObserver(dir, "Lab", 1803);

            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);
            await observer.DisposeAsync();

            JsonElement replacement = Assert.Single(Replacements(await ReadAllRecords()));
            JsonElement action = replacement.GetProperty("Action");

            Assert.Equal("Answers", action.GetProperty("SwitchSpecies").GetString());
            Assert.Equal(1, action.GetProperty("SwitchTeamIndex").GetInt32());
            Assert.Equal("P1", replacement.GetProperty("Side").GetString());
        }

        [Fact]
        public async Task Replacement_RecordingDoesNotDisturbTheBattle()
        {
            // The encoder now runs on a state whose acting active has
            // fainted, which nothing asked it to do before. It must still
            // touch neither the state nor the battle's random stream.
            var (plain, plainEngine) = FaintingBattle(seed: 1804);
            plainEngine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy());

            var (observed, observedEngine) = FaintingBattle(seed: 1804);
            var observer = new BattleObserver(dir, "Lab", 1804);

            observedEngine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);
            await observer.DisposeAsync();

            Assert.Equal(plain.Outcome, observed.Outcome);
            Assert.Equal(plain.TurnNumber, observed.TurnNumber);
            Assert.Equal(plain.Rng.Clone().Next(1000000), observed.Rng.Clone().Next(1000000));
            Assert.Equal(
                string.Join("|", plain.Player1.Team.Select(p => p.Species + ":" + p.CurrentHP)),
                string.Join("|", observed.Player1.Team.Select(p => p.Species + ":" + p.CurrentHP)));
        }

        [Fact]
        public async Task Replacement_StateIsFiniteEvenWithAFaintedActive()
        {
            var (_, engine) = FaintingBattle(seed: 1805);
            var observer = new BattleObserver(dir, "Lab", 1805);

            engine.RunBattle(new RandomMoveStrategy(), new RandomMoveStrategy(), observer: observer);
            await observer.DisposeAsync();

            List<JsonElement> replacements = Replacements(await ReadAllRecords());

            Assert.NotEmpty(replacements);
            Assert.All(replacements, r =>
            {
                float[] state = Floats(r, "State");

                Assert.Equal(ObservationSchema.FeatureCount, state.Length);
                Assert.All(state, v => Assert.True(float.IsFinite(v), "feature was " + v));
            });
        }

        // ---------------- a caller that has not been updated -------------

        [Fact]
        public async Task Replacement_WithoutCandidates_KeepsTheEmptyMaskThatExcludesIt()
        {
            // The two new arguments are optional, so an old caller still
            // compiles. What it produces must stay untrainable rather than
            // become a record whose state contains the answer: an empty
            // mask marks the chosen index illegal, and every reader drops
            // a record whose own choice is not allowed.
            var mine = new List<PokemonState>
            {
                TestKit.Mon("Down", hp: 100),
                TestKit.Mon("Up", hp: 100)
            };
            var theirs = new List<PokemonState> { TestKit.Mon("Foe", hp: 100) };

            var (state, _) = TestKit.Battle(1806, mine, theirs);
            var observer = new BattleObserver(dir, "Lab", 1806);

            observer.OnReplacement(state, state.Player1, "Old caller", mine[1]);
            await observer.DisposeAsync();

            JsonElement record = Assert.Single(Replacements(await ReadAllRecords()));
            float[] mask = Floats(record, "LegalActions");

            Assert.Equal(ObservationSchema.ActionSlots, mask.Length);
            Assert.All(mask, v => Assert.Equal(0f, v));

            int team = record.GetProperty("Action").GetProperty("SwitchTeamIndex").GetInt32();

            Assert.Equal(0f, mask[ObservationSchema.MoveSlots + team]);
            Assert.Equal(JsonValueKind.Null, record.GetProperty("TeacherScores").ValueKind);
        }

        // ---------------- the ranking that rides along -------------------

        static (BattleState State, PlayerState Self) FaintedActive(int seed)
        {
            var mine = new List<PokemonState>
            {
                TestKit.Mon("Fallen", hp: 100, moves: TestKit.Move("Gone", power: 40)),
                TestKit.Mon("Sturdy", type: PokemonType.Rock, hp: 300,
                    moves: TestKit.Move("Rock Hit", type: PokemonType.Rock, power: 80)),
                TestKit.Mon("Frail", hp: 40, moves: TestKit.Move("Weak Hit", power: 10))
            };
            var theirs = new List<PokemonState>
            {
                TestKit.Mon("Foe", hp: 200, moves: TestKit.Move("Foe Hit", power: 50))
            };

            var (state, _) = TestKit.Battle(seed, mine, theirs);

            state.Player1.ActivePokemon.CurrentHP = 0;

            return (state, state.Player1);
        }

        [Fact]
        public void MonteCarloReplacement_RanksEveryCandidateAndNoMove()
        {
            var (state, self) = FaintedActive(1807);
            var brain = new MonteCarloStrategy(
                new MonteCarloConfig
                {
                    SimulationsPerAction = 8,
                    MaxRolloutTurns = 8,

                    // These tests assert on WHICH candidates got scored, so
                    // the decision budget must not be what decides that on a
                    // loaded machine.
                    MaxDecisionMilliseconds = 60000
                },
                seed: 1807);

            int chosen = brain.ChooseReplacement(state, self, new List<int> { 1, 2 });

            Assert.Contains(chosen, new[] { 1, 2 });

            IReadOnlyList<float>? scores = ((ITeacherPolicy)brain).LastMoveScores;

            Assert.NotNull(scores);
            Assert.Equal(ObservationSchema.ActionSlots, scores!.Count);

            // A replacement has no move to rank, and no opinion about the
            // bench positions it was not offered.
            for (int i = 0; i < ObservationSchema.MoveSlots; i++)
                Assert.True(float.IsNaN(scores[i]), "move slot " + i + " was ranked");

            Assert.True(float.IsNaN(scores[ObservationSchema.MoveSlots + 0]));
            Assert.True(float.IsFinite(scores[ObservationSchema.MoveSlots + 1]));
            Assert.True(float.IsFinite(scores[ObservationSchema.MoveSlots + 2]));
        }

        [Fact]
        public void MonteCarloReplacement_DoesNotPublishThePreviousDecisionsRanking()
        {
            // ChooseAction leaves a ranking over the move slots behind. If
            // ChooseReplacement did not clear it, a forced replacement -
            // which never reaches the scoring loop - would be recorded
            // carrying the last move decision's numbers as if they were an
            // opinion about which Pokemon to send in.
            var mine = new List<PokemonState>
            {
                TestKit.Mon("Actor", hp: 200, moves: new[]
                {
                    TestKit.Move("Jab", power: 30),
                    TestKit.Move("Hook", power: 60)
                }),
                TestKit.Mon("Bench", hp: 200, moves: TestKit.Move("Bench Hit", power: 40))
            };
            var theirs = new List<PokemonState> { TestKit.Mon("Foe", hp: 200) };

            var (state, engine) = TestKit.Battle(1808, mine, theirs);
            var brain = new MonteCarloStrategy(
                new MonteCarloConfig
                {
                    SimulationsPerAction = 8,
                    MaxRolloutTurns = 8,
                    MaxDecisionMilliseconds = 60000
                },
                seed: 1808);

            brain.ChooseAction(state, state.Player1, engine.GetLegalActions(state.Player1));

            Assert.NotNull(((ITeacherPolicy)brain).LastMoveScores);

            state.Player1.ActivePokemon.CurrentHP = 0;

            // One candidate: nothing to weigh, so nothing to report.
            brain.ChooseReplacement(state, state.Player1, new List<int> { 1 });

            Assert.Null(((ITeacherPolicy)brain).LastMoveScores);
        }

        [Fact]
        public async Task Replacement_FromARankingStrategy_CarriesTheRanking()
        {
            var mine = new List<PokemonState>
            {
                TestKit.Mon("Glass", hp: 1, moves: TestKit.Move("Tap", power: 1)),
                TestKit.Mon("Wall", hp: 400, moves: TestKit.Move("Wall Hit", power: 40)),
                TestKit.Mon("Blade", hp: 200, moves: TestKit.Move("Blade Hit", power: 90))
            };
            var theirs = new List<PokemonState>
            {
                TestKit.Mon("Bruiser", hp: 400, moves: TestKit.Move("Crush", power: 150))
            };

            var (_, engine) = TestKit.Battle(1809, mine, theirs);
            var brain = new MonteCarloStrategy(
                new MonteCarloConfig
                {
                    SimulationsPerAction = 6,
                    MaxRolloutTurns = 6,
                    MaxDecisionMilliseconds = 60000
                },
                seed: 1809);

            var observer = new BattleObserver(dir, "Lab", 1809);

            engine.RunBattle(brain, new RandomMoveStrategy(), observer: observer);

            int ranked = observer.RankedCount;

            await observer.DisposeAsync();

            List<JsonElement> replacements = Replacements(await ReadAllRecords());
            JsonElement first = replacements.First(r => r.GetProperty("Side").GetString() == "P1");
            JsonElement teacher = first.GetProperty("TeacherScores");

            Assert.Equal(JsonValueKind.Array, teacher.ValueKind);
            Assert.Equal(ObservationSchema.ActionSlots, teacher.GetArrayLength());

            // Scored on the switch half, silent about the moves.
            List<JsonElement> entries = teacher.EnumerateArray().ToList();

            for (int i = 0; i < ObservationSchema.MoveSlots; i++)
                Assert.Equal(JsonValueKind.Null, entries[i].ValueKind);

            Assert.Contains(entries.Skip(ObservationSchema.MoveSlots),
                e => e.ValueKind == JsonValueKind.Number);

            Assert.True(ranked > 0);
        }

        // ---------------- the count the panel reports --------------------

        [Fact]
        public async Task RankedCount_CountsOnlyWhatCarriesARanking()
        {
            var mine = new List<PokemonState>
            {
                TestKit.Mon("Mine", hp: 200, moves: TestKit.Move("Hit", power: 40)),
                TestKit.Mon("Bench", hp: 200, moves: TestKit.Move("Bench Hit", power: 40))
            };
            var theirs = new List<PokemonState> { TestKit.Mon("Theirs", hp: 200) };

            var (state, engine) = TestKit.Battle(1810, mine, theirs);
            var observer = new BattleObserver(dir, "Lab", 1810);

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);

            Assert.Equal(0, observer.RankedCount);

            observer.OnDecision(state, state.Player1, "No ranking", legal, legal[0]);
            Assert.Equal(0, observer.RankedCount);

            var scores = new float[ObservationSchema.ActionSlots];

            for (int i = 0; i < scores.Length; i++)
                scores[i] = float.NaN;

            scores[0] = 0.5f;

            observer.OnDecision(state, state.Player1, "Ranked", legal, legal[0], new FakeTeacher(scores));
            Assert.Equal(1, observer.RankedCount);

            // All-NaN is "evaluated nothing", not a ranking.
            var empty = new float[ObservationSchema.ActionSlots];

            for (int i = 0; i < empty.Length; i++)
                empty[i] = float.NaN;

            observer.OnReplacement(state, state.Player1, "Ranked nothing", mine[1],
                new List<int> { 1 }, new FakeTeacher(empty));

            Assert.Equal(1, observer.RankedCount);

            observer.OnReplacement(state, state.Player1, "Ranked", mine[1],
                new List<int> { 1 }, new FakeTeacher(scores));

            Assert.Equal(2, observer.RankedCount);

            await observer.DisposeAsync();
        }

        /// <summary>Section 182 changed OnDecision and OnReplacement to
        /// take the ranking STRATEGY rather than a bare score vector, so
        /// that a recorder can also read which action it wanted and what
        /// risk appetite it ranked at. This stands in for one.</summary>
        sealed class FakeTeacher : ITeacherPolicy
        {
            readonly float[] scores;

            public FakeTeacher(float[] scores) => this.scores = scores;

            public IReadOnlyList<float>? LastMoveScores => scores;
            public int LastTeacherAction => -1;
            public float RiskAppetite => 0f;
        }
    }
}