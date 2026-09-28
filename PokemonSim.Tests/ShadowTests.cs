using System;
using System.IO;
using PokemonSim.Observation;
using PokemonSim.Shadow;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>Section 156. The ONNX shadow evaluator: graceful when the
    /// model is missing or broken, validated and functional when the
    /// author's pokemon_ai.onnx is on hand (it flows into the test output
    /// as PokemonSimData/pokemon_ai.onnx through the engine project's
    /// content items, same as the data files).</summary>
    public class ShadowTests
    {
        static string? ShippedModelPath()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "PokemonSimData", "pokemon_ai.onnx");
            return File.Exists(path) ? path : null;
        }

        [Fact]
        public void NoPath_ReportsObservationOnly_AndNeverCrashes()
        {
            var evaluator = new OnnxShadowEvaluator(null);

            Assert.False(evaluator.Status.Available);
            Assert.Contains("observation only", evaluator.Status.Description);
            Assert.Null(evaluator.Predict(new float[10], new float[] { 1, 1, 1, 1 }));
        }

        [Fact]
        public void MissingFile_ReportsWhereItLooked()
        {
            string missing = Path.Combine(Path.GetTempPath(), "definitely-not-here", "nope.onnx");

            var evaluator = new OnnxShadowEvaluator(missing);

            Assert.False(evaluator.Status.Available);
            Assert.Contains("no shadow model at", evaluator.Status.Description);
            Assert.Null(evaluator.Predict(new float[10], new float[] { 1, 1, 1, 1 }));
        }

        [Fact]
        public void GarbageFile_ReportsLoadFailure_NoCrash()
        {
            string garbage = Path.Combine(Path.GetTempPath(), $"garbage-{Guid.NewGuid():N}.onnx");
            File.WriteAllBytes(garbage, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            try
            {
                var evaluator = new OnnxShadowEvaluator(garbage);

                Assert.False(evaluator.Status.Available);
                Assert.Contains("failed to load", evaluator.Status.Description);
                Assert.Null(evaluator.Predict(new float[10], new float[] { 1, 1, 1, 1 }));
            }
            finally
            {
                File.Delete(garbage);
            }
        }

        [Fact]
        public void WrongInputLength_YieldsNoOpinion()
        {
            var evaluator = new OnnxShadowEvaluator(ShippedModelPath());

            if (!evaluator.Status.Available)
                return;   // no model on this machine - covered by the tests above

            Assert.Null(evaluator.Predict(new float[3], new float[] { 1, 1, 1, 1 }));
            Assert.Null(evaluator.Predict(new float[ObservationSchema.FeatureCount], new float[] { 1, 1 }));
        }

        [Fact]
        public void ShippedModel_LoadsValidatesAndScores()
        {
            string? path = ShippedModelPath();

            if (path == null)
                return;   // model not on this machine; the graceful paths above still hold

            using var evaluator = new OnnxShadowEvaluator(path);

            // §311. The shipped pokemon_ai.onnx is ten features wide with a
            // four-wide output - the §156 encoder, from before the vector
            // said anything about what a move WAS. It was already blind to
            // its own moves and structurally unable to propose a switch;
            // §311 retired its width outright, so it is refused rather than
            // fed the first ten columns of a vector where column 8 is a stat
            // stage instead of the weather.
            //
            // This test is therefore about the REFUSAL until a §311 model
            // ships: it must be a clean, named one rather than a crash or a
            // confident wrong answer.
            if (!evaluator.Status.Available)
            {
                Assert.Contains("\u00a7156", evaluator.Status.Description);
                Assert.Contains("retrained", evaluator.Status.Description);
                Assert.Null(evaluator.Predict(
                    new float[ObservationSchema.FeatureCount],
                    new float[ObservationSchema.ActionSlots]));
                return;
            }

            int width = evaluator.Status.InputWidth;
            int actions = evaluator.Status.OutputWidth;

            Assert.Contains($"{width} -> {actions}", evaluator.Status.Description);
            Assert.Contains(width, ObservationSchema.AcceptedFeatureCounts);
            Assert.Contains(actions, ObservationSchema.AcceptedActionCounts);

            var state = new float[width];
            state[0] = 1f;
            state[1] = 0.6f;

            // §176: mask the whole action space. A move-only model reads
            // its first four entries and behaves as it always did.
            var movesOpen = new float[ObservationSchema.ActionSlots];

            for (int i = 0; i < ObservationSchema.MoveSlots; i++)
                movesOpen[i] = 1f;

            ShadowPrediction? open = evaluator.Predict(state, movesOpen);

            Assert.NotNull(open);
            Assert.InRange(open!.PreferredMoveIndex, 0, ObservationSchema.MoveSlots - 1);
            Assert.Equal(actions, open.Scores.Length);

            // Legal-action masking: with one slot legal, that slot is the
            // preference no matter what the network thinks.
            var onlySlotTwo = new float[ObservationSchema.ActionSlots];
            onlySlotTwo[2] = 1f;

            ShadowPrediction? masked = evaluator.Predict(state, onlySlotTwo);

            Assert.NotNull(masked);
            Assert.Equal(2, masked!.PreferredMoveIndex);

            // Nothing legal (a Struggle turn with no bench): no opinion.
            Assert.Null(evaluator.Predict(state, new float[ObservationSchema.ActionSlots]));
        }
    }
}