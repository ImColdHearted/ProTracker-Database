using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 303. The power-formula family: seventeen moves whose power is
    /// worked out from the battle rather than read from the file.
    ///
    /// Most of these are tested by calling the effect with a known damage
    /// figure rather than by playing a turn and comparing two numbers. The
    /// engine rolls 85-100% into every hit, and two runs that take different
    /// branches draw a different number of times from the same seed, so a
    /// turn-against-turn comparison would be testing the RNG as much as the
    /// formula. Apply() is the formula; a turn is the wiring, and there are
    /// tests for that too.
    /// </summary>
    public class Section303Tests
    {
        static Section303Tests()
        {
            MoveDex.EnsureLoaded();
            PokemonDex.EnsureLoaded();
        }

        /// <summary>Runs one effect over a fixed damage figure and returns
        /// what it made of it.</summary>
        static int Scaled(IMoveEffect effect, BattleState state, PokemonState attacker,
                          PokemonState defender, MoveState move, int damage = 1000)
        {
            bool cancelled = false;
            effect.Apply(state, attacker, defender, move, ref damage, ref cancelled);
            return damage;
        }

        static (BattleState, PokemonState, PokemonState) Field(int seed = 1)
        {
            PokemonState a = Mon("Machamp", type: PokemonType.Fighting);
            PokemonState b = Mon("Blissey", hp: 700);
            var (state, _) = Duel(seed, a, b);
            return (state, a, b);
        }

        // ---- the family's own rule ---------------------------------------

        [Fact]
        public void A_formula_rescales_the_damage_by_the_power_it_reports()
        {
            // Punishment at its listed 60 with a +3 target is 60 + 60 = 120,
            // so twice the listed power and twice the damage.
            var (state, a, b) = Field();
            b.AttackStage = 3;

            MoveState punishment = MoveDex.Get("Punishment");

            Assert.Equal(60, punishment.Power);
            Assert.Equal(2000, Scaled(new PowerFromTargetBoostsEffect(), state, a, b, punishment));
        }

        [Fact]
        public void A_formula_that_changes_nothing_leaves_the_damage_alone()
        {
            var (state, a, b) = Field();

            MoveState punishment = MoveDex.Get("Punishment");

            Assert.Equal(1000, Scaled(new PowerFromTargetBoostsEffect(), state, a, b, punishment));
        }

        [Fact]
        public void Every_formula_runs_before_the_damage_is_final()
        {
            foreach (IMoveEffect effect in new IMoveEffect[]
            {
                new PowerIfNoItemEffect(), new PowerIfTargetHurtEffect(),
                new PowerIfHurtByTargetEffect(), new PowerIfMovingFirstEffect(),
                new PowerIfMovingLastEffect(), new PowerIfTargetAsleepEffect(),
                new PowerOnElectricTerrainEffect(), new PowerFromTargetBoostsEffect(),
                new PowerFromSpeedRatioEffect(), new PowerFromUserHpBandsEffect(),
                new PowerFromTargetWeightEffect(), new PowerFromWeightRatioEffect(),
            })
            {
                Assert.Equal(MovePhase.BeforeDamage, effect.Phase);
            }
        }

        [Fact]
        public void The_registry_knows_every_name_the_data_uses()
        {
            foreach (string name in new[]
            {
                "PowerIfNoItem", "PowerIfTargetHurt", "PowerIfHurtByTarget",
                "PowerIfMovingFirst", "PowerIfMovingLast", "PowerIfTargetAsleep",
                "PowerOnElectricTerrain", "PowerFromTargetBoosts", "PowerFromSpeedRatio",
                "PowerFromUserHpBands", "PowerFromTargetWeight", "PowerFromWeightRatio",
            })
            {
                Assert.True(MoveEffectRegistry.IsKnown(name), name + " is not registered");
            }
        }

        // ---- the doublers -------------------------------------------------

        [Fact]
        public void Acrobatics_doubles_with_empty_hands()
        {
            var (state, a, b) = Field();
            MoveState move = MoveDex.Get("Acrobatics");
            var effect = new PowerIfNoItemEffect();

            // §303: the data listed Acrobatics at 110 - already doubled, from back
            // when nothing could double it. With the effect wired that would have
            // meant 220 barehanded, so the listed power is the real 55 again.
            Assert.Equal(55, move.Power);

            Assert.Equal(2000, Scaled(effect, state, a, b, move));

            a.HeldItemId = "Sitrus Berry";
            Assert.Equal(1000, Scaled(effect, state, a, b, move));
        }

        [Fact]
        public void Assurance_doubles_on_a_target_already_hurt_this_turn()
        {
            var (state, a, b) = Field();
            MoveState move = MoveDex.Get("Assurance");
            var effect = new PowerIfTargetHurtEffect();

            Assert.Equal(1000, Scaled(effect, state, a, b, move));

            b.LastPhysicalDamageTaken = 40;
            Assert.Equal(2000, Scaled(effect, state, a, b, move));
        }

        [Fact]
        public void Avalanche_and_Revenge_double_when_the_user_has_been_hit()
        {
            var (state, a, b) = Field();
            MoveState move = MoveDex.Get("Avalanche");
            var effect = new PowerIfHurtByTargetEffect();

            Assert.Equal(1000, Scaled(effect, state, a, b, move));

            a.LastSpecialDamageTaken = 12;
            Assert.Equal(2000, Scaled(effect, state, a, b, move));

            Assert.Contains("PowerIfHurtByTarget", MoveDex.Get("Revenge").Effects!);
        }

        [Fact]
        public void Bolt_Beak_doubles_only_while_the_target_has_not_moved()
        {
            var (state, a, b) = Field();
            MoveState move = MoveDex.Get("Bolt Beak");
            var effect = new PowerIfMovingFirstEffect();

            Assert.Equal(2000, Scaled(effect, state, a, b, move));

            b.ActedThisTurn = true;
            Assert.Equal(1000, Scaled(effect, state, a, b, move));
        }

        [Fact]
        public void Payback_is_the_mirror_of_it()
        {
            var (state, a, b) = Field();
            MoveState move = MoveDex.Get("Payback");
            var effect = new PowerIfMovingLastEffect();

            Assert.Equal(1000, Scaled(effect, state, a, b, move));

            b.ActedThisTurn = true;
            Assert.Equal(2000, Scaled(effect, state, a, b, move));
        }

        [Fact]
        public void Rising_Voltage_wants_electric_terrain_and_a_grounded_target()
        {
            var (state, a, b) = Field();
            MoveState move = MoveDex.Get("Rising Voltage");
            var effect = new PowerOnElectricTerrainEffect();

            Assert.Equal(1000, Scaled(effect, state, a, b, move));

            state.Environment.Terrain = TerrainType.Electric;
            Assert.Equal(2000, Scaled(effect, state, a, b, move));

            b.Types = new List<PokemonType> { PokemonType.Flying };
            Assert.Equal(1000, Scaled(effect, state, a, b, move));
        }

        [Fact]
        public void Wake_Up_Slap_doubles_on_a_sleeper_and_wakes_it()
        {
            var (state, a, b) = Field();
            MoveState move = MoveDex.Get("Wake-Up Slap");
            var effect = new PowerIfTargetAsleepEffect();

            Assert.Equal(1000, Scaled(effect, state, a, b, move));

            b.Status = StatusCondition.Sleep;
            b.SleepTurns = 3;

            Assert.Equal(2000, Scaled(effect, state, a, b, move));
            Assert.Equal(StatusCondition.None, b.Status);
            Assert.Equal(0, b.SleepTurns);
            Assert.True(LogContains(state, "was woken up"));
        }

        // ---- the arithmetic ------------------------------------------------

        [Fact]
        public void Punishment_counts_only_the_target_raised_stages_and_caps_at_200()
        {
            var (state, a, b) = Field();
            MoveState move = MoveDex.Get("Punishment");
            var effect = new PowerFromTargetBoostsEffect();

            b.DefenseStage = -2;                       // drops do not count
            Assert.Equal(1000, Scaled(effect, state, a, b, move));

            b.DefenseStage = 0;
            b.AttackStage = 6;
            b.SpeedStage = 6;                          // 60 + 240, capped at 200
            Assert.Equal(1000 * 200 / 60, Scaled(effect, state, a, b, move));
        }

        [Fact]
        public void Gyro_Ball_pays_the_slow()
        {
            MoveState move = MoveDex.Get("Gyro Ball");
            var effect = new PowerFromSpeedRatioEffect();

            PokemonState slow = Mon("Ferrothorn", speed: 20);
            PokemonState fast = Mon("Jolteon", speed: 200);
            var (state, _) = Duel(2, slow, fast);

            // 25 * 200 / 20 + 1 = 251, capped at 150.
            Assert.Equal(1000 * 150 / 60, Scaled(effect, state, slow, fast, move));

            // The other way round: 25 * 20 / 200 + 1 = 3.
            Assert.Equal(Math.Max(1, 1000 * 3 / 60), Scaled(effect, state, fast, slow, move));
        }

        [Fact]
        public void Reversal_climbs_as_the_user_falls()
        {
            MoveState move = MoveDex.Get("Reversal");
            var effect = new PowerFromUserHpBandsEffect();

            PokemonState a = Mon("Machamp", hp: 480);
            PokemonState b = Mon("Blissey", hp: 700);
            var (state, _) = Duel(3, a, b);

            Assert.Equal(480, a.MaxHP);

            a.CurrentHP = 480;                                   // full: the weakest band
            Assert.Equal(1000 * 20 / 70, Scaled(effect, state, a, b, move));

            a.CurrentHP = 5;                                     // ratio 0: the strongest
            Assert.Equal(1000 * 200 / 70, Scaled(effect, state, a, b, move));
        }

        // ---- weight --------------------------------------------------------

        [Fact]
        public void The_dex_carries_a_weight_for_every_species()
        {
            List<PokemonSpecies> all = PokemonDex.All();

            Assert.NotEmpty(all);
            Assert.All(all, species => Assert.True(species.WeightKg > 0, species.Name + " has no weight"));

            Assert.Equal(460d, PokemonDex.WeightOf("Snorlax"), 3);
            Assert.Equal(6d, PokemonDex.WeightOf("Pikachu"), 3);
            Assert.Equal(950d, PokemonDex.WeightOf("Groudon"), 3);
            Assert.Equal(0d, PokemonDex.WeightOf("Not A Pokemon"), 3);
        }

        [Fact]
        public void Low_Kick_reads_the_target_weight()
        {
            MoveState move = MoveDex.Get("Low Kick");
            var effect = new PowerFromTargetWeightEffect();

            PokemonState user = Mon("Machamp");
            var (state, _) = Duel(4, user, Mon("Snorlax"));

            // Snorlax is 460kg - the top band.
            Assert.Equal(1000 * 120 / 60, Scaled(effect, state, user, Mon("Snorlax"), move));

            // Pikachu is 6kg - the bottom one.
            Assert.Equal(1000 * 20 / 60, Scaled(effect, state, user, Mon("Pikachu"), move));

            // A species the dex has never heard of leaves the power alone
            // rather than reading as a featherweight.
            Assert.Equal(1000, Scaled(effect, state, user, Mon("Not A Pokemon"), move));
        }

        [Fact]
        public void Heavy_Slam_reads_both_weights()
        {
            MoveState move = MoveDex.Get("Heavy Slam");
            var effect = new PowerFromWeightRatioEffect();

            PokemonState heavy = Mon("Snorlax");      // 460kg
            PokemonState light = Mon("Pikachu");      // 6kg
            var (state, _) = Duel(5, heavy, light);

            // More than five times heavier: the top band.
            Assert.Equal(1000 * 120 / 80, Scaled(effect, state, heavy, light, move));

            // The other way round is the floor.
            Assert.Equal(1000 * 40 / 80, Scaled(effect, state, light, heavy, move));
        }

        // ---- the data ------------------------------------------------------

        [Fact]
        public void Every_move_in_the_batch_is_wired()
        {
            var wired = new Dictionary<string, string>
            {
                ["Acrobatics"] = "PowerIfNoItem",
                ["Assurance"] = "PowerIfTargetHurt",
                ["Avalanche"] = "PowerIfHurtByTarget",
                ["Revenge"] = "PowerIfHurtByTarget",
                ["Bolt Beak"] = "PowerIfMovingFirst",
                ["Fishious Rend"] = "PowerIfMovingFirst",
                ["Payback"] = "PowerIfMovingLast",
                ["Wake-Up Slap"] = "PowerIfTargetAsleep",
                ["Rising Voltage"] = "PowerOnElectricTerrain",
                ["Punishment"] = "PowerFromTargetBoosts",
                ["Gyro Ball"] = "PowerFromSpeedRatio",
                ["Reversal"] = "PowerFromUserHpBands",
                ["Low Kick"] = "PowerFromTargetWeight",
                ["Grass Knot"] = "PowerFromTargetWeight",
                ["Heavy Slam"] = "PowerFromWeightRatio",
                ["Heat Crash"] = "PowerFromWeightRatio",
            };

            foreach (var pair in wired)
            {
                MoveState move = MoveDex.Get(pair.Key);

                Assert.NotNull(move.Effects);
                Assert.Contains(pair.Value, move.Effects!);
            }
        }

        [Fact]
        public void Eruption_is_a_150_power_move_that_fades_with_the_user()
        {
            MoveState eruption = MoveDex.Get("Eruption");

            Assert.Equal(150, eruption.Power);
            Assert.Contains("UserHpPower", eruption.Effects!);

            // The same move Water Spout already was.
            Assert.Equal(MoveDex.Get("Water Spout").Power, eruption.Power);
        }

        [Fact]
        public void A_wired_move_really_hits_harder_in_the_battle()
        {
            // One end-to-end pass, to show the wiring reaches the damage and
            // not just the effect class: Low Kick into a 460kg target should
            // beat Low Kick into a 6kg one, at any roll.
            int Hit(string targetSpecies)
            {
                MoveState lowKick = MoveDex.Get("Low Kick");
                lowKick.Accuracy = 0;

                PokemonState user = Mon("Machamp", type: PokemonType.Fighting, moves: lowKick);
                PokemonState target = Mon(targetSpecies, hp: 700, defense: 100);

                var (state, engine) = Duel(9, user, target);

                engine.RunTurn(
                    MoveAction(state, user, lowKick),
                    MoveAction(state, target, target.Moves[0]));

                return 700 - target.CurrentHP;
            }

            Assert.True(Hit("Snorlax") > Hit("Pikachu") * 2,
                "Low Kick should hit a 460kg target far harder than a 6kg one");
        }
    }
}
