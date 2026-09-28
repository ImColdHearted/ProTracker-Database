using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Data;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §306. The move oracle: what a move's power really is, asked of the
    /// engine rather than worked out a second time.
    ///
    /// These are the tests the tracker's Damage Calculator could never have -
    /// it is a window, and a window is tested by opening it. The oracle lives
    /// in the simulator so that this file can exist.
    /// </summary>
    public class Section306Tests
    {
        static Section306Tests()
        {
            MoveDex.EnsureLoaded();
            PokemonDex.EnsureLoaded();
        }

        /// <summary>A plain 100-stat pair, the way a calculator opens.</summary>
        static MoveOracle.Context Field(
            string attacker = "Machamp",
            string defender = "Blissey",
            string[]? defenderTypes = null)
        {
            return new MoveOracle.Context
            {
                Attacker = new MoveOracle.Side
                {
                    Species = attacker,
                    Types = new[] { "Fighting" },
                    Level = 100,
                    MaxHp = 300,
                    CurrentHp = 300,
                },
                Defender = new MoveOracle.Side
                {
                    Species = defender,
                    Types = defenderTypes is { Length: > 0 } ? defenderTypes : new[] { "Normal" },
                    Level = 100,
                    MaxHp = 700,
                    CurrentHp = 700,
                },
            };
        }

        static MoveOracle.Answer Ask(string move, MoveOracle.Context context) =>
            MoveOracle.Ask(move, context);

        // ---- the three kinds of answer -----------------------------------

        [Fact]
        public void A_move_with_no_formula_reports_its_listed_power()
        {
            MoveOracle.Answer answer = Ask("Tackle", Field());

            Assert.Equal(MoveOracle.AnswerKind.Listed, answer.Kind);
            Assert.Equal(MoveDex.Get("Tackle").Power, answer.Power);
        }

        [Fact]
        public void A_move_the_simulator_has_never_heard_of_says_so()
        {
            MoveOracle.Answer answer = Ask("Not A Real Move", Field());

            Assert.Equal(MoveOracle.AnswerKind.Unknown, answer.Kind);
        }

        [Fact]
        public void A_move_that_names_its_own_damage_is_told_apart_from_one_that_scales()
        {
            // Seismic Toss deals the user's level, whatever the probe was.
            MoveOracle.Answer toss = Ask("Seismic Toss", Field());

            Assert.Equal(MoveOracle.AnswerKind.FixedDamage, toss.Kind);
            Assert.Equal(100, toss.Damage);

            // Low Kick scales what it is given, so it reports a power.
            MoveOracle.Answer kick = Ask("Low Kick", Field(defender: "Snorlax"));

            Assert.Equal(MoveOracle.AnswerKind.Power, kick.Kind);
        }

        [Fact]
        public void Super_Fang_takes_half_of_what_is_left()
        {
            MoveOracle.Context field = Field();
            field.Defender.CurrentHp = 400;

            MoveOracle.Answer answer = Ask("Super Fang", field);

            Assert.Equal(MoveOracle.AnswerKind.FixedDamage, answer.Kind);
            Assert.Equal(200, answer.Damage);
        }

        // ---- the formulas the calculator could not answer -----------------

        [Fact]
        public void Low_Kick_reads_the_targets_weight()
        {
            // Snorlax is 460kg - the top band - and Gastly is a whisper.
            int heavy = Ask("Low Kick", Field(defender: "Snorlax")).Power;
            int light = Ask("Low Kick", Field(defender: "Pikachu")).Power;

            Assert.Equal(120, heavy);
            Assert.Equal(20, light);
        }

        [Fact]
        public void Grass_Knot_reads_the_same_weight_as_Low_Kick()
        {
            Assert.Equal(
                Ask("Low Kick", Field(defender: "Snorlax")).Power,
                Ask("Grass Knot", Field(defender: "Snorlax")).Power);
        }

        [Fact]
        public void Reversal_is_strongest_at_the_last_gasp()
        {
            MoveOracle.Context field = Field();

            field.Attacker.CurrentHp = 1;
            Assert.Equal(200, Ask("Reversal", field).Power);

            field.Attacker.CurrentHp = field.Attacker.MaxHp;
            Assert.Equal(20, Ask("Reversal", field).Power);
        }

        [Fact]
        public void Gyro_Ball_is_strongest_when_the_user_is_slowest()
        {
            MoveOracle.Context field = Field();

            field.Attacker.Speed = 10;
            field.Defender.Speed = 500;

            Assert.Equal(150, Ask("Gyro Ball", field).Power);

            field.Attacker.Speed = 500;
            field.Defender.Speed = 10;

            Assert.Equal(1, Ask("Gyro Ball", field).Power);
        }

        [Fact]
        public void Return_and_Frustration_run_opposite_ways()
        {
            MoveOracle.Context field = Field();

            field.Attacker.Happiness = 255;
            Assert.Equal(102, Ask("Return", field).Power);
            Assert.Equal(1, Ask("Frustration", field).Power);

            field.Attacker.Happiness = 0;
            Assert.Equal(1, Ask("Return", field).Power);
            Assert.Equal(102, Ask("Frustration", field).Power);
        }

        [Fact]
        public void Punishment_grows_with_the_targets_boosts()
        {
            MoveOracle.Context field = Field();

            Assert.Equal(60, Ask("Punishment", field).Power);

            field.Defender.AttackStage = 3;
            field.Defender.SpeedStage = 2;

            Assert.Equal(160, Ask("Punishment", field).Power);
        }

        [Fact]
        public void Heavy_Slam_reads_both_weights()
        {
            // Snorlax (460kg) onto Pikachu (6kg) is far past five times.
            Assert.Equal(120, Ask("Heavy Slam",
                Field(attacker: "Snorlax", defender: "Pikachu")).Power);

            // And the other way round is the floor.
            Assert.Equal(40, Ask("Heavy Slam",
                Field(attacker: "Pikachu", defender: "Snorlax")).Power);
        }

        // ---- the ones that needed a new input -----------------------------

        [Fact]
        public void Acrobatics_doubles_with_empty_hands()
        {
            MoveOracle.Context field = Field();

            field.Attacker.Item = "None";
            Assert.Equal(110, Ask("Acrobatics", field).Power);

            field.Attacker.Item = "Life Orb";
            Assert.Equal(MoveOracle.AnswerKind.Listed, Ask("Acrobatics", field).Kind);
        }

        [Fact]
        public void Facade_doubles_off_a_status()
        {
            MoveOracle.Context field = Field();

            Assert.Equal(MoveOracle.AnswerKind.Listed, Ask("Facade", field).Kind);

            field.Attacker.Status = "Burned";
            Assert.Equal(140, Ask("Facade", field).Power);
        }

        [Fact]
        public void Hex_doubles_off_the_targets_status()
        {
            MoveOracle.Context field = Field();

            field.Defender.Status = "Paralyzed";

            Assert.Equal(130, Ask("Hex", field).Power);
        }

        [Fact]
        public void Payback_doubles_when_the_user_moves_second()
        {
            MoveOracle.Context field = Field();

            field.AttackerMovesFirst = true;
            Assert.Equal(MoveOracle.AnswerKind.Listed, Ask("Payback", field).Kind);

            field.AttackerMovesFirst = false;
            Assert.Equal(100, Ask("Payback", field).Power);
        }

        [Fact]
        public void Bolt_Beak_doubles_when_the_user_moves_first()
        {
            MoveOracle.Context field = Field();

            field.AttackerMovesFirst = true;
            Assert.Equal(170, Ask("Bolt Beak", field).Power);

            field.AttackerMovesFirst = false;
            Assert.Equal(MoveOracle.AnswerKind.Listed, Ask("Bolt Beak", field).Kind);
        }

        [Fact]
        public void Pursuit_doubles_into_a_target_that_is_leaving()
        {
            MoveOracle.Context field = Field();

            Assert.Equal(MoveOracle.AnswerKind.Listed, Ask("Pursuit", field).Kind);

            field.DefenderSwitchingOut = true;
            Assert.Equal(80, Ask("Pursuit", field).Power);
        }

        [Fact]
        public void Rage_Fist_grows_with_the_hits_taken()
        {
            MoveOracle.Context field = Field();

            field.Attacker.TimesAttacked = 3;

            Assert.Equal(200, Ask("Rage Fist", field).Power);
        }

        [Fact]
        public void Fury_Cutter_grows_with_the_turns_in_a_row()
        {
            MoveOracle.Context field = Field();

            field.Attacker.ConsecutiveUses = 1;
            Assert.Equal(MoveOracle.AnswerKind.Listed, Ask("Fury Cutter", field).Kind);

            field.Attacker.ConsecutiveUses = 3;
            Assert.Equal(160, Ask("Fury Cutter", field).Power);
        }

        [Fact]
        public void Stomping_Tantrum_doubles_after_a_failure()
        {
            MoveOracle.Context field = Field();

            field.Attacker.LastMoveFailed = true;

            Assert.Equal(150, Ask("Stomping Tantrum", field).Power);
        }

        [Fact]
        public void Rising_Voltage_needs_the_terrain_and_a_grounded_target()
        {
            MoveOracle.Context field = Field();

            field.Terrain = "Electric";
            Assert.Equal(140, Ask("Rising Voltage", field).Power);

            // A Flying target is not standing on it.
            MoveOracle.Context flying = Field(defenderTypes: new[] { "Flying" });
            flying.Terrain = "Electric";
            Assert.Equal(MoveOracle.AnswerKind.Listed, Ask("Rising Voltage", flying).Kind);
        }

        [Fact]
        public void Weather_Ball_changes_its_type_and_its_power()
        {
            MoveOracle.Context field = Field();

            MoveOracle.Answer plain = Ask("Weather Ball", field);
            Assert.Equal("Normal", plain.Type);

            field.Weather = "Rain";

            MoveOracle.Answer wet = Ask("Weather Ball", field);
            Assert.Equal("Water", wet.Type);
            Assert.Equal(100, wet.Power);
        }

        // ---- the shape of the answer --------------------------------------

        [Fact]
        public void The_hit_count_comes_back_with_the_answer()
        {
            MoveOracle.Answer spear = Ask("Icicle Spear", Field());

            Assert.Equal(2, spear.MinHits);
            Assert.Equal(5, spear.MaxHits);

            Assert.Equal(1, Ask("Tackle", Field()).MaxHits);
        }

        [Fact]
        public void Asking_twice_gives_the_same_answer()
        {
            // The oracle resolves the move against a throwaway battle, and a
            // formula may change that battle as it answers - Wake-Up Slap
            // wakes the target up. If the throwaway were shared, the second
            // question would be a different question.
            MoveOracle.Context field = Field();
            field.Defender.Status = "Asleep";

            MoveOracle.Answer first = Ask("Wake-Up Slap", field);
            MoveOracle.Answer second = Ask("Wake-Up Slap", field);

            Assert.Equal(140, first.Power);
            Assert.Equal(first.Power, second.Power);
        }

        [Fact]
        public void Asking_does_not_disturb_the_move_in_the_dex()
        {
            MoveOracle.Context field = Field();
            field.Weather = "Rain";

            Ask("Weather Ball", field);

            // Weather Ball writes its own power and type as it resolves. If
            // the oracle handed out the dex's own instance rather than a
            // copy, every later reader would see a Water move.
            MoveState fresh = MoveDex.Get("Weather Ball");

            Assert.Equal(PokemonType.Normal, fresh.Type);
            Assert.Equal(50, fresh.Power);
        }

        [Fact]
        public void A_move_the_calculator_can_be_told_about_is_one_it_can_offer()
        {
            // §306a. The calculator builds its move list from its own file,
            // which carries no power for any of these - so a list filtered on
            // that power dropped every one of them before the learnset was
            // consulted, and §306 answered twenty-six moves that could not be
            // picked. IsDamaging is the question the list asks instead.
            foreach (string name in new[]
            {
                "Low Kick", "Grass Knot", "Heavy Slam", "Heat Crash", "Gyro Ball",
                "Electro Ball", "Flail", "Reversal", "Punishment", "Return",
                "Frustration", "Hard Press", "Beat Up", "Seismic Toss", "Night Shade",
                "Super Fang", "Nature's Madness", "Ruination", "Psywave", "Endeavor",
                "Final Gambit", "Counter", "Mirror Coat", "Metal Burst",
            })
            {
                Assert.True(MoveOracle.IsDamaging(name), name + " would not be offered");
            }
        }

        [Fact]
        public void A_status_move_is_not_offered_as_a_damaging_one()
        {
            Assert.False(MoveOracle.IsDamaging("Swords Dance"));
            Assert.False(MoveOracle.IsDamaging("Recover"));
            Assert.False(MoveOracle.IsDamaging("Not A Real Move"));
            Assert.False(MoveOracle.IsDamaging(null));
        }

        [Fact]
        public void Every_move_the_calculator_could_not_answer_is_answered_now()
        {
            // The thirteen the tracker's own move file has no power for, and
            // the thirteen whose answer is a set damage. If any of these ever
            // stops being answerable, the calculator goes back to refusing it.
            var powers = new[]
            {
                "Low Kick", "Grass Knot", "Heavy Slam", "Heat Crash", "Gyro Ball",
                "Electro Ball", "Flail", "Reversal", "Punishment", "Return",
                "Frustration", "Hard Press", "Beat Up",
            };

            var fixedDamage = new[]
            {
                "Seismic Toss", "Night Shade", "Super Fang", "Nature's Madness",
                "Ruination", "Psywave", "Endeavor", "Final Gambit",
            };

            MoveOracle.Context field = Field(defender: "Snorlax");
            field.Attacker.CurrentHp = field.Attacker.MaxHp / 4;
            field.Defender.CurrentHp = field.Defender.MaxHp / 2;
            field.Attacker.Happiness = 0;
            field.Defender.AttackStage = 2;
            field.Attacker.Speed = 20;
            field.Defender.Speed = 300;

            foreach (string name in powers)
            {
                MoveOracle.Answer answer = Ask(name, field);

                Assert.True(
                    answer.Kind is MoveOracle.AnswerKind.Power or MoveOracle.AnswerKind.Listed,
                    $"{name} came back as {answer.Kind}");

                Assert.True(answer.Power > 0, $"{name} reported no power");
            }

            foreach (string name in fixedDamage)
            {
                MoveOracle.Answer answer = Ask(name, field);

                Assert.True(
                    answer.Kind == MoveOracle.AnswerKind.FixedDamage,
                    $"{name} came back as {answer.Kind}");
            }
        }
    }
}
