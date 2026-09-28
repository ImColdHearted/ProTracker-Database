using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Data;
using PokemonSim.Engine.Items;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §309. Held items, measured off the engine rather than listed a second
    /// time - §307's argument, one section later and with a worse starting
    /// position.
    ///
    /// The Damage Calculator offered seven items and implemented four of them
    /// in its own damage chain while the engine implemented those four and a
    /// hundred more. Worse, the DEFENDER's item box had nothing behind it at
    /// all: the request the calculator built carried an AttackerItem and no
    /// DefenderItem, so Eviolite, Assault Vest, Air Balloon and all eighteen
    /// resist berries were controls that could not do anything. Several of
    /// the facts below exist specifically to pin that half.
    /// </summary>
    public class Section309Tests
    {
        static Section309Tests()
        {
            MoveDex.EnsureLoaded();
            PokemonDex.EnsureLoaded();
        }

        static MoveOracle.Context Field(string[]? defenderTypes = null)
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
                    Types = defenderTypes is { Length: > 0 } ? defenderTypes : new[] { "Normal" },
                    Level = 100,
                    MaxHp = 700,
                    CurrentHp = 700,
                },
            };
        }

        static double AttackerHolding(string item, string move, string[]? defenderTypes = null)
        {
            MoveOracle.Context field = Field(defenderTypes);
            field.Attacker.Item = item;
            return MoveOracle.ItemFactor(move, field);
        }

        static double DefenderHolding(string item, string move, string[]? defenderTypes = null)
        {
            MoveOracle.Context field = Field(defenderTypes);
            field.Defender.Item = item;
            return MoveOracle.ItemFactor(move, field);
        }

        // ---- the four the calculator used to do itself --------------------

        [Theory]
        [InlineData("Life Orb", "Close Combat", 1.25, 1.35)]
        [InlineData("Muscle Band", "Close Combat", 1.05, 1.15)]
        [InlineData("Wise Glasses", "Flamethrower", 1.05, 1.15)]
        [InlineData("Expert Belt", "Close Combat", 1.15, 1.25)]
        public void The_four_rules_the_calculator_carried_are_all_the_engines_now(
            string item, string move, double low, double high)
        {
            Assert.InRange(AttackerHolding(item, move), low, high);
        }

        /// <summary>Expert Belt only pays on a super-effective hit, and
        /// Fighting into a Normal is the Field's whole point.</summary>
        [Fact]
        public void Expert_Belt_pays_nothing_on_a_neutral_hit()
        {
            Assert.Equal(1.0, AttackerHolding("Expert Belt", "Flamethrower"), 3);
        }

        // ---- the ones it never had ----------------------------------------

        [Theory]
        [InlineData("Choice Band", "Close Combat")]
        [InlineData("Choice Specs", "Flamethrower")]
        public void A_Choice_item_is_half_again_on_the_stat_it_names(string item, string move)
        {
            Assert.InRange(AttackerHolding(item, move), 1.45, 1.55);
        }

        [Fact]
        public void A_Choice_item_does_nothing_to_the_other_half_of_the_split()
        {
            Assert.Equal(1.0, AttackerHolding("Choice Band", "Flamethrower"), 3);
            Assert.Equal(1.0, AttackerHolding("Choice Specs", "Close Combat"), 3);
        }

        [Fact]
        public void A_type_booster_pays_on_its_own_type_and_nothing_else()
        {
            Assert.InRange(AttackerHolding("Charcoal", "Flamethrower"), 1.15, 1.25);
            Assert.Equal(1.0, AttackerHolding("Charcoal", "Close Combat"), 3);
        }

        // ---- the defending half, which had nothing behind it --------------

        [Fact]
        public void Eviolite_on_the_DEFENDER_is_the_bug_this_section_fixes()
        {
            // Two thirds, because it is a half-again on the defending stat
            // rather than a third off the damage.
            Assert.InRange(DefenderHolding("Eviolite", "Close Combat"), 0.6, 0.72);
        }

        [Fact]
        public void An_Assault_Vest_only_answers_a_special_move()
        {
            Assert.InRange(DefenderHolding("Assault Vest", "Flamethrower"), 0.6, 0.72);
            Assert.Equal(1.0, DefenderHolding("Assault Vest", "Close Combat"), 3);
        }

        [Fact]
        public void A_resist_berry_halves_the_super_effective_hit_it_names()
        {
            // Fire into a Grass defender, with an Occa Berry in the way.
            Assert.InRange(
                DefenderHolding("Occa Berry", "Flamethrower", new[] { "Grass" }), 0.45, 0.55);
        }

        [Fact]
        public void Chilan_is_the_odd_one_out_and_halves_any_Normal_hit()
        {
            // Normal into Normal is not super-effective, and Chilan pays anyway.
            Assert.InRange(DefenderHolding("Chilan Berry", "Body Slam"), 0.45, 0.55);
        }

        /// <summary>
        /// §309 fixed this in the engine rather than in the window.
        /// Grounding.IsGrounded had known about an unpopped balloon since
        /// §159, but nothing consulted it when a Ground move landed - this
        /// engine's Ground immunity belonged entirely to LevitateEffect,
        /// which is keyed on the ABILITY - so a balloon lifted its holder
        /// over Spikes and the terrain and then took an Earthquake in full.
        /// </summary>
        [Fact]
        public void An_Air_Balloon_makes_the_target_immune_to_the_ground()
        {
            Assert.Equal(0.0, DefenderHolding("Air Balloon", "Earthquake"), 3);
        }

        [Fact]
        public void A_balloon_is_no_defence_against_anything_else()
        {
            Assert.Equal(1.0, DefenderHolding("Air Balloon", "Close Combat"), 3);
        }

        /// <summary>Thousand Arrows reaches what is hovering, whatever is
        /// holding it up - which is the reason the immunity is written
        /// against the same flag the Flying one is.</summary>
        [Fact]
        public void Thousand_Arrows_comes_through_the_balloon()
        {
            Assert.Equal(1.0, DefenderHolding("Air Balloon", "Thousand Arrows"), 3);
        }

        // ---- the composition, which is the design ------------------------

        /// <summary>
        /// The two factors are measured off the SAME probe so that their
        /// product is the engine's own answer rather than an assumption that
        /// an ability and an item never meet: the ability factor is taken
        /// over a world that still has the items, and the item factor over a
        /// world that has already lost the abilities, so the middle term
        /// cancels. Huge Power doubles the Attack and a Choice Band adds half
        /// again to it, and three is what the pair has to come to.
        /// </summary>
        [Fact]
        public void The_two_factors_multiply_to_what_both_together_do()
        {
            MoveOracle.Context field = Field();
            field.Attacker.Ability = "Huge Power";
            field.Attacker.Item = "Choice Band";

            double ability = MoveOracle.AbilityFactor("Close Combat", field);
            double item = MoveOracle.ItemFactor("Close Combat", field);

            Assert.InRange(ability, 1.9, 2.1);
            Assert.InRange(item, 1.45, 1.55);
            Assert.InRange(ability * item, 2.85, 3.15);
        }

        [Fact]
        public void Nothing_held_is_exactly_one_and_costs_no_probe()
        {
            Assert.Equal(1.0, MoveOracle.ItemFactor("Close Combat", Field()));
        }

        [Fact]
        public void An_item_the_dex_has_never_heard_of_changes_nothing()
        {
            Assert.Equal(1.0, AttackerHolding("Rubber Duck", "Close Combat"), 3);
        }

        // ---- the catalog, and what it admits it cannot do -----------------

        [Fact]
        public void Every_supported_item_has_a_display_name_and_no_two_share_one()
        {
            IReadOnlyList<string> names = HeldItems.DisplayNamesInOrder;

            Assert.Equal(HeldItems.SupportedIds.Count, names.Count);
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        }

        [Fact]
        public void A_display_name_normalizes_back_to_the_id_it_came_from()
        {
            foreach (string name in HeldItems.DisplayNamesInOrder)
                Assert.True(HeldItems.IsSupported(name), name + " does not round-trip");
        }

        /// <summary>The window offers all 105 and says which of them cannot
        /// matter. A stone is inert unless its holder has already mega
        /// evolved, and a calculator picks the mega forme as a species.</summary>
        [Fact]
        public void No_mega_stone_or_Z_Crystal_claims_to_change_a_calculation()
        {
            foreach (string id in HeldItems.SupportedIds)
            {
                if (HeldItems.IsMegaStone(id) || HeldItems.IsZCrystal(id))
                    Assert.False(HeldItems.AffectsDamageOrKo(id), id);
            }
        }

        [Theory]
        [InlineData("Heat Rock")]
        [InlineData("Damp Rock")]
        [InlineData("Icy Rock")]
        [InlineData("Smooth Rock")]
        [InlineData("Light Clay")]
        [InlineData("Lum Berry")]
        [InlineData("Wide Lens")]
        [InlineData("Scope Lens")]
        public void A_duration_or_an_accuracy_is_not_a_damage_number(string item)
        {
            Assert.True(HeldItems.IsSupported(item));
            Assert.False(HeldItems.AffectsDamageOrKo(item));
        }

        [Theory]
        [InlineData("Life Orb")]
        [InlineData("Choice Band")]
        [InlineData("Eviolite")]
        [InlineData("Occa Berry")]
        [InlineData("Leftovers")]
        [InlineData("Focus Sash")]
        [InlineData("Rocky Helmet")]
        [InlineData("Black Sludge")]
        [InlineData("Sitrus Berry")]
        [InlineData("Air Balloon")]
        public void And_everything_that_moves_a_roll_or_a_count_says_so(string item)
        {
            Assert.True(HeldItems.AffectsDamageOrKo(item), item);
        }

        [Fact]
        public void Something_nobody_is_holding_affects_nothing()
        {
            Assert.False(HeldItems.AffectsDamageOrKo(null));
            Assert.False(HeldItems.AffectsDamageOrKo("None"));
            Assert.False(HeldItems.AffectsDamageOrKo("Rubber Duck"));
        }
    }
}
