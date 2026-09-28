using System;
using System.Collections.Generic;
using PokemonSim.Engine.Strategies;
using PokemonSim.Evaluation;
using PokemonSim.Models;
using PokemonSim.Observation;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §324. The win-rate harness.
    ///
    /// This is the measurement system for the whole AI project, so the
    /// thing being tested is not "does it produce a number" but "can the
    /// number be wrong while looking right." Two ways it can:
    ///
    ///   - THE SEAT. The engine reports Player1Wins. Which contender that
    ///     was depends on the seat swap, and getting it backwards produces
    ///     a report that is exactly inverted for half the run - a model
    ///     that lost 40-60 reported as winning 60-40. Most of this file is
    ///     about that one argument.
    ///
    ///   - THE ARITHMETIC. A win rate that does not sum, an interval that
    ///     can leave 0..1, a mirror quoted as a strength result.
    ///
    /// The seat tests use hand-built states and outcomes rather than real
    /// battles on purpose: a suite that can only reach the attribution by
    /// playing five hundred battles is a suite nobody runs.
    /// </summary>
    public class Section324Tests
    {
        static BattleState Position()
        {
            var mine = new List<PokemonState>
            {
                TestKit.Mon("Mine", PokemonType.Normal, moves: new[] { TestKit.Move("My Hit") })
            };

            var theirs = new List<PokemonState>
            {
                TestKit.Mon("Theirs", PokemonType.Fire, moves: new[] { TestKit.Move("Their Hit") })
            };

            return TestKit.Battle(324, mine, theirs).State;
        }

        static MatchConfiguration Match(
            Contender? one = null, Contender? two = null, int battles = 4, bool swap = true) =>
            new MatchConfiguration
            {
                One = one ?? Contender.Baseline("One"),
                Two = two ?? Contender.Baseline("Two"),
                Battles = battles,
                SwapSeats = swap,
                Seed = 324
            };

        static MatchReport ReportOf(MatchTally tally, MatchConfiguration match) =>
            tally.Report(match, new SimulationResult(), 324, DateTime.UtcNow, null, null);

        // ==============================================================
        // The seat
        // ==============================================================

        [Fact]
        public void AWinInSeatOneGoesToWhoeverSatThere()
        {
            BattleState state = Position();

            var tally = new MatchTally(2);
            tally.Record(0, oneWasFirst: true, BattleOutcome.Player1Wins, state);

            MatchReport report = ReportOf(tally, Match());

            Assert.Equal(1, report.One.Wins);
            Assert.Equal(0, report.Two.Wins);
        }

        [Fact]
        public void TheSAMEOutcomeGoesTheOtherWayWhenTheSeatsWereSwapped()
        {
            // The whole bug, in one pair of assertions: identical engine
            // outcome, opposite attribution, because the seat differed.
            BattleState state = Position();

            var tally = new MatchTally(2);
            tally.Record(0, oneWasFirst: false, BattleOutcome.Player1Wins, state);

            MatchReport report = ReportOf(tally, Match());

            Assert.Equal(0, report.One.Wins);
            Assert.Equal(1, report.Two.Wins);
        }

        [Fact]
        public void APerfectlySplitPairIsOneWinEach()
        {
            BattleState state = Position();

            var tally = new MatchTally(2);

            // One wins its game in seat one; Two wins its game in seat one.
            tally.Record(0, oneWasFirst: true, BattleOutcome.Player1Wins, state);
            tally.Record(1, oneWasFirst: false, BattleOutcome.Player1Wins, state);

            MatchReport report = ReportOf(tally, Match());

            Assert.Equal(1, report.One.Wins);
            Assert.Equal(1, report.Two.Wins);
            Assert.Equal(0.5, report.One.WinRate);
            Assert.Equal(0.5, report.Two.WinRate);
        }

        [Fact]
        public void TheSeatSplitSeparatesSeatLuckFromStrength()
        {
            BattleState state = Position();

            var tally = new MatchTally(4);

            // Whoever sits first wins, every time. That is a pure seat
            // advantage and the report has to be able to say so.
            tally.Record(0, oneWasFirst: true, BattleOutcome.Player1Wins, state);
            tally.Record(1, oneWasFirst: false, BattleOutcome.Player1Wins, state);
            tally.Record(2, oneWasFirst: true, BattleOutcome.Player1Wins, state);
            tally.Record(3, oneWasFirst: false, BattleOutcome.Player1Wins, state);

            MatchReport report = ReportOf(tally, Match());

            // Dead level overall...
            Assert.Equal(0.5, report.One.WinRate);

            // ...and 100 points of seat advantage underneath it.
            Assert.Equal(100, report.One.SeatAdvantagePoints);
            Assert.Equal(100, report.Two.SeatAdvantagePoints);
        }

        [Fact]
        public void EachContenderSitsInBothSeatsEquallyOften()
        {
            BattleState state = Position();

            var tally = new MatchTally(4);

            for (int i = 0; i < 4; i++)
                tally.Record(i, i % 2 == 0, BattleOutcome.Draw, state);

            MatchReport report = ReportOf(tally, Match());

            Assert.Equal(2, report.One.AsPlayerOneBattles);
            Assert.Equal(2, report.One.AsPlayerTwoBattles);
            Assert.Equal(2, report.Two.AsPlayerOneBattles);
            Assert.Equal(2, report.Two.AsPlayerTwoBattles);
        }

        // ==============================================================
        // The arithmetic
        // ==============================================================

        [Fact]
        public void WinsAndLossesAndDrawsAccountForEveryBattle()
        {
            BattleState state = Position();

            var tally = new MatchTally(5);
            tally.Record(0, true, BattleOutcome.Player1Wins, state);
            tally.Record(1, false, BattleOutcome.Player1Wins, state);
            tally.Record(2, true, BattleOutcome.Draw, state);
            tally.Record(3, true, BattleOutcome.Player2Wins, state);
            tally.Record(4, true, BattleOutcome.Cancelled, state);

            MatchReport report = ReportOf(tally, Match(battles: 5));

            Assert.Equal(4, report.Battles);
            Assert.Equal(1, report.Cancelled);
            Assert.Equal(report.Battles, report.One.Wins + report.One.Losses + report.One.Draws);
            Assert.Equal(report.Battles, report.Two.Wins + report.Two.Losses + report.Two.Draws);
        }

        [Fact]
        public void ADrawIsHalfAPointToEachSide()
        {
            BattleState state = Position();

            var tally = new MatchTally(2);
            tally.Record(0, true, BattleOutcome.Draw, state);
            tally.Record(1, false, BattleOutcome.Draw, state);

            MatchReport report = ReportOf(tally, Match());

            Assert.Equal(1.0, report.One.Score);
            Assert.Equal(1.0, report.Two.Score);
        }

        [Fact]
        public void TheTwoWinRatesAlwaysSumToOne()
        {
            // The property that makes a lead meaningful. It holds only
            // because a draw is a half.
            BattleState state = Position();

            var tally = new MatchTally(6);
            tally.Record(0, true, BattleOutcome.Player1Wins, state);
            tally.Record(1, false, BattleOutcome.Player1Wins, state);
            tally.Record(2, true, BattleOutcome.Draw, state);
            tally.Record(3, false, BattleOutcome.Player2Wins, state);
            tally.Record(4, true, BattleOutcome.Player1Wins, state);
            tally.Record(5, false, BattleOutcome.Draw, state);

            MatchReport report = ReportOf(tally, Match(battles: 6));

            Assert.Equal(1.0, report.One.WinRate + report.Two.WinRate, 10);

            // Spelled out from the six records above rather than restated
            // from the report's own fields - an assertion written in terms
            // of LeadPoints would be true whatever LeadPoints did.
            Assert.Equal(3, report.One.Wins);      // battles 0, 3, 4
            Assert.Equal(1, report.Two.Wins);      // battle 1
            Assert.Equal(2, report.One.Draws);     // battles 2, 5
            Assert.Equal(4.0 / 6.0, report.One.WinRate, 10);
            Assert.Equal((4.0 / 6.0 - 2.0 / 6.0) * 100, report.LeadPoints, 10);
        }

        [Fact]
        public void ACancelledBattleScoresForNobody()
        {
            BattleState state = Position();

            var tally = new MatchTally(2);
            tally.Record(0, true, BattleOutcome.Cancelled, state);
            tally.Record(1, false, BattleOutcome.Cancelled, state);

            MatchReport report = ReportOf(tally, Match());

            Assert.Equal(0, report.Battles);
            Assert.Equal(2, report.Cancelled);
            Assert.Equal(0, report.One.Score);
        }

        // ==============================================================
        // The interval
        // ==============================================================

        [Fact]
        public void TheIntervalStaysInsideZeroToOneAtTheExtremes()
        {
            // The textbook normal interval fails exactly here, which is why
            // this is Wilson's.
            (double low, double high) = MatchReport.WilsonInterval(0, 10);
            Assert.True(low >= 0, $"low was {low}");

            (double low2, double high2) = MatchReport.WilsonInterval(10, 10);
            Assert.True(high2 <= 1, $"high was {high2}");

            Assert.True(high > 0);
            Assert.True(low2 < 1);
        }

        [Fact]
        public void TheIntervalBracketsTheRateAndNarrowsWithBattles()
        {
            (double low, double high) = MatchReport.WilsonInterval(50, 100);
            Assert.True(low < 0.5 && 0.5 < high);

            (double wideLow, double wideHigh) = MatchReport.WilsonInterval(5, 10);
            (double tightLow, double tightHigh) = MatchReport.WilsonInterval(500, 1000);

            Assert.True(tightHigh - tightLow < wideHigh - wideLow);
        }

        [Fact]
        public void NoBattlesGivesNoInterval()
        {
            (double low, double high) = MatchReport.WilsonInterval(0, 0);
            Assert.Equal(0, low);
            Assert.Equal(0, high);
        }

        [Fact]
        public void TheBattleCountNeededGrowsAsTheDifferenceShrinks()
        {
            int five = MatchReport.BattlesToResolve(5);
            int three = MatchReport.BattlesToResolve(3);
            int two = MatchReport.BattlesToResolve(2);

            Assert.True(five < three && three < two);

            // Quartering the gap should roughly quadruple the battles.
            Assert.InRange(MatchReport.BattlesToResolve(2.5) / (double)five, 3.5, 4.5);

            Assert.Equal(int.MaxValue, MatchReport.BattlesToResolve(0));
        }

        // ==============================================================
        // The mirror, and the refusal
        // ==============================================================

        [Fact]
        public void TwoCopiesOfOneModelAreMarkedAsAMirror()
        {
            MatchReport report = ReportOf(
                new MatchTally(2),
                Match(Contender.Model("v12.onnx", "A"), Contender.Model("v12.onnx", "B")));

            Assert.True(report.IsMirror);
        }

        [Fact]
        public void TheNamesDoNotDecideIt()
        {
            // Naming both sides the same thing must not make a real
            // comparison look like a sanity check, and naming a mirror's
            // sides differently must not hide one.
            MatchReport differentFiles = ReportOf(
                new MatchTally(2),
                Match(Contender.Model("v12.onnx", "Same"), Contender.Model("v11.onnx", "Same")));

            Assert.False(differentFiles.IsMirror);
        }

        [Fact]
        public void ABrainWithNoFileCanStillBeAMirror()
        {
            MatchReport report = ReportOf(
                new MatchTally(2),
                Match(Contender.MonteCarlo(), Contender.MonteCarlo()));

            Assert.True(report.IsMirror);
        }

        [Fact]
        public void DifferentRiskIsNotAMirror()
        {
            MatchReport report = ReportOf(
                new MatchTally(2),
                Match(Contender.MonteCarlo(risk: 0f), Contender.MonteCarlo(risk: 1f)));

            Assert.False(report.IsMirror);
        }

        [Fact]
        public void AMatchIsRefusedBeforeItStartsWhenAModelCannotRead()
        {
            var match = Match(Contender.Model("v12.onnx", "Contender"), Contender.Baseline());

            string? problem = WinRateHarness.Refuse(
                match, _ => new NarrowEvaluator());

            Assert.NotNull(problem);
            Assert.Contains("Contender", problem!);
        }

        [Fact]
        public void AMatchOfTwoBrainsWithNoModelNeedsNoEvaluator()
        {
            Assert.Null(WinRateHarness.Refuse(
                Match(Contender.MonteCarlo(), Contender.Baseline()), _ => null));
        }

        [Fact]
        public void AMatchTooShortToPairIsRefused()
        {
            Assert.NotNull(WinRateHarness.Refuse(
                Match(battles: 1), _ => null));
        }

        /// <summary>A model that reads the wrong number of features - the
        /// §322 failure, which must now stop a match rather than produce
        /// one.</summary>
        sealed class NarrowEvaluator : IShadowEvaluator
        {
            public ShadowEvaluatorStatus Status { get; } = new ShadowEvaluatorStatus
            {
                Available = true,
                InputWidth = ObserverEncoder.TotalFeatures,
                OutputWidth = ObservationSchema.ActionSlots,
                Description = "a model from before the fair observation"
            };

            public ShadowPrediction? Predict(float[] state, float[] legalActionMask) => null;
        }
    }
}
