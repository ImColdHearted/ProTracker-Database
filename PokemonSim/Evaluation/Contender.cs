using System;
using PokemonSim.Engine.Strategies;
using PokemonSim.Observation;

namespace PokemonSim.Evaluation
{
    /// <summary>§324. Which brain a contender is.</summary>
    public enum BrainKind
    {
        /// <summary>The boss brain - a rollout search. The strongest thing
        /// in the project and the benchmark that matters, and by a wide
        /// margin the slowest.</summary>
        MonteCarlo,

        /// <summary>§154's preserved RandomMoveStrategy: a uniformly random
        /// legal move, switching only when forced. Not meant to be a strong
        /// opponent - it is the floor.</summary>
        Baseline,

        /// <summary>A trained model, named by file, playing through
        /// NeuralStrategy.</summary>
        Neural
    }

    /// <summary>
    /// §324. One side of a match: a brain, a name to report it under, and
    /// whatever that brain needs to be built.
    ///
    /// WHY THIS IS A DESCRIPTION AND NOT A STRATEGY. A strategy is a live
    /// object with counters in it; a match needs a FRESH one for every
    /// battle on every seat, or two battles running in parallel share one
    /// brain's state and the result is quietly wrong in a way no assertion
    /// would catch. So a contender is inert - it describes what to build,
    /// and the harness builds one per battle per seat.
    ///
    /// The exception is the evaluator, which is the onnx session: expensive
    /// to open, safe to call concurrently, so it is built once per
    /// contender and shared by every strategy that contender produces. Note
    /// "per contender" and not "per file" - a mirror match holds two
    /// contenders pointing at the same model and they get a session each,
    /// so the sanity check it exists to be is genuinely two independent
    /// players rather than one player wired to both seats.
    ///
    /// Everything is set through the one private constructor rather than
    /// object initializers, so there is a single place a new field can be
    /// forgotten.
    /// </summary>
    public sealed class Contender
    {
        Contender(
            BrainKind kind,
            string name,
            string? modelPath,
            float risk,
            bool omniscient,
            int? rollouts)
        {
            Kind = kind;
            Name = name;
            ModelPath = modelPath;
            Risk = risk;
            Omniscient = omniscient;
            Rollouts = rollouts;
        }

        public BrainKind Kind { get; }

        /// <summary>What this side is called in the report. A match between
        /// two model files is unreadable without it.</summary>
        public string Name { get; }

        /// <summary>The model file, for a Neural contender. Null means "the
        /// installed model", whatever the caller resolves that to.</summary>
        public string? ModelPath { get; }

        /// <summary>§182's dial, for the brains that have one.</summary>
        public float Risk { get; }

        /// <summary>§319/§322. Which observation a Neural contender reads
        /// the battle through. False - the fair vector - is the answer for
        /// every model anyone trains; true exists so the cost of honesty can
        /// be measured by playing one against the other.</summary>
        public bool Omniscient { get; }

        /// <summary>Rollouts per candidate action, for a Monte Carlo
        /// contender. Null takes MonteCarloConfig's own default.</summary>
        public int? Rollouts { get; }

        /// <summary>
        /// §332. Whether this brain, given the same position and the same
        /// seed, always answers the same way.
        ///
        /// It matters because §331 made a mirror's test structural: two
        /// identical brains on the same teams in the same seats with the
        /// same rolls play the same battle, so every pairing must split. A
        /// pairing that does not is a fault - IF the brain was capable of
        /// determinism in the first place.
        ///
        /// THE MONTE CARLO BRAIN IS NOT. It stops searching on a wall clock
        /// (MonteCarloConfig.MaxDecisionMilliseconds, and
        /// MonteCarloStrategy's `stopwatch.ElapsedMilliseconds >=`), so how
        /// many rollouts it gets through depends on how busy the machine
        /// was at that moment. Four hundred battles across every core is
        /// exactly when that bites. A Monte Carlo mirror duly came back
        /// with 56 of 200 pairings unsplit - a true report of a real
        /// divergence, and nothing whatever to do with the harness.
        ///
        /// Left unsaid, that alarm would fire on every Monte Carlo mirror
        /// forever, and an alarm that always fires is one nobody reads.
        /// </summary>
        public bool IsDeterministic => Kind != BrainKind.MonteCarlo;

        public static Contender MonteCarlo(
            float risk = ObservationSchema.NeutralRisk,
            int? rollouts = null,
            string? name = null)
        {
            string label = name ?? "Monte Carlo brain (risk " + risk.ToString("0.##") + ")";

            return new Contender(
                BrainKind.MonteCarlo, label, null, risk, false, rollouts);
        }

        public static Contender Baseline(string? name = null)
        {
            return new Contender(
                BrainKind.Baseline, name ?? "Baseline AI", null,
                ObservationSchema.NeutralRisk, false, null);
        }

        public static Contender Model(
            string? modelPath,
            string? name = null,
            float risk = ObservationSchema.NeutralRisk,
            bool omniscient = false)
        {
            return new Contender(
                BrainKind.Neural, name ?? NameOf(modelPath), modelPath, risk, omniscient, null);
        }

        static string NameOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "Installed model";

            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        public override string ToString() => Name;
    }
}
