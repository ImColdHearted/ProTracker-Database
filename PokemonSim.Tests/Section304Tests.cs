using System;
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
    /// §304. The lock-ins and the leftovers - everything the audit had left.
    ///
    /// Two kinds of test, for the same reason §303 had two. A formula is
    /// tested by handing its effect a known damage figure and reading back
    /// what it made of it, because the engine rolls 85-100% into every hit
    /// and a turn-against-turn comparison would be measuring the RNG. A
    /// mechanic - a lock, a volatile, a counter - is tested by playing the
    /// turn, because the wiring is the thing that could be wrong.
    /// </summary>
    public class Section304Tests
    {
        static Section304Tests()
        {
            MoveDex.EnsureLoaded();
            PokemonDex.EnsureLoaded();
        }

        static int Scaled(IMoveEffect effect, BattleState state, PokemonState attacker,
                          PokemonState defender, MoveState move, int damage = 1000)
        {
            bool cancelled = false;
            effect.Apply(state, attacker, defender, move, ref damage, ref cancelled);
            return damage;
        }

        static bool Cancelled(IMoveEffect effect, BattleState state, PokemonState attacker,
                              PokemonState defender, MoveState move)
        {
            int damage = 0;
            bool cancelled = false;
            effect.Apply(state, attacker, defender, move, ref damage, ref cancelled);
            return cancelled;
        }

        /// <summary>The real move out of the data file - effects and all -
        /// with its accuracy roll taken away, so a test of what a move DOES
        /// is never decided by whether it hit.</summary>
        static MoveState Sure(string name)
        {
            MoveState move = MoveDex.Get(name).Clone();
            move.Accuracy = 0;
            return move;
        }

        /// <summary>The average of many damage rolls. One roll is 85-100%
        /// of the real number, so two single rolls can never be compared;
        /// the average of thirty can.</summary>
        static double AverageDamage(BattleState state, PokemonState attacker,
                                    PokemonState defender, MoveState move)
        {
            double total = 0;

            for (int i = 0; i < 30; i++)
                total += DamageCalculator.CalculateDamage(state, attacker, defender, move).Damage;

            return total / 30.0;
        }

        static (BattleState, PokemonState, PokemonState) Field(int seed = 1)
        {
            PokemonState a = Mon("Machamp", type: PokemonType.Fighting);
            PokemonState b = Mon("Blissey", hp: 700);
            var (state, _) = Duel(seed, a, b);
            return (state, a, b);
        }

        // ================= the lock-in ====================================

        [Fact]
        public void Outrage_locks_its_user_in()
        {
            MoveState outrage = Sure("Outrage");
            PokemonState a = Mon("Dragonite", moves: new[] { outrage, Move("Other") });
            PokemonState b = Mon("Snorlax", hp: 4000);

            var (state, engine) = Duel(7, a, b);

            engine.RunTurn(MoveAction(state, a, outrage), MoveAction(state, b, b.Moves[0]));

            Assert.NotNull(a.LockedMove);
            Assert.True(a.LockConfusesOnEnd);
            Assert.InRange(a.LockedTurns, 1, 2);
        }

        [Fact]
        public void A_locked_user_is_offered_that_move_and_nothing_else()
        {
            MoveState outrage = Sure("Outrage");
            PokemonState a = Mon("Dragonite", moves: new[] { outrage, Move("Other") });
            PokemonState bench = Mon("Spare");
            PokemonState b = Mon("Snorlax", hp: 4000);

            var (state, engine) = Battle(7,
                new List<PokemonState> { a, bench },
                new List<PokemonState> { b });

            engine.RunTurn(MoveAction(state, a, outrage), MoveAction(state, b, b.Moves[0]));

            var legal = engine.GetLegalActions(state.Player1);

            Assert.Single(legal);
            Assert.Equal(BattleActionType.Move, legal[0].Type);
            Assert.Same(outrage, legal[0].Move);
        }

        [Fact]
        public void A_lock_that_runs_its_course_confuses_the_user()
        {
            MoveState outrage = Sure("Outrage");
            PokemonState a = Mon("Dragonite", moves: outrage);
            PokemonState b = Mon("Snorlax", hp: 4000);

            var (state, _) = Duel(7, a, b);

            // Start the lock once, then spend exactly the turns it rolled -
            // spending a fixed three would start a SECOND lock on the pass
            // after the first one ended.
            int damage = 40;
            bool cancelled = false;

            new LockInEffect(2, 3, confuses: true)
                .Apply(state, a, b, outrage, ref damage, ref cancelled);

            int rolled = a.LockedTurns;

            Assert.InRange(rolled, 2, 3);

            for (int i = 0; i < rolled; i++)
                LockIn.Spend(state, a, outrage);

            Assert.Null(a.LockedMove);
            Assert.True(a.ConfusionTurns > 0);
            Assert.True(LogContains(state, "tired itself out"));
        }

        [Fact]
        public void A_lock_that_is_broken_does_not_confuse()
        {
            var (state, a, b) = Field();

            a.LockedMove = a.Moves[0];
            a.LockedTurns = 2;
            a.LockConfusesOnEnd = true;

            LockIn.Break(state, a);

            Assert.Null(a.LockedMove);
            Assert.Equal(0, a.ConfusionTurns);
            Assert.True(a.MoveFailedThisTurn);
        }

        [Fact]
        public void Uproars_lock_never_confuses()
        {
            var (state, a, b) = Field();

            MoveState uproar = MoveDex.Get("Uproar");

            int damage = 40;
            bool cancelled = false;
            MoveEffectRegistry.Get("LockIn3")!
                .Apply(state, a, b, uproar, ref damage, ref cancelled);

            Assert.Equal(3, a.LockedTurns);
            Assert.False(a.LockConfusesOnEnd);
        }

        [Fact]
        public void Nobody_falls_asleep_during_an_uproar()
        {
            var (state, a, b) = Field();

            a.LockedMove = MoveDex.Get("Uproar");
            a.LockedTurns = 3;

            MoveResolver.TryInflictStatus(state, b, StatusCondition.Sleep,
                announceFailure: true, substituteBlocks: false, source: a);

            Assert.Equal(StatusCondition.None, b.Status);
            Assert.True(LogContains(state, "can't sleep in an uproar"));
        }

        [Fact]
        public void An_uproar_wakes_a_sleeping_target()
        {
            var (state, a, b) = Field();

            b.Status = StatusCondition.Sleep;
            b.SleepTurns = 3;

            int damage = 40;
            bool cancelled = false;
            new UproarWakeEffect().Apply(state, a, b, MoveDex.Get("Uproar"), ref damage, ref cancelled);

            Assert.Equal(StatusCondition.None, b.Status);
            Assert.Equal(0, b.SleepTurns);
        }

        [Fact]
        public void Leaving_the_field_ends_a_lock_without_confusing()
        {
            MoveState outrage = Sure("Outrage");
            PokemonState a = Mon("Dragonite", moves: outrage);
            PokemonState bench = Mon("Spare");
            PokemonState b = Mon("Snorlax");

            var (state, _) = Battle(7,
                new List<PokemonState> { a, bench },
                new List<PokemonState> { b });

            a.LockedMove = outrage;
            a.LockedTurns = 2;
            a.LockConfusesOnEnd = true;

            SwitchResolver.Resolve(state, state.Player1, bench, voluntary: false);

            Assert.Null(a.LockedMove);
            Assert.Equal(0, a.ConfusionTurns);
        }

        [Fact]
        public void A_locked_user_may_not_switch()
        {
            MoveState outrage = Sure("Outrage");
            PokemonState a = Mon("Dragonite", moves: outrage);
            PokemonState bench = Mon("Spare");
            PokemonState b = Mon("Snorlax");

            var (state, engine) = Battle(7,
                new List<PokemonState> { a, bench },
                new List<PokemonState> { b });

            a.LockedMove = outrage;
            a.LockedTurns = 2;

            Assert.DoesNotContain(engine.GetLegalActions(state.Player1),
                action => action.Type == BattleActionType.Switch);
        }

        // ================= the consecutive ramps ==========================

        [Fact]
        public void Fury_Cutter_doubles_each_turn_and_stops_at_four_times()
        {
            var (state, a, b) = Field();

            MoveState fury = MoveDex.Get("Fury Cutter");
            var effect = new PowerFromConsecutiveUsesEffect(doubles: true, maxSteps: 3);

            a.ConsecutiveMoveUses = 1;
            Assert.Equal(1000, Scaled(effect, state, a, b, fury));

            a.ConsecutiveMoveUses = 2;
            Assert.Equal(2000, Scaled(effect, state, a, b, fury));

            a.ConsecutiveMoveUses = 3;
            Assert.Equal(4000, Scaled(effect, state, a, b, fury));

            a.ConsecutiveMoveUses = 9;
            Assert.Equal(4000, Scaled(effect, state, a, b, fury));
        }

        [Fact]
        public void Echoed_Voice_adds_its_own_power_each_turn_up_to_five()
        {
            var (state, a, b) = Field();

            MoveState echo = MoveDex.Get("Echoed Voice");
            var effect = new PowerFromConsecutiveUsesEffect(doubles: false, maxSteps: 5);

            a.ConsecutiveMoveUses = 3;
            Assert.Equal(3000, Scaled(effect, state, a, b, echo));

            a.ConsecutiveMoveUses = 12;
            Assert.Equal(5000, Scaled(effect, state, a, b, echo));
        }

        [Fact]
        public void Rollout_doubles_again_for_a_curled_up_user()
        {
            var (state, a, b) = Field();

            MoveState rollout = MoveDex.Get("Rollout");
            var effect = new PowerFromConsecutiveUsesEffect(doubles: true, maxSteps: 5, defenseCurlDoubles: true);

            a.ConsecutiveMoveUses = 2;
            Assert.Equal(2000, Scaled(effect, state, a, b, rollout));

            a.DefenseCurled = true;
            Assert.Equal(4000, Scaled(effect, state, a, b, rollout));
        }

        [Fact]
        public void A_different_move_starts_the_count_again()
        {
            PokemonState a = Mon("Scizor");

            LockIn.CountUse(a, Move("Fury Cutter"));
            LockIn.CountUse(a, Move("Fury Cutter"));
            Assert.Equal(2, a.ConsecutiveMoveUses);

            LockIn.CountUse(a, Move("Bullet Punch"));
            Assert.Equal(1, a.ConsecutiveMoveUses);
            Assert.Equal("Bullet Punch", a.ConsecutiveMoveName);
        }

        [Fact]
        public void Defense_Curl_sets_the_latch_Rollout_reads()
        {
            var (state, a, b) = Field();

            int damage = 0;
            bool cancelled = false;
            new DefenseCurlEffect().Apply(state, a, b, MoveDex.Get("Defense Curl"), ref damage, ref cancelled);

            Assert.True(a.DefenseCurled);
        }

        // ================= the counters ===================================

        [Fact]
        public void Rage_Fist_grows_with_the_hits_its_owner_has_taken()
        {
            var (state, a, b) = Field();

            MoveState fist = MoveDex.Get("Rage Fist");
            var effect = new PowerFromTimesAttackedEffect();

            Assert.Equal(50, fist.Power);
            Assert.Equal(1000, Scaled(effect, state, a, b, fist));

            a.TimesAttacked = 3;
            Assert.Equal(4000, Scaled(effect, state, a, b, fist));

            a.TimesAttacked = 99;
            Assert.Equal(7000, Scaled(effect, state, a, b, fist));
        }

        [Fact]
        public void A_multi_hit_move_counts_as_many_hits_as_it_lands()
        {
            MoveState spear = Move("Icicle Spear", power: 25);
            spear.MinHits = 3;
            spear.MaxHits = 3;

            PokemonState a = Mon("Cloyster", moves: spear);
            PokemonState b = Mon("Annihilape", hp: 900);

            var (state, engine) = Duel(5, a, b);

            engine.RunTurn(MoveAction(state, a, spear), MoveAction(state, b, b.Moves[0]));

            Assert.Equal(3, b.TimesAttacked);
        }

        [Fact]
        public void Stomping_Tantrum_doubles_after_a_move_that_failed()
        {
            var (state, a, b) = Field();

            MoveState tantrum = MoveDex.Get("Stomping Tantrum");
            var effect = new PowerIfLastMoveFailedEffect();

            Assert.Equal(1000, Scaled(effect, state, a, b, tantrum));

            a.MoveFailedLastTurn = true;
            Assert.Equal(2000, Scaled(effect, state, a, b, tantrum));
        }

        [Fact]
        public void Triple_Kick_hits_harder_each_time()
        {
            var (state, a, b) = Field();

            MoveState kick = MoveDex.Get("Triple Kick");
            var effect = new PowerFromHitNumberEffect();

            Assert.Equal(10, kick.Power);

            state.CurrentHitNumber = 1;
            Assert.Equal(1000, Scaled(effect, state, a, b, kick));

            state.CurrentHitNumber = 2;
            Assert.Equal(2000, Scaled(effect, state, a, b, kick));

            state.CurrentHitNumber = 3;
            Assert.Equal(3000, Scaled(effect, state, a, b, kick));
        }

        [Fact]
        public void Pursuit_doubles_on_a_target_that_has_declared_a_switch()
        {
            PokemonState a = Mon("Tyranitar");
            PokemonState b = Mon("Alakazam");
            PokemonState bench = Mon("Spare");

            var (state, _) = Battle(3,
                new List<PokemonState> { a },
                new List<PokemonState> { b, bench });

            MoveState pursuit = MoveDex.Get("Pursuit");
            var effect = new PowerIfTargetSwitchingEffect();

            Assert.Equal(1000, Scaled(effect, state, a, b, pursuit));

            state.DeclaredP2 = SwitchAction(state, b, bench);
            Assert.Equal(2000, Scaled(effect, state, a, b, pursuit));

            // ...and not once the switch has already happened.
            b.ActedThisTurn = true;
            Assert.Equal(1000, Scaled(effect, state, a, b, pursuit));
        }

        [Fact]
        public void Upper_Hand_fails_unless_the_target_is_winding_up_a_priority_attack()
        {
            PokemonState a = Mon("Sirfetch'd");
            PokemonState b = Mon("Scizor", moves: Move("Bullet Punch", priority: 1));

            var (state, _) = Duel(3, a, b);

            MoveState upper = MoveDex.Get("Upper Hand");
            var effect = new UpperHandGateEffect();

            // Nothing declared - the gate stays out of the way.
            Assert.False(Cancelled(effect, state, a, b, upper));

            state.DeclaredP2 = MoveAction(state, b, Move("Bulk Up", category: MoveCategory.Status));
            Assert.True(Cancelled(effect, state, a, b, upper));

            state.DeclaredP2 = MoveAction(state, b, b.Moves[0]);
            Assert.False(Cancelled(effect, state, a, b, upper));
        }

        [Fact]
        public void Upper_Hand_flinches_for_certain_when_it_lands()
        {
            Assert.Equal(1.0, MoveDex.Get("Upper Hand").FlinchChance, 6);
        }

        [Fact]
        public void Return_and_Frustration_are_opposite_ends_of_the_same_number()
        {
            var (state, a, b) = Field();

            MoveState ret = MoveDex.Get("Return");
            MoveState frust = MoveDex.Get("Frustration");

            Assert.Equal(102, ret.Power);
            Assert.Equal(102, frust.Power);

            // The default is the maximum, so Return is at its strongest and
            // Frustration at its weakest.
            Assert.Equal(255, a.Happiness);
            Assert.Equal(1000, Scaled(new PowerFromHappinessEffect(false), state, a, b, ret));

            a.Happiness = 0;
            Assert.Equal(1000, Scaled(new PowerFromHappinessEffect(true), state, a, b, frust));
            Assert.Equal(9, Scaled(new PowerFromHappinessEffect(false), state, a, b, ret));
        }

        [Fact]
        public void Water_Shuriken_is_only_stronger_in_Ash_Greninjas_hands()
        {
            var (state, a, b) = Field();

            MoveState shuriken = MoveDex.Get("Water Shuriken");
            var effect = new PowerIfAshGreninjaEffect();

            Assert.Equal(15, shuriken.Power);
            Assert.Equal(1000, Scaled(effect, state, a, b, shuriken));

            PokemonState ash = Mon("Ash-Greninja");
            ash.AbilityId = "Battle Bond";
            Assert.Equal(1333, Scaled(effect, state, ash, b, shuriken));

            // The species alone is not enough - the ability is the reason.
            PokemonState plain = Mon("Greninja");
            Assert.Equal(1000, Scaled(effect, state, plain, b, shuriken));
        }

        [Fact]
        public void Beat_Up_throws_one_punch_per_able_party_member()
        {
            PokemonState a = Mon("Weavile");
            PokemonState fit = Mon("Houndoom");
            PokemonState burnt = Mon("Umbreon");
            PokemonState down = Mon("Absol");

            burnt.Status = StatusCondition.Burn;
            down.CurrentHP = 0;

            var (state, _) = Battle(3,
                new List<PokemonState> { a, fit, burnt, down },
                new List<PokemonState> { Mon("Blissey", hp: 700) });

            int damage = 0;
            bool cancelled = false;
            new BeatUpEffect().Apply(state, a, state.Player2.ActivePokemon,
                MoveDex.Get("Beat Up"), ref damage, ref cancelled);

            Assert.Equal(2, state.HitCountOverride);
        }

        // ================= the leftovers ==================================

        [Fact]
        public void A_kick_that_misses_costs_half_the_users_health()
        {
            MoveState kick = MoveDex.Get("High Jump Kick").Clone();
            PokemonState a = Mon("Hitmonlee", hp: 200, moves: kick);
            PokemonState b = Mon("Gengar");

            var (state, _) = Duel(1, a, b);

            CrashDamageEffect.OnMiss(state, a, kick);

            Assert.Equal(100, a.CurrentHP);
            Assert.True(LogContains(state, "crashed"));
        }

        [Fact]
        public void A_move_without_the_marker_never_crashes()
        {
            var (state, a, b) = Field();

            int before = a.CurrentHP;
            CrashDamageEffect.OnMiss(state, a, Move("Tackle"));

            Assert.Equal(before, a.CurrentHP);
        }

        [Fact]
        public void Smack_Down_brings_a_Flyer_to_the_ground()
        {
            var (state, a, _) = Field();

            PokemonState flyer = Mon("Skarmory", type: PokemonType.Flying);

            Assert.False(Grounding.IsGrounded(state, flyer));

            int damage = 40;
            bool cancelled = false;
            new SmackDownEffect().Apply(state, a, flyer, MoveDex.Get("Smack Down"), ref damage, ref cancelled);

            Assert.True(flyer.SmackedDown);
            Assert.True(Grounding.IsGrounded(state, flyer));
        }

        [Fact]
        public void Roost_costs_the_user_its_Flying_type_for_the_turn()
        {
            var (state, _, b) = Field();

            PokemonState bird = Mon("Skarmory", type: PokemonType.Flying);

            int damage = 0;
            bool cancelled = false;
            new RoostEffect().Apply(state, bird, b, MoveDex.Get("Roost"), ref damage, ref cancelled);

            Assert.True(bird.RoostedThisTurn);
            Assert.True(Grounding.IsGrounded(state, bird));
        }

        [Fact]
        public void Psychic_Noise_stops_the_target_healing()
        {
            var (state, a, b) = Field();

            int damage = 40;
            bool cancelled = false;
            new HealBlockEffect().Apply(state, a, b, MoveDex.Get("Psychic Noise"), ref damage, ref cancelled);

            Assert.Equal(2, b.HealBlockTurns);

            b.CurrentHP = 100;

            Assert.True(Cancelled(new HealEffect(), state, b, a, MoveDex.Get("Recover")));
            Assert.Equal(100, b.CurrentHP);
        }

        [Fact]
        public void Sparkling_Aria_washes_the_targets_burn_off()
        {
            var (state, a, b) = Field();

            b.Status = StatusCondition.Burn;

            int damage = 40;
            bool cancelled = false;
            new CureTargetBurnEffect().Apply(state, a, b, MoveDex.Get("Sparkling Aria"), ref damage, ref cancelled);

            Assert.Equal(StatusCondition.None, b.Status);
        }

        [Fact]
        public void Glaive_Rush_leaves_its_user_open_until_it_moves_again()
        {
            MoveState glaive = Sure("Glaive Rush");
            PokemonState a = Mon("Baxcalibur", moves: new[] { glaive, Move("Wait") });
            PokemonState b = Mon("Blissey", hp: 900);

            var (state, engine) = Duel(11, a, b);

            engine.RunTurn(MoveAction(state, a, glaive), MoveAction(state, b, b.Moves[0]));
            Assert.True(a.GlaiveRushActive);

            engine.RunTurn(MoveAction(state, a, a.Moves[1]), MoveAction(state, b, b.Moves[0]));
            Assert.False(a.GlaiveRushActive);
        }

        [Fact]
        public void Spectral_Thief_takes_the_raised_stages_and_leaves_the_dropped_ones()
        {
            var (state, a, b) = Field();

            b.AttackStage = 3;
            b.SpeedStage = 2;
            b.DefenseStage = -2;

            int damage = 0;
            bool cancelled = false;
            new SpectralThiefEffect().Apply(state, a, b, MoveDex.Get("Spectral Thief"), ref damage, ref cancelled);

            Assert.Equal(3, a.AttackStage);
            Assert.Equal(2, a.SpeedStage);
            Assert.Equal(0, b.AttackStage);
            Assert.Equal(0, b.SpeedStage);

            // The drop is the target's own problem and stays with it.
            Assert.Equal(-2, b.DefenseStage);
            Assert.Equal(0, a.DefenseStage);
        }

        [Fact]
        public void A_wish_lands_a_turn_later_and_is_worth_the_wishers_health()
        {
            MoveState wish = Sure("Wish");
            PokemonState a = Mon("Blissey", hp: 700, moves: new[] { wish, Move("Wait") });
            PokemonState b = Mon("Snorlax", hp: 900);

            var (state, engine) = Duel(13, a, b);

            a.CurrentHP = 100;

            engine.RunTurn(MoveAction(state, a, wish), MoveAction(state, b, b.Moves[0]));

            // Half the WISHER's maximum health, fixed when the wish was
            // made - not a share of whoever collects it.
            Assert.Equal(350, state.WishHealP1);

            int afterFirstTurn = a.CurrentHP;

            engine.RunTurn(MoveAction(state, a, a.Moves[1]), MoveAction(state, b, b.Moves[0]));

            Assert.True(a.CurrentHP > afterFirstTurn);
            Assert.Equal(0, state.WishTurnsP1);
        }

        [Fact]
        public void Revival_Blessing_brings_one_back_at_half_health()
        {
            PokemonState a = Mon("Pawmot");
            PokemonState fallen = Mon("Spare", hp: 200);
            fallen.CurrentHP = 0;

            var (state, _) = Battle(3,
                new List<PokemonState> { a, fallen },
                new List<PokemonState> { Mon("Snorlax") });

            int damage = 0;
            bool cancelled = false;
            new RevivalBlessingEffect().Apply(state, a, state.Player2.ActivePokemon,
                MoveDex.Get("Revival Blessing"), ref damage, ref cancelled);

            Assert.Equal(100, fallen.CurrentHP);
            Assert.False(fallen.Fainted);
        }

        [Fact]
        public void Revival_Blessing_fails_with_nobody_to_revive()
        {
            var (state, a, b) = Field();

            Assert.True(Cancelled(new RevivalBlessingEffect(), state, a, b,
                MoveDex.Get("Revival Blessing")));
        }

        [Fact]
        public void Charge_doubles_the_next_Electric_move_and_only_it()
        {
            var (state, a, b) = Field();

            a.ChargeActive = true;

            MoveState bolt = Move("Thunderbolt", type: PokemonType.Electric,
                category: MoveCategory.Special, power: 90);
            MoveState tackle = Move("Tackle", power: 90);

            double electric = AverageDamage(state, a, b, bolt);

            a.ChargeActive = false;
            double plain = AverageDamage(state, a, b, bolt);

            Assert.InRange(electric / plain, 1.8, 2.2);

            // A Normal move gets nothing out of it.
            a.ChargeActive = true;
            double normal = AverageDamage(state, a, b, tackle);

            a.ChargeActive = false;
            double normalPlain = AverageDamage(state, a, b, tackle);

            Assert.InRange(normal / normalPlain, 0.9, 1.1);
        }

        [Fact]
        public void A_flattening_move_doubles_on_a_Minimized_target()
        {
            var (state, a, b) = Field();

            MoveState stomp = MoveDex.Get("Stomp");

            Assert.True(stomp.IsFlattening);
            Assert.False(MoveDex.Get("Tackle").IsFlattening);

            double plain = AverageDamage(state, a, b, stomp);

            b.Minimized = true;
            double flattened = AverageDamage(state, a, b, stomp);

            Assert.InRange(flattened / plain, 1.8, 2.2);
        }

        [Fact]
        public void Glaive_Rush_doubles_what_lands_on_its_user()
        {
            var (state, a, b) = Field();

            MoveState tackle = Move("Tackle", power: 80);

            double plain = AverageDamage(state, a, b, tackle);

            b.GlaiveRushActive = true;
            double exposed = AverageDamage(state, a, b, tackle);

            Assert.InRange(exposed / plain, 1.8, 2.2);
        }

        [Fact]
        public void An_ion_deluge_turns_a_Normal_move_Electric()
        {
            var (state, a, _) = Field();

            PokemonState ground = Mon("Marowak", type: PokemonType.Ground);
            MoveState tackle = Move("Tackle", power: 80);

            // Electric does nothing to a Ground type; Normal does.
            Assert.True(AverageDamage(state, a, ground, tackle) > 0);

            state.IonDelugeTurns = 1;

            Assert.Equal(0, DamageCalculator.CalculateDamage(state, a, ground, tackle).Damage);
        }

        [Fact]
        public void Darkest_Lariat_does_not_see_the_targets_boosts()
        {
            var (state, a, b) = Field();

            MoveState lariat = MoveDex.Get("Darkest Lariat");

            Assert.True(lariat.IgnoresDefensiveBoosts);
            Assert.True(lariat.IgnoresEvasion);

            MoveState ordinary = Move("Ordinary", power: lariat.Power);

            b.DefenseStage = 6;
            double lariatBoosted = AverageDamage(state, a, b, lariat);
            double ordinaryBoosted = AverageDamage(state, a, b, ordinary);

            b.DefenseStage = 0;
            double lariatPlain = AverageDamage(state, a, b, lariat);
            double ordinaryPlain = AverageDamage(state, a, b, ordinary);

            // Six stages of Defense is a quarter of the damage - for a move
            // that can see them.
            Assert.InRange(ordinaryBoosted / ordinaryPlain, 0.2, 0.3);

            // Darkest Lariat cannot, so nothing changed.
            Assert.InRange(lariatBoosted / lariatPlain, 0.9, 1.1);
        }

        [Fact]
        public void Thousand_Arrows_reaches_a_Flying_target()
        {
            var (state, a, _) = Field();

            PokemonState flyer = Mon("Tornadus", type: PokemonType.Flying);

            MoveState arrows = MoveDex.Get("Thousand Arrows");

            Assert.True(arrows.IgnoresFlyingImmunity);

            DamageResult result = DamageCalculator.CalculateDamage(state, a, flyer, arrows);

            Assert.True(result.Effectiveness > 0);
            Assert.True(result.Damage > 0);
        }

        [Fact]
        public void Shell_Side_Arm_picks_the_side_that_hurts_more()
        {
            MoveState shell = MoveDex.Get("Shell Side Arm");

            Assert.True(shell.UsesBetterDamage);
            Assert.False(shell.UsesHigherOffense);
            Assert.Equal(StatusCondition.Poison, shell.InflictStatus);
            Assert.Equal(0.2, shell.StatusChance, 6);
        }

        // ================= the wiring =====================================

        [Fact]
        public void The_registry_knows_every_name_the_data_uses()
        {
            foreach (string name in new[]
            {
                "LockIn2to3Confuse", "LockIn3", "LockIn5", "UproarWake",
                "PowerFromFuryCutter", "PowerFromEchoedVoice", "PowerFromRollout",
                "DefenseCurl", "Minimize", "PowerFromTimesAttacked",
                "PowerIfLastMoveFailed", "PowerFromHitNumber", "PowerIfTargetSwitching",
                "PowerFromHappiness", "PowerFromLowHappiness", "PowerIfAshGreninja",
                "BeatUp", "PowerFromBeatUpAlly", "CrashDamage", "SmackDown",
                "HealBlock", "CureTargetBurn", "GlaiveRush", "Rage", "Charge",
                "Roost", "IonDeluge", "SpectralThief", "Wish", "RevivalBlessing",
                "UpperHandGate",
            })
            {
                Assert.True(MoveEffectRegistry.IsKnown(name), name + " is not registered");
            }
        }

        [Fact]
        public void Every_move_this_section_touched_asks_for_its_effect()
        {
            var expected = new Dictionary<string, string>
            {
                ["High Jump Kick"] = "CrashDamage",
                ["Jump Kick"] = "CrashDamage",
                ["Supercell Slam"] = "CrashDamage",
                ["Dragon Tail"] = "ForcedSwitch",
                ["Spectral Thief"] = "SpectralThief",
                ["Plasma Fists"] = "IonDeluge",
                ["Revival Blessing"] = "RevivalBlessing",
                ["Wish"] = "Wish",
                ["Upper Hand"] = "UpperHandGate",
                ["Psychic Noise"] = "HealBlock",
                ["Smack Down"] = "SmackDown",
                ["Sparkling Aria"] = "CureTargetBurn",
                ["Thousand Arrows"] = "SmackDown",
                ["Charge"] = "Charge",
                ["Defense Curl"] = "DefenseCurl",
                ["Glaive Rush"] = "GlaiveRush",
                ["Minimize"] = "Minimize",
                ["Outrage"] = "LockIn2to3Confuse",
                ["Thrash"] = "LockIn2to3Confuse",
                ["Petal Dance"] = "LockIn2to3Confuse",
                ["Rage"] = "Rage",
                ["Roost"] = "Roost",
                ["Uproar"] = "LockIn3",
                ["Rollout"] = "LockIn5",
                ["Beat Up"] = "BeatUp",
                ["Echoed Voice"] = "PowerFromEchoedVoice",
                ["Fury Cutter"] = "PowerFromFuryCutter",
                ["Pursuit"] = "PowerIfTargetSwitching",
                ["Rage Fist"] = "PowerFromTimesAttacked",
                ["Return"] = "PowerFromHappiness",
                ["Frustration"] = "PowerFromLowHappiness",
                ["Stomping Tantrum"] = "PowerIfLastMoveFailed",
                ["Temper Flare"] = "PowerIfLastMoveFailed",
                ["Triple Kick"] = "PowerFromHitNumber",
                ["Triple Axel"] = "PowerFromHitNumber",
                ["Water Shuriken"] = "PowerIfAshGreninja",
            };

            foreach (var pair in expected)
            {
                MoveState move = MoveDex.Get(pair.Key);

                Assert.NotNull(move.Effects);
                Assert.Contains(pair.Value, move.Effects!);
            }
        }

        [Fact]
        public void A_clone_carries_the_lock_and_gets_its_own_move()
        {
            MoveState outrage = Move("Outrage", power: 40);
            PokemonState a = Mon("Dragonite", moves: new[] { outrage, Move("Other") });

            a.LockedMove = outrage;
            a.LockedTurns = 2;
            a.LockConfusesOnEnd = true;
            a.TimesAttacked = 4;
            a.ConsecutiveMoveUses = 3;
            a.Happiness = 120;

            PokemonState clone = a.Clone();

            Assert.Equal(2, clone.LockedTurns);
            Assert.True(clone.LockConfusesOnEnd);
            Assert.Equal(4, clone.TimesAttacked);
            Assert.Equal(3, clone.ConsecutiveMoveUses);
            Assert.Equal(120, clone.Happiness);

            Assert.NotNull(clone.LockedMove);
            Assert.NotSame(outrage, clone.LockedMove);
            Assert.Same(clone.Moves[0], clone.LockedMove);
        }
    }
}
