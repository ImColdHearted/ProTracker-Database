using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using PokemonSim.Simulation;

namespace PokemonSim.Evaluation
{
    /// <summary>
    /// §324. The running score of a match, written from Parallel.For
    /// workers and read once at the end.
    ///
    /// Everything here is interlocked because every method on it runs on a
    /// pool thread, several at a time, and a win rate that is occasionally
    /// short by one is worse than useless - it would be wrong only
    /// sometimes, which is the hardest kind of wrong to notice.
    ///
    /// It holds the per-battle brains only while their battle is running.
    /// §181 learned this the expensive way with observers: keeping one
    /// object per battle alive for the whole run is fine at a hundred and
    /// is the run's memory ceiling at a hundred thousand. A brain's numbers
    /// are final the moment its battle ends, so they are folded in then and
    /// the brain is let go.
    /// </summary>
    public sealed class MatchTally
    {
        /// <summary>§328. What each mirrored pair was worth to One, while
        /// it is still being played. Half-points so a draw is an integer
        /// and the whole thing stays interlocked: a win is 2, a draw 1.</summary>
        sealed class PairRow
        {
            public int HalfPoints;
            public int Battles;
        }

        readonly ConcurrentDictionary<int, PairRow> pairs = new();

        readonly ConcurrentDictionary<int, CountingStrategy> oneBrains = new();
        readonly ConcurrentDictionary<int, CountingStrategy> twoBrains = new();

        // §335. The move-choice counters, one accumulator per side. A class
        // rather than another eight fields doubled, so that the two sides
        // cannot drift apart the way a copied block eventually does - the
        // folding happens in ONE method that both sides call.
        readonly MoveCounters oneMoves = new();
        readonly MoveCounters twoMoves = new();

        int completed;

        int oneWins, twoWins, draws, cancelled;
        int oneFirstWins, oneFirstBattles, oneSecondWins, oneSecondBattles;
        int twoFirstWins, twoFirstBattles, twoSecondWins, twoSecondBattles;

        long survivors, decided;
        long oneDecisions, oneChoices, twoDecisions, twoChoices;
        long oneSwitches, oneReplacements, twoSwitches, twoReplacements;

        readonly int expected;

        /// <summary>§335. What one side chose, summed over every battle.
        /// Longs because a four-hundred-battle match is tens of thousands of
        /// turns and these are added from every worker thread.</summary>
        sealed class MoveCounters
        {
            long moves, status, wasted, damaging, noEffect, weaker;

            readonly long[] slots = new long[CountingStrategy.MoveSlots];

            public void Add(CountingStrategy brain)
            {
                Interlocked.Add(ref moves, brain.MoveChoices);
                Interlocked.Add(ref status, brain.StatusChoices);
                Interlocked.Add(ref wasted, brain.WastedStatus);
                Interlocked.Add(ref damaging, brain.DamagingChoices);
                Interlocked.Add(ref noEffect, brain.NoEffectChoices);
                Interlocked.Add(ref weaker, brain.WeakerThanAvailable);

                for (int slot = 0; slot < slots.Length; slot++)
                    Interlocked.Add(ref slots[slot], brain.SlotChoices(slot));
            }

            public int Moves => (int)Interlocked.Read(ref moves);
            public int Status => (int)Interlocked.Read(ref status);
            public int Wasted => (int)Interlocked.Read(ref wasted);
            public int Damaging => (int)Interlocked.Read(ref damaging);
            public int NoEffect => (int)Interlocked.Read(ref noEffect);
            public int Weaker => (int)Interlocked.Read(ref weaker);

            public int[] Slots()
            {
                var copy = new int[slots.Length];

                for (int slot = 0; slot < slots.Length; slot++)
                    copy[slot] = (int)Interlocked.Read(ref slots[slot]);

                return copy;
            }
        }

        public MatchTally(int expectedBattles)
        {
            expected = expectedBattles;
        }

        public int Completed() => Interlocked.Increment(ref completed);

        /// <summary>Called as a brain is built, so its battle can fold its
        /// numbers in and release it when it finishes.</summary>
        public void Remember(int battle, bool isOne, CountingStrategy brain)
        {
            if (isOne)
                oneBrains[battle] = brain;
            else
                twoBrains[battle] = brain;
        }

        /// <summary>
        /// One finished battle.
        ///
        /// <paramref name="oneWasFirst"/> is the only thing standing between
        /// this and a report that is exactly backwards for half the run: the
        /// engine says Player1Wins, and which contender that was depends on
        /// the seat swap.
        /// </summary>
        public void Record(
            int battle, int pair, bool oneWasFirst, BattleOutcome outcome, BattleState state)
        {
            FoldIn(battle);

            if (outcome == BattleOutcome.Cancelled)
            {
                Interlocked.Increment(ref cancelled);
                return;
            }

            bool oneWon = oneWasFirst
                ? outcome == BattleOutcome.Player1Wins
                : outcome == BattleOutcome.Player2Wins;

            bool twoWon = oneWasFirst
                ? outcome == BattleOutcome.Player2Wins
                : outcome == BattleOutcome.Player1Wins;

            if (oneWon)
                Interlocked.Increment(ref oneWins);
            else if (twoWon)
                Interlocked.Increment(ref twoWins);
            else
                Interlocked.Increment(ref draws);

            // §328: the pair is the unit the interval is computed over.
            // Both halves are played on the same two teams, so when a
            // pairing is lopsided both of its battles go the same way -
            // and two battles that agree because of the teams carry the
            // information of one.
            PairRow row = pairs.GetOrAdd(pair, _ => new PairRow());

            Interlocked.Add(ref row.HalfPoints, oneWon ? 2 : twoWon ? 0 : 1);
            Interlocked.Increment(ref row.Battles);

            // The seat split. A draw counts as a battle in the seat but not
            // as a win, which is what makes the two seat rates comparable.
            if (oneWasFirst)
            {
                Interlocked.Increment(ref oneFirstBattles);
                Interlocked.Increment(ref twoSecondBattles);

                if (oneWon) Interlocked.Increment(ref oneFirstWins);
                if (twoWon) Interlocked.Increment(ref twoSecondWins);
            }
            else
            {
                Interlocked.Increment(ref oneSecondBattles);
                Interlocked.Increment(ref twoFirstBattles);

                if (oneWon) Interlocked.Increment(ref oneSecondWins);
                if (twoWon) Interlocked.Increment(ref twoFirstWins);
            }

            if (outcome == BattleOutcome.Player1Wins || outcome == BattleOutcome.Player2Wins)
            {
                PlayerState winner = outcome == BattleOutcome.Player1Wins
                    ? state.Player1
                    : state.Player2;

                int standing = 0;

                foreach (PokemonState mon in winner.Team)
                {
                    if (mon.CurrentHP > 0)
                        standing++;
                }

                Interlocked.Add(ref survivors, standing);
                Interlocked.Increment(ref decided);
            }
        }

        /// <summary>A brain's counters are final when its battle is; take
        /// them and let it go.</summary>
        void FoldIn(int battle)
        {
            if (oneBrains.TryRemove(battle, out CountingStrategy? one))
            {
                Interlocked.Add(ref oneDecisions, one.Decisions);
                Interlocked.Add(ref oneSwitches, one.VoluntarySwitches);
                Interlocked.Add(ref oneReplacements, one.Replacements);

                oneMoves.Add(one);

                // Only a model knows how often it really answered; every
                // other brain answers every turn by construction.
                if (one.Inner is NeuralStrategy model)
                    Interlocked.Add(ref oneChoices, model.ModelChoiceCount);
                else
                    Interlocked.Add(ref oneChoices, one.Decisions);
            }

            if (twoBrains.TryRemove(battle, out CountingStrategy? two))
            {
                Interlocked.Add(ref twoDecisions, two.Decisions);
                Interlocked.Add(ref twoSwitches, two.VoluntarySwitches);
                Interlocked.Add(ref twoReplacements, two.Replacements);

                twoMoves.Add(two);

                if (two.Inner is NeuralStrategy other)
                    Interlocked.Add(ref twoChoices, other.ModelChoiceCount);
                else
                    Interlocked.Add(ref twoChoices, two.Decisions);
            }
        }

        /// <summary>Anything a battle that threw before finishing left
        /// behind. Rare, and it must not silently lose its decisions.</summary>
        void FoldInStragglers()
        {
            foreach (int battle in oneBrains.Keys)
                FoldIn(battle);

            foreach (int battle in twoBrains.Keys)
                FoldIn(battle);
        }

        public MatchReport Report(
            MatchConfiguration match,
            SimulationResult result,
            int seed,
            DateTime started,
            IShadowEvaluator? oneEvaluator,
            IShadowEvaluator? twoEvaluator)
        {
            FoldInStragglers();

            int one = Volatile.Read(ref oneWins);
            int two = Volatile.Read(ref twoWins);
            int tied = Volatile.Read(ref draws);

            var oneSide = new SideResult
            {
                Name = match.One.Name,
                Wins = one,
                Losses = two,
                Draws = tied,
                AsPlayerOneWins = Volatile.Read(ref oneFirstWins),
                AsPlayerOneBattles = Volatile.Read(ref oneFirstBattles),
                AsPlayerTwoWins = Volatile.Read(ref oneSecondWins),
                AsPlayerTwoBattles = Volatile.Read(ref oneSecondBattles),
                Decisions = (int)Interlocked.Read(ref oneDecisions),
                OwnChoices = (int)Interlocked.Read(ref oneChoices),
                VoluntarySwitches = (int)Interlocked.Read(ref oneSwitches),
                Replacements = (int)Interlocked.Read(ref oneReplacements),
                MoveChoices = oneMoves.Moves,
                StatusChoices = oneMoves.Status,
                WastedStatus = oneMoves.Wasted,
                DamagingChoices = oneMoves.Damaging,
                NoEffectChoices = oneMoves.NoEffect,
                WeakerThanAvailable = oneMoves.Weaker,
                SlotChoices = oneMoves.Slots()
            };

            var twoSide = new SideResult
            {
                Name = match.Two.Name,
                Wins = two,
                Losses = one,
                Draws = tied,
                AsPlayerOneWins = Volatile.Read(ref twoFirstWins),
                AsPlayerOneBattles = Volatile.Read(ref twoFirstBattles),
                AsPlayerTwoWins = Volatile.Read(ref twoSecondWins),
                AsPlayerTwoBattles = Volatile.Read(ref twoSecondBattles),
                Decisions = (int)Interlocked.Read(ref twoDecisions),
                OwnChoices = (int)Interlocked.Read(ref twoChoices),
                VoluntarySwitches = (int)Interlocked.Read(ref twoSwitches),
                Replacements = (int)Interlocked.Read(ref twoReplacements),
                MoveChoices = twoMoves.Moves,
                StatusChoices = twoMoves.Status,
                WastedStatus = twoMoves.Wasted,
                DamagingChoices = twoMoves.Damaging,
                NoEffectChoices = twoMoves.NoEffect,
                WeakerThanAvailable = twoMoves.Weaker,
                SlotChoices = twoMoves.Slots()
            };

            long finished = Interlocked.Read(ref decided);

            return new MatchReport
            {
                One = oneSide,
                Two = twoSide,
                Battles = one + two + tied,
                Draws = tied,
                Cancelled = Volatile.Read(ref cancelled),
                AverageTurns = result.AverageTurns,
                AverageSurvivorsForWinner =
                    finished == 0 ? 0 : Interlocked.Read(ref survivors) / (double)finished,
                Seed = seed,
                SeatsSwapped = match.SwapSeats,
                IsMirror = IsMirror(match),
                PairScores = CompletePairs(),
                Tiers = match.Tiers.Name,
                BothSidesDeterministic =
                    match.One.IsDeterministic && match.Two.IsDeterministic,
                OneModel = Describe(match.One, oneEvaluator),
                TwoModel = Describe(match.Two, twoEvaluator),
                Elapsed = DateTime.UtcNow - started
            };
        }

        /// <summary>
        /// §328. One score per COMPLETE pair, as a fraction of one.
        ///
        /// Only pairs that got both halves count. A half-played pair - one
        /// battle cancelled, or a run stopped part way - is not a mirrored
        /// comparison, and averaging it in would quietly reintroduce the
        /// seat and team luck the pairing exists to remove.
        ///
        /// An unpaired run leaves every pair at one battle, so this comes
        /// back empty and MatchReport falls back to Wilson's interval.
        /// </summary>
        List<double> CompletePairs()
        {
            var scores = new List<double>();

            foreach (KeyValuePair<int, PairRow> entry in pairs)
            {
                if (Volatile.Read(ref entry.Value.Battles) == 2)
                    scores.Add(Volatile.Read(ref entry.Value.HalfPoints) / 4.0);
            }

            return scores;
        }

        /// <summary>
        /// §324. The two sides are configured identically, whatever they
        /// were named.
        ///
        /// Decided on the CONFIGURATION and not on the names, because a
        /// caller is free to call the sides "A" and "B" - and a mirror
        /// quoted as a strength result is the single mistake this class
        /// exists to prevent.
        ///
        /// It covers a Monte Carlo mirror as well as a model one. Two
        /// copies of the boss brain are just as much a test of the harness
        /// as two copies of a model, and cheaper to reason about; there is
        /// no reason to make that one not count.
        /// </summary>
        static bool IsMirror(MatchConfiguration match)
        {
            Contender one = match.One;
            Contender two = match.Two;

            if (one.Kind != two.Kind)
                return false;

            if (one.Risk != two.Risk || one.Rollouts != two.Rollouts)
                return false;

            if (one.Kind != BrainKind.Neural)
                return true;

            return one.Omniscient == two.Omniscient &&
                   string.Equals(one.ModelPath ?? "", two.ModelPath ?? "",
                       StringComparison.OrdinalIgnoreCase);
        }

        static string Describe(Contender side, IShadowEvaluator? evaluator)
        {
            if (side.Kind != BrainKind.Neural)
                return "";

            string file = string.IsNullOrWhiteSpace(side.ModelPath)
                ? "(installed model)"
                : side.ModelPath!;

            return evaluator == null ? file : file + " - " + evaluator.Status.Description;
        }

        /// <summary>Only for the tests, which need to know the harness
        /// counted every battle it was given.</summary>
        public int ExpectedBattles => expected;
    }
}
