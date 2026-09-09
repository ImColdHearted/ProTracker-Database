using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 185. The deep-learning model finally plays a person.
    ///
    /// Sections 156 to 184 kept it in the laboratory: it watched, it was
    /// measured against the Monte Carlo brain, it was trained and retrained
    /// - and the only opponent a player ever actually faced was the boss
    /// brain. Custom opponents are where that changes, and only there. A
    /// Boss Database fight has guides written against how it plays and a
    /// boss that played differently would invalidate them; a custom
    /// opponent has none to break.
    ///
    /// Two things had to be true first. Section 183's book only ever
    /// applied to the Monte Carlo brain, because the network never played
    /// anybody - so a model opponent would have repeated itself forever,
    /// which is exactly the feature 183 existed to prevent. And the book's
    /// nudge was sized against rollout means, which are win probabilities
    /// between 0 and about 1.1; a network's raw outputs are logits on
    /// whatever scale training left them. Applying a 0.14 nudge to those
    /// would be either meaningless or total. The softmax is what puts the
    /// two back into the same currency, and most of what is pinned here is
    /// that it does.
    /// </summary>
    public class Section185Tests
    {
        /// <summary>An evaluator that returns exactly the scores a test
        /// hands it, so what the network "thinks" is a fixture rather than
        /// a trained accident.</summary>
        sealed class ScoredEvaluator : IShadowEvaluator
        {
            readonly float[] scores;

            public ScoredEvaluator(float[] scores)
            {
                this.scores = scores;
                Status = new ShadowEvaluatorStatus
                {
                    Available = true,
                    InputWidth = ObservationSchema.FeatureCount,
                    OutputWidth = scores.Length,
                    Description = "test"
                };
            }

            public ShadowEvaluatorStatus Status { get; }

            public ShadowPrediction? Predict(float[] state, float[] legalActionMask)
            {
                int best = -1;
                float bestScore = float.NegativeInfinity;

                for (int i = 0; i < scores.Length && i < legalActionMask.Length; i++)
                {
                    if (legalActionMask[i] > 0 && scores[i] > bestScore)
                    {
                        bestScore = scores[i];
                        best = i;
                    }
                }

                if (best < 0)
                    return null;

                return new ShadowPrediction
                {
                    PreferredActionIndex = best,
                    Score = bestScore,
                    Scores = scores.ToArray()
                };
            }
        }

        static float[] Over(params (int Index, float Score)[] entries)
        {
            var scores = new float[ObservationSchema.ActionSlots];

            for (int i = 0; i < scores.Length; i++)
                scores[i] = float.NegativeInfinity;

            foreach ((int index, float score) in entries)
                scores[index] = score;

            return scores;
        }

        /// <summary>One active with two moves and a bench, which gives the
        /// network a real choice between slot 0 and slot 1.</summary>
        static (BattleState State, BattleEngine Engine) Fixture(int seed) => TestKit.Battle(seed,
            new List<PokemonState>
            {
                TestKit.Mon("Mine", hp: 200, moves: new[]
                {
                    TestKit.Move("First", power: 40),
                    TestKit.Move("Second", power: 40)
                }),
                TestKit.Mon("Bench", hp: 200, moves: TestKit.Move("Bench Hit", power: 40))
            },
            new List<PokemonState> { TestKit.Mon("Theirs", hp: 200, moves: TestKit.Move("Hit", power: 40)) });

        static BattleMemory BookPreferring(string situation, int action, int battles)
        {
            var memory = new BattleMemory();

            for (int i = 0; i < battles; i++)
            {
                BattleRecall recall = memory.Begin();

                recall.Note(situation, action);
                recall.Settle(won: false);
            }

            return memory;
        }

        static string Here(BattleState state) => BattleMemory.Situation(
            state.Player1.ActivePokemon, state.Player2.ActivePokemon);

        // ---------------- with no book, nothing changes -------------------

        [Fact]
        public void WithNoBookTheNetworkPlaysItsOwnPick()
        {
            var (state, engine) = Fixture(1850);

            var strategy = new NeuralStrategy(new ScoredEvaluator(Over((0, 1f), (1, 5f))));

            BattleAction chosen = strategy.ChooseAction(
                state, state.Player1, engine.GetLegalActions(state.Player1));

            Assert.Equal(1, chosen.Move!.Index);
        }

        [Fact]
        public void AnEmptyBookChangesNothingEither()
        {
            var (state, engine) = Fixture(1851);

            var strategy = new NeuralStrategy(
                new ScoredEvaluator(Over((0, 1f), (1, 1.02f))), new BattleMemory().Begin());

            BattleAction chosen = strategy.ChooseAction(
                state, state.Player1, engine.GetLegalActions(state.Player1));

            Assert.Equal(1, chosen.Move!.Index);
        }

        // ---------------- the softmax is the whole point ------------------

        [Fact]
        public void TheBookCanBreakANearTie()
        {
            var (state, engine) = Fixture(1852);

            // Two moves the network barely separates, and a book that has
            // watched it play the one it prefers over and over.
            BattleMemory memory = BookPreferring(Here(state), action: 1, battles: 12);

            var strategy = new NeuralStrategy(
                new ScoredEvaluator(Over((0, 1.00f), (1, 1.02f))), memory.Begin());

            BattleAction chosen = strategy.ChooseAction(
                state, state.Player1, engine.GetLegalActions(state.Player1));

            Assert.Equal(0, chosen.Move!.Index);
        }

        [Fact]
        public void TheBookCannotOverturnALandslide()
        {
            var (state, engine) = Fixture(1853);

            BattleMemory memory = BookPreferring(Here(state), action: 1, battles: 40);

            // Six logits apart is a share of roughly 0.9975 to 0.0025 - far
            // outside anything a preference is allowed to move.
            var strategy = new NeuralStrategy(
                new ScoredEvaluator(Over((0, 0f), (1, 6f))), memory.Begin());

            BattleAction chosen = strategy.ChooseAction(
                state, state.Player1, engine.GetLegalActions(state.Player1));

            Assert.Equal(1, chosen.Move!.Index);
        }

        [Fact]
        public void RawLogitScaleDoesNotDecideHowMuchTheBookIsWorth()
        {
            // The reason the softmax is there. These two evaluators disagree
            // by the same tiny SHARE but by wildly different raw numbers; a
            // nudge applied to the raw values would flip one and not the
            // other, which would make the book's strength depend on how a
            // training run happened to scale its outputs.
            foreach ((float low, float high) in new[] { (1.00f, 1.02f), (100.00f, 100.02f) })
            {
                var (state, engine) = Fixture(1854);

                BattleMemory memory = BookPreferring(Here(state), action: 1, battles: 12);

                var strategy = new NeuralStrategy(
                    new ScoredEvaluator(Over((0, low), (1, high))), memory.Begin());

                BattleAction chosen = strategy.ChooseAction(
                    state, state.Player1, engine.GetLegalActions(state.Player1));

                Assert.Equal(0, chosen.Move!.Index);
            }
        }

        [Fact]
        public void EnormousScoresDoNotOverflowTheShares()
        {
            var (state, engine) = Fixture(1855);

            BattleMemory memory = BookPreferring(Here(state), action: 1, battles: 5);

            var strategy = new NeuralStrategy(
                new ScoredEvaluator(Over((0, 1e30f), (1, 1e30f))), memory.Begin());

            BattleAction chosen = strategy.ChooseAction(
                state, state.Player1, engine.GetLegalActions(state.Player1));

            // Whatever it picks, it must be a real legal move rather than a
            // NaN comparison falling through to nothing.
            Assert.NotNull(chosen.Move);
            Assert.InRange(chosen.Move!.Index, 0, 1);
        }

        // ---------------- the book never breaks the rules ------------------

        [Fact]
        public void TheBookNeverResurrectsAnIllegalAction()
        {
            var (state, engine) = Fixture(1856);

            // The book has never seen slot 3, so it is the most "novel"
            // thing on the board - and it is not legal, because this
            // Pokemon has two moves.
            BattleMemory memory = BookPreferring(Here(state), action: 0, battles: 20);

            var strategy = new NeuralStrategy(
                new ScoredEvaluator(Over((0, 1f), (1, 1.01f), (3, 0.9f))), memory.Begin());

            BattleAction chosen = strategy.ChooseAction(
                state, state.Player1, engine.GetLegalActions(state.Player1));

            Assert.NotNull(chosen.Move);
            Assert.InRange(chosen.Move!.Index, 0, 1);
        }

        [Fact]
        public void AForcedChoiceIsNotSecondGuessed()
        {
            var (state, engine) = TestKit.Battle(1857,
                new List<PokemonState>
                {
                    TestKit.Mon("Mine", hp: 200, moves: TestKit.Move("Only", power: 40))
                },
                new List<PokemonState> { TestKit.Mon("Theirs", hp: 200) });

            BattleMemory memory = BookPreferring(Here(state), action: 0, battles: 30);

            var strategy = new NeuralStrategy(
                new ScoredEvaluator(Over((0, 1f))), memory.Begin());

            BattleAction chosen = strategy.ChooseAction(
                state, state.Player1, engine.GetLegalActions(state.Player1));

            Assert.Equal(0, chosen.Move!.Index);
        }

        // ---------------- and it writes what it played --------------------

        [Fact]
        public void TheNetworkNotesWhatItPlayed()
        {
            var (state, engine) = Fixture(1858);

            var memory = new BattleMemory();
            BattleRecall recall = memory.Begin();

            var strategy = new NeuralStrategy(new ScoredEvaluator(Over((0, 1f), (1, 5f))), recall);

            strategy.ChooseAction(state, state.Player1, engine.GetLegalActions(state.Player1));

            Assert.Equal(1, recall.NoteCount);
            Assert.Equal(0, memory.SituationCount);   // not until the battle ends

            recall.Settle(won: true);

            Assert.Equal(1, memory.SituationCount);
        }

        [Fact]
        public void AReplacementIsNotedOnItsOwnPage()
        {
            var (state, _) = Fixture(1859);

            var memory = new BattleMemory();
            BattleRecall recall = memory.Begin();

            var strategy = new NeuralStrategy(
                new ScoredEvaluator(Over((ObservationSchema.MoveSlots + 1, 5f))), recall);

            state.Player1.ActivePokemon.CurrentHP = 0;

            int chosen = strategy.ChooseReplacement(state, state.Player1, new List<int> { 1 });

            Assert.Equal(1, chosen);
            Assert.Equal(1, recall.NoteCount);

            recall.Settle(won: true);

            // The replacement page is keyed on what is standing opposite,
            // not on the matchup the move decisions used.
            Assert.NotEqual(0.0,
                memory.Adjust(BattleMemory.ReplacementSituation(state.Player2.ActivePokemon),
                              ObservationSchema.MoveSlots + 1, 4));
            Assert.Equal(0.0, memory.Adjust(Here(state), ObservationSchema.MoveSlots + 1, 4));
        }

        [Fact]
        public void TheNetworkStillFallsThroughWhenItCannotAnswer()
        {
            var (state, engine) = Fixture(1860);

            // Nothing legal scored: the evaluator declines, and the player
            // has to reach the preserved baseline rather than throw.
            var strategy = new NeuralStrategy(
                new ScoredEvaluator(Over((3, 5f))), new BattleMemory().Begin());

            BattleAction chosen = strategy.ChooseAction(
                state, state.Player1, engine.GetLegalActions(state.Player1));

            Assert.NotNull(chosen);
            Assert.Equal(0, strategy.ModelChoiceCount);
            Assert.Equal(1, strategy.DecisionCount);
        }
    }
}