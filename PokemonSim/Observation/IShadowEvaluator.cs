namespace PokemonSim.Observation
{
    /// <summary>
    /// Section 156. The seam the shadow model plugs into. The engine only
    /// knows this interface; the ONNX-backed implementation lives in the
    /// separate PokemonSim.Shadow project so the engine (and anything that
    /// references only it) never depends on the onnx runtime, and the app
    /// keeps working identically when no model can be loaded. A shadow
    /// prediction is advisory metadata on an observation record - nothing
    /// anywhere consults it to pick a real action.
    /// </summary>
    public interface IShadowEvaluator
    {
        ShadowEvaluatorStatus Status { get; }

        /// <summary>Score an encoded state against the model's actions.
        /// Section 175: pass the full ObservationSchema.FeatureCount
        /// vector; an implementation backed by a narrower model takes the
        /// prefix it needs (every earlier width is a prefix of the current
        /// one). Section 176: the mask may be MoveSlots or ActionSlots
        /// wide, and must be at least as wide as the model's output - a
        /// move-only model reads the first four entries of an action mask
        /// and behaves as it always did. Returns null when the evaluator is
        /// unavailable, the inference failed (the failure goes into
        /// Status.Description), or nothing was legal - callers treat null
        /// as "no shadow opinion" and carry on.</summary>
        ShadowPrediction? Predict(float[] state, float[] legalActionMask);
    }

    public sealed class ShadowEvaluatorStatus
    {
        public bool Available { get; init; }
        public string Description { get; init; } = "";

        /// <summary>Section 175. How many features the loaded model asks
        /// for - one of ObservationSchema.AcceptedFeatureCounts. Zero when
        /// nothing is loaded.</summary>
        public int InputWidth { get; init; }

        /// <summary>Section 176. How many actions the loaded model scores -
        /// ActionSlots for one that can say "switch", MoveSlots for a
        /// move-only model from before section 176. Zero when nothing is
        /// loaded.</summary>
        public int OutputWidth { get; init; }

        /// <summary>Whether this model has a switch head at all.</summary>
        public bool CanChooseSwitches => OutputWidth > ObservationSchema.MoveSlots;
    }

    public sealed class ShadowPrediction
    {
        /// <summary>Section 176. The model's pick over the whole action
        /// space: 0 to MoveSlots-1 is a move slot, MoveSlots + i means
        /// "send in Team[i]".</summary>
        public int PreferredActionIndex { get; init; } = -1;

        /// <summary>The move slot the model preferred, or -1 when what it
        /// preferred was a switch. Readers written before section 176 see
        /// exactly what they always did on a move turn, and "no opinion"
        /// rather than a wrong number on a switch turn.</summary>
        public int PreferredMoveIndex =>
            PreferredActionIndex >= 0 && PreferredActionIndex < ObservationSchema.MoveSlots
                ? PreferredActionIndex
                : -1;

        /// <summary>The team position the model wanted to send in, or -1
        /// when it preferred a move.</summary>
        public int PreferredTeamIndex =>
            PreferredActionIndex >= ObservationSchema.MoveSlots
                ? PreferredActionIndex - ObservationSchema.MoveSlots
                : -1;

        public string? PreferredMoveName { get; set; }
        public float Score { get; init; }
        public float[] Scores { get; init; } = System.Array.Empty<float>();
    }

    /// <summary>The no-model evaluator: observation-only mode.</summary>
    public sealed class NullShadowEvaluator : IShadowEvaluator
    {
        public NullShadowEvaluator(string description)
        {
            Status = new ShadowEvaluatorStatus { Available = false, Description = description };
        }

        public ShadowEvaluatorStatus Status { get; }

        public ShadowPrediction? Predict(float[] state, float[] legalMoveMask) => null;
    }
}