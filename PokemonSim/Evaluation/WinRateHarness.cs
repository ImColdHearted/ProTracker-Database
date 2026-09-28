using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using PokemonSim.Simulation;

namespace PokemonSim.Evaluation
{
    /// <summary>
    /// §324. What a match was: everything the harness needs to run one, and
    /// nothing about who is asking.
    ///
    /// Either seat takes any brain. That is the point - "model vs Monte
    /// Carlo" is not a mode here, it is two contenders, and so is "v12 vs
    /// v11", "Monte Carlo vs baseline" and "v12 vs itself". Nothing needs
    /// adding for a matchup nobody has thought of yet.
    /// </summary>
    public sealed class MatchConfiguration
    {
        public required Contender One { get; init; }
        public required Contender Two { get; init; }

        /// <summary>How many battles. Rounded UP to an even number when
        /// seats are swapped, because half a mirrored pair is not a
        /// measurement.</summary>
        public int Battles { get; init; } = 200;

        /// <summary>Null draws a fresh one and reports it. A number
        /// reproduces a run, which is what you want the moment a result
        /// surprises you.</summary>
        public int? Seed { get; init; }

        /// <summary>
        /// §324. Play every team pairing twice, seats swapped.
        ///
        /// On by default, and turning it off is only for showing what it
        /// is worth: without it a result mixes "this brain is better" with
        /// "this brain sat in the better seat and drew the better teams",
        /// and no amount of extra battles separates those two.
        /// </summary>
        public bool SwapSeats { get; init; } = true;

        /// <summary>Team size. The default is the builder's own.</summary>
        public int TeamSize { get; init; } = TeamBuilder.MaxTeamSize;

        /// <summary>
        /// §327. Which competitive tiers the random teams come from.
        ///
        /// It belongs on the MATCH rather than in a setting somewhere,
        /// because it changes what the win rate means. A model measured on
        /// OU and UU teams and one measured on every format including
        /// Ubers are not two readings of the same quantity, and a graph
        /// that plotted them on one line would be worse than no graph -
        /// which is why the report carries it and the saved JSON records
        /// it beside the schema.
        /// </summary>
        public TierFilter Tiers { get; init; } = TierFilter.Standard;
    }

    /// <summary>§324. How one contender did, from its own point of view.</summary>
    public sealed class SideResult
    {
        public required string Name { get; init; }
        public int Wins { get; init; }
        public int Losses { get; init; }
        public int Draws { get; init; }

        /// <summary>Chess scoring: a win is one, a draw is a half. Half is
        /// not a compromise - it is the only value that makes two swapped
        /// seats sum to the number of battles, which every other number in
        /// this report depends on.</summary>
        public double Score => Wins + Draws / 2.0;

        public int Battles => Wins + Losses + Draws;

        public double WinRate => Battles == 0 ? 0 : Score / Battles;

        /// <summary>§324. The same score split by which seat it was earned
        /// in. A mirror match reports nothing else worth having.</summary>
        public int AsPlayerOneWins { get; init; }
        public int AsPlayerOneBattles { get; init; }
        public int AsPlayerTwoWins { get; init; }
        public int AsPlayerTwoBattles { get; init; }

        public double AsPlayerOneRate =>
            AsPlayerOneBattles == 0 ? 0 : AsPlayerOneWins / (double)AsPlayerOneBattles;

        public double AsPlayerTwoRate =>
            AsPlayerTwoBattles == 0 ? 0 : AsPlayerTwoWins / (double)AsPlayerTwoBattles;

        /// <summary>How much better the first seat was, in percentage
        /// points. Near zero is the answer a healthy harness gives.</summary>
        public double SeatAdvantagePoints =>
            (AsPlayerOneRate - AsPlayerTwoRate) * 100;

        /// <summary>§324. How often the brain actually answered, for a
        /// model. Zero here is §322 all over again and means the whole
        /// report is about random play - which is why it is on the report
        /// rather than in a log.</summary>
        public int Decisions { get; init; }
        public int OwnChoices { get; init; }

        public double AnsweredShare => Decisions == 0 ? 1 : OwnChoices / (double)Decisions;

        /// <summary>
        /// §326. How often this brain chose to switch instead of attacking.
        ///
        /// On the report because of what §325's first results said: the
        /// model loses to random legal moves. A model that had learned
        /// nothing would draw with random, so losing to it means choosing
        /// worse than chance, and switching is the cheapest way to do that
        /// in this engine - the baseline never volunteers one, so every
        /// switch is a turn it spends on damage and you do not.
        ///
        /// Forced replacements are counted apart. They are nobody's
        /// decision, and folding them in would make every brain that loses
        /// a Pokemon look like a switcher.
        /// </summary>
        public int VoluntarySwitches { get; init; }
        public int Replacements { get; init; }

        public double SwitchShare =>
            Decisions == 0 ? 0 : VoluntarySwitches / (double)Decisions;

        /// <summary>
        /// §335. What it chose on the turns it attacked.
        ///
        /// §326 closed off switching as the explanation for the model losing
        /// to random legal moves, which left the moves themselves. These are
        /// the three cheapest questions that can be asked of a chosen move
        /// without re-running the engine, and between them they separate the
        /// three ways a policy can be bad: it has collapsed onto one output
        /// (the slot spread), it does not know what the type chart is (the
        /// no-effect rate), or it knows and picks the worse one anyway (the
        /// weaker-option rate).
        ///
        /// Every one of them is reported for BOTH sides, because a number
        /// like "12% of its attacks did nothing" means nothing at all until
        /// you know what a brain that wins scores on the same measure.
        /// </summary>
        public int MoveChoices { get; init; }
        public int StatusChoices { get; init; }

        /// <summary>§341. Of those, the ones that could not change anything
        /// on the board they were played into.</summary>
        public int WastedStatus { get; init; }
        public int DamagingChoices { get; init; }
        public int NoEffectChoices { get; init; }
        public int WeakerThanAvailable { get; init; }

        /// <summary>Times each of the four slots was the one chosen.</summary>
        public IReadOnlyList<int> SlotChoices { get; init; } = Array.Empty<int>();

        public double StatusShare =>
            MoveChoices == 0 ? 0 : StatusChoices / (double)MoveChoices;

        /// <summary>Over STATUS choices, not over all of them: the question
        /// is what share of the status moves it played were empty, and an
        /// attack cannot be one of those.</summary>
        public double WastedStatusShare =>
            StatusChoices == 0 ? 0 : WastedStatus / (double)StatusChoices;

        /// <summary>Over DAMAGING choices, not over all of them - a status
        /// move cannot be type-ineffective and counting it in the
        /// denominator would flatter a brain that spams status.</summary>
        public double NoEffectShare =>
            DamagingChoices == 0 ? 0 : NoEffectChoices / (double)DamagingChoices;

        public double WeakerShare =>
            DamagingChoices == 0 ? 0 : WeakerThanAvailable / (double)DamagingChoices;

        /// <summary>The share taken by the slot it picked most often. 25% is
        /// a brain spreading evenly over four moves; 100% is a brain that
        /// has only one output left. It is a floor on concentration, not a
        /// distribution - the whole spread is in SlotChoices.</summary>
        public double TopSlotShare
        {
            get
            {
                int total = 0;
                int top = 0;

                for (int slot = 0; slot < SlotChoices.Count; slot++)
                {
                    total += SlotChoices[slot];

                    if (SlotChoices[slot] > top)
                        top = SlotChoices[slot];
                }

                return total == 0 ? 0 : top / (double)total;
            }
        }
    }

    /// <summary>§324. What a match produced.</summary>
    public sealed class MatchReport
    {
        public required SideResult One { get; init; }
        public required SideResult Two { get; init; }

        public int Battles { get; init; }
        public int Draws { get; init; }
        public int Cancelled { get; init; }
        public double AverageTurns { get; init; }
        public int Seed { get; init; }
        public bool SeatsSwapped { get; init; }
        public TimeSpan Elapsed { get; init; }

        /// <summary>Average Pokemon left standing for the winner of each
        /// decided battle - a cheap read on HOW a brain wins, taken off the
        /// finished state rather than computed.</summary>
        public double AverageSurvivorsForWinner { get; init; }

        /// <summary>§324. Both sides are the same model file. The result is
        /// then a statement about the HARNESS, not about a model, and
        /// saying so on the report is the only thing stopping someone
        /// quoting a 50% as a strength number.</summary>
        public bool IsMirror { get; init; }

        /// <summary>What the two models turned out to be, for the record.
        /// Empty for a brain with no file.</summary>
        public string OneModel { get; init; } = "";
        public string TwoModel { get; init; } = "";

        /// <summary>§327. The tier pool the teams were drawn from, by
        /// name. Two results with different values here are not
        /// comparable.</summary>
        public string Tiers { get; init; } = "";

        /// <summary>§332. Whether BOTH brains answer the same way twice.
        /// When they do, a pairing that fails to split is a fault; when
        /// they do not, the halves are free to diverge and only the
        /// statistical test means anything.</summary>
        public bool BothSidesDeterministic { get; init; }

        /// <summary>The lead in percentage points, positive when One is
        /// ahead. The number a model comparison is actually about.</summary>
        public double LeadPoints => (One.WinRate - Two.WinRate) * 100;

        /// <summary>
        /// §328. What each mirrored PAIR was worth to One: 1 for winning
        /// both halves, 0.5 for a split, 0 for losing both, and the halves
        /// in between for pairs containing a draw.
        ///
        /// This is the list the interval is computed from. See Interval.
        /// </summary>
        public IReadOnlyList<double> PairScores { get; init; } = Array.Empty<double>();

        /// <summary>
        /// §328. A 95% interval on One's win rate, over PAIRS.
        ///
        /// WHY THIS CHANGED. §324 used Wilson's interval on the battle
        /// count, which treats 400 battles as 400 independent trials. They
        /// are not. They are 200 pairs, and both halves of a pair are
        /// played on the same two teams - so when a pairing is lopsided,
        /// both of its battles go the same way. Two battles that agree
        /// because of the teams carry the information of one.
        ///
        /// The evidence was two runs of the same matchup landing 7 points
        /// apart - 33.2% and 40.2% - with 95% intervals that barely
        /// overlapped, and a MIRROR match coming out 55.75% when the same
        /// mirror had been 49.9% a day before. Both are what clustering
        /// looks like from the outside: results further apart than the
        /// stated uncertainty allows.
        ///
        /// So the unit is the pair. Each pair contributes one score, the
        /// interval comes from the observed SPREAD of those scores, and
        /// nothing has to be assumed about how correlated the halves are -
        /// it is measured. When the halves are independent this gives
        /// exactly what Wilson's gave (the arithmetic agrees to the third
        /// decimal); when a pairing decides both halves it widens by about
        /// 1.41, which is the honest number.
        ///
        /// An unpaired run - SwapSeats off - has no pairs to spread over,
        /// and falls back to Wilson's on battles.
        /// </summary>
        public (double Low, double High) Interval =>
            PairScores.Count >= 2
                ? PairedInterval(PairScores)
                : WilsonInterval(One.Score, One.Battles);

        /// <summary>§328. True when the interval came from the pairs. On
        /// the report because the two are not the same claim.</summary>
        public bool IntervalIsPaired => PairScores.Count >= 2;

        /// <summary>
        /// §328. The mean of the pair scores, plus or minus 1.96 standard
        /// errors of that mean, clamped to 0..1.
        ///
        /// A plain normal interval on a mean is right here in a way it was
        /// not on a proportion: the quantity being averaged is already
        /// bounded and symmetric-ish, there are hundreds of pairs, and the
        /// clamp deals with the ends. What Wilson's was protecting against
        /// - a proportion near 0 or 1 with few trials - is not this.
        /// </summary>
        public static (double Low, double High) PairedInterval(
            IReadOnlyList<double> pairs, double z = 1.96)
        {
            if (pairs.Count == 0)
                return (0, 0);

            if (pairs.Count == 1)
                return (Math.Clamp(pairs[0], 0, 1), Math.Clamp(pairs[0], 0, 1));

            double mean = pairs.Average();
            double sum = 0;

            foreach (double score in pairs)
                sum += (score - mean) * (score - mean);

            // Sample variance: n-1, because the mean was estimated from
            // these same pairs.
            double variance = sum / (pairs.Count - 1);
            double half = z * Math.Sqrt(variance / pairs.Count);

            return (Math.Clamp(mean - half, 0, 1), Math.Clamp(mean + half, 0, 1));
        }

        /// <summary>
        /// §324's interval, kept because an unpaired run still needs one,
        /// and because the tests compare the two.
        ///
        /// Wilson rather than the textbook normal interval because that one
        /// misbehaves exactly where a proportion result lives - near 0 and
        /// 1, and at small counts - and can hand back a bound outside 0..1,
        /// which would be a nonsense win rate printed with authority.
        /// </summary>

        public static (double Low, double High) WilsonInterval(double score, int battles, double z = 1.96)
        {
            if (battles <= 0)
                return (0, 0);

            double p = score / battles;
            double d = 1 + z * z / battles;
            double centre = (p + z * z / (2.0 * battles)) / d;
            double half = z * Math.Sqrt(p * (1 - p) / battles + z * z / (4.0 * battles * battles)) / d;

            return (Math.Max(0, centre - half), Math.Min(1, centre + half));
        }

        /// <summary>
        /// §324. How many battles it would take to resolve a difference of
        /// this many percentage points, at 95%.
        ///
        /// It belongs on the report because the most common way to misread
        /// one of these is to treat a lead smaller than the interval as a
        /// result. When that happens the honest answer is not an opinion,
        /// it is a number of battles.
        /// </summary>
        /// <summary>
        /// §328. A mirror whose own interval does not contain 50%.
        ///
        /// This is the one number in the system that must come out even:
        /// the same brain on both seats, each playing both teams. Until
        /// §328 a mirror could land 5.75 points off and the report would
        /// say only that the SEATS were level - which they were, because
        /// the brain was beating itself in both of them.
        ///
        /// When this is true nothing else measured by the harness can be
        /// trusted, so it is stated in those words rather than as a
        /// footnote.
        /// </summary>
        /// <summary>
        /// §331. How many pairs did NOT split - one side taking both
        /// halves instead of one each.
        ///
        /// This is the number a mirror is really about. §330 gave both
        /// halves of a pair the same teams in the same seats, and §331
        /// gave the brains in those seats the same rolls, so for two
        /// identical brains the two halves are one battle with the labels
        /// swapped. Each contender must win exactly one. Every pair splits,
        /// and this is zero.
        /// </summary>
        public int UnsplitPairs
        {
            get
            {
                int odd = 0;

                foreach (double score in PairScores)
                {
                    if (Math.Abs(score - 0.5) > 1e-9)
                        odd++;
                }

                return odd;
            }
        }

        /// <summary>
        /// §331. A mirror that did not come out the way a mirror must.
        ///
        /// §328 asked whether the interval contained 50%. After §330 that
        /// question stopped being able to fail: identical brains on
        /// identical teams split every pair, so the win rate is 50.0% by
        /// CONSTRUCTION, whatever else is wrong. Four consecutive mirrors
        /// came back at exactly 50.0% with a zero-width interval, which
        /// proved the pairing was symmetric and proved nothing else.
        ///
        /// So the test is no longer the estimate, it is the SPREAD. A
        /// single pair that failed to split means the two halves diverged -
        /// the brains are not deterministic, or the seats are not being
        /// filled the same way, or something reached one half and not the
        /// other. That is a real fault and this is how it shows.
        ///
        /// The interval test is kept for the unpaired case, where there are
        /// no pairs to split and it is the only check there is.
        /// </summary>
        public bool MirrorIsSkewed
        {
            get
            {
                if (!IsMirror)
                    return false;

                // §332: the structural test needs brains that CAN be
                // structural. The Monte Carlo brain stops searching on a
                // wall clock, so its two halves legitimately diverge and
                // every one of its mirrors would alarm forever.
                if (IntervalIsPaired && BothSidesDeterministic)
                    return UnsplitPairs > 0;

                (double low, double high) = Interval;

                return low > 0.5 || high < 0.5;
            }
        }

        /// <summary>
        /// §331. How often seat one won, over the pairings.
        ///
        /// In a mirror this is the ONLY number carrying information, and it
        /// is a clean one: both halves are the same battle, so whoever won
        /// seat one won it in both, and the count is a direct measurement
        /// of whether this engine favours moving first. Four mirrors put it
        /// at 52.25% over 800 pairings - z = 1.27, no evidence of an edge.
        /// </summary>
        public double SeatOneWinShare =>
            One.AsPlayerOneBattles + Two.AsPlayerOneBattles == 0
                ? 0
                : (One.AsPlayerOneWins + Two.AsPlayerOneWins) /
                  (double)(One.AsPlayerOneBattles + Two.AsPlayerOneBattles);

        public static int BattlesToResolve(double points, double z = 1.96)
        {
            if (points <= 0)
                return int.MaxValue;

            double delta = points / 100.0;

            return (int)Math.Ceiling(z * z * 0.25 / (delta * delta));
        }
    }

    /// <summary>
    /// §324. The measurement system for the AI project.
    ///
    /// §320 named this as the next step and said why: every knob above it -
    /// soft-target weight, the three head weights, history depth, hidden
    /// width - was being chosen by looking at imitation accuracy, which
    /// measures agreement with a teacher rather than strength. Self-play
    /// without a way to tell whether generation N+1 beats generation N is a
    /// loop with no gradient.
    ///
    /// So the thing this must be is TRUSTWORTHY, and the two ways a harness
    /// like this lies are both addressed in the code rather than in a
    /// caveat:
    ///
    /// SEAT AND TEAM LUCK. Fresh random teams every battle means a brain
    /// can win by drawing better ones, and the first seat may simply be
    /// worth something. Both are removed the same way: battles are run in
    /// PAIRS. Both halves of a pair use the same two teams and the same
    /// battle seed; the seats are swapped between them. Whatever a pairing
    /// was worth, both contenders got it.
    ///
    /// A BRAIN THAT IS NOT PLAYING. §322 was a model that loaded, was
    /// reported as playing, and answered nothing - every decision fell
    /// through to random. A win rate measured under that is a win rate for
    /// random play with a model's name on it. So a model is refused before
    /// the run if it cannot read the observation, and the share of
    /// decisions it actually answered is on the report either way.
    ///
    /// This harness records nothing. It writes no observations and no
    /// battle files: a measurement that changes the training corpus is not
    /// a measurement, and 500 battles of lab recording is 60 MB nobody
    /// asked for.
    /// </summary>
    public static class WinRateHarness
    {
        /// <summary>
        /// §324. Why this match cannot be run, in words, or null.
        ///
        /// Asked before a battle starts, because after one starts nothing
        /// can tell an unplayable model from a losing one - which is the
        /// whole lesson of §322.
        /// </summary>
        public static string? Refuse(
            MatchConfiguration match, Func<Contender, IShadowEvaluator?> evaluators)
        {
            foreach (Contender side in new[] { match.One, match.Two })
            {
                if (side.Kind != BrainKind.Neural)
                    continue;

                string? problem = NeuralStrategy.Incompatibility(evaluators(side), side.Omniscient);

                if (problem != null)
                    return side.Name + " cannot play: " + problem;
            }

            if (match.Battles < 2)
                return "a match needs at least two battles.";

            return null;
        }

        /// <summary>
        /// Runs the match. <paramref name="buildEvaluator"/> turns a model
        /// path into an evaluator - injected because the onnx runtime lives
        /// in PokemonSim.Shadow and nothing in this project may reference
        /// it (the §156 seam, still holding).
        /// </summary>
        public static MatchReport Run(
            MatchConfiguration match,
            ISpeciesSource speciesSource,
            Func<string?, IShadowEvaluator> buildEvaluator,
            CancellationToken cancellationToken = default,
            Action<int, int>? progress = null)
        {
            DateTime started = DateTime.UtcNow;

            int seed = match.Seed ?? (Environment.TickCount & 0x7fffffff);

            // An odd count cannot be paired, and an unpaired battle is one
            // seat's result reported as both.
            int battles = match.SwapSeats && match.Battles % 2 != 0
                ? match.Battles + 1
                : match.Battles;

            MoveDex.EnsureLoaded();
            _ = speciesSource.AllSpeciesNames.Count;

            IShadowEvaluator? oneEval = match.One.Kind == BrainKind.Neural
                ? buildEvaluator(match.One.ModelPath)
                : null;

            // Built separately even when the two paths are identical - see
            // Contender's remarks on why a mirror needs two sessions.
            IShadowEvaluator? twoEval = match.Two.Kind == BrainKind.Neural
                ? buildEvaluator(match.Two.ModelPath)
                : null;

            var tally = new MatchTally(battles);

            try
            {
                // Which contender sits in seat one for battle i. Even
                // battles are One; odd battles are Two, on the same teams
                // and the same seed as the even battle before them.
                bool OneIsFirst(int i) => !match.SwapSeats || i % 2 == 0;

                // The pairing index: both halves of a pair share it, which
                // is what makes them the same two teams.
                int PairOf(int i) => match.SwapSeats ? i / 2 : i;

                IBattleStrategy Build(
                    Contender side, IShadowEvaluator? evaluator, int i, bool isOne, bool isFirstSeat) =>
                    NewBrain(side, evaluator, seed, i, PairOf(i), isOne, isFirstSeat,
                        tally, cancellationToken);

                SimulationResult result = BattleSimulator.Run(
                    stateFactory: i => BuildPairedBattle(
                        speciesSource, seed, PairOf(i), match, OneIsFirst(i)),
                    simulations: battles,
                    baseSeed: seed,
                    cancellationToken: cancellationToken,
                    player1Strategy: i => OneIsFirst(i)
                        ? Build(match.One, oneEval, i, true, true)
                        : Build(match.Two, twoEval, i, false, true),
                    player2Strategy: i => OneIsFirst(i)
                        ? Build(match.Two, twoEval, i, false, false)
                        : Build(match.One, oneEval, i, true, false),
                    // Deliberately none. See the class remarks: a
                    // measurement that writes to the training corpus is not
                    // a measurement.
                    observerFactory: null,
                    onBattleCompleted: (i, _) =>
                        progress?.Invoke(tally.Completed(), battles),
                    // §324: both halves of a pair start from the same roll,
                    // so the pairing is a controlled comparison rather than
                    // two unrelated battles on the same teams.
                    seedForBattle: i => seed + PairOf(i),
                    onBattleFinished: (i, outcome, state) =>
                        tally.Record(i, PairOf(i), OneIsFirst(i), outcome, state));

                return tally.Report(match, result, seed, started, oneEval, twoEval);
            }
            finally
            {
                (oneEval as IDisposable)?.Dispose();
                (twoEval as IDisposable)?.Dispose();
            }
        }

        /// <summary>A brain for ONE battle on ONE seat. Never shared - see
        /// Contender.</summary>
        static IBattleStrategy NewBrain(
            Contender side,
            IShadowEvaluator? evaluator,
            int seed,
            int battle,
            int pair,
            bool isOne,
            bool isFirstSeat,
            MatchTally tally,
            CancellationToken cancellationToken)
        {
            var counted = new CountingStrategy(
                RawBrain(side, evaluator, seed, pair, isFirstSeat, cancellationToken));

            tally.Remember(battle, isOne, counted);

            return counted;
        }

        /// <summary>§326: the brain itself, before the counter goes round
        /// it. Split out so the wrapping happens in exactly one place and
        /// no future brain can be added without it.</summary>
        static IBattleStrategy RawBrain(
            Contender side,
            IShadowEvaluator? evaluator,
            int seed,
            int pair,
            bool isFirstSeat,
            CancellationToken cancellationToken)
        {
            if (side.Kind == BrainKind.Baseline)
                return new RandomMoveStrategy();

            if (side.Kind == BrainKind.MonteCarlo)
            {
                // §331: the seed comes from the PAIR and the SEAT, never
                // from the battle index or from which contender this is.
                //
                // The seat part is §324's: without it the two brains in one
                // battle would roll the same rollouts as each other every
                // turn, and a mirror whose two sides think identically is
                // not a sanity check, it is a coin that always lands the
                // same way up.
                //
                // The pair part is §331's, and it is what makes the pairing
                // exact rather than approximate. Both halves of a pair put
                // the same teams in the same seats; if the brains in those
                // seats also draw the same rolls, the two halves are the
                // same battle with the labels swapped, and every pair must
                // split. Seeding by battle index instead left a Monte Carlo
                // pair as two unrelated games, which is the team luck the
                // pairing exists to remove.
                var brain = new MonteCarloStrategy(
                    ConfigFor(side), seed ^ (0x51ED + pair * 2 + (isFirstSeat ? 0 : 1)));

                brain.CancellationToken = cancellationToken;

                return brain;
            }

            var neural = new NeuralStrategy(evaluator!) { Omniscient = side.Omniscient };
            neural.RiskAppetite = side.Risk;

            return neural;
        }

        /// <summary>MonteCarloConfig's properties are init-only, so this is
        /// an initializer rather than a few assignments - and the fallback
        /// reads the DEFAULT INSTANCE rather than repeating 24 here, since
        /// two constants that have to agree is the shape of bug this
        /// project keeps finding.</summary>
        static readonly MonteCarloConfig DefaultBrain = new MonteCarloConfig();

        static MonteCarloConfig ConfigFor(Contender side) =>
            new MonteCarloConfig
            {
                RiskAppetite = side.Risk,
                SimulationsPerAction = side.Rollouts ?? DefaultBrain.SimulationsPerAction
            };

        /// <summary>
        /// §324. Both halves of a pair get the SAME two teams, built from
        /// the pairing index rather than the battle index - and they are
        /// handed to the seats in swapped order, so the contender that had
        /// team A in the first half has team B in the second.
        ///
        /// That is the whole variance-reduction argument in two lines: the
        /// pair cannot be won by drawing the better team, because each side
        /// played both of them.
        /// </summary>
        static BattleState BuildPairedBattle(
            ISpeciesSource speciesSource, int seed, int pair, MatchConfiguration match, bool oneIsFirst)
        {
            List<PokemonState> teamA = BuildTeam(speciesSource, match, seed ^ (pair * 2 + 1));
            List<PokemonState> teamB = BuildTeam(speciesSource, match, seed ^ (pair * 2 + 2));

            return Seat(match, oneIsFirst, teamA, teamB, seed + pair);
        }

        /// <summary>
        /// §330. Who sits where, and with which team. The whole
        /// variance-reduction argument is these six lines, so they are
        /// their own function and the tests call it directly.
        ///
        /// THE TEAM FOLLOWS THE SEAT, NOT THE CONTENDER. §324 wrote this
        /// the other way round - the team was chosen by `oneIsFirst`
        /// alongside the contender - which meant One held team A in BOTH
        /// halves and Two held team B in both. The seats swapped and the
        /// teams did not, so the pairing removed seat bias and left team
        /// luck completely uncancelled, while the panel said in as many
        /// words that a result could not be won by drawing the better
        /// team.
        ///
        /// A MIRROR MATCH IS WHAT CAUGHT IT. Same brain both seats, 400
        /// battles, and §328's interval came out 6.9 points wide - which
        /// is 99.5% of the widest it can mathematically be, meaning almost
        /// every pair went 2-0 or 0-2 and almost none split. Two identical
        /// brains cannot beat each other twice in a row; a better TEAM can,
        /// and did, because its owner never had to give it back.
        ///
        /// Written correctly, seat one always holds team A and seat two
        /// always holds team B, and it is the CONTENDERS who move:
        ///
        ///     half 1   One = seat one (team A)   Two = seat two (team B)
        ///     half 2   Two = seat one (team A)   One = seat two (team B)
        ///
        /// so across the pair each contender has played both teams and sat
        /// in both seats, and whatever the pairing was worth, both of them
        /// got it.
        ///
        /// What this costs: the per-contender seat split is noisier than it
        /// was, because a contender's two seats now also carry different
        /// teams. It stays UNBIASED - team A and team B are both random
        /// draws, so over many pairs they are exchangeable - but it is an
        /// average rather than a controlled comparison. That is the right
        /// trade: the win rate is what the harness is for, and seat bias is
        /// the thing being ruled out rather than measured.
        /// </summary>
        public static BattleState Seat(
            MatchConfiguration match,
            bool oneIsFirst,
            List<PokemonState> teamA,
            List<PokemonState> teamB,
            int battleSeed)
        {
            Contender first = oneIsFirst ? match.One : match.Two;
            Contender second = oneIsFirst ? match.Two : match.One;

            var p1 = new PlayerState
            {
                Name = first.Name,
                Team = teamA,
                ActivePokemon = teamA[0]
            };

            var p2 = new PlayerState
            {
                Name = second.Name,
                Team = teamB,
                ActivePokemon = teamB[0]
            };

            return new BattleState
            {
                Player1 = p1,
                Player2 = p2,
                Rng = new BattleRng(battleSeed)
            };
        }
        static List<PokemonState> BuildTeam(
            ISpeciesSource speciesSource, MatchConfiguration match, int seed)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                TeamBuildResult team = RandomTeams.Build(
                    speciesSource, match.TeamSize, seed + attempt * 7919,
                    tiers: match.Tiers);

                if (team.Ok && team.Team.Count > 0)
                    return team.Team;
            }

            throw new InvalidOperationException(
                "The harness could not build a random team from the " + match.Tiers.Name +
                " pool - is the species catalog loaded, and does it overlap those tiers?");
        }
    }
}
