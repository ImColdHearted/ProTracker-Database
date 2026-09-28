using System;
using System.Collections.Generic;
using PokemonSim.Data;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §308. The six conditions the Damage Calculator stopped ASKING for, and
    /// the ten moves that turn on them.
    ///
    /// The window's field column lost Attacker moves first, Defender
    /// switching out, Hurt this turn, Last move failed and Defense Curl,
    /// because PRO has no doubles and half of what sat beside them is a
    /// generation past it. Dropping a control that feeds a real move is
    /// normally how a calculator starts lying, and the answer was not to put
    /// the controls back: it was to have the window measure each condition
    /// off this oracle and print what the other number would be on the move's
    /// own line.
    ///
    /// That makes these six the load-bearing part. If any one of them
    /// silently stopped reaching the battle the oracle builds, the window
    /// would not get a wrong number - it would get no sentence at all, and
    /// Pursuit's 80 would just quietly stop being mentioned. A silent
    /// omission is the hardest kind of rot to notice, so every one of them is
    /// pinned here, in both directions: the plain reading the row shows, and
    /// the bent reading the note names.
    /// </summary>
    public class Section308Tests
    {
        static Section308Tests()
        {
            MoveDex.EnsureLoaded();
            PokemonDex.EnsureLoaded();
        }

        /// <summary>A plain 100-stat pair, the way a calculator opens - and
        /// the way DamageCalculatorViewModel.BuildContext now always builds
        /// it: the user moves first, nobody is leaving, nothing is hurt.
        /// </summary>
        static MoveOracle.Context Field()
        {
            return new MoveOracle.Context
            {
                Attacker = new MoveOracle.Side
                {
                    Species = "Machamp",
                    Types = new[] { "Fighting" },
                    Level = 100,
                    MaxHp = 300,
                    CurrentHp = 300,
                },
                Defender = new MoveOracle.Side
                {
                    Species = "Blissey",
                    Types = new[] { "Normal" },
                    Level = 100,
                    MaxHp = 700,
                    CurrentHp = 700,
                },
            };
        }

        /// <summary>The six, exactly as ConditionalNote bends them.</summary>
        public static IEnumerable<object[]> TheSix() => new[]
        {
            new object[] { "Payback", "movesLast", 50, 100 },
            new object[] { "Bolt Beak", "movesLast", 170, 85 },
            new object[] { "Fishious Rend", "movesLast", 170, 85 },
            new object[] { "Pursuit", "switching", 40, 80 },
            new object[] { "Avalanche", "userHurt", 60, 120 },
            new object[] { "Revenge", "userHurt", 60, 120 },
            new object[] { "Assurance", "targetHurt", 60, 120 },
            new object[] { "Stomping Tantrum", "lastFailed", 75, 150 },
            new object[] { "Temper Flare", "lastFailed", 75, 150 },
            new object[] { "Rollout", "curled", 30, 60 },
        };

        static void Bend(MoveOracle.Context context, string which)
        {
            switch (which)
            {
                case "movesLast": context.AttackerMovesFirst = false; break;
                case "switching": context.DefenderSwitchingOut = true; break;
                case "userHurt": context.Attacker.HurtThisTurn = true; break;
                case "targetHurt": context.Defender.HurtThisTurn = true; break;
                case "lastFailed": context.Attacker.LastMoveFailed = true; break;
                case "curled": context.Attacker.DefenseCurled = true; break;
                default: throw new ArgumentOutOfRangeException(nameof(which), which, null);
            }
        }

        // ---- the plain reading, which is the number the row shows ---------

        [Theory]
        [MemberData(nameof(TheSix))]
        public void The_plain_reading_is_what_the_window_now_shows(
            string move, string which, int plain, int bent)
        {
            _ = which;
            _ = bent;

            MoveOracle.Answer answer = MoveOracle.Ask(move, Field());

            Assert.True(
                answer.Kind is MoveOracle.AnswerKind.Listed or MoveOracle.AnswerKind.Power,
                $"{move} came back as {answer.Kind}, which the window would not price at all.");

            Assert.Equal(plain, answer.Power);
        }

        // ---- and the bent one, which is what the note has to be able to say

        [Theory]
        [MemberData(nameof(TheSix))]
        public void Every_condition_the_window_dropped_still_moves_its_number(
            string move, string which, int plain, int bent)
        {
            MoveOracle.Context context = Field();
            Bend(context, which);

            MoveOracle.Answer answer = MoveOracle.Ask(move, context);

            Assert.True(
                answer.Kind is MoveOracle.AnswerKind.Listed or MoveOracle.AnswerKind.Power,
                $"{move} came back as {answer.Kind} once {which} was set, so the note would say nothing.");

            Assert.Equal(bent, answer.Power);
            Assert.NotEqual(plain, answer.Power);
        }

        /// <summary>The note is built by comparing two answers, so the
        /// difference has to survive being asked for twice in a row off two
        /// separately built contexts - which is the thing that would break if
        /// Ask ever started carrying state between calls.</summary>
        [Theory]
        [MemberData(nameof(TheSix))]
        public void Asking_plain_then_bent_then_plain_gives_the_same_pair(
            string move, string which, int plain, int bent)
        {
            Assert.Equal(plain, MoveOracle.Ask(move, Field()).Power);

            MoveOracle.Context context = Field();
            Bend(context, which);
            Assert.Equal(bent, MoveOracle.Ask(move, context).Power);

            Assert.Equal(plain, MoveOracle.Ask(move, Field()).Power);
        }

        /// <summary>Bending one condition must not move a move that does not
        /// read it. This is what stops the window printing "or 90 if the user
        /// has curled up" against Close Combat.</summary>
        [Theory]
        [InlineData("movesLast")]
        [InlineData("switching")]
        [InlineData("userHurt")]
        [InlineData("targetHurt")]
        [InlineData("lastFailed")]
        [InlineData("curled")]
        public void A_move_that_reads_none_of_them_is_unmoved_by_all_of_them(string which)
        {
            foreach (string move in new[] { "Close Combat", "Earthquake", "Surf", "Thunderbolt" })
            {
                int plain = MoveOracle.Ask(move, Field()).Power;

                MoveOracle.Context context = Field();
                Bend(context, which);

                Assert.Equal(plain, MoveOracle.Ask(move, context).Power);
            }
        }

        /// <summary>Rollout ramps on its own count as well, and the curl
        /// doubles whatever the ramp has reached - so the note's second
        /// number moves with the "Turns in a row" box rather than being a
        /// fixed 60.</summary>
        [Fact]
        public void The_curl_doubles_whatever_the_ramp_has_already_reached()
        {
            MoveOracle.Context context = Field();
            context.Attacker.ConsecutiveUses = 3;

            int ramped = MoveOracle.Ask("Rollout", context).Power;

            Assert.Equal(120, ramped);

            context.Attacker.DefenseCurled = true;

            Assert.Equal(ramped * 2, MoveOracle.Ask("Rollout", context).Power);
        }
    }
}
