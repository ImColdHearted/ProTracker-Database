using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>Section 171: the deep-learning player. The network only
    /// ever chooses a move it was actually asked about and that is
    /// actually legal; everything it has no output for falls through to
    /// the preserved baseline behaviour.</summary>
    public class Section171Tests
    {
        /// <summary>An evaluator that answers with whatever the test wants,
        /// and remembers what it was asked.</summary>
        sealed class FakeEvaluator : IShadowEvaluator
        {
            readonly Func<float[], float[], ShadowPrediction?> answer;

            public FakeEvaluator(Func<float[], float[], ShadowPrediction?> answer, bool available = true)
            {
                this.answer = answer;
                Status = new ShadowEvaluatorStatus { Available = available, Description = "fake" };
            }

            public ShadowEvaluatorStatus Status { get; }
            public int Calls { get; private set; }
            public float[]? LastState { get; private set; }
            public float[]? LastMask { get; private set; }

            public ShadowPrediction? Predict(float[] state, float[] legalMoveMask)
            {
                Calls++;
                LastState = state;
                LastMask = legalMoveMask;
                return answer(state, legalMoveMask);
            }
        }

        // §176 made PreferredMoveIndex a read of PreferredActionIndex, so
        // a fake states its preference over the whole action space. Four
        // scores still, because this section's model is move-only.
        static ShadowPrediction Prefers(int index) =>
            new()
            {
                PreferredActionIndex = index,
                Score = 1f,
                Scores = new float[ObservationSchema.MoveSlots]
            };

        /// <summary>A two-move attacker facing a wall, both sides alive.</summary>
        static (BattleState State, BattleEngine Engine, PokemonState Mine) Setup()
        {
            PokemonState mine = Mon("Learner", hp: 300,
                moves: new[] { Move("First", power: 40), Move("Second", power: 60) });
            PokemonState theirs = Mon("Wall", hp: 300, moves: Move("Wait", category: MoveCategory.Status, power: 0));

            (BattleState state, BattleEngine engine) = Battle(171,
                new List<PokemonState> { mine }, new List<PokemonState> { theirs });

            return (state, engine, mine);
        }

        [Fact]
        public void ItPlaysTheMoveTheModelPrefers()
        {
            (BattleState state, BattleEngine engine, _) = Setup();
            var evaluator = new FakeEvaluator((_, _) => Prefers(1));
            var strategy = new NeuralStrategy(evaluator);

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);
            BattleAction chosen = strategy.ChooseAction(state, state.Player1, legal);

            Assert.Equal(BattleActionType.Move, chosen.Type);
            Assert.Equal("Second", chosen.Move!.Name);
            Assert.Equal(1, evaluator.Calls);
            Assert.Equal(1, strategy.DecisionCount);
            Assert.Equal(1, strategy.ModelChoiceCount);
        }

        [Fact]
        public void ItAsksOnTheSameFeaturesTheObserverRecords()
        {
            (BattleState state, BattleEngine engine, _) = Setup();
            var evaluator = new FakeEvaluator((_, _) => Prefers(0));

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);
            new NeuralStrategy(evaluator).ChooseAction(state, state.Player1, legal);

            // Exactly what an observation record would carry - so the
            // network plays on the features it was trained on. §175: the
            // legal actions are part of that state, so they go in here too.
            Assert.Equal(ObserverEncoder.Encode(state, state.Player1, legal), evaluator.LastState);

            // §176: the mask covers the whole action space now, and its
            // first four entries are still the move mask.
            Assert.Equal(ObserverEncoder.LegalActionMask(legal, state.Player1), evaluator.LastMask);
        }

        [Fact]
        public void ADeclinedPredictionFallsBackToALegalMove()
        {
            (BattleState state, BattleEngine engine, _) = Setup();
            var evaluator = new FakeEvaluator((_, _) => null);
            var strategy = new NeuralStrategy(evaluator);

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);
            BattleAction chosen = strategy.ChooseAction(state, state.Player1, legal);

            Assert.Contains(chosen, legal);
            Assert.Equal(1, strategy.DecisionCount);
            Assert.Equal(0, strategy.ModelChoiceCount);
        }

        [Fact]
        public void AnIllegalPreferenceIsNeverPlayed()
        {
            // Slot 3 exists in the model's four outputs but not on this
            // Pokemon, which only knows two moves.
            (BattleState state, BattleEngine engine, _) = Setup();
            var strategy = new NeuralStrategy(new FakeEvaluator((_, _) => Prefers(3)));

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);
            BattleAction chosen = strategy.ChooseAction(state, state.Player1, legal);

            Assert.Contains(chosen, legal);
            Assert.NotEqual(3, chosen.Move?.Index ?? -1);
            Assert.Equal(0, strategy.ModelChoiceCount);
        }

        [Fact]
        public void AStruggleTurnNeverReachesTheModel()
        {
            // No usable move at all: the mask is all zero, so there is
            // nothing the four-output network could be asked about.
            PokemonState mine = Mon("Empty", hp: 200, moves: Move("Gone", power: 40, pp: 0));
            PokemonState theirs = Mon("Wall", hp: 200, moves: Move("Wait", category: MoveCategory.Status, power: 0));

            (BattleState state, BattleEngine engine) = Battle(172,
                new List<PokemonState> { mine }, new List<PokemonState> { theirs });

            mine.Moves[0].CurrentPP = 0;

            var evaluator = new FakeEvaluator((_, _) => Prefers(0));
            var strategy = new NeuralStrategy(evaluator);

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);
            BattleAction chosen = strategy.ChooseAction(state, state.Player1, legal);

            Assert.Equal(0, evaluator.Calls);
            Assert.Contains(chosen, legal);
            Assert.Equal(1, strategy.DecisionCount);
            Assert.Equal(0, strategy.ModelChoiceCount);
        }

        [Fact]
        public void ReplacementsStayTheBaselinePick()
        {
            (BattleState state, BattleEngine engine, _) = Setup();
            var evaluator = new FakeEvaluator((_, _) => Prefers(0));
            var strategy = new NeuralStrategy(evaluator);

            int picked = strategy.ChooseReplacement(state, state.Player1, new[] { 0 });

            Assert.Equal(0, picked);
            Assert.Equal(0, evaluator.Calls);   // no switch head to ask
        }

        [Fact]
        public void ItNamesItselfAndIsAnOrdinaryStrategy()
        {
            IBattleStrategy strategy = new NeuralStrategy(new FakeEvaluator((_, _) => null));

            Assert.Equal("Deep learning", strategy.Name);
        }
    }
}