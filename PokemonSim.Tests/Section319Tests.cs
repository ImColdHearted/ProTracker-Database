using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Models;
using PokemonSim.Observation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §319. The observation stops cheating.
    ///
    /// §311's vector handed the network the opponent's exact computed stats,
    /// its ability and its held item, and computed effectiveness, damage and
    /// a nullification flag out of all three. Fifty-four of its 483 columns
    /// were information no player could have.
    ///
    /// The half of this file that matters most is the DIFFERENTIAL test:
    /// encode a position, change something the opponent has not revealed,
    /// encode again, and require the two vectors to be identical column for
    /// column. That is a property no amount of reading the encoder can
    /// establish and no future edit can quietly break - if a hidden fact ever
    /// reaches the vector, the vector moves, and this fails.
    /// </summary>
    public class Section319Tests
    {
        static PokemonState Mon(string species, PokemonType type = PokemonType.Normal,
                                params MoveState[] moves) =>
            TestKit.Mon(species, type, moves: moves);

        static (BattleState State, BattleEngine Engine) Fight(
            int seed = 319, List<PokemonState>? mine = null, List<PokemonState>? theirs = null)
        {
            mine ??= new List<PokemonState> { Mon("Mine", PokemonType.Normal, TestKit.Move("My Hit")) };
            theirs ??= new List<PokemonState> { Mon("Theirs", PokemonType.Normal, TestKit.Move("Their Hit")) };

            return TestKit.Battle(seed, mine, theirs);
        }

        static float[] Fair(BattleState state) =>
            FairEncoder.Encode(state, state.Player1, LegalActions.For(state, state.Player1));

        static int At(string block, int offset = 0) => ObservationV8.Map.Start(block) + offset;

        // =============================================================
        // 1. the basics
        // =============================================================

        [Fact]
        public void The_vector_is_the_declared_width_and_every_column_is_finite()
        {
            (BattleState state, _) = Fight();

            float[] f = Fair(state);

            Assert.Equal(ObservationV8.Map.Count, f.Length);
            Assert.All(f, v => Assert.True(float.IsFinite(v), "a feature was not a real number"));
        }

        [Fact]
        public void The_layout_declares_every_column_it_contains()
        {
            Assert.Equal(ObservationV8.Map.Count, ObservationV8.Map.Features.Count);

            for (int i = 0; i < ObservationV8.Map.Features.Count; i++)
                Assert.Equal(i, ObservationV8.Map.Features[i].Index);
        }

        [Fact]
        public void No_column_of_the_fair_layout_is_marked_hidden()
        {
            Assert.Empty(ObservationV8.Map.HiddenFeatures);
        }

        [Fact]
        public void Our_own_hit_points_are_ours_to_know()
        {
            (BattleState state, _) = Fight();

            state.Player1.ActivePokemon.CurrentHP = state.Player1.ActivePokemon.MaxHP / 2;

            float[] f = Fair(state);

            Assert.InRange(f[At(ObservationV8.Self)], 0.49f, 0.51f);
        }

        // =============================================================
        // 2. THE DIFFERENTIAL TEST - hidden information cannot move it
        // =============================================================

        /// <summary>
        /// The whole section in one fact. Every one of these mutations changes
        /// something the engine knows and the opponent has never shown; none
        /// of them may move a single column.
        /// </summary>
        [Fact]
        public void Hidden_opponent_information_cannot_move_the_fair_vector()
        {
            var theirs = new List<PokemonState>
            {
                Mon("Theirs", PokemonType.Normal, TestKit.Move("Their Hit")),
                Mon("Bench", PokemonType.Water, TestKit.Move("Bench Hit")),
            };

            (BattleState state, BattleEngine engine) = Fight(theirs: theirs);

            TestKit.Clash(state, engine);

            float[] before = Fair(state);

            PokemonState foe = state.Player2.ActivePokemon;
            PokemonState bench = state.Player2.Team[1];

            // Its held item, which it has never used.
            foe.HeldItemId = "lifeorb";

            // Its ability, which has never fired.
            foe.AbilityId = "levitate";

            // Its EVs, its nature and its computed stats.
            foe.SpeedEV = 252;
            foe.AttackEV = 252;
            foe.Nature = Nature.Adamant;
            foe.Stats.Attack = 999;
            foe.Stats.Defense = 999;
            foe.Stats.Speed = 999;

            // Its IVs.
            foe.SpeedIV = 0;

            // A move it has not used.
            foe.Moves.Add(TestKit.Move("Never Used", PokemonType.Dragon, power: 200));

            // The whole benched Pokemon nobody has seen.
            bench.Species = "Something Else";
            bench.Types = new List<PokemonType> { PokemonType.Dragon, PokemonType.Fire };
            bench.HeldItemId = "choicescarf";
            bench.AbilityId = "intimidate";
            bench.CurrentHP = 1;

            float[] after = Fair(state);

            AssertSame(before, after);
        }

        [Fact]
        public void Their_bench_hit_points_are_not_ours_to_know()
        {
            var theirs = new List<PokemonState>
            {
                Mon("Theirs", PokemonType.Normal, TestKit.Move("Their Hit")),
                Mon("Bench", PokemonType.Water, TestKit.Move("Bench Hit")),
            };

            (BattleState state, BattleEngine engine) = Fight(theirs: theirs);

            TestKit.Clash(state, engine);

            float[] before = Fair(state);

            state.Player2.Team[1].CurrentHP = 1;

            AssertSame(before, Fair(state));
        }

        [Fact]
        public void An_item_they_have_not_spent_is_not_ours_to_know()
        {
            (BattleState state, BattleEngine engine) = Fight();

            TestKit.Clash(state, engine);

            float[] before = Fair(state);

            state.Player2.ActivePokemon.HeldItemId = "focussash";

            AssertSame(before, Fair(state));
        }

        [Fact]
        public void An_ability_that_has_not_fired_is_not_ours_to_know()
        {
            (BattleState state, BattleEngine engine) = Fight();

            TestKit.Clash(state, engine);

            float[] before = Fair(state);

            state.Player2.ActivePokemon.AbilityId = "waterabsorb";

            AssertSame(before, Fair(state));
        }

        /// <summary>The opposite of the one above, and just as important: our
        /// OWN hidden things are ours, and changing them must move the
        /// vector. A test that only proved the opponent's side would also
        /// pass on an encoder that returned all zeroes.</summary>
        [Fact]
        public void Our_own_item_is_ours_to_know()
        {
            (BattleState state, BattleEngine engine) = Fight();

            TestKit.Clash(state, engine);

            float[] before = Fair(state);

            state.Player1.ActivePokemon.HeldItemId = "lifeorb";

            float[] after = Fair(state);

            Assert.False(before.SequenceEqual(after),
                "our own held item must reach the vector - it is ours");
        }

        // =============================================================
        // 3. what IS learned, and when
        // =============================================================

        [Fact]
        public void A_move_they_have_used_becomes_known()
        {
            (BattleState state, BattleEngine engine) = Fight();

            TestKit.Clash(state, engine);

            int slot0 = At(ObservationV8.OpponentMoves);
            int stride = ObservationV8.Map.Size(ObservationV8.OpponentMoves) / ObservationV8.MoveSlots;

            float[] f = Fair(state);

            Assert.Equal(1f, f[slot0]);                 // known
            Assert.Equal(0f, f[slot0 + 1]);             // not unknown
        }

        [Fact]
        public void A_move_they_have_not_used_stays_explicitly_unknown()
        {
            (BattleState state, BattleEngine engine) = Fight();

            TestKit.Clash(state, engine);

            int start = At(ObservationV8.OpponentMoves);
            int stride = ObservationV8.Map.Size(ObservationV8.OpponentMoves) / ObservationV8.MoveSlots;

            float[] f = Fair(state);

            for (int slot = 1; slot < ObservationV8.MoveSlots; slot++)
            {
                Assert.Equal(0f, f[start + slot * stride]);          // not known
                Assert.Equal(1f, f[start + slot * stride + 1]);      // UNKNOWN
            }
        }

        /// <summary>The distinction the user asked for by name: an unrevealed
        /// slot and a revealed one must not encode identically.</summary>
        [Fact]
        public void Unknown_and_known_are_not_the_same_row()
        {
            (BattleState state, BattleEngine engine) = Fight();

            TestKit.Clash(state, engine);

            int start = At(ObservationV8.OpponentMoves);
            int stride = ObservationV8.Map.Size(ObservationV8.OpponentMoves) / ObservationV8.MoveSlots;

            float[] f = Fair(state);

            float[] known = f.Skip(start).Take(stride).ToArray();
            float[] unknown = f.Skip(start + stride).Take(stride).ToArray();

            Assert.False(known.SequenceEqual(unknown));
        }

        [Fact]
        public void A_bench_member_they_have_never_shown_is_marked_unseen()
        {
            var theirs = new List<PokemonState>
            {
                Mon("Theirs", PokemonType.Normal, TestKit.Move("Their Hit")),
                Mon("Bench", PokemonType.Water, TestKit.Move("Bench Hit")),
            };

            (BattleState state, BattleEngine engine) = Fight(theirs: theirs);

            TestKit.Clash(state, engine);

            int start = At(ObservationV8.OpponentTeam);
            int stride = ObservationV8.Map.Size(ObservationV8.OpponentTeam) / ObservationV8.TeamSlots;

            float[] f = Fair(state);

            Assert.Equal(1f, f[start + 1]);                  // slot 0 seen
            Assert.Equal(0f, f[start + 2]);                  // and not unseen

            Assert.Equal(0f, f[start + stride + 1]);         // slot 1 not seen
            Assert.Equal(1f, f[start + stride + 2]);         // UNSEEN
        }

        [Fact]
        public void A_switch_reveals_the_Pokemon_that_came_in()
        {
            var theirs = new List<PokemonState>
            {
                Mon("Theirs", PokemonType.Normal, TestKit.Move("Their Hit")),
                Mon("Bench", PokemonType.Water, TestKit.Move("Bench Hit")),
            };

            (BattleState state, BattleEngine engine) = Fight(theirs: theirs);

            engine.RunTurn(
                TestKit.MoveAction(state, state.Player1.ActivePokemon, state.Player1.ActivePokemon.Moves[0]),
                TestKit.SwitchAction(state, state.Player2.ActivePokemon, state.Player2.Team[1]));

            int start = At(ObservationV8.OpponentTeam);
            int stride = ObservationV8.Map.Size(ObservationV8.OpponentTeam) / ObservationV8.TeamSlots;

            float[] f = Fair(state);

            Assert.Equal(1f, f[start + stride + 1]);         // now seen
            Assert.Equal(0f, f[start + stride + 2]);         // no longer unseen
        }

        [Fact]
        public void A_repeated_move_shows_as_a_streak()
        {
            (BattleState state, BattleEngine engine) = Fight();

            TestKit.Clash(state, engine);
            TestKit.Clash(state, engine);
            TestKit.Clash(state, engine);

            SideKnowledge them = state.Knowledge.Against(state, state.Player1);
            ObservedPokemon active = them.Slots.First(s => s.Active);

            Assert.True(active.LastMoveStreak >= 2,
                $"three identical moves should read as a streak, got {active.LastMoveStreak}");
        }

        // =============================================================
        // 4. the visible position
        // =============================================================

        [Fact]
        public void Their_status_is_visible_because_it_is_announced()
        {
            (BattleState state, BattleEngine engine) = Fight();

            TestKit.Clash(state, engine);

            state.Player2.ActivePokemon.Status = StatusCondition.Burn;
            state.Knowledge.BeginTurn(state);

            float[] f = Fair(state);

            Assert.Equal(1f, f[At(ObservationV8.Opponent, 9)]);
        }

        [Fact]
        public void Their_stat_boosts_are_visible_because_they_are_announced()
        {
            (BattleState state, BattleEngine engine) = Fight();

            TestKit.Clash(state, engine);

            state.Player2.ActivePokemon.AttackStage = 2;
            state.Knowledge.BeginTurn(state);

            float[] f = Fair(state);

            Assert.InRange(f[At(ObservationV8.Opponent, 2)], 0.32f, 0.34f);
        }

        [Fact]
        public void Weather_is_a_one_hot_and_never_an_ordinal()
        {
            (BattleState state, _) = Fight();

            state.Environment.Weather = WeatherType.Sandstorm;

            float[] f = Fair(state);

            int at = At(ObservationV8.Field, 14);

            Assert.Equal(1f, f[at + (int)WeatherType.Sandstorm]);
            Assert.Equal(1f, f.Skip(at).Take(5).Sum());
        }

        [Fact]
        public void Terrain_is_a_one_hot_too()
        {
            (BattleState state, _) = Fight();

            state.Environment.Terrain = TerrainType.Electric;

            float[] f = Fair(state);

            int at = At(ObservationV8.Field, 19);

            Assert.Equal(1f, f[at + (int)TerrainType.Electric]);
            Assert.Equal(1f, f.Skip(at).Take(5).Sum());
        }

        [Fact]
        public void Hazards_are_carried_for_both_sides()
        {
            (BattleState state, _) = Fight();

            state.StealthRockP1 = true;
            state.SpikesP2 = 3;

            float[] f = Fair(state);

            Assert.Equal(1f, f[At(ObservationV8.Hazards)]);
            Assert.Equal(1f, f[At(ObservationV8.Hazards, 5)]);
        }

        [Fact]
        public void A_fainted_team_member_reads_as_fainted()
        {
            var mine = new List<PokemonState>
            {
                Mon("Mine", PokemonType.Normal, TestKit.Move("My Hit")),
                Mon("Spare", PokemonType.Normal, TestKit.Move("Spare Hit")),
            };

            (BattleState state, _) = Fight(mine: mine);

            state.Player1.Team[1].CurrentHP = 0;

            int stride = ObservationV8.Map.Size(ObservationV8.SelfTeam) / ObservationV8.TeamSlots;

            float[] f = Fair(state);

            Assert.Equal(0f, f[At(ObservationV8.SelfTeam) + stride + 1]);
        }

        // =============================================================
        // 5. history
        // =============================================================

        [Fact]
        public void History_fills_as_the_battle_goes_on()
        {
            (BattleState state, BattleEngine engine) = Fight();

            Assert.Empty(state.Knowledge.History);

            TestKit.Clash(state, engine);
            Assert.Single(state.Knowledge.History);

            TestKit.Clash(state, engine);
            Assert.Equal(2, state.Knowledge.History.Count);

            int at = At(ObservationV8.History);
            int stride = ObservationV8.Map.Size(ObservationV8.History) / ObservationV8.HistoryTurns;

            float[] f = Fair(state);

            Assert.Equal(1f, f[at]);                 // newest row present
            Assert.Equal(1f, f[at + stride]);        // and the one before it
            Assert.Equal(0f, f[at + 2 * stride]);    // and nothing older
        }

        [Fact]
        public void History_never_grows_past_its_window()
        {
            (BattleState state, BattleEngine engine) = Fight();

            for (int i = 0; i < BattleKnowledge.HistoryTurns + 6; i++)
            {
                if (state.Outcome != BattleOutcome.Unfinished)
                    break;

                TestKit.Clash(state, engine);
            }

            Assert.True(state.Knowledge.History.Count <= BattleKnowledge.HistoryTurns,
                $"the window held {state.Knowledge.History.Count} rows");
        }

        /// <summary>Newest first, so the row a network should weight most sits
        /// at the same offset whether the battle is four turns old or forty.</summary>
        [Fact]
        public void The_newest_turn_is_always_the_first_row()
        {
            (BattleState state, BattleEngine engine) = Fight();

            TestKit.Clash(state, engine);
            TestKit.Clash(state, engine);

            int at = At(ObservationV8.History);

            float[] f = Fair(state);

            Assert.Equal(1f, f[at + 1]);             // age of the newest row is 1
        }

        // =============================================================
        // 6. the boundary itself
        // =============================================================

        [Fact]
        public void A_rollout_cannot_write_into_the_real_battles_history()
        {
            (BattleState state, BattleEngine engine) = Fight();

            TestKit.Clash(state, engine);

            BattleState clone = state.Clone();
            var cloneEngine = new BattleEngine(clone);

            TestKit.Clash(clone, cloneEngine);
            TestKit.Clash(clone, cloneEngine);

            Assert.Single(state.Knowledge.History);
            Assert.True(clone.Knowledge.History.Count > state.Knowledge.History.Count);
        }

        [Fact]
        public void The_clone_starts_from_what_the_original_knew()
        {
            (BattleState state, BattleEngine engine) = Fight();

            TestKit.Clash(state, engine);

            BattleState clone = state.Clone();

            Assert.Equal(state.Knowledge.History.Count, clone.Knowledge.History.Count);
            Assert.Equal(
                state.Knowledge.OfPlayer2.Slots.Count(s => s.Seen),
                clone.Knowledge.OfPlayer2.Slots.Count(s => s.Seen));
        }

        // =============================================================
        // 7. teacher isolation, and versioning
        // =============================================================

        /// <summary>The §182 risk dial is the ONLY column that is not about
        /// the battle, and it sits alone at the end where it disturbs nothing.
        /// A teacher score reaching the state would be the same class of
        /// mistake as a hidden item reaching it.</summary>
        [Fact]
        public void The_only_teacher_column_is_the_risk_dial()
        {
            List<FeatureSpec> meta = ObservationV8.Map.Features
                .Where(f => f.Visibility == Visibility.Meta)
                .ToList();

            Assert.Single(meta);
            Assert.Equal("risk", meta[0].Name);
            Assert.Equal(ObservationV8.Map.Count - 1, meta[0].Index);
        }

        [Fact]
        public void The_risk_dial_is_written_where_it_is_declared()
        {
            (BattleState state, _) = Fight();

            float[] f = FairEncoder.Encode(state, state.Player1, null, risk: 1f);

            Assert.Equal(1f, f[ObservationV8.Map.Start(ObservationV8.Risk)]);
        }

        [Fact]
        public void Every_column_declares_what_it_means_and_how_it_is_scaled()
        {
            Assert.All(ObservationV8.Map.Features, f =>
            {
                Assert.False(string.IsNullOrWhiteSpace(f.Name));
                Assert.False(string.IsNullOrWhiteSpace(f.Meaning));
                Assert.False(string.IsNullOrWhiteSpace(f.Normalization));
                Assert.False(string.IsNullOrWhiteSpace(f.Block));
            });
        }

        [Fact]
        public void A_block_name_that_does_not_exist_throws_rather_than_returning_zero()
        {
            Assert.Throws<KeyNotFoundException>(() => ObservationV8.Map.Start("no.such.block"));
        }

        // =============================================================

        static void AssertSame(float[] before, float[] after)
        {
            Assert.Equal(before.Length, after.Length);

            var moved = new List<string>();

            for (int i = 0; i < before.Length; i++)
            {
                if (Math.Abs(before[i] - after[i]) > 1e-6f)
                {
                    FeatureSpec spec = ObservationV8.Map.Features[i];

                    moved.Add($"{i} {spec.Block}.{spec.Name} {before[i]} -> {after[i]}");
                }
            }

            Assert.True(moved.Count == 0,
                "hidden information reached the observation vector:\n  " + string.Join("\n  ", moved));
        }
    }
}
