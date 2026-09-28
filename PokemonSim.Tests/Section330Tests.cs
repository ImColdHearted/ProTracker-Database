using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Evaluation;
using PokemonSim.Models;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §330. The pairing gives each contender both teams.
    ///
    /// §324 promised that "a result cannot be won by drawing the better
    /// team or sitting in the better seat" and delivered only the second
    /// half. It chose the team alongside the contender rather than
    /// alongside the seat, so One held team A in BOTH halves of every pair
    /// and Two held team B in both. The seats swapped; the teams never did.
    ///
    /// A MIRROR CAUGHT IT. Same brain on both sides, 400 battles, and
    /// §328's interval came out 6.9 points wide - 99.5% of the widest it
    /// can mathematically be, meaning almost every pair went 2-0 or 0-2.
    /// Two identical brains cannot beat each other twice running; a better
    /// team can, and did, because its owner never had to give it back.
    ///
    /// These tests are about the seating alone, with hand-made teams, so
    /// they say exactly one thing and say it without a species catalog.
    /// </summary>
    public class Section330Tests
    {
        const string One = "Contender";
        const string Two = "Opponent";

        static MatchConfiguration Match() => new MatchConfiguration
        {
            One = Contender.Baseline(One),
            Two = Contender.Baseline(Two),
            Battles = 4
        };

        static List<PokemonState> Team(string tag) => new List<PokemonState>
        {
            TestKit.Mon(tag + "-lead", PokemonType.Normal, moves: new[] { TestKit.Move(tag + " Hit") }),
            TestKit.Mon(tag + "-bench", PokemonType.Water, moves: new[] { TestKit.Move(tag + " Bench") })
        };

        static string Names(PlayerState side) =>
            string.Join(",", side.Team.Select(m => m.Species));

        static (BattleState First, BattleState Second) Pair()
        {
            MatchConfiguration match = Match();

            // The same two teams for both halves - which is the point of a
            // pair, and what the harness builds from the pairing index.
            List<PokemonState> a = Team("A");
            List<PokemonState> b = Team("B");

            BattleState first = WinRateHarness.Seat(match, true, a, b, 330);

            // Rebuilt, because the engine mutates the states it is given -
            // the harness builds fresh teams per battle too.
            BattleState second = WinRateHarness.Seat(match, false, Team("A"), Team("B"), 330);

            return (first, second);
        }

        // ==============================================================
        // The fix
        // ==============================================================

        [Fact]
        public void EachContenderPlaysBothTeamsAcrossThePair()
        {
            // THE regression. Before §330 both of these were team A.
            (BattleState half1, BattleState half2) = Pair();

            string oneInFirstHalf = Names(half1.Player1);
            string oneInSecondHalf = Names(half2.Player2);

            Assert.Equal(One, half1.Player1.Name);
            Assert.Equal(One, half2.Player2.Name);

            Assert.NotEqual(oneInFirstHalf, oneInSecondHalf);
        }

        [Fact]
        public void AndTheTeamItPlaysSecondIsTheOneItsOpponentHadFirst()
        {
            // The precise statement: the two contenders trade teams.
            (BattleState half1, BattleState half2) = Pair();

            Assert.Equal(Names(half1.Player2), Names(half2.Player2));   // One gets Two's first team
            Assert.Equal(Names(half1.Player1), Names(half2.Player1));   // Two gets One's first team
        }

        [Fact]
        public void EachContenderAlsoSitsInBothSeats()
        {
            (BattleState half1, BattleState half2) = Pair();

            Assert.Equal(One, half1.Player1.Name);
            Assert.Equal(Two, half1.Player2.Name);

            Assert.Equal(Two, half2.Player1.Name);
            Assert.Equal(One, half2.Player2.Name);
        }

        [Fact]
        public void TheTEAMSDoNotMoveBetweenTheHalves()
        {
            // The mechanism, stated on its own: seat one always holds team
            // A and seat two always holds team B. It is the contenders who
            // move. Getting this backwards is precisely what §324 did.
            (BattleState half1, BattleState half2) = Pair();

            Assert.Equal(Names(half1.Player1), Names(half2.Player1));
            Assert.Equal(Names(half1.Player2), Names(half2.Player2));
        }

        [Fact]
        public void ATeamIsNeverSharedBetweenTheTwoSeats()
        {
            (BattleState half1, _) = Pair();

            Assert.NotSame(half1.Player1.Team, half1.Player2.Team);
            Assert.NotEqual(Names(half1.Player1), Names(half1.Player2));
        }

        // ==============================================================
        // What rides along with it
        // ==============================================================

        [Fact]
        public void BothHalvesOfAPairStartFromTheSameRoll()
        {
            // §324's other variance reduction, and it has to survive this
            // change: the two halves are only a controlled comparison if
            // they start identically.
            MatchConfiguration match = Match();

            BattleState first = WinRateHarness.Seat(match, true, Team("A"), Team("B"), 4242);
            BattleState second = WinRateHarness.Seat(match, false, Team("A"), Team("B"), 4242);

            Assert.Equal(first.Rng.Next(1000), second.Rng.Next(1000));
        }

        [Fact]
        public void TheActivePokemonIsTheLeadOfItsOwnSeatsTeam()
        {
            (BattleState half1, BattleState half2) = Pair();

            foreach (BattleState state in new[] { half1, half2 })
            {
                Assert.Same(state.Player1.Team[0], state.Player1.ActivePokemon);
                Assert.Same(state.Player2.Team[0], state.Player2.ActivePokemon);
            }
        }

        [Fact]
        public void TheSeatCarriesTheNameOfWhoeverIsSittingInIt()
        {
            // The report attributes wins by seat, so a name on the wrong
            // seat would invert half the result.
            (BattleState half1, BattleState half2) = Pair();

            Assert.NotEqual(half1.Player1.Name, half2.Player1.Name);
            Assert.NotEqual(half1.Player2.Name, half2.Player2.Name);
        }
    }
}
