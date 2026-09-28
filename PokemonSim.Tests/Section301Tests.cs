using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 301. The first batch out of the move-effect audit: the five
    /// recharge moves, and the secondaries that needed nothing but data.
    ///
    /// The recharge moves were the worst thing the audit found. Hyper Beam
    /// and Giga Impact are in 713 learnsets between them and were 150-power
    /// moves with no cost at all - strictly the best move in the simulator,
    /// and the strategies knew it. MustRecharge is the mirror of Charging:
    /// a charge turn is spent before the damage, a recharge turn after it.
    ///
    /// The data half is here too, because a stat change written into
    /// moves.json is as much a behaviour as a class is, and nothing else
    /// would have caught Ancient Power - whose effect sat on a misspelt row
    /// no learnset has ever named.
    /// </summary>
    public class Section301Tests
    {
        static Section301Tests()
        {
            MoveDex.EnsureLoaded();
        }

        static readonly string[] RechargeMoves =
        {
            "Hyper Beam", "Giga Impact", "Blast Burn", "Frenzy Plant", "Hydro Cannon"
        };

        // ---- the recharge turn -------------------------------------------

        [Fact]
        public void Every_recharge_move_in_the_data_carries_the_effect()
        {
            foreach (string name in RechargeMoves)
            {
                MoveState move = MoveDex.Get(name);

                Assert.NotNull(move.Effects);
                Assert.Contains("Recharge", move.Effects!);
            }
        }

        [Fact]
        public void The_registry_knows_the_effect()
        {
            Assert.True(MoveEffectRegistry.IsKnown("Recharge"));
            Assert.IsType<RechargeEffect>(MoveEffectRegistry.Get("Recharge"));
        }

        [Fact]
        public void A_landed_recharge_move_owes_the_next_turn()
        {
            MoveState beam = MoveDex.Get("Hyper Beam");
            beam.Accuracy = 0;                       // 0 = cannot miss, per MoveResolver

            PokemonState attacker = Mon("Snorlax", moves: beam);
            PokemonState defender = Mon("Blissey", hp: 700);

            var (state, engine) = Duel(1, attacker, defender);

            engine.RunTurn(
                MoveAction(state, attacker, beam),
                MoveAction(state, defender, defender.Moves[0]));

            Assert.True(attacker.MustRecharge);
            Assert.Same(beam, attacker.RechargeMove);
        }

        [Fact]
        public void The_recharge_turn_is_spent_and_then_released()
        {
            MoveState beam = MoveDex.Get("Hyper Beam");
            beam.Accuracy = 0;

            PokemonState attacker = Mon("Snorlax", moves: beam);
            PokemonState defender = Mon("Blissey", hp: 700);

            var (state, engine) = Duel(2, attacker, defender);

            engine.RunTurn(
                MoveAction(state, attacker, beam),
                MoveAction(state, defender, defender.Moves[0]));

            int hpAfterFirst = defender.CurrentHP;

            engine.RunTurn(
                MoveAction(state, attacker, beam),
                MoveAction(state, defender, defender.Moves[0]));

            Assert.True(LogContains(state, "must recharge"));
            Assert.Equal(hpAfterFirst, defender.CurrentHP);   // the beam did not go off
            Assert.False(attacker.MustRecharge);              // and the debt is paid

            engine.RunTurn(
                MoveAction(state, attacker, beam),
                MoveAction(state, defender, defender.Moves[0]));

            Assert.True(defender.CurrentHP < hpAfterFirst);    // it fires again
        }

        [Fact]
        public void A_recharge_turn_costs_no_PP()
        {
            MoveState beam = MoveDex.Get("Hyper Beam");
            beam.Accuracy = 0;

            int startingPP = beam.CurrentPP;

            PokemonState attacker = Mon("Snorlax", moves: beam);
            PokemonState defender = Mon("Blissey", hp: 700);

            var (state, engine) = Duel(3, attacker, defender);

            engine.RunTurn(MoveAction(state, attacker, beam), MoveAction(state, defender, defender.Moves[0]));
            engine.RunTurn(MoveAction(state, attacker, beam), MoveAction(state, defender, defender.Moves[0]));

            Assert.Equal(startingPP - 1, beam.CurrentPP);
        }

        [Fact]
        public void A_move_the_target_was_immune_to_owes_nothing()
        {
            // Normal against Ghost: the hit loop breaks before any damage,
            // which is the one way to reach the AfterMove phase without
            // having connected.
            MoveState beam = MoveDex.Get("Hyper Beam");
            beam.Accuracy = 0;

            PokemonState attacker = Mon("Snorlax", moves: beam);
            PokemonState defender = Mon("Gengar", type: PokemonType.Ghost);

            var (state, engine) = Duel(4, attacker, defender);

            engine.RunTurn(
                MoveAction(state, attacker, beam),
                MoveAction(state, defender, defender.Moves[0]));

            Assert.Equal(defender.MaxHP, defender.CurrentHP);
            Assert.False(attacker.MustRecharge);
        }

        [Fact]
        public void The_effect_itself_refuses_a_move_that_dealt_nothing()
        {
            PokemonState attacker = Mon("Snorlax");
            PokemonState defender = Mon("Blissey");
            var (state, _) = Duel(5, attacker, defender);

            var effect = new RechargeEffect();
            int damage = 0;
            bool cancelled = false;

            effect.Apply(state, attacker, defender, attacker.Moves[0], ref damage, ref cancelled);
            Assert.False(attacker.MustRecharge);

            damage = 1;
            effect.Apply(state, attacker, defender, attacker.Moves[0], ref damage, ref cancelled);
            Assert.True(attacker.MustRecharge);
        }

        [Fact]
        public void The_effect_runs_after_the_move_so_a_miss_never_reaches_it()
        {
            Assert.Equal(MovePhase.AfterMove, new RechargeEffect().Phase);
        }

        [Fact]
        public void Recharging_leaves_one_legal_action_and_no_switch()
        {
            MoveState beam = MoveDex.Get("Hyper Beam");
            beam.Accuracy = 0;

            PokemonState attacker = Mon("Snorlax", moves: beam);
            PokemonState bench = Mon("Pikachu", type: PokemonType.Electric);
            PokemonState defender = Mon("Blissey", hp: 700);

            var (state, engine) = Battle(6,
                new List<PokemonState> { attacker, bench },
                new List<PokemonState> { defender });

            List<BattleAction> before = LegalActions.For(state, state.Player1);
            Assert.Contains(before, a => a.Type == BattleActionType.Switch);

            engine.RunTurn(
                MoveAction(state, attacker, beam),
                MoveAction(state, defender, defender.Moves[0]));

            List<BattleAction> during = LegalActions.For(state, state.Player1);

            Assert.Single(during);
            Assert.Equal(BattleActionType.Move, during[0].Type);
            Assert.Same(beam, during[0].Move);
            Assert.DoesNotContain(during, a => a.Type == BattleActionType.Switch);
        }

        [Fact]
        public void A_clone_carries_the_debt_and_its_own_move()
        {
            // The Monte Carlo strategy plays clones out thousands of times a
            // turn; a clone that dropped the debt would rate Hyper Beam as
            // free all over again.
            MoveState beam = MoveDex.Get("Hyper Beam");
            beam.Accuracy = 0;

            PokemonState attacker = Mon("Snorlax", moves: beam);
            PokemonState defender = Mon("Blissey", hp: 700);

            var (state, engine) = Duel(7, attacker, defender);

            engine.RunTurn(
                MoveAction(state, attacker, beam),
                MoveAction(state, defender, defender.Moves[0]));

            PokemonState clone = attacker.Clone();

            Assert.True(clone.MustRecharge);
            Assert.NotNull(clone.RechargeMove);
            Assert.NotSame(attacker.RechargeMove, clone.RechargeMove);
            Assert.Same(clone.Moves[0], clone.RechargeMove);
        }

        [Fact]
        public void Leaving_the_field_clears_the_debt()
        {
            PokemonState mon = Mon("Snorlax");
            mon.MustRecharge = true;
            mon.RechargeMove = mon.Moves[0];

            PokemonState bench = Mon("Pikachu", type: PokemonType.Electric);
            PokemonState defender = Mon("Blissey");

            var (state, _) = Battle(8,
                new List<PokemonState> { mon, bench },
                new List<PokemonState> { defender });

            SwitchResolver.Resolve(state, state.Player1, bench);

            Assert.False(mon.MustRecharge);
            Assert.Null(mon.RechargeMove);
        }

        // ---- the data-only secondaries -----------------------------------

        [Fact]
        public void Ancient_Power_carries_the_effect_its_misspelt_twin_had()
        {
            // The learnsets name "Ancient Power" eight times and
            // "AncientPower" never, so the effect used to belong to a row
            // nothing could reach. Silver Wind is the row it is copied from.
            MoveState silverWind = MoveDex.Get("Silver Wind");

            foreach (string name in new[] { "Ancient Power", "AncientPower", "Ominous Wind" })
            {
                MoveState move = MoveDex.Get(name);

                Assert.Equal(silverWind.SecondaryChance, move.SecondaryChance, 6);
                Assert.NotNull(move.StatChanges);
                Assert.Equal(5, move.StatChanges!.Count);
                Assert.All(move.StatChanges!, change =>
                {
                    Assert.Equal("self", change.Target);
                    Assert.Equal(1, change.Stages);
                });
            }
        }

        [Fact]
        public void Bulldoze_lowers_the_target_Speed_every_time()
        {
            MoveState bulldoze = MoveDex.Get("Bulldoze");
            bulldoze.Accuracy = 0;

            Assert.Equal(0d, bulldoze.SecondaryChance, 6);   // no roll: it always lands

            PokemonState attacker = Mon("Donphan", type: PokemonType.Ground, moves: bulldoze);
            PokemonState defender = Mon("Blissey", hp: 700);

            var (state, engine) = Duel(9, attacker, defender);

            engine.RunTurn(
                MoveAction(state, attacker, bulldoze),
                MoveAction(state, defender, defender.Moves[0]));

            Assert.Equal(-1, defender.SpeedStage);
        }

        [Fact]
        public void Rock_Climb_can_confuse()
        {
            MoveState rockClimb = MoveDex.Get("Rock Climb");

            Assert.NotNull(rockClimb.Effects);
            Assert.Contains("Confuse20", rockClimb.Effects!);
            Assert.True(MoveEffectRegistry.IsKnown("Confuse20"));
        }

        [Fact]
        public void Spicy_Extract_aims_at_the_target()
        {
            MoveState spicy = MoveDex.Get("Spicy Extract");

            Assert.NotNull(spicy.StatChanges);
            Assert.All(spicy.StatChanges!, change => Assert.Equal("opponent", change.Target));

            PokemonState attacker = Mon("Appletun", type: PokemonType.Grass, moves: spicy);
            PokemonState defender = Mon("Blissey", hp: 700);

            var (state, engine) = Duel(10, attacker, defender);

            engine.RunTurn(
                MoveAction(state, attacker, spicy),
                MoveAction(state, defender, defender.Moves[0]));

            Assert.Equal(2, defender.AttackStage);
            Assert.Equal(-2, defender.DefenseStage);
            Assert.Equal(0, attacker.AttackStage);
            Assert.Equal(0, attacker.DefenseStage);
        }

        [Fact]
        public void Nothing_else_in_the_data_changed()
        {
            // The batch touched ten rows. Every other move keeps whatever it
            // had, and the file still holds every move it held.
            Assert.True(MoveDex.AllNames().Count >= 673);

            foreach (string name in RechargeMoves)
                Assert.Equal(150, MoveDex.Get(name).Power);
        }
    }
}
