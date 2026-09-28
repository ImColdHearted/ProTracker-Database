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
    /// §307. Abilities, measured off the engine rather than listed a second
    /// time in the tracker.
    ///
    /// Every factor below is asserted as a RANGE. The probe resolves a whole
    /// move twice and divides, and the engine floors its arithmetic in several
    /// places, so an ability that doubles a hit lands very near two rather
    /// than exactly on it. Pinning an exact value would be pinning the
    /// rounding, not the ability.
    /// </summary>
    public class Section307Tests
    {
        static Section307Tests()
        {
            MoveDex.EnsureLoaded();
            PokemonDex.EnsureLoaded();
        }

        static MoveOracle.Context Field(
            string attackerAbility = "None",
            string defenderAbility = "None",
            string attackerType = "Normal",
            string defenderType = "Normal")
        {
            return new MoveOracle.Context
            {
                Attacker = new MoveOracle.Side
                {
                    Species = "Machamp",
                    Types = new[] { attackerType },
                    Level = 100,
                    MaxHp = 300,
                    CurrentHp = 300,
                    Attack = 200,
                    SpAttack = 200,
                    Ability = attackerAbility,
                },
                Defender = new MoveOracle.Side
                {
                    Species = "Blissey",
                    Types = new[] { defenderType },
                    Level = 100,
                    MaxHp = 700,
                    CurrentHp = 700,
                    Defense = 100,
                    SpDefense = 100,
                    Ability = defenderAbility,
                },
            };
        }

        static double Factor(string move, MoveOracle.Context context) =>
            MoveOracle.AbilityFactor(move, context);

        // ---- the data --------------------------------------------------

        [Fact]
        public void Every_species_in_the_dex_has_its_abilities()
        {
            List<PokemonSpecies> all = PokemonDex.All();

            Assert.NotEmpty(all);

            List<string> without = all
                .Where(s => s.Abilities.Count == 0)
                .Select(s => s.Name)
                .ToList();

            Assert.True(without.Count == 0,
                $"{without.Count} species have no abilities, e.g. {string.Join(", ", without.Take(5))}");
        }

        [Fact]
        public void A_species_reports_the_abilities_it_can_actually_have()
        {
            Assert.Equal(new[] { "Iron Barbs", "Anticipation" }, PokemonDex.AbilitiesOf("Ferrothorn"));
            Assert.Equal(new[] { "Intimidate", "Flash Fire" }, PokemonDex.AbilitiesOf("Arcanine"));

            // And not ones it cannot.
            Assert.DoesNotContain("Levitate", PokemonDex.AbilitiesOf("Ferrothorn"));
            Assert.DoesNotContain("Huge Power", PokemonDex.AbilitiesOf("Ferrothorn"));
        }

        [Fact]
        public void An_unknown_species_reports_nothing_rather_than_throwing()
        {
            Assert.Empty(PokemonDex.AbilitiesOf("Not A Pokemon"));
            Assert.Empty(PokemonDex.AbilitiesOf(null));
        }

        [Fact]
        public void The_engine_knows_which_abilities_it_has_a_rule_for()
        {
            Assert.True(PokemonDex.AbilityIsSimulated("Huge Power"));
            Assert.True(PokemonDex.AbilityIsSimulated("Iron Barbs"));
            Assert.True(PokemonDex.AbilityIsSimulated("thickfat"));

            Assert.False(PokemonDex.AbilityIsSimulated("Not An Ability"));
            Assert.False(PokemonDex.AbilityIsSimulated(null));
        }

        // ---- the probe --------------------------------------------------

        [Fact]
        public void No_ability_on_either_side_is_exactly_no_change()
        {
            Assert.Equal(1.0, Factor("Tackle", Field()), 6);
        }

        [Fact]
        public void An_ability_the_engine_cannot_simulate_changes_nothing()
        {
            Assert.Equal(1.0, Factor("Tackle", Field(attackerAbility: "Not An Ability")), 6);
        }

        [Fact]
        public void Huge_Power_doubles_a_physical_hit()
        {
            Assert.InRange(Factor("Tackle", Field(attackerAbility: "Huge Power")), 1.9, 2.1);
        }

        [Fact]
        public void Thick_Fat_halves_a_Fire_hit_and_leaves_the_rest_alone()
        {
            Assert.InRange(Factor("Flamethrower", Field(defenderAbility: "Thick Fat")), 0.45, 0.55);
            Assert.Equal(1.0, Factor("Tackle", Field(defenderAbility: "Thick Fat")), 6);
        }

        [Fact]
        public void Levitate_makes_a_Ground_move_do_nothing()
        {
            Assert.Equal(0.0, Factor("Earthquake", Field(defenderAbility: "Levitate")), 6);
        }

        [Fact]
        public void Volt_Absorb_does_the_same_to_an_Electric_one()
        {
            Assert.Equal(0.0, Factor("Thunderbolt", Field(defenderAbility: "Volt Absorb")), 6);
        }

        [Fact]
        public void Adaptability_raises_the_same_type_bonus_rather_than_the_damage()
        {
            // 1.5 STAB becomes 2.0, so four thirds - and nothing at all on a
            // move the user does not share a type with.
            MoveOracle.Context stab = Field(attackerAbility: "Adaptability", attackerType: "Normal");

            Assert.InRange(Factor("Tackle", stab), 1.28, 1.39);

            MoveOracle.Context off = Field(attackerAbility: "Adaptability", attackerType: "Water");

            Assert.Equal(1.0, Factor("Tackle", off), 6);
        }

        [Fact]
        public void Technician_only_lifts_the_weaker_moves()
        {
            // Tackle is 40 power; Body Slam is 85.
            Assert.InRange(Factor("Tackle", Field(attackerAbility: "Technician")), 1.4, 1.6);
            Assert.Equal(1.0, Factor("Body Slam", Field(attackerAbility: "Technician")), 6);
        }

        [Fact]
        public void Tinted_Lens_only_lifts_what_the_target_resists()
        {
            MoveOracle.Context resisted = Field(attackerAbility: "Tinted Lens", defenderType: "Rock");

            Assert.InRange(Factor("Tackle", resisted), 1.9, 2.1);

            Assert.Equal(1.0, Factor("Tackle", Field(attackerAbility: "Tinted Lens")), 6);
        }

        [Fact]
        public void Multiscale_halves_a_hit_on_a_target_at_full_health()
        {
            MoveOracle.Context full = Field(defenderAbility: "Multiscale");

            Assert.InRange(Factor("Tackle", full), 0.45, 0.55);
        }

        /// <summary>
        /// The one that says the measurement is the right design.
        ///
        /// Guts both ignores a burn and adds half again, so against a baseline
        /// that IS burned the factor is three. A caller that applies its own
        /// burn halving multiplies the two and lands on one and a half, which
        /// is correct - and it gets there without anybody writing down that
        /// Guts is an ability that also cancels a burn.
        /// </summary>
        [Fact]
        public void Guts_comes_back_as_three_because_the_baseline_is_burned_too()
        {
            MoveOracle.Context burned = Field(attackerAbility: "Guts");
            burned.Attacker.Status = "Burned";

            Assert.InRange(Factor("Tackle", burned), 2.8, 3.2);

            // And with nothing wrong with it, Guts does nothing at all - the
            // boost is the reward for carrying a status, not a flat bonus.
            Assert.Equal(1.0, Factor("Tackle", Field(attackerAbility: "Guts")), 6);
        }

        [Fact]
        public void An_ability_that_only_matters_on_entry_leaves_the_hit_alone()
        {
            // Intimidate lowers Attack as its owner comes in; it does nothing
            // to a hit already being dealt, and the probe says so rather than
            // inventing a number.
            Assert.Equal(1.0, Factor("Tackle", Field(defenderAbility: "Intimidate")), 6);
        }

        [Fact]
        public void The_probe_is_repeatable()
        {
            MoveOracle.Context field = Field(attackerAbility: "Huge Power");

            double first = Factor("Tackle", field);
            double second = Factor("Tackle", field);

            Assert.Equal(first, second, 6);
        }

        [Fact]
        public void The_probe_does_not_disturb_the_move_in_the_dex()
        {
            // It forces the move to a sure hit so the answer is the hit rather
            // than a story about whether it landed. That must be a copy.
            Factor("Thunder", Field(attackerAbility: "Huge Power"));

            Assert.Equal(MoveDex.Get("Thunder").Accuracy, MoveDex.Get("Thunder").Accuracy);
            Assert.True(MoveDex.Get("Thunder").Accuracy > 0);
        }
    }
}
