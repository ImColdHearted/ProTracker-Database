using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Engine;
using PokemonSim.Engine.Abilities;
using PokemonSim.Engine.Effects;
using PokemonSim.Engine.Items;
using PokemonSim.Models;
using PokemonSim.Observation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §311. What the network can finally SEE - and the one thing it was
    /// being told that was false.
    ///
    /// §156's ten features grew by appending to §184's two hundred and five,
    /// and in all that growth nothing ever said what a Pokemon WAS. No types,
    /// no ability, no held item, no stats. Worse, effectiveness came off the
    /// type chart alone, so the state told the model an Earthquake does
    /// double to a Levitate Bronzong and had no column anywhere that could
    /// have let it learn otherwise.
    ///
    /// The facts here are in two halves: the layout adds up and cannot drift,
    /// and the four things that were missing are really there.
    /// </summary>
    public class Section311Tests
    {
        static (BattleState State, PokemonState Self, PokemonState Foe) Duel(
            int seed = 311, PokemonState? self = null, PokemonState? foe = null)
        {
            self ??= TestKit.Mon("Mine", moves: TestKit.Move("Hit"));
            foe ??= TestKit.Mon("Theirs", moves: TestKit.Move("Hit"));

            (BattleState state, _) = TestKit.Duel(seed, self, foe);
            return (state, state.Player1.ActivePokemon, state.Player2.ActivePokemon);
        }

        static void Attach(PokemonState mon, string abilityId)
        {
            mon.AbilityId = abilityId;
            mon.PassiveEffects.Clear();

            if (AbilityFactory.TryCreate(abilityId, out IAbility ability))
                mon.PassiveEffects.AddRange(ability.GetEffects());
        }

        // ---- the layout, which has to add up -----------------------------

        /// <summary>The one-hot is written by the enum's own value, so a
        /// nineteenth type would silently spill into the block after it. This
        /// is what stops that.</summary>
        [Fact]
        public void The_type_one_hot_is_exactly_as_wide_as_the_enum()
        {
            Assert.Equal(Enum.GetValues<PokemonType>().Length, ObserverEncoder.TypeSlots);
            Assert.Equal(18, ObserverEncoder.TypeSlots);
        }

        [Fact]
        public void The_weather_and_terrain_one_hots_cover_their_enums()
        {
            Assert.Equal(Enum.GetValues<WeatherType>().Length, ObserverEncoder.WeatherSlots);
            Assert.Equal(Enum.GetValues<TerrainType>().Length, ObserverEncoder.TerrainSlots);
        }

        /// <summary>Two constants that have to agree, which is exactly the
        /// shape of bug this vector has had before.</summary>
        [Fact]
        public void The_blocks_add_up_to_the_width_the_schema_declares()
        {
            Assert.Equal(ObservationSchema.FeatureCount, ObserverEncoder.TotalFeatures);
            Assert.Equal(483, ObservationSchema.FeatureCount);
            Assert.Equal(7, ObservationSchema.Version);
        }

        /// <summary>Every block starts where the one before it ended - no
        /// gap, no overlap, and no hand-typed index anywhere.</summary>
        [Fact]
        public void The_blocks_tile()
        {
            Assert.Equal(0, ObserverEncoder.PositionBlockStart);

            int[] starts =
            {
                ObserverEncoder.PositionBlockStart,
                ObserverEncoder.TypeBlockStart,
                ObserverEncoder.StatBlockStart,
                ObserverEncoder.AbilityBlockStart,
                ObserverEncoder.ItemBlockStart,
                ObserverEncoder.MoveBlockStart,
                ObserverEncoder.TeamBlockStart,
                ObserverEncoder.HazardBlockStart,
                ObserverEncoder.FieldBlockStart,
                ObserverEncoder.ActiveBlockStart,
                ObserverEncoder.RiskBlockStart,
            };

            int[] sizes =
            {
                ObserverEncoder.PositionBlockSize,
                ObserverEncoder.TypeBlockSize,
                ObserverEncoder.StatBlockSize,
                ObserverEncoder.AbilityBlockSize,
                ObserverEncoder.ItemBlockSize,
                ObserverEncoder.MoveBlockSize,
                ObserverEncoder.TeamBlockSize,
                ObserverEncoder.HazardBlockSize,
                ObserverEncoder.FieldBlockSize,
                ObserverEncoder.ActiveBlockSize,
                ObserverEncoder.RiskBlockSize,
            };

            for (int i = 1; i < starts.Length; i++)
                Assert.Equal(starts[i - 1] + sizes[i - 1], starts[i]);

            Assert.Equal(ObservationSchema.FeatureCount,
                         starts[starts.Length - 1] + sizes[sizes.Length - 1]);
        }

        [Fact]
        public void Only_one_width_is_accepted_and_the_rest_are_named_history()
        {
            Assert.Equal(new[] { ObservationSchema.FeatureCount },
                         ObservationSchema.AcceptedFeatureCounts);

            Assert.Equal(new[] { 10, 76, 160, 202, 203, 205 },
                         ObservationSchema.RetiredFeatureCounts);

            Assert.DoesNotContain(ObservationSchema.FeatureCount,
                                  ObservationSchema.RetiredFeatureCounts);
        }

        // ---- the lie §311 was written to stop ----------------------------

        /// <summary>
        /// The whole section in one fact. Before §311 this read 2.0, because
        /// Effectiveness was the type chart and nothing else - so the model
        /// was trained on states that said an Earthquake is the right answer
        /// to a Levitate Bronzong.
        /// </summary>
        [Fact]
        public void A_Levitate_defender_makes_a_Ground_move_worth_nothing()
        {
            var quake = TestKit.Move("Quake", PokemonType.Ground, power: 100);
            var mine = TestKit.Mon("Mine", moves: quake);
            var foe = TestKit.Mon("Bronzong", PokemonType.Steel);

            (BattleState state, _, PokemonState target) = Duel(311, mine, foe);

            Assert.Equal(2f, ObserverEncoder.Effectiveness(state, PokemonType.Ground, target));

            Attach(target, "levitate");

            Assert.Equal(0f, ObserverEncoder.Effectiveness(state, PokemonType.Ground, target));
            Assert.True(ObserverEncoder.Nullifies(state, target, PokemonType.Ground));
        }

        [Fact]
        public void An_Air_Balloon_does_the_same_without_any_ability_at_all()
        {
            var foe = TestKit.Mon("Floaty");

            (BattleState state, _, PokemonState target) = Duel(312, null, foe);

            Assert.False(ObserverEncoder.Nullifies(state, target, PokemonType.Ground));

            target.HeldItemId = "Air Balloon";

            Assert.True(ObserverEncoder.Nullifies(state, target, PokemonType.Ground));
            Assert.Equal(0f, ObserverEncoder.Effectiveness(state, PokemonType.Ground, target));
        }

        [Theory]
        [InlineData("flashfire", "Fire")]
        [InlineData("voltabsorb", "Electric")]
        [InlineData("waterabsorb", "Water")]
        [InlineData("sapsipper", "Grass")]
        public void Every_absorb_answers_for_its_own_type(string abilityId, string typeName)
        {
            var foe = TestKit.Mon("Eater");

            (BattleState state, _, PokemonState target) = Duel(313, null, foe);

            Attach(target, abilityId);

            var eaten = Enum.Parse<PokemonType>(typeName);

            Assert.True(ObserverEncoder.Nullifies(state, target, eaten));

            // And nothing else: an absorb that ate everything would be a
            // worse lie than the one this replaced.
            foreach (PokemonType other in Enum.GetValues<PokemonType>())
            {
                if (other != eaten && other != PokemonType.Ground)
                    Assert.False(ObserverEncoder.Nullifies(state, target, other), other.ToString());
            }
        }

        /// <summary>A cancelled move and a resisted one both read zero at the
        /// effectiveness column. This is the column that says which, and it
        /// is the one a network can learn an ability from.</summary>
        [Fact]
        public void The_move_block_says_when_it_was_cancelled_rather_than_resisted()
        {
            var quake = TestKit.Move("Quake", PokemonType.Ground, power: 100);
            var mine = TestKit.Mon("Mine", moves: quake);
            var foe = TestKit.Mon("Bronzong", PokemonType.Steel);

            (BattleState state, _, PokemonState target) = Duel(314, mine, foe);

            Attach(target, "levitate");

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            int at = ObserverEncoder.MoveBlockStart;

            Assert.Equal(0f, f[at + 1]);    // effectiveness
            Assert.Equal(1f, f[at + 20]);   // and it was cancelled outright
        }

        // ---- the four things that were missing ---------------------------

        [Fact]
        public void A_dual_type_lights_exactly_two_of_its_eighteen()
        {
            var mine = TestKit.Mon("Mine", PokemonType.Water);
            mine.Types.Add(PokemonType.Flying);

            (BattleState state, _, _) = Duel(315, mine);

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            int at = ObserverEncoder.TypeBlockStart;

            Assert.Equal(1f, f[at + (int)PokemonType.Water]);
            Assert.Equal(1f, f[at + (int)PokemonType.Flying]);
            Assert.Equal(2, Enumerable.Range(0, ObserverEncoder.TypeSlots).Count(i => f[at + i] > 0f));
        }

        [Fact]
        public void A_move_carries_its_own_type_into_its_own_slot()
        {
            var bolt = TestKit.Move("Bolt", PokemonType.Electric, power: 90);
            var mine = TestKit.Mon("Mine", moves: bolt);

            (BattleState state, _, _) = Duel(316, mine);

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            int at = ObserverEncoder.MoveBlockStart + 21;

            Assert.Equal(1f, f[at + (int)PokemonType.Electric]);
            Assert.Equal(1, Enumerable.Range(0, ObserverEncoder.TypeSlots).Count(i => f[at + i] > 0f));
        }

        /// <summary>§307 found 184 of the dex's 308 ability names have no rule
        /// in this engine. A network told a Pokemon "has Anticipation" when
        /// Anticipation does nothing here would be learning noise, so the
        /// state says which.</summary>
        [Fact]
        public void An_ability_the_engine_cannot_simulate_says_so()
        {
            (BattleState state, PokemonState self, _) = Duel(317);

            Attach(self, "Anticipation");

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            int at = ObserverEncoder.AbilityBlockStart;

            Assert.Equal(1f, f[at]);        // it has one
            Assert.Equal(0f, f[at + 1]);    // and the engine has no rule for it
            Assert.False(AbilityFactory.IsSupported("Anticipation"));
        }

        [Fact]
        public void A_simulated_ability_reports_the_phases_it_hooks()
        {
            (BattleState state, PokemonState self, _) = Duel(318);

            Attach(self, "levitate");

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            int at = ObserverEncoder.AbilityBlockStart;

            Assert.Equal(1f, f[at]);        // named
            Assert.Equal(1f, f[at + 1]);    // simulated
            Assert.Equal(1f, f[at + 2]);    // hooks BeforeMove
            Assert.Equal(1f, f[at + 7]);    // on defence only
            Assert.Equal(1f, f[at + 8]);    // and cancels a whole type
        }

        [Theory]
        [InlineData("Choice Band", 5)]
        [InlineData("Life Orb", 3)]
        [InlineData("Occa Berry", 6)]
        [InlineData("Leftovers", 7)]
        [InlineData("Focus Sash", 8)]
        public void An_item_reports_what_it_does(string item, int offset)
        {
            (BattleState state, PokemonState self, _) = Duel(319);

            self.HeldItemId = item;

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            int at = ObserverEncoder.ItemBlockStart;

            Assert.Equal(1f, f[at]);            // holding something
            Assert.Equal(1f, f[at + 1]);        // which the engine simulates
            Assert.Equal(1f, f[at + offset]);   // and this is what it does
        }

        [Fact]
        public void Holding_nothing_leaves_the_whole_item_block_zero()
        {
            (BattleState state, _, _) = Duel(320);

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            for (int i = 0; i < ObserverEncoder.ItemBlockStride; i++)
                Assert.Equal(0f, f[ObserverEncoder.ItemBlockStart + i]);
        }

        /// <summary>§156 wrote the enum's own value into one column, so a
        /// network was handed a number line on which Sandstorm sits between
        /// Rain and Hail. Exactly one of five is the property an ordinal
        /// could never have.</summary>
        [Fact]
        public void The_weather_is_a_one_hot_and_never_an_ordinal()
        {
            (BattleState state, _, _) = Duel(321);

            foreach (WeatherType weather in Enum.GetValues<WeatherType>())
            {
                state.Environment.Weather = weather;

                float[] f = ObserverEncoder.Encode(state, state.Player1);

                int at = ObserverEncoder.FieldBlockStart + 14;

                Assert.Equal(1f, f[at + (int)weather]);
                Assert.Equal(1, Enumerable.Range(0, ObserverEncoder.WeatherSlots)
                                 .Count(i => f[at + i] > 0f));
            }
        }

        /// <summary>The other half of why it never switched: a switch head
        /// that could not see what its bench WAS.</summary>
        [Fact]
        public void Every_bench_position_carries_its_own_types()
        {
            var mine = TestKit.Mon("Lead", moves: TestKit.Move("Hit"));
            var bench = TestKit.Mon("Bench", PokemonType.Ghost, moves: TestKit.Move("Hit"));

            (BattleState state, _) = TestKit.Battle(
                322,
                new List<PokemonState> { mine, bench },
                new List<PokemonState> { TestKit.Mon("Foe", moves: TestKit.Move("Hit")) });

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            int at = ObserverEncoder.TeamBlockStart + ObserverEncoder.TeamBlockStride + 7;

            Assert.Equal(1f, f[at + (int)PokemonType.Ghost]);
        }

        [Fact]
        public void Both_actives_report_their_stats_and_their_footing()
        {
            (BattleState state, _, _) = Duel(323);

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            for (int i = 0; i < ObserverEncoder.StatBlockSize; i++)
                Assert.InRange(f[ObserverEncoder.StatBlockStart + i], 0f, 1f);

            Assert.Equal(1f, f[ObserverEncoder.PositionBlockStart + 36]);
            Assert.Equal(1f, f[ObserverEncoder.PositionBlockStart + 37]);
        }

        [Fact]
        public void Everything_written_is_a_real_number()
        {
            (BattleState state, _, _) = Duel(324);

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            Assert.Equal(ObservationSchema.FeatureCount, f.Length);
            Assert.All(f, value => Assert.True(float.IsFinite(value)));
        }
    }
}
