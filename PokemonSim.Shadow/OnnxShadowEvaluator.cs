using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PokemonSim.Observation;

namespace PokemonSim.Shadow
{
    /// <summary>
    /// Section 156. The shadow model behind IShadowEvaluator: loads a
    /// pokemon_ai.onnx (a policy over the four move slots) and scores
    /// observed states. Deliberately name-agnostic about tensors: the
    /// originally shipped model was exported by a newer torch than its
    /// script suggests, so its input is called "x" and its output
    /// "linear_2" rather than "input"/"output" - validation therefore goes
    /// by SHAPE: exactly one float input (batch 1 or dynamic), exactly one
    /// output whose last dimension is 4. Anything else marks the evaluator
    /// unavailable with a description that says precisely what was wrong,
    /// and the Simulator carries on collecting observations without a
    /// shadow opinion. Repeated inference failures disable it for the rest
    /// of the run rather than spamming a broken model.
    ///
    /// Section 175 made the input WIDTH a property of the model rather
    /// than a constant, and section 176 did the same for the output. A
    /// model declares itself by shape and is accepted if its input is one
    /// of ObservationSchema.AcceptedFeatureCounts and its output one of
    /// AcceptedActionCounts. Because every past feature vector is a strict
    /// PREFIX of the current one, entry for entry, an older model is fed
    /// the front of a current state and behaves exactly as it did; and
    /// because the four move slots are still the first four outputs, a
    /// move-only model from before the switch head still answers move
    /// questions and simply never proposes a switch. Upgrading the encoder
    /// orphans nobody's model.
    /// </summary>
    public sealed class OnnxShadowEvaluator : IShadowEvaluator, IDisposable
    {
        const int MaxFailures = 3;

        readonly InferenceSession? session;
        readonly string inputName = "";

        // Section 175: how many of the state's features this particular
        // model wants. Predict feeds it that prefix.
        readonly int inputWidth;

        // Section 176: how many actions it scores - four (moves only) or
        // ActionSlots (moves and switches).
        readonly int outputWidth;

        /// <summary>§320: WHICH output carries the policy. A three-headed
        /// model's first result is not automatically the one to score with,
        /// so the name is kept rather than the position.</summary>
        readonly string outputName = "";
        int failures;
        ShadowEvaluatorStatus status;

        public ShadowEvaluatorStatus Status => status;

        public OnnxShadowEvaluator(string? modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                status = new ShadowEvaluatorStatus
                {
                    Available = false,
                    Description = "no shadow model found - observation only"
                };
                return;
            }

            if (!File.Exists(modelPath))
            {
                status = new ShadowEvaluatorStatus
                {
                    Available = false,
                    Description = $"no shadow model at {modelPath} - observation only"
                };
                return;
            }

            try
            {
                session = new InferenceSession(modelPath);

                string? problem = Validate(session, out inputName, out outputName,
                                           out inputWidth, out outputWidth);

                if (problem != null)
                {
                    session.Dispose();
                    session = null;
                    status = new ShadowEvaluatorStatus
                    {
                        Available = false,
                        Description = $"shadow model incompatible: {problem}"
                    };
                    return;
                }

                var notes = new List<string>();

                if (inputWidth != ObservationSchema.FeatureCount)
                    notes.Add(GenerationOf(inputWidth) + " features");

                if (outputWidth <= ObservationSchema.MoveSlots)
                    notes.Add("no switch head");

                status = new ShadowEvaluatorStatus
                {
                    Available = true,
                    InputWidth = inputWidth,
                    OutputWidth = outputWidth,
                    Description = $"shadow model loaded ({inputWidth} -> {outputWidth}"
                        + (notes.Count > 0 ? ", " + string.Join(", ", notes) : string.Empty)
                        + $"): {Path.GetFileName(modelPath)}"
                };
            }
            catch (Exception ex)
            {
                session?.Dispose();
                session = null;
                status = new ShadowEvaluatorStatus
                {
                    Available = false,
                    Description = "shadow model failed to load: " + FirstLine(ex.Message)
                };
            }
        }

        static string? Validate(InferenceSession session, out string inputName,
                                out string outputName,
                                out int inputWidth, out int outputWidth)
        {
            inputName = "";
            outputName = "";
            inputWidth = 0;
            outputWidth = 0;

            if (session.InputMetadata.Count != 1)
                return $"expected 1 input tensor, found {session.InputMetadata.Count}";

            KeyValuePair<string, NodeMetadata> input = session.InputMetadata.First();

            int[] inDims = input.Value.Dimensions;

            // Section 175: any width this build knows, and the model says
            // which. Section 176 made the list a schema constant.
            bool widthOk =
                inDims.Length == 2 &&
                Array.IndexOf(ObservationSchema.AcceptedFeatureCounts, inDims[1]) >= 0;

            bool inputOk =
                input.Value.ElementType == typeof(float) &&
                inDims.Length == 2 &&
                (inDims[0] == 1 || inDims[0] < 0) &&
                widthOk;

            if (!inputOk)
            {
                // §311. A model asking for a RETIRED width is not a broken
                // file, it is an old one, and saying so is the difference
                // between "retrain this" and "something is wrong with my
                // exporter". It is still refused: the prefix rule that made
                // an old width safe to feed is gone, so handing it the first
                // 205 columns of a 481-column vector would score nonsense
                // rather than fail, which is the worse outcome.
                if (inDims.Length == 2 &&
                    Array.IndexOf(ObservationSchema.RetiredFeatureCounts, inDims[1]) >= 0)
                {
                    return $"input \"{input.Key}\" asks for {inDims[1]} features, which was the "
                         + $"{GenerationOf(inDims[1])} layout. §311 re-laid the vector, so those "
                         + $"columns no longer mean what they meant - this model has to be retrained "
                         + $"at {ObservationSchema.FeatureCount}.";
                }

                return $"input \"{input.Key}\" is {input.Value.ElementType.Name}[{string.Join(",", inDims)}], "
                     + $"expected float[1,N] with N one of {string.Join("/", ObservationSchema.AcceptedFeatureCounts)}";
            }

            inputWidth = inDims[1];

            // §320. A model may now carry three heads - policy, value and
            // opponent prediction - from one shared trunk. The POLICY head is
            // the one this evaluator scores with, and it is found by name
            // when the exporter gave it one and by position otherwise.
            //
            // Position is a safe fallback and not a guess: §320's exporter
            // writes policy_logits FIRST for exactly this reason, and a model
            // from before §320 has one output, which is its policy. So both
            // generations load, and neither has to be detected by version.
            if (session.OutputMetadata.Count < 1)
                return "the model has no outputs";

            KeyValuePair<string, NodeMetadata> output = session.OutputMetadata
                .FirstOrDefault(o => o.Key == PolicyOutputName);

            if (output.Key == null)
                output = session.OutputMetadata.First();

            int[] outDims = output.Value.Dimensions;

            bool outputOk =
                output.Value.ElementType == typeof(float) &&
                outDims.Length >= 1 &&
                Array.IndexOf(ObservationSchema.AcceptedActionCounts, outDims[outDims.Length - 1]) >= 0;

            if (!outputOk)
                return $"output \"{output.Key}\" is {output.Value.ElementType.Name}[{string.Join(",", outDims)}], "
                     + $"expected [...,N] with N one of {string.Join("/", ObservationSchema.AcceptedActionCounts)}";

            inputName = input.Key;
            outputName = output.Key;
            outputWidth = outDims[outDims.Length - 1];
            return null;
        }

        /// <summary>§320. What the exporter calls the policy head. A model
        /// without it is a single-head model from before §320, whose one
        /// output IS its policy.</summary>
        internal const string PolicyOutputName = "policy_logits";

        public ShadowPrediction? Predict(float[] state, float[] legalActionMask)
        {
            if (session == null || !status.Available)
                return null;

            // Section 176: the mask must cover at least what this model
            // scores. A four-output model happily reads the first four of
            // a ten-wide action mask; a ten-output model handed a
            // four-wide move mask would be guessing about the switches it
            // was never told the legality of, so that is refused.
            if (state.Length < inputWidth || legalActionMask.Length < outputWidth)
                return null;

            try
            {
                // Section 175: a model narrower than the current vector is
                // fed its prefix, which is exactly the section 156 ten.
                float[] input = state.Length == inputWidth
                    ? state
                    : state.Take(inputWidth).ToArray();

                var tensor = new DenseTensor<float>(input, new[] { 1, inputWidth });

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(
                    new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });

                // §320: the POLICY output, by the name Validate settled on -
                // not merely the first result, which on a three-headed model
                // is only the policy by the exporter's convention.
                DisposableNamedOnnxValue policy =
                    results.FirstOrDefault(r => r.Name == outputName) ?? results.First();

                float[] scores = policy.AsEnumerable<float>().ToArray();

                if (scores.Length < outputWidth)
                    throw new InvalidOperationException($"model returned {scores.Length} scores");

                // Legal-action masking: an illegal action can never be the
                // shadow's preference.
                int best = -1;
                float bestScore = float.NegativeInfinity;

                for (int i = 0; i < outputWidth; i++)
                {
                    if (legalActionMask[i] <= 0)
                        continue;

                    if (scores[i] > bestScore)
                    {
                        bestScore = scores[i];
                        best = i;
                    }
                }

                if (best < 0)
                    return null;   // nothing legal to prefer (Struggle turns)

                return new ShadowPrediction
                {
                    PreferredActionIndex = best,
                    Score = bestScore,
                    Scores = scores.Take(outputWidth).ToArray()
                };
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref failures) >= MaxFailures)
                {
                    status = new ShadowEvaluatorStatus
                    {
                        Available = false,
                        Description = "shadow inference disabled after repeated failures: " + FirstLine(ex.Message)
                    };
                }

                return null;
            }
        }

        /// <summary>Section 177. Which generation an older input width
        /// belongs to, for the status line - so "§184" tells the reader
        /// exactly how far behind a loaded model is rather than merely that
        /// it is behind. §311 named the rest of them, because every one of
        /// these is now a refusal rather than a slightly stale accept.
        /// </summary>
        static string GenerationOf(int width) => width switch
        {
            10 => "§156",
            76 => "§175",
            160 => "§176",
            202 => "§177",
            203 => "§182",
            205 => "§184",
            _ => $"{width}-feature",
        };

        static string FirstLine(string text)
        {
            int cut = text.IndexOfAny(new[] { '\r', '\n' });
            return cut < 0 ? text : text.Substring(0, cut);
        }

        public void Dispose() => session?.Dispose();
    }
}