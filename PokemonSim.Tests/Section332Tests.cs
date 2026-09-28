using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Evaluation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §332. The strict mirror check only applies to brains that can be
    /// strict.
    ///
    /// §331 made a mirror structural: identical brains, identical teams,
    /// identical rolls, so every pairing must split. The model mirror duly
    /// came back with all 200 split. The MONTE CARLO mirror came back with
    /// 56 of 200 unsplit - a true report of a real divergence, and nothing
    /// to do with the harness.
    ///
    /// That brain stops searching on a wall clock, so how many rollouts it
    /// completes depends on how busy the machine was. Four hundred battles
    /// across every core is exactly when that bites. Left alone, §331's
    /// alarm would fire on every Monte Carlo mirror forever - and an alarm
    /// that always fires is one nobody reads, which is §323's lesson
    /// arriving for the third time.
    /// </summary>
    public class Section332Tests
    {
        static MatchReport Mirror(
            IReadOnlyList<double> pairs, bool deterministic, bool isMirror = true)
        {
            int wins = (int)Math.Round(pairs.Sum() * 2);
            int battles = pairs.Count * 2;

            return new MatchReport
            {
                One = new SideResult { Name = "m", Wins = wins, Losses = battles - wins },
                Two = new SideResult { Name = "m", Wins = battles - wins, Losses = wins },
                Battles = battles,
                IsMirror = isMirror,
                PairScores = pairs,
                BothSidesDeterministic = deterministic
            };
        }

        static IReadOnlyList<double> WithUnsplit(int split, int won, int lost) =>
            Enumerable.Repeat(0.5, split)
                .Concat(Enumerable.Repeat(1.0, won))
                .Concat(Enumerable.Repeat(0.0, lost))
                .ToList();

        // ==============================================================
        // Which brains are which
        // ==============================================================

        [Fact]
        public void AModelAnswersTheSameWayTwice()
        {
            Assert.True(Contender.Model("v12.onnx").IsDeterministic);
        }

        [Fact]
        public void SoDoesTheBaseline()
        {
            // It draws from the BATTLE's rng, which the pairing holds fixed.
            Assert.True(Contender.Baseline().IsDeterministic);
        }

        [Fact]
        public void TheMonteCarloBrainDoesNot()
        {
            // MonteCarloConfig.MaxDecisionMilliseconds - it stops on a clock.
            Assert.False(Contender.MonteCarlo().IsDeterministic);
        }

        // ==============================================================
        // The strict check, and when it is withheld
        // ==============================================================

        [Fact]
        public void ADeterministicMirrorIsStillHeldToEveryPairingSplitting()
        {
            Assert.True(Mirror(WithUnsplit(199, 1, 0), deterministic: true).MirrorIsSkewed);
            Assert.False(Mirror(WithUnsplit(200, 0, 0), deterministic: true).MirrorIsSkewed);
        }

        [Fact]
        public void ANonDeterministicMirrorIsNotFlaggedForDiverging()
        {
            // The exact shape of the real Monte Carlo run: 56 of 200
            // unsplit, mean near 50%. That is the brain, not a fault.
            MatchReport report = Mirror(WithUnsplit(144, 28, 28), deterministic: false);

            Assert.Equal(56, report.UnsplitPairs);
            Assert.False(report.MirrorIsSkewed);
        }

        [Fact]
        public void ButItIsStillJudgedOnTheInterval()
        {
            // Withholding the strict test must not mean withholding every
            // test. A non-deterministic mirror that lands nowhere near even
            // is still a fault.
            MatchReport report = Mirror(WithUnsplit(0, 190, 10), deterministic: false);

            (double low, double high) = report.Interval;

            Assert.True(low > 0.5, $"interval was {low:F3}-{high:F3}");
            Assert.True(report.MirrorIsSkewed);
        }

        [Fact]
        public void TheUnsplitCountIsStillReportedEitherWay()
        {
            // Not flagged is not the same as not measured - the number is
            // still worth seeing on a Monte Carlo mirror.
            Assert.Equal(56, Mirror(WithUnsplit(144, 28, 28), deterministic: false).UnsplitPairs);
            Assert.Equal(56, Mirror(WithUnsplit(144, 28, 28), deterministic: true).UnsplitPairs);
        }

        [Fact]
        public void ARealMatchIsNeverHeldToEitherMirrorStandard()
        {
            var pairs = WithUnsplit(0, 150, 50);

            Assert.False(Mirror(pairs, deterministic: true, isMirror: false).MirrorIsSkewed);
            Assert.False(Mirror(pairs, deterministic: false, isMirror: false).MirrorIsSkewed);
        }

        [Fact]
        public void DeterminismIsAboutBOTHSides()
        {
            var match = new MatchConfiguration
            {
                One = Contender.Model("v12.onnx"),
                Two = Contender.MonteCarlo()
            };

            Assert.False(match.One.IsDeterministic && match.Two.IsDeterministic);
        }
    }
}
