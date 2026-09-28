using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §305. The field has slots now, and an action says which one it is
    /// aimed at. Nothing about a singles battle changed, and that is the
    /// whole claim this suite exists to check.
    ///
    /// The strongest test here is the first one: two identical battles from
    /// the same seed, one driven through the two-action RunTurn that every
    /// existing caller uses and one through the new list form, have to
    /// produce the same log line for line. The engine's rolls all come from
    /// the seeded rng, so any difference in what the two paths do - a
    /// different target, a different order, an extra roll - shows up as a
    /// different log.
    /// </summary>
    public class Section305Tests
    {
        static Section305Tests()
        {
            MoveDex.EnsureLoaded();
            PokemonDex.EnsureLoaded();
        }

        /// <summary>Two teams built the same way every time, so two battles
        /// from the same seed start from the same place.</summary>
        static (BattleState State, BattleEngine Engine) Arena(int seed)
        {
            var team1 = new List<PokemonState>
            {
                Mon("Machamp", type: PokemonType.Fighting, hp: 260, attack: 130, speed: 70,
                    moves: new[] { Move("Close Combat", power: 90), Move("Bullet Punch", power: 40, priority: 1) }),
                Mon("Gengar", type: PokemonType.Ghost, hp: 200, attack: 110, speed: 130,
                    moves: new[] { Move("Shadow Ball", category: MoveCategory.Special, power: 80) }),
            };

            var team2 = new List<PokemonState>
            {
                Mon("Blissey", hp: 500, attack: 40, defense: 60, speed: 55,
                    moves: new[] { Move("Seismic Toss", power: 50), Move("Soft-Boiled", category: MoveCategory.Status, power: 0) }),
                Mon("Skarmory", type: PokemonType.Flying, hp: 240, attack: 80, defense: 140, speed: 70,
                    moves: new[] { Move("Brave Bird", power: 120) }),
            };

            return Battle(seed, team1, team2);
        }

        /// <summary>Plays the same five turns through whichever entry point
        /// the caller hands it, and returns the battle's own text.</summary>
        static List<string> Play(int seed, Action<BattleEngine, BattleState, BattleAction, BattleAction> runTurn)
        {
            var (state, engine) = Arena(seed);

            for (int turn = 0; turn < 5 && state.Outcome == BattleOutcome.Unfinished; turn++)
            {
                var a1 = engine.GetLegalActions(state.Player1).First(a => a.Type == BattleActionType.Move);
                var a2 = engine.GetLegalActions(state.Player2).First(a => a.Type == BattleActionType.Move);

                runTurn(engine, state, a1, a2);

                foreach (var player in new[] { state.Player1, state.Player2 })
                {
                    while (engine.NeedsReplacement(player))
                    {
                        var next = player.Team.First(p => !p.Fainted);
                        engine.Replace(player, next);
                    }
                }
            }

            return state.Log.Lines.ToList();
        }

        // ---- the claim ---------------------------------------------------

        [Fact]
        public void The_two_turn_entry_points_play_the_same_battle()
        {
            for (int seed = 1; seed <= 12; seed++)
            {
                List<string> pair = Play(seed, (engine, state, a1, a2) => engine.RunTurn(a1, a2));
                List<string> list = Play(seed, (engine, state, a1, a2) => engine.RunTurn(new[] { a1, a2 }));

                Assert.Equal(pair, list);
                Assert.NotEmpty(pair);
            }
        }

        [Fact]
        public void A_battle_still_finishes_when_two_strategies_play_it_out()
        {
            var (state, engine) = Arena(99);

            BattleOutcome outcome = engine.RunBattle(
                new RandomMoveStrategy(), new RandomMoveStrategy());

            Assert.NotEqual(BattleOutcome.Unfinished, outcome);
        }

        // ---- the shape ---------------------------------------------------

        [Fact]
        public void A_battle_is_singles_unless_somebody_says_otherwise()
        {
            var (state, _) = Arena(1);

            Assert.Equal(BattleFormat.Singles, state.Format);
            Assert.Equal(1, state.SlotsPerSide);
            Assert.Single(state.Player1.Active);
            Assert.Single(state.Player2.Active);
        }

        [Fact]
        public void The_old_name_is_slot_zero_in_both_directions()
        {
            var (state, _) = Arena(1);

            PlayerState side = state.Player1;

            Assert.Same(side.Active[0], side.ActivePokemon);

            PokemonState other = side.Team[1];
            side.ActivePokemon = other;

            Assert.Same(other, side.Active[0]);

            side.Active[0] = side.Team[0];

            Assert.Same(side.Team[0], side.ActivePokemon);
        }

        [Fact]
        public void A_side_knows_which_slot_its_Pokemon_are_standing_in()
        {
            var (state, _) = Arena(1);

            PlayerState side = state.Player1;

            Assert.Equal(0, side.SlotOf(side.ActivePokemon));
            Assert.Equal(-1, side.SlotOf(side.Team[1]));
            Assert.Equal(-1, side.SlotOf(state.Player2.ActivePokemon));
        }

        [Fact]
        public void Standing_skips_a_slot_whose_occupant_has_fainted()
        {
            var (state, _) = Arena(1);

            Assert.Single(state.Player1.Standing());

            state.Player1.ActivePokemon.CurrentHP = 0;

            Assert.Empty(state.Player1.Standing());
        }

        [Fact]
        public void The_opposing_side_is_read_slot_by_slot()
        {
            var (state, _) = Arena(1);

            var opponents = state.GetOpponents(state.Player1.ActivePokemon).ToList();

            Assert.Single(opponents);
            Assert.Same(state.Player2.ActivePokemon, opponents[0]);

            state.Player2.ActivePokemon.CurrentHP = 0;

            Assert.Empty(state.GetOpponents(state.Player1.ActivePokemon));
        }

        [Fact]
        public void An_empty_slot_is_the_one_waiting_for_a_replacement()
        {
            var (state, engine) = Arena(1);

            Assert.Equal(-1, engine.EmptySlot(state.Player1));
            Assert.False(engine.NeedsReplacement(state.Player1));

            state.Player1.ActivePokemon.CurrentHP = 0;

            Assert.Equal(0, engine.EmptySlot(state.Player1));
            Assert.True(engine.NeedsReplacement(state.Player1));
        }

        // ---- the targeting -----------------------------------------------

        [Fact]
        public void Every_move_the_menu_offers_says_what_it_is_aimed_at()
        {
            var (state, engine) = Arena(1);

            var moves = engine.GetLegalActions(state.Player1)
                .Where(a => a.Type == BattleActionType.Move)
                .ToList();

            Assert.NotEmpty(moves);

            foreach (var action in moves)
            {
                Assert.Same(state.Player2.ActivePokemon, action.Target);
                Assert.Equal(0, action.TargetSlot);
            }
        }

        [Fact]
        public void A_switch_still_puts_the_newcomer_in_front_of_the_attack()
        {
            // The regression this section could most easily have introduced:
            // storing the target on the action, then hitting the Pokemon
            // that was standing there rather than the one standing there
            // now. A move is aimed at a position.
            var (state, engine) = Arena(4);

            PokemonState wasThere = state.Player2.ActivePokemon;
            PokemonState comesIn = state.Player2.Team[1];

            var attack = engine.GetLegalActions(state.Player1)
                .First(a => a.Type == BattleActionType.Move);

            Assert.Same(wasThere, attack.Target);

            var switchAway = engine.GetLegalActions(state.Player2)
                .First(a => a.Type == BattleActionType.Switch);

            int before = comesIn.CurrentHP;

            engine.RunTurn(attack, switchAway);

            // The switch resolves first, so the newcomer is what took the
            // hit - not the Pokemon the action names.
            Assert.Same(comesIn, state.Player2.ActivePokemon);
            Assert.True(comesIn.CurrentHP < before);
            Assert.Equal(wasThere.MaxHP, wasThere.CurrentHP);
        }

        [Fact]
        public void An_action_built_by_hand_still_aims_at_the_active()
        {
            // Every caller outside LegalActions - the tests, the dev
            // console, the tracker's Simulator window - builds actions
            // without a target, and must keep working.
            var (state, engine) = Arena(6);

            var byHand = new BattleAction
            {
                Type = BattleActionType.Move,
                User = state.Player1.ActivePokemon,
                Move = state.Player1.ActivePokemon.Moves[0],
                Priority = 0,
                Speed = 999,
            };

            Assert.Null(byHand.Target);
            Assert.Equal(0, byHand.TargetSlot);

            PokemonState defender = state.Player2.ActivePokemon;
            int before = defender.CurrentHP;

            engine.RunTurn(byHand,
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.True(defender.CurrentHP < before);
        }

        // ---- the clone ---------------------------------------------------

        [Fact]
        public void A_cloned_battle_carries_its_slots_and_its_format()
        {
            var (state, _) = Arena(2);

            BattleState clone = state.Clone();

            Assert.Equal(state.Format, clone.Format);
            Assert.Equal(state.Player1.Active.Count, clone.Player1.Active.Count);

            // Its own objects, from its own team - not the original's.
            Assert.NotSame(state.Player1.Active[0], clone.Player1.Active[0]);
            Assert.Same(clone.Player1.Active[0], clone.Player1.ActivePokemon);
            Assert.Contains(clone.Player1.Active[0], clone.Player1.Team);
            Assert.DoesNotContain(clone.Player1.Active[0], state.Player1.Team);
        }

        [Fact]
        public void A_clone_of_a_battle_whose_second_Pokemon_is_out_keeps_the_right_one()
        {
            var (state, engine) = Arena(2);

            SwitchResolver.Resolve(state, state.Player1, state.Player1.Team[1]);

            BattleState clone = state.Clone();

            Assert.Equal(1, clone.Player1.Team.IndexOf(clone.Player1.ActivePokemon));
            Assert.Equal(state.Player1.ActivePokemon.Species, clone.Player1.ActivePokemon.Species);
        }

        // ---- nothing plays doubles yet -----------------------------------

        [Fact]
        public void Doubles_is_named_but_nothing_acts_on_it()
        {
            // The format exists so the slot-shaped code has something to be
            // shaped by. If this ever starts failing it is because somebody
            // built doubles - which is the point - and this test should be
            // replaced rather than deleted.
            Assert.Equal(2, (int)BattleFormat.Doubles);

            var (state, engine) = Arena(1);

            Assert.Equal(BattleFormat.Singles, state.Format);
            Assert.Equal(state.Player1.Active.Count, state.SlotsPerSide);
            Assert.Equal(state.Player2.Active.Count, state.SlotsPerSide);
        }
    }
}
