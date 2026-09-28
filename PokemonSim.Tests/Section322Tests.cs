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
    /// §322. The player reads what the observer writes.
    ///
    /// §319 moved BattleObserver to FairEncoder and left NeuralStrategy -
    /// the class that actually plays - encoding with ObserverEncoder. The
    /// two had been the same call since §171, so changing one and not the
    /// other looked like one change.
    ///
    /// How it failed is the reason this file is mostly about widths. A fair
    /// model asks for FairEncoder.FeatureCount; the omniscient vector is
    /// ObserverEncoder.TotalFeatures; OnnxShadowEvaluator.Predict returns
    /// null when the state is narrower than the model, and NeuralStrategy
    /// reads null as "no opinion this turn" and answers with the baseline.
    /// So the model loaded, the run was labelled deep learning, every
    /// decision was a random move, and nothing anywhere said so.
    ///
    /// Two kinds of test therefore: the vector the model is handed is the
    /// FAIR one, from the ACTING side's point of view - and a width that
    /// cannot work is refused before the run instead of discovered never.
    /// </summary>
    public class Section322Tests
    {
        /// <summary>An evaluator that answers every turn and keeps what it
        /// was shown. Answering matters: a fake that declined would send
        /// the strategy down the fallback path and prove nothing about
        /// what it encodes.</summary>
        sealed class Recorder : IShadowEvaluator
        {
            public Recorder(int inputWidth, bool available = true)
            {
                Status = new ShadowEvaluatorStatus
                {
                    Available = available,
                    InputWidth = inputWidth,
                    OutputWidth = ObservationSchema.ActionSlots,
                    Description = available ? "test recorder" : "nothing loaded"
                };
            }

            public ShadowEvaluatorStatus Status { get; }

            public float[]? LastState { get; private set; }
            public int Calls { get; private set; }

            public ShadowPrediction? Predict(float[] state, float[] legalActionMask)
            {
                LastState = state;
                Calls++;

                for (int i = 0; i < legalActionMask.Length; i++)
                {
                    if (legalActionMask[i] > 0)
                    {
                        return new ShadowPrediction
                        {
                            PreferredActionIndex = i,
                            Score = 1f,
                            Scores = new float[legalActionMask.Length]
                        };
                    }
                }

                return null;
            }
        }

        static (BattleState State, BattleEngine Engine) Fight(int seed = 322)
        {
            var mine = new List<PokemonState>
            {
                TestKit.Mon("Mine", PokemonType.Normal, moves: new[] { TestKit.Move("My Hit") }),
                TestKit.Mon("MyBench", PokemonType.Water, moves: new[] { TestKit.Move("Bench Hit") })
            };

            var theirs = new List<PokemonState>
            {
                TestKit.Mon("Theirs", PokemonType.Fire, moves: new[] { TestKit.Move("Their Hit") }),
                TestKit.Mon("TheirBench", PokemonType.Grass, moves: new[] { TestKit.Move("Their Bench Hit") })
            };

            return TestKit.Battle(seed, mine, theirs);
        }

        // ==============================================================
        // The premise. Without this the bug could not have happened and
        // every test below would be vacuously true.
        // ==============================================================

        [Fact]
        public void TheFairAndOmniscientVectorsAreDifferentWidths()
        {
            Assert.NotEqual(ObserverEncoder.TotalFeatures, FairEncoder.FeatureCount);
        }

        [Fact]
        public void TheSchemaWidthIsTheFairOne()
        {
            Assert.Equal(FairEncoder.FeatureCount, ObservationSchema.FeatureCount);
        }

        // ==============================================================
        // What the model is handed
        // ==============================================================

        [Fact]
        public void ThePlayerHandsTheModelTheFairVector()
        {
            (BattleState state, _) = Fight();

            var recorder = new Recorder(FairEncoder.FeatureCount);
            var player = new NeuralStrategy(recorder);

            player.ChooseAction(state, state.Player1, LegalActions.For(state, state.Player1));

            Assert.Equal(1, recorder.Calls);
            Assert.NotNull(recorder.LastState);
            Assert.Equal(FairEncoder.FeatureCount, recorder.LastState!.Length);
        }

        [Fact]
        public void ItIsTheFairVectorColumnForColumn()
        {
            // Width alone would pass on any encoder that happened to agree.
            // This is the whole vector, so the §319 leak guarantees carry
            // over to play rather than only to recording.
            (BattleState state, _) = Fight();

            var recorder = new Recorder(FairEncoder.FeatureCount);
            var player = new NeuralStrategy(recorder) { RiskAppetite = 0.25f };

            IReadOnlyList<BattleAction> legal = LegalActions.For(state, state.Player1);

            // Taken BEFORE the call, so the comparison cannot be made true
            // by anything ChooseAction does on its way past.
            float[] expected = FairEncoder.Encode(state, state.Player1, legal, 0.25f);

            player.ChooseAction(state, state.Player1, legal);

            Assert.Equal(expected, recorder.LastState!);
        }

        [Fact]
        public void TheOmniscientPlayerStillReadsTheOldVector()
        {
            (BattleState state, _) = Fight();

            var recorder = new Recorder(ObserverEncoder.TotalFeatures);
            var player = new NeuralStrategy(recorder) { Omniscient = true };

            player.ChooseAction(state, state.Player1, LegalActions.For(state, state.Player1));

            Assert.Equal(ObserverEncoder.TotalFeatures, recorder.LastState!.Length);
        }

        [Fact]
        public void EncoderWidthAnnouncesWhichOneItIs()
        {
            var fair = new NeuralStrategy(new Recorder(FairEncoder.FeatureCount));
            var omniscient = new NeuralStrategy(new Recorder(ObserverEncoder.TotalFeatures))
            {
                Omniscient = true
            };

            Assert.Equal(FairEncoder.FeatureCount, fair.EncoderWidth);
            Assert.Equal(ObserverEncoder.TotalFeatures, omniscient.EncoderWidth);
        }

        // ==============================================================
        // Perspective. The harness this was found while building puts the
        // network on BOTH seats, which §171 never did.
        // ==============================================================

        [Fact]
        public void PlayerTwoIsShownPlayerTwosSide()
        {
            (BattleState state, _) = Fight();

            var recorder = new Recorder(FairEncoder.FeatureCount);
            var player = new NeuralStrategy(recorder);

            IReadOnlyList<BattleAction> legal = LegalActions.For(state, state.Player2);

            float[] expected = FairEncoder.Encode(state, state.Player2, legal);

            player.ChooseAction(state, state.Player2, legal);

            Assert.Equal(expected, recorder.LastState!);
        }

        [Fact]
        public void TheTwoSeatsAreNotShownTheSameThing()
        {
            // Guards the test above: if the encoder ignored its actor
            // argument, PlayerTwoIsShownPlayerTwosSide would still pass.
            (BattleState state, _) = Fight();

            Assert.NotEqual(
                FairEncoder.Encode(state, state.Player1, LegalActions.For(state, state.Player1)),
                FairEncoder.Encode(state, state.Player2, LegalActions.For(state, state.Player2)));
        }

        [Fact]
        public void ReplacementsUseTheSameEncoder()
        {
            // §176's other entry point. It had the same line and the same
            // bug, and a post-faint switch is exactly where a model that
            // silently stopped choosing would be hardest to notice.
            (BattleState state, _) = Fight();

            var recorder = new Recorder(FairEncoder.FeatureCount);
            var player = new NeuralStrategy(recorder);

            player.ChooseReplacement(state, state.Player1, new[] { 1 });

            Assert.Equal(1, recorder.Calls);
            Assert.Equal(FairEncoder.FeatureCount, recorder.LastState!.Length);
        }

        // ==============================================================
        // The refusal: the check that turns this failure loud
        // ==============================================================

        [Fact]
        public void AModelOfTheRightWidthIsAccepted()
        {
            Assert.Null(NeuralStrategy.Incompatibility(new Recorder(FairEncoder.FeatureCount)));
        }

        [Fact]
        public void TheExactWidthTheOldPlayerWroteIsRefused()
        {
            // The regression. A model trained on the current corpus reads
            // FairEncoder.FeatureCount; the vector §319 left this class
            // writing was ObserverEncoder.TotalFeatures. That pairing is
            // the bug, and it must now be a refusal.
            string? problem = NeuralStrategy.Incompatibility(
                new Recorder(ObserverEncoder.TotalFeatures));

            Assert.NotNull(problem);
            Assert.Contains(ObserverEncoder.TotalFeatures.ToString(), problem!);
            Assert.Contains(FairEncoder.FeatureCount.ToString(), problem!);
        }

        [Fact]
        public void TheOmniscientPlayerRefusesAFairModel()
        {
            Assert.NotNull(NeuralStrategy.Incompatibility(
                new Recorder(FairEncoder.FeatureCount), omniscient: true));

            Assert.Null(NeuralStrategy.Incompatibility(
                new Recorder(ObserverEncoder.TotalFeatures), omniscient: true));
        }

        [Fact]
        public void AnUnavailableModelReportsItsOwnReason()
        {
            string? problem = NeuralStrategy.Incompatibility(
                new Recorder(FairEncoder.FeatureCount, available: false));

            Assert.Equal("nothing loaded", problem);
        }

        [Fact]
        public void NoEvaluatorAtAllIsRefusedRatherThanCrashed()
        {
            Assert.NotNull(NeuralStrategy.Incompatibility(null));
        }
    }
}
