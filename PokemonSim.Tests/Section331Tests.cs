using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Evaluation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §331. What a mirror can still tell you.
    ///
    /// §330 gave both halves of a pairing the same teams in the same seats,
    /// and §331 gives the brains in those seats the same rolls. For two
    /// identical brains that makes the two halves ONE battle with the
    /// players' labels swapped, so each side wins exactly one and the win
    /// rate is 50.0% by construction.
    ///
    /// Four consecutive mirrors came back at exactly 50.0% with a zero-width
    /// interval. That proved the pairing symmetric and proved nothing else -
    /// §328's question, "does the interval contain 50%", had become one that
    /// cannot fail.
    ///
    /// So the test moved from the estimate to the SPREAD. A pairing that did
    /// not split means the halves diverged, and that is a real fault.
    /// </summary>
    public class Section331Tests
    {
        static MatchReport Mirror(IReadOnlyList<double> pairs, bool isMirror = true)
        {
            int wins = (int)Math.Round(pairs.Sum() * 2);
            int battles = pairs.Count * 2;

            return new MatchReport
            {
                One = new SideResult { Name = "m", Wins = wins, Losses = battles - wins },
                Two = new SideResult { Name = "m", Wins = battles - wins, Losses = wins },
                Battles = battles,
                IsMirror = isMirror,
                PairScores = pairs
            };
        }

        static IReadOnlyList<double> AllSplit(int n) =>
            Enumerable.Repeat(0.5, n).ToList();

        // ==============================================================
        // The spread is the test now
        // ==============================================================

        [Fact]
        public void APerfectMirrorSplitsEveryPairing()
        {
            MatchReport report = Mirror(AllSplit(200));

            Assert.Equal(0, report.UnsplitPairs);
            Assert.False(report.MirrorIsSkewed);
        }

        [Fact]
        public void ASINGLEPairingThatDidNotSplitIsAFault()
        {
            // The whole point of the change. 199 of 200 pairings behaving is
            // not "near enough" - with identical brains the one that did not
            // split is evidence the halves diverged.
            var pairs = AllSplit(199).ToList();
            pairs.Add(1.0);

            MatchReport report = Mirror(pairs);

            Assert.Equal(1, report.UnsplitPairs);
            Assert.True(report.MirrorIsSkewed);
        }

        [Fact]
        public void ThatFaultIsInvisibleToTheOldTest()
        {
            // §328 asked whether the interval contained 50%. Here it does -
            // one pairing at 1.0 and one at 0.0 leaves the mean at exactly
            // 50% - and the mirror is still broken.
            var pairs = AllSplit(198).ToList();
            pairs.Add(1.0);
            pairs.Add(0.0);

            MatchReport report = Mirror(pairs);

            (double low, double high) = report.Interval;

            Assert.True(low <= 0.5 && 0.5 <= high, "the old test would have passed this");
            Assert.True(report.MirrorIsSkewed, "the new one must not");
        }

        [Fact]
        public void UnsplitCountsPairingsAndNotBattles()
        {
            var pairs = AllSplit(10).ToList();
            pairs.Add(1.0);
            pairs.Add(0.0);
            pairs.Add(1.0);

            Assert.Equal(3, Mirror(pairs).UnsplitPairs);
        }

        [Fact]
        public void ADrawnPairingStillCountsAsSplit()
        {
            // Both halves drawn is a half each, which is a split.
            Assert.Equal(0, Mirror(AllSplit(50)).UnsplitPairs);
        }

        [Fact]
        public void OnlyAMirrorIsHeldToThisStandard()
        {
            // A real match is SUPPOSED to have pairings that go 2-0. That is
            // one side being better, which is the thing being measured.
            var pairs = Enumerable.Repeat(1.0, 150).Concat(Enumerable.Repeat(0.0, 50)).ToList();

            Assert.False(Mirror(pairs, isMirror: false).MirrorIsSkewed);
            Assert.True(Mirror(pairs, isMirror: true).MirrorIsSkewed);
        }

        [Fact]
        public void AnUnpairedMirrorFallsBackToTheIntervalTest()
        {
            // No pairings to split, so the only check available is §328's.
            var even = new MatchReport
            {
                One = new SideResult { Name = "m", Wins = 100, Losses = 100 },
                Two = new SideResult { Name = "m", Wins = 100, Losses = 100 },
                Battles = 200,
                IsMirror = true
            };

            Assert.False(even.IntervalIsPaired);
            Assert.False(even.MirrorIsSkewed);

            var lopsided = new MatchReport
            {
                One = new SideResult { Name = "m", Wins = 180, Losses = 20 },
                Two = new SideResult { Name = "m", Wins = 20, Losses = 180 },
                Battles = 200,
                IsMirror = true
            };

            Assert.True(lopsided.MirrorIsSkewed);
        }

        // ==============================================================
        // The number that still measures something
        // ==============================================================

        [Fact]
        public void SeatOneWinShareIsBothSidesWinsInThatSeat()
        {
            var report = new MatchReport
            {
                One = new SideResult
                {
                    Name = "m",
                    AsPlayerOneWins = 104,
                    AsPlayerOneBattles = 200
                },
                Two = new SideResult
                {
                    Name = "m",
                    AsPlayerOneWins = 96,
                    AsPlayerOneBattles = 200
                }
            };

            Assert.Equal(0.5, report.SeatOneWinShare, 10);
        }

        [Fact]
        public void AnEngineThatFavouredMovingFirstWouldShowUpThere()
        {
            var report = new MatchReport
            {
                One = new SideResult
                {
                    Name = "m", AsPlayerOneWins = 150, AsPlayerOneBattles = 200
                },
                Two = new SideResult
                {
                    Name = "m", AsPlayerOneWins = 150, AsPlayerOneBattles = 200
                }
            };

            Assert.Equal(0.75, report.SeatOneWinShare, 10);
        }

        [Fact]
        public void NoBattlesGivesNoShareRatherThanADividedByZero()
        {
            var report = new MatchReport
            {
                One = new SideResult { Name = "m" },
                Two = new SideResult { Name = "m" }
            };

            Assert.Equal(0, report.SeatOneWinShare);
        }
    }
}
