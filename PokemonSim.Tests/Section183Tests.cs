using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 183. The opponent's book: what it remembers between battles,
    /// and the several things it must not do with what it remembers.
    ///
    /// Every battle before this section was decided from nothing, so two
    /// identical positions a week apart produced identical play. Against a
    /// fixed boss team that means a player learns the script in half a dozen
    /// attempts. The brain now keeps a small book per matchup and shades
    /// away from lines it has been repeating and toward lines that won.
    ///
    /// Most of what is pinned here is restraint. An empty book must change
    /// nothing at all. A battle must not teach itself mid-way. A battle
    /// nobody finished must teach nothing. A teacher, which advises without
    /// playing, must never write. A corrupt file must cost the feature and
    /// not the battle. And what is stored must stay two species names, an
    /// action and two counts - it is a book on positions, not on a person.
    /// </summary>
    public class Section183Tests
    {
        static PokemonState Mon(string species) => TestKit.Mon(species, hp: 200);

        static string Matchup(string mine, string theirs) =>
            BattleMemory.Situation(Mon(mine), Mon(theirs));

        static BattleMemory BookWith(string situation, int action, int battles, int wins)
        {
            var memory = new BattleMemory();

            for (int i = 0; i < battles; i++)
            {
                BattleRecall recall = memory.Begin();

                recall.Note(situation, action);
                recall.Settle(won: i < wins);
            }

            return memory;
        }

        // ---------------- an empty book changes nothing ------------------

        [Fact]
        public void AFreshBookHasNothingToSay()
        {
            var memory = new BattleMemory();

            Assert.Equal(0, memory.SituationCount);
            Assert.Equal(0.0, memory.Adjust(Matchup("gengar", "skarmory"), 0, 4));
        }

        [Fact]
        public void AForcedChoiceIsNeverSecondGuessed()
        {
            // One legal action is not a decision, so there is nothing to
            // vary and nothing to learn from varying.
            BattleMemory memory = BookWith(Matchup("a", "b"), action: 0, battles: 10, wins: 10);

            Assert.Equal(0.0, memory.Adjust(Matchup("a", "b"), 0, legalCount: 1));
            Assert.NotEqual(0.0, memory.Adjust(Matchup("a", "b"), 0, legalCount: 4));
        }

        [Fact]
        public void ABrainWithAnEmptyBookPlaysTheBattleItAlwaysPlayed()
        {
            // The feature costs nothing until it has something to say, and
            // "nothing" has to mean identical - not merely similar.
            static (BattleState, BattleEngine) Fixture() => TestKit.Battle(1830,
                new List<PokemonState>
                {
                    TestKit.Mon("A1", hp: 180, moves: new[]
                    {
                        TestKit.Move("A Hit", power: 60),
                        TestKit.Move("A Jab", power: 45)
                    }),
                    TestKit.Mon("A2", hp: 180, moves: TestKit.Move("A Two", power: 60))
                },
                new List<PokemonState>
                {
                    TestKit.Mon("B1", hp: 180, moves: TestKit.Move("B Hit", power: 60)),
                    TestKit.Mon("B2", hp: 180, moves: TestKit.Move("B Two", power: 60))
                });

            var config = new MonteCarloConfig
            {
                SimulationsPerAction = 6,
                MaxRolloutTurns = 6,
                MaxDecisionMilliseconds = 60000
            };

            var (plain, plainEngine) = Fixture();
            plainEngine.RunBattle(new MonteCarloStrategy(config, seed: 55), new RandomMoveStrategy());

            var (booked, bookedEngine) = Fixture();
            bookedEngine.RunBattle(
                new MonteCarloStrategy(config, seed: 55, recall: new BattleMemory().Begin()),
                new RandomMoveStrategy());

            Assert.Equal(plain.Outcome, booked.Outcome);
            Assert.Equal(plain.TurnNumber, booked.TurnNumber);
            Assert.Equal(plain.Rng.Clone().Next(1000000), booked.Rng.Clone().Next(1000000));
            Assert.Equal(
                string.Join("|", plain.Player2.Team.Select(p => p.Species + ":" + p.CurrentHP)),
                string.Join("|", booked.Player2.Team.Select(p => p.Species + ":" + p.CurrentHP)));
        }

        // ---------------- varying, and learning --------------------------

        [Fact]
        public void ARepeatedLineIsShadedDownAndAnUnusedOneIsNot()
        {
            string here = Matchup("garchomp", "skarmory");
            BattleMemory memory = BookWith(here, action: 0, battles: 10, wins: 5);

            double repeated = memory.Adjust(here, 0, legalCount: 4);
            double untouched = memory.Adjust(here, 3, legalCount: 4);

            Assert.True(repeated < 0, $"a line used every time should shade down, got {repeated}");
            Assert.True(untouched > 0, $"an unused line should shade up, got {untouched}");
            Assert.True(untouched > repeated);
        }

        [Fact]
        public void AWinningLineIsPreferredToALosingOneAtTheSameFrequency()
        {
            var memory = new BattleMemory();
            string here = Matchup("gyarados", "ferrothorn");

            // Same number of uses, opposite results.
            for (int i = 0; i < 12; i++)
            {
                BattleRecall recall = memory.Begin();

                recall.Note(here, 0);
                recall.Settle(won: true);

                recall = memory.Begin();
                recall.Note(here, 1);
                recall.Settle(won: false);
            }

            Assert.True(memory.Adjust(here, 0, 4) > memory.Adjust(here, 1, 4),
                        "the line that keeps winning should be preferred");
        }

        [Fact]
        public void OneLuckyBattleIsNotAConviction()
        {
            // Isolate the outcome term by holding the use count fixed and
            // moving only the result: the gap between "always won" and
            // "always lost" IS that term, because the novelty half depends
            // on use alone and cancels.
            string here = Matchup("a", "b");

            static double Gap(string situation, int battles)
            {
                double won = BookWith(situation, 0, battles, wins: battles).Adjust(situation, 0, 4);
                double lost = BookWith(situation, 0, battles, wins: 0).Adjust(situation, 0, 4);

                return won - lost;
            }

            double afterOne = Gap(here, 1);
            double afterFive = Gap(here, 5);
            double afterForty = Gap(here, 40);

            Assert.True(afterOne < afterFive,
                        $"one battle ({afterOne:F4}) should count for less than five ({afterFive:F4})");
            Assert.True(afterFive < afterForty,
                        $"five battles ({afterFive:F4}) should count for less than forty ({afterForty:F4})");

            // And even a perfect record never becomes a certainty.
            Assert.True(afterForty <= 2 * BattleMemory.OutcomeWeight + 1e-9, afterForty.ToString());
        }

        [Fact]
        public void TheNudgeIsNeverBigEnoughToOverrideRealJudgement()
        {
            // Rollout scores run about 0 to 1.1. A book that could move one
            // by more than a tenth would stop being a preference and start
            // being a decision.
            double ceiling = BattleMemory.NoveltyWeight + BattleMemory.OutcomeWeight;

            foreach (int battles in new[] { 1, 5, 20, 100, 500 })
            {
                foreach (int wins in new[] { 0, battles / 2, battles })
                {
                    string here = Matchup("x", "y");
                    BattleMemory memory = BookWith(here, 0, battles, wins);

                    foreach (int action in new[] { 0, 1, 9 })
                    {
                        double nudge = memory.Adjust(here, action, 10);

                        Assert.True(Math.Abs(nudge) <= ceiling + 1e-9,
                                    $"{battles} battles, {wins} wins, action {action}: {nudge}");
                    }
                }
            }
        }

        [Fact]
        public void TheBookStaysBoundedHoweverManyBattlesArePlayed()
        {
            string here = Matchup("a", "b");
            BattleMemory memory = BookWith(here, 0, battles: 400, wins: 200);

            using JsonDocument document = JsonDocument.Parse(memory.ToJson());

            string cell = document.RootElement
                .GetProperty("matchups").GetProperty(here).GetProperty("0").GetString()!;

            double uses = double.Parse(cell.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture);

            Assert.True(uses <= BattleMemory.RecencyWindow + 1,
                        $"four hundred battles left {uses} uses on the books");
        }

        // ---------------- when it learns, and when it does not -----------

        [Fact]
        public void ABattleDoesNotTeachItselfWhileItIsStillBeingPlayed()
        {
            // A brain that learned from its own choice the moment it made
            // it would start shading against a move mid-battle, which is a
            // different and much worse feature than the one asked for.
            var memory = new BattleMemory();
            BattleRecall recall = memory.Begin();
            string here = Matchup("a", "b");

            for (int i = 0; i < 5; i++)
                recall.Note(here, 0);

            Assert.Equal(5, recall.NoteCount);
            Assert.Equal(0.0, recall.Adjust(here, 0, 4));
            Assert.Equal(0, memory.SituationCount);

            recall.Settle(won: true);

            Assert.NotEqual(0.0, recall.Adjust(here, 0, 4));
            Assert.Equal(1, memory.SituationCount);
        }

        [Fact]
        public void ABattleNobodyFinishedTeachesNothing()
        {
            var memory = new BattleMemory();
            BattleRecall recall = memory.Begin();

            recall.Note(Matchup("a", "b"), 0);

            // No Settle: the window was closed, the app was quit, the
            // battle was abandoned. Nothing is concluded from it.
            Assert.Equal(0, memory.SituationCount);
            Assert.False(recall.Settled);
        }

        [Fact]
        public void SettlingTwiceCountsOnce()
        {
            var memory = new BattleMemory();
            BattleRecall recall = memory.Begin();
            string here = Matchup("a", "b");

            recall.Note(here, 0);
            recall.Settle(won: true);

            double once = memory.Adjust(here, 0, 4);

            recall.Settle(won: true);
            recall.Settle(won: false);

            Assert.Equal(once, memory.Adjust(here, 0, 4));
        }

        [Fact]
        public void OnlyABrainThatPlayedWritesToTheBook()
        {
            // Advise is what a teacher calls. A teacher's opinion about a
            // battle it is not playing is not a battle it played, and a
            // book full of hypotheticals would be a book about nobody.
            var memory = new BattleMemory();
            BattleRecall recall = memory.Begin();

            var (state, engine) = TestKit.Battle(1831,
                new List<PokemonState>
                {
                    TestKit.Mon("Mine", hp: 200, moves: new[]
                    {
                        TestKit.Move("One", power: 40),
                        TestKit.Move("Two", power: 60)
                    })
                },
                new List<PokemonState> { TestKit.Mon("Theirs", hp: 200) });

            var brain = new MonteCarloStrategy(
                new MonteCarloConfig
                {
                    SimulationsPerAction = 4,
                    MaxRolloutTurns = 4,
                    MaxDecisionMilliseconds = 60000
                },
                seed: 1831, recall: recall);

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);

            brain.Advise(state, state.Player1, legal);
            Assert.Equal(0, recall.NoteCount);

            brain.ChooseAction(state, state.Player1, legal);
            Assert.Equal(1, recall.NoteCount);
        }

        [Fact]
        public void WhoComesInIsItsOwnQuestion()
        {
            // What just fainted is gone; the choice is about what is
            // standing opposite, so it gets its own key rather than
            // sharing one with the move decision that preceded it.
            string moves = BattleMemory.Situation(Mon("gengar"), Mon("skarmory"));
            string replacement = BattleMemory.ReplacementSituation(Mon("skarmory"));

            Assert.NotEqual(moves, replacement);
            Assert.StartsWith("replace|", replacement);
            Assert.Contains("skarmory", replacement);
        }

        // ---------------- the file ---------------------------------------

        [Fact]
        public void TheBookSurvivesBeingWrittenAndReadBack()
        {
            string here = Matchup("garchomp", "skarmory");
            BattleMemory before = BookWith(here, action: 2, battles: 7, wins: 5);

            BattleMemory after = BattleMemory.FromJson(before.ToJson());

            Assert.Equal(before.SituationCount, after.SituationCount);
            Assert.Equal(before.Adjust(here, 2, 4), after.Adjust(here, 2, 4), 6);
            Assert.Equal(before.Adjust(here, 3, 4), after.Adjust(here, 3, 4), 6);
        }

        [Fact]
        public void ARuinedFileCostsTheFeatureAndNotTheBattle()
        {
            foreach (string? broken in new[]
            {
                null, "", "   ", "{", "[]", "null", "not json at all",
                "{\"version\":99,\"matchups\":{}}",
                "{\"version\":1}",
                "{\"version\":1,\"matchups\":[]}",
                "{\"version\":1,\"matchups\":{\"a|b\":\"nonsense\"}}",
                "{\"version\":1,\"matchups\":{\"a|b\":{\"x\":\"1,1\"}}}",
                "{\"version\":1,\"matchups\":{\"a|b\":{\"0\":\"NaN,1\"}}}",
                "{\"version\":1,\"matchups\":{\"a|b\":{\"0\":\"-5,-5\"}}}"
            })
            {
                BattleMemory memory = BattleMemory.FromJson(broken);

                Assert.Equal(0, memory.SituationCount);
                Assert.Equal(0.0, memory.Adjust(Matchup("a", "b"), 0, 4));
            }
        }

        [Fact]
        public void MoreWinsThanBattlesIsRefusedRatherThanBelieved()
        {
            BattleMemory memory = BattleMemory.FromJson(
                "{\"version\":1,\"matchups\":{\"a|b\":{\"0\":\"4,900\"}}}");

            // The row is kept but the impossible part is clamped, so a
            // hand-edited file cannot manufacture a certainty.
            Assert.Equal(1, memory.SituationCount);
            Assert.True(Math.Abs(memory.Adjust("a|b", 0, 4)) <=
                        BattleMemory.NoveltyWeight + BattleMemory.OutcomeWeight + 1e-9);
        }

        [Fact]
        public void TheStoredShapeIsMatchupsAndCountsAndNothingElse()
        {
            // The privacy claim, as an assertion: two species names, an
            // action index, and two numbers. No teams, no items, no turn
            // order, no timestamps, nothing about who was playing.
            var memory = new BattleMemory();

            for (int i = 0; i < 3; i++)
            {
                BattleRecall recall = memory.Begin();

                recall.Note(Matchup("garchomp", "skarmory"), 1);
                recall.Note(BattleMemory.ReplacementSituation(Mon("skarmory")), 5);
                recall.Settle(won: i == 0);
            }

            using JsonDocument document = JsonDocument.Parse(memory.ToJson());
            JsonElement root = document.RootElement;

            Assert.Equal(new[] { "version", "matchups" },
                         root.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(BattleMemory.FileVersion, root.GetProperty("version").GetInt32());

            foreach (JsonProperty situation in root.GetProperty("matchups").EnumerateObject())
            {
                Assert.Matches(@"^(replace\|)?[a-z0-9 .'\-]*(\|[a-z0-9 .'\-]*)?$", situation.Name);

                foreach (JsonProperty action in situation.Value.EnumerateObject())
                {
                    Assert.Matches(@"^\d+$", action.Name);
                    Assert.Matches(@"^\d+(\.\d+)?,\d+(\.\d+)?$", action.Value.GetString()!);
                }
            }
        }
    }
}