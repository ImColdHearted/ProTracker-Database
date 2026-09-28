using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Evaluation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §328. The interval is computed over pairs, not battles.
    ///
    /// §324 used Wilson's interval on the battle count, which treats 400
    /// battles as 400 independent trials. They are not: they are 200 pairs,
    /// and both halves of a pair are played on the same two teams, so a
    /// lopsided pairing sends both its battles the same way.
    ///
    /// Two results said so from the outside. The same matchup ran 33.2% and
    /// 40.2% on two nights, seven points apart with intervals that barely
    /// overlapped. And a MIRROR - the same brain on both seats - came out
    /// 55.75% where the same mirror had been 49.9%. Both are what
    /// clustering looks like: answers further apart than the stated
    /// uncertainty allows.
    ///
    /// The tests that matter are the two ends: independent halves must
    /// reproduce what Wilson's gave, and perfectly correlated halves must
    /// widen by about the square root of two.
    /// </summary>
    public class Section328Tests
    {
        static IReadOnlyList<double> Pairs(int wonBoth, int split, int lostBoth)
        {
            var scores = new List<double>();

            for (int i = 0; i < wonBoth; i++) scores.Add(1.0);
            for (int i = 0; i < split; i++) scores.Add(0.5);
            for (int i = 0; i < lostBoth; i++) scores.Add(0.0);

            return scores;
        }

        static double Width((double Low, double High) interval) =>
            interval.High - interval.Low;

        // ==============================================================
        // The two ends
        // ==============================================================

        [Fact]
        public void IndependentHalvesGiveWhatTheOldIntervalGave()
        {
            // 200 pairs at exactly the split a fair coin produces when the
            // two halves are independent: a quarter both, a half split, a
            // quarter neither.
            IReadOnlyList<double> pairs = Pairs(50, 100, 50);

            (double low, double high) = MatchReport.PairedInterval(pairs);
            (double wLow, double wHigh) = MatchReport.WilsonInterval(200, 400);

            Assert.Equal(0.5, (low + high) / 2, 6);

            // Same claim, to within a rounding error.
            Assert.InRange(Width((low, high)) / Width((wLow, wHigh)), 0.95, 1.05);
        }

        [Fact]
        public void PerfectlyCorrelatedHalvesWidenByAboutRootTwo()
        {
            // Every pairing decided both its battles - the case where two
            // battles carry the information of one.
            IReadOnlyList<double> pairs = Pairs(100, 0, 100);

            (double low, double high) = MatchReport.PairedInterval(pairs);
            (double wLow, double wHigh) = MatchReport.WilsonInterval(200, 400);

            Assert.Equal(0.5, (low + high) / 2, 6);
            Assert.InRange(Width((low, high)) / Width((wLow, wHigh)), 1.3, 1.5);
        }

        [Fact]
        public void PairsThatAllAgreeWithEachOtherGiveNoWidthAtAll()
        {
            // Every pairing split, so there is no spread to measure and the
            // answer is exactly 50% with nothing either side of it. Odd
            // looking, and correct: the data really does say that.
            (double low, double high) = MatchReport.PairedInterval(Pairs(0, 200, 0));

            Assert.Equal(0.5, low, 6);
            Assert.Equal(0.5, high, 6);
        }

        // ==============================================================
        // Bounds and degenerate input
        // ==============================================================

        [Fact]
        public void TheIntervalNeverLeavesZeroToOne()
        {
            (double low, double high) = MatchReport.PairedInterval(Pairs(200, 0, 0));
            Assert.InRange(low, 0, 1);
            Assert.InRange(high, 0, 1);
            Assert.Equal(1, high, 6);

            (double low2, double high2) = MatchReport.PairedInterval(Pairs(0, 0, 200));
            Assert.InRange(low2, 0, 1);
            Assert.Equal(0, low2, 6);
        }

        [Fact]
        public void NoPairsGivesNoInterval()
        {
            Assert.Equal((0.0, 0.0), MatchReport.PairedInterval(Array.Empty<double>()));
        }

        [Fact]
        public void OnePairIsAPointAndNotAnInterval()
        {
            // One pairing is not a measurement of anything, and an interval
            // drawn from a sample of one would be a lie with a number on it.
            Assert.Equal((1.0, 1.0), MatchReport.PairedInterval(new[] { 1.0 }));
        }

        [Fact]
        public void MorePairsNarrowTheInterval()
        {
            double few = Width(MatchReport.PairedInterval(Pairs(5, 10, 5)));
            double many = Width(MatchReport.PairedInterval(Pairs(500, 1000, 500)));

            Assert.True(many < few / 5, $"{many} vs {few}");
        }

        // ==============================================================
        // Which interval a report uses
        // ==============================================================

        [Fact]
        public void APairedRunUsesThePairs()
        {
            var report = new MatchReport
            {
                One = new SideResult { Name = "a", Wins = 100, Losses = 100 },
                Two = new SideResult { Name = "b", Wins = 100, Losses = 100 },
                Battles = 200,
                PairScores = Pairs(25, 50, 25)
            };

            Assert.True(report.IntervalIsPaired);
            Assert.Equal(MatchReport.PairedInterval(report.PairScores), report.Interval);
        }

        [Fact]
        public void AnUnpairedRunFallsBackRatherThanReportingNothing()
        {
            var report = new MatchReport
            {
                One = new SideResult { Name = "a", Wins = 100, Losses = 100 },
                Two = new SideResult { Name = "b", Wins = 100, Losses = 100 },
                Battles = 200
            };

            Assert.False(report.IntervalIsPaired);
            Assert.Equal(MatchReport.WilsonInterval(100, 200), report.Interval);
        }

        // ==============================================================
        // The mirror alarm
        // ==============================================================

        static MatchReport Mirror(int wonBoth, int split, int lostBoth)
        {
            int pairs = wonBoth + split + lostBoth;
            int wins = wonBoth * 2 + split;

            return new MatchReport
            {
                One = new SideResult { Name = "m", Wins = wins, Losses = pairs * 2 - wins },
                Two = new SideResult { Name = "m", Wins = pairs * 2 - wins, Losses = wins },
                Battles = pairs * 2,
                IsMirror = true,
                PairScores = Pairs(wonBoth, split, lostBoth)
            };
        }

        [Fact]
        public void AnEvenMirrorIsNotFlagged()
        {
            Assert.False(Mirror(50, 100, 50).MirrorIsSkewed);
        }

        [Fact]
        public void AMirrorWhoseIntervalMissesFiftyIsFlagged()
        {
            // The whole point. One brain beating itself, in both seats.
            Assert.True(Mirror(180, 10, 10).MirrorIsSkewed);
            Assert.True(Mirror(10, 10, 180).MirrorIsSkewed);
        }

        [Fact]
        public void AMirrorThatIsMerelyNoisyIsNotFlagged()
        {
            // A few points off on a small sample is not a fault, and a
            // sanity check that cried wolf would stop being read.
            Assert.False(Mirror(12, 20, 8).MirrorIsSkewed);
        }

        [Fact]
        public void OnlyAMirrorCanBeFlagged()
        {
            var lopsided = new MatchReport
            {
                One = new SideResult { Name = "a", Wins = 360, Losses = 40 },
                Two = new SideResult { Name = "b", Wins = 40, Losses = 360 },
                Battles = 400,
                IsMirror = false,
                PairScores = Pairs(180, 10, 10)
            };

            Assert.False(lopsided.MirrorIsSkewed);
        }
    }
}
