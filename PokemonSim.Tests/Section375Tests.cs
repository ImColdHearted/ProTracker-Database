using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Engine.Items;
using PokemonSim.Models;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §375. Forty-two more held items, each of which has to DO something,
    /// because the picker that offers them promises exactly that.
    ///
    /// The sixteen plates are boosters and the eighteen Gems are one-shot
    /// boosters, so those are damage arithmetic on a seeded duel - the same
    /// seed with and without the item, and the number has to be the other
    /// number times the multiplier, floor per hit. The eight with effects of
    /// their own each get the test their effect allows: a Shell Bell and an
    /// Eject Button are deterministic; a Focus Band, a Quick Claw, a Razor
    /// Fang and a Bright Powder are dice, so those run a few hundred seeds
    /// and require the count to sit where one-in-ten or one-in-five puts it,
    /// and to be zero without the item.
    /// </summary>
    public class Section375Tests
    {
        /// <summary>One Pokemon with one move - every duel here needs no
        /// more. The move is positional because a named params argument
        /// cannot take a bare element (see TestKit.Mon).</summary>
        static PokemonState Mon(string species, MoveState? move = null, PokemonType type = PokemonType.Normal,
                                int hp = 200, int attack = 100, int defense = 100, int speed = 100) =>
            TestKit.Mon(species, type, hp, attack, defense, speed, move ?? TestKit.Move("Tap", power: 1));

        /// <summary>One clash: both sides use their first move. Returns how
        /// much the defender (player 2) lost.</summary>
        static int DamageTaken(BattleState state, BattleEngine engine)
        {
            int before = state.Player2.ActivePokemon.CurrentHP;
            TestKit.Clash(state, engine);
            return before - state.Player2.ActivePokemon.CurrentHP;
        }

        /// <summary>The same duel twice, seed and all, once with the item on
        /// the attacker and once without. The move is a factory so each
        /// battle gets its own MoveState - PP and the like are per battle.</summary>
        static (int Held, int Bare) DamageWithAndWithout(string item, Func<MoveState> move, int seed = 375)
        {
            int bare;
            {
                (BattleState state, BattleEngine engine) = TestKit.Duel(seed,
                    Mon("Holder", move()), Mon("Target", TestKit.Move("Tap", power: 1), speed: 1));
                bare = DamageTaken(state, engine);
            }

            int held;
            {
                (BattleState state, BattleEngine engine) = TestKit.Duel(seed,
                    Mon("Holder", move()), Mon("Target", TestKit.Move("Tap", power: 1), speed: 1));
                state.Player1.ActivePokemon.HeldItemId = item;
                held = DamageTaken(state, engine);
            }

            return (held, bare);
        }

        // =============================================================
        // 1. the catalog
        // =============================================================

        public static IEnumerable<object[]> TheFortyTwo()
        {
            foreach (string name in new[]
            {
                "Fist Plate", "Sky Plate", "Toxic Plate", "Earth Plate", "Stone Plate", "Insect Plate",
                "Spooky Plate", "Iron Plate", "Flame Plate", "Splash Plate", "Meadow Plate", "Zap Plate",
                "Mind Plate", "Icicle Plate", "Draco Plate", "Dread Plate",
                "Normal Gem", "Fire Gem", "Water Gem", "Electric Gem", "Grass Gem", "Ice Gem",
                "Fighting Gem", "Poison Gem", "Ground Gem", "Flying Gem", "Psychic Gem", "Bug Gem",
                "Rock Gem", "Ghost Gem", "Dragon Gem", "Dark Gem", "Steel Gem", "Fairy Gem",
                "Focus Band", "Shell Bell", "Shed Shell", "Quick Claw",
                "Razor Fang", "Razor Claw", "Eject Button", "Bright Powder",
            })
            {
                yield return new object[] { name };
            }
        }

        [Theory]
        [MemberData(nameof(TheFortyTwo))]
        public void Every_new_item_is_supported_under_its_pretty_name(string name)
        {
            Assert.True(HeldItems.IsSupported(name), name);
            Assert.Equal(name, HeldItems.DisplayName(name));
            Assert.Contains(name, HeldItems.DisplayNamesInOrder);
        }

        [Fact]
        public void The_catalog_is_a_hundred_and_forty_seven()
        {
            Assert.Equal(147, HeldItems.DisplayNamesInOrder.Count);
            Assert.Equal(42, TheFortyTwo().Count());
        }

        [Fact]
        public void A_plate_boosts_the_type_its_name_says_and_a_gem_answers_to_one()
        {
            Assert.Equal(PokemonType.Fire, HeldItems.BoostedType("Flame Plate"));
            Assert.Equal(PokemonType.Dragon, HeldItems.BoostedType("Draco Plate"));
            Assert.Equal(PokemonType.Fairy, HeldItems.BoostedType("Pixie Plate"));
            Assert.Equal(PokemonType.Fire, HeldItems.BoostedType("Charcoal"));
            Assert.Null(HeldItems.BoostedType("Fire Gem"));

            Assert.True(HeldItems.IsGem("Fire Gem"));
            Assert.Equal(PokemonType.Fire, HeldItems.GemType("firegem"));
            Assert.Null(HeldItems.GemType("Flame Plate"));
            Assert.False(HeldItems.IsGem("Flame Plate"));
            Assert.False(HeldItems.IsBerry("Fire Gem"));
        }

        /// <summary>§309's calculator banner and §311's encoder read these;
        /// the eight that move dice or turns cannot move a damage roll.</summary>
        [Fact]
        public void The_encoder_and_the_calculator_see_the_new_items_the_right_way()
        {
            Assert.True(HeldItems.BoostsDamage("Fire Gem"));
            Assert.True(HeldItems.BoostsDamage("Flame Plate"));
            Assert.True(HeldItems.AffectsDamageOrKo("Fire Gem"));
            Assert.True(HeldItems.AffectsDamageOrKo("Flame Plate"));

            foreach (string dice in new[] { "Focus Band", "Shell Bell", "Shed Shell", "Quick Claw",
                                            "Razor Fang", "Razor Claw", "Eject Button", "Bright Powder" })
            {
                Assert.False(HeldItems.AffectsDamageOrKo(dice), dice);
                Assert.False(HeldItems.BoostsDamage(dice), dice);
                Assert.False(HeldItems.SavesItsHolderOnce(dice), dice);
            }
        }

        // =============================================================
        // 2. plates and gems - damage arithmetic
        // =============================================================

        [Fact]
        public void A_plate_is_a_fifth_more_on_its_type_and_nothing_on_another()
        {
            (int held, int bare) = DamageWithAndWithout("Flame Plate",
                () => TestKit.Move("Ember", PokemonType.Fire, MoveCategory.Special, power: 60));

            Assert.True(bare > 0);
            Assert.Equal((int)(bare * 1.2), held);

            (held, bare) = DamageWithAndWithout("Flame Plate",
                () => TestKit.Move("Splash Hit", PokemonType.Water, MoveCategory.Special, power: 60));

            Assert.Equal(bare, held);
        }

        [Fact]
        public void A_gem_is_thirty_percent_once_and_is_gone()
        {
            (BattleState state, BattleEngine engine) = TestKit.Duel(375,
                Mon("Holder", TestKit.Move("Ember", PokemonType.Fire, MoveCategory.Special, power: 60)),
                Mon("Target", TestKit.Move("Tap", power: 1), hp: 1000, speed: 1));

            PokemonState holder = state.Player1.ActivePokemon;
            holder.HeldItemId = "Fire Gem";

            int first = DamageTaken(state, engine);

            Assert.Null(holder.HeldItemId);
            Assert.True(holder.LostItem);
            Assert.False(holder.GemBoosting);
            Assert.True(TestKit.LogContains(state, "The Fire Gem strengthened Ember's power!"));

            (int held, int bare) = DamageWithAndWithout("Fire Gem",
                () => TestKit.Move("Ember", PokemonType.Fire, MoveCategory.Special, power: 60));

            Assert.Equal((int)(bare * HeldItems.GemMultiplier), held);
            Assert.Equal(held, first);
        }

        [Fact]
        public void A_gem_of_another_type_stays_in_the_bag()
        {
            (int held, int bare) = DamageWithAndWithout("Water Gem",
                () => TestKit.Move("Ember", PokemonType.Fire, MoveCategory.Special, power: 60));

            Assert.Equal(bare, held);

            (BattleState state, BattleEngine engine) = TestKit.Duel(375,
                Mon("Holder", TestKit.Move("Ember", PokemonType.Fire, MoveCategory.Special, power: 60)),
                Mon("Target", TestKit.Move("Tap", power: 1), speed: 1));

            state.Player1.ActivePokemon.HeldItemId = "Water Gem";
            TestKit.Clash(state, engine);

            Assert.Equal("watergem", state.Player1.ActivePokemon.HeldItemId);
            Assert.False(state.Player1.ActivePokemon.LostItem);
        }

        /// <summary>The Gem is spent on the first hit and the move keeps the
        /// boost to its last: two hits, both a third stronger, floor each -
        /// which is more than one boosted hit and one plain one could reach.</summary>
        [Fact]
        public void A_gem_boosts_every_hit_of_a_multi_hit_move()
        {
            MoveState DoubleHit()
            {
                MoveState move = TestKit.Move("Double Hit", PokemonType.Normal, MoveCategory.Physical, power: 35);
                move.MinHits = 2;
                move.MaxHits = 2;
                return move;
            }

            int bare;
            {
                (BattleState state, BattleEngine engine) = TestKit.Duel(375,
                    Mon("Holder", DoubleHit()), Mon("Target", TestKit.Move("Tap", power: 1), hp: 1000, speed: 1));
                bare = DamageTaken(state, engine);
            }

            int held;
            {
                (BattleState state, BattleEngine engine) = TestKit.Duel(375,
                    Mon("Holder", DoubleHit()), Mon("Target", TestKit.Move("Tap", power: 1), hp: 1000, speed: 1));
                state.Player1.ActivePokemon.HeldItemId = "Normal Gem";
                held = DamageTaken(state, engine);
                Assert.Null(state.Player1.ActivePokemon.HeldItemId);
            }

            // Two floors can lose at most one point against the floor of the
            // sum - and one boosted hit plus one plain one would be a third
            // of the plain hit short of this, which is several points.
            int boosted = (int)(bare * HeldItems.GemMultiplier);

            Assert.InRange(held, boosted - 1, boosted);
        }

        // =============================================================
        // 3. the eight
        // =============================================================

        [Fact]
        public void A_razor_claw_is_a_scope_lens()
        {
            PokemonState mon = Mon("Holder");

            mon.HeldItemId = "Razor Claw";
            Assert.Equal(1, HeldItems.CritStageBonus(mon));

            mon.HeldItemId = "Scope Lens";
            Assert.Equal(1, HeldItems.CritStageBonus(mon));

            mon.HeldItemId = "Shell Bell";
            Assert.Equal(0, HeldItems.CritStageBonus(mon));
        }

        [Fact]
        public void A_shell_bell_gives_an_eighth_of_the_damage_back()
        {
            (BattleState state, BattleEngine engine) = TestKit.Duel(375,
                Mon("Holder", TestKit.Move("Slam", power: 80)),
                Mon("Target", TestKit.Move("Tap", power: 1), hp: 1000, speed: 1));

            PokemonState holder = state.Player1.ActivePokemon;
            holder.HeldItemId = "Shell Bell";
            holder.CurrentHP = 100;

            int dealt = DamageTaken(state, engine);
            int tapped = 0;

            // The target's Tap lands after Slam (speed 1): subtract it back
            // out, reading it off the log's own line.
            string? tapLine = state.Log.Lines.FirstOrDefault(l => l.StartsWith("Holder lost ", StringComparison.Ordinal));
            if (tapLine != null)
                tapped = int.Parse(tapLine.Split(' ')[2]);

            Assert.True(dealt > 8);
            Assert.Equal(100 + Math.Max(1, dealt / 8) - tapped, holder.CurrentHP);
            Assert.True(TestKit.LogContains(state, "restored a little HP using its Shell Bell!"));
            Assert.Equal("shellbell", holder.HeldItemId);
        }

        [Fact]
        public void A_shell_bell_at_full_health_has_nothing_to_restore()
        {
            (BattleState state, BattleEngine engine) = TestKit.Duel(375,
                Mon("Holder", TestKit.Move("Slam", power: 80), speed: 1),
                Mon("Target", TestKit.Move("Nothing", category: MoveCategory.Status, power: 0), hp: 1000));

            state.Player1.ActivePokemon.HeldItemId = "Shell Bell";
            TestKit.Clash(state, engine);

            Assert.False(TestKit.LogContains(state, "Shell Bell"));
        }

        [Fact]
        public void An_eject_button_sends_its_holder_to_the_bench_and_is_spent()
        {
            PokemonState holder = Mon("Holder", TestKit.Move("Tap", power: 1), speed: 1);
            PokemonState bench = Mon("Bench", TestKit.Move("Tap", power: 1));

            (BattleState state, BattleEngine engine) = TestKit.Battle(375,
                new List<PokemonState> { holder, bench },
                new List<PokemonState> { Mon("Foe", TestKit.Move("Slam", power: 60)) });

            holder.HeldItemId = "Eject Button";

            engine.RunTurn(
                TestKit.MoveAction(state, holder, holder.Moves[0]),
                TestKit.MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.Same(bench, state.Player1.ActivePokemon);
            Assert.Null(holder.HeldItemId);
            Assert.True(holder.LostItem);
            Assert.True(holder.CurrentHP < holder.MaxHP);
            Assert.True(TestKit.LogContains(state, "Holder is switched out with the Eject Button!"));
            Assert.True(TestKit.LogContains(state, "P1 sent out Bench!"));
        }

        /// <summary>A hit the substitute took is not a hit the holder took.</summary>
        [Fact]
        public void An_eject_button_ignores_a_hit_the_substitute_soaked()
        {
            PokemonState holder = Mon("Holder", TestKit.Move("Tap", power: 1), speed: 1);
            PokemonState bench = Mon("Bench", TestKit.Move("Tap", power: 1));

            (BattleState state, BattleEngine engine) = TestKit.Battle(375,
                new List<PokemonState> { holder, bench },
                new List<PokemonState> { Mon("Foe", TestKit.Move("Slam", power: 60)) });

            holder.HeldItemId = "Eject Button";
            holder.SubstituteHP = 150;

            engine.RunTurn(
                TestKit.MoveAction(state, holder, holder.Moves[0]),
                TestKit.MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.Same(holder, state.Player1.ActivePokemon);
            Assert.Equal("ejectbutton", holder.HeldItemId);
            Assert.Equal(holder.MaxHP, holder.CurrentHP);
            Assert.True(TestKit.LogContains(state, "The substitute took the hit for Holder!"));
        }

        [Fact]
        public void An_eject_button_with_no_bench_stays_put()
        {
            (BattleState state, BattleEngine engine) = TestKit.Duel(375,
                Mon("Holder", TestKit.Move("Tap", power: 1), speed: 1),
                Mon("Foe", TestKit.Move("Slam", power: 60)));

            state.Player1.ActivePokemon.HeldItemId = "Eject Button";
            TestKit.Clash(state, engine);

            Assert.Equal("ejectbutton", state.Player1.ActivePokemon.HeldItemId);
            Assert.False(TestKit.LogContains(state, "Eject Button"));
        }

        [Fact]
        public void A_shed_shell_opens_the_door_shadow_tag_closes()
        {
            PokemonState holder = Mon("Holder", TestKit.Move("Tap", power: 1));
            PokemonState bench = Mon("Bench", TestKit.Move("Tap", power: 1));

            (BattleState state, _) = TestKit.Battle(375,
                new List<PokemonState> { holder, bench },
                new List<PokemonState> { Mon("Trapper", TestKit.Move("Tap", power: 1)) });

            state.Player2.ActivePokemon.AbilityId = "shadowtag";

            Assert.DoesNotContain(LegalActions.For(state, state.Player1), a => a.Type == BattleActionType.Switch);

            holder.HeldItemId = "Shed Shell";

            Assert.Contains(LegalActions.For(state, state.Player1), a => a.Type == BattleActionType.Switch && a.SwitchTarget == bench);

            // Roots are the holder's own doing; the shell does not undo them.
            holder.Rooted = true;

            Assert.DoesNotContain(LegalActions.For(state, state.Player1), a => a.Type == BattleActionType.Switch);
        }

        [Fact]
        public void A_focus_band_holds_on_one_time_in_ten_and_keeps_itself()
        {
            int survived = 0;
            int fainted = 0;

            for (int seed = 1; seed <= 300; seed++)
            {
                (BattleState state, BattleEngine engine) = TestKit.Duel(seed,
                    Mon("Slayer", TestKit.Move("Slam", power: 250), attack: 400),
                    Mon("Holder", TestKit.Move("Tap", power: 1), hp: 60, speed: 1));

                PokemonState holder = state.Player2.ActivePokemon;
                holder.HeldItemId = "Focus Band";

                TestKit.Clash(state, engine);

                bool hungOn = TestKit.LogContains(state, "Holder hung on using its Focus Band!");

                if (holder.Fainted)
                {
                    fainted++;
                    Assert.False(hungOn);
                }
                else
                {
                    survived++;
                    Assert.Equal(1, holder.CurrentHP);
                    Assert.True(hungOn);
                    Assert.Equal("focusband", holder.HeldItemId);
                    Assert.False(holder.LostItem);
                }
            }

            Assert.InRange(survived, 10, 60);
            Assert.True(fainted > 200);
        }

        [Fact]
        public void A_quick_claw_goes_first_one_time_in_five_and_never_without_it()
        {
            int Count(bool withClaw)
            {
                int first = 0;

                for (int seed = 1; seed <= 300; seed++)
                {
                    (BattleState state, BattleEngine engine) = TestKit.Duel(seed,
                        Mon("Slow", TestKit.Move("Slow Tap", power: 1), speed: 1),
                        Mon("Fast", TestKit.Move("Fast Tap", power: 1), speed: 100));

                    if (withClaw)
                        state.Player1.ActivePokemon.HeldItemId = "Quick Claw";

                    TestKit.Clash(state, engine);

                    var lines = state.Log.Lines.ToList();
                    int slow = lines.FindIndex(l => l.Contains("Slow used Slow Tap!"));
                    int fast = lines.FindIndex(l => l.Contains("Fast used Fast Tap!"));
                    bool announced = TestKit.LogContains(state, "Slow's Quick Claw let it move first!");

                    Assert.True(slow >= 0 && fast >= 0);
                    Assert.Equal(announced, slow < fast);

                    if (slow < fast)
                        first++;
                }

                return first;
            }

            Assert.Equal(0, Count(withClaw: false));
            Assert.InRange(Count(withClaw: true), 30, 100);
        }

        [Fact]
        public void A_quick_claw_never_beats_priority()
        {
            int beaten = 0;

            for (int seed = 1; seed <= 100; seed++)
            {
                (BattleState state, BattleEngine engine) = TestKit.Duel(seed,
                    Mon("Slow", TestKit.Move("Slow Tap", power: 1), speed: 1),
                    Mon("Fast", TestKit.Move("Quick Tap", power: 1, priority: 1), speed: 100));

                state.Player1.ActivePokemon.HeldItemId = "Quick Claw";
                TestKit.Clash(state, engine);

                var lines = state.Log.Lines.ToList();
                if (lines.FindIndex(l => l.Contains("Slow used")) < lines.FindIndex(l => l.Contains("Fast used")))
                    beaten++;
            }

            Assert.Equal(0, beaten);
        }

        [Fact]
        public void A_razor_fang_lends_a_flinch_to_a_move_without_one()
        {
            int Count(bool withFang)
            {
                int flinches = 0;

                for (int seed = 1; seed <= 300; seed++)
                {
                    (BattleState state, BattleEngine engine) = TestKit.Duel(seed,
                        Mon("Biter", TestKit.Move("Bite", power: 40)),
                        Mon("Target", TestKit.Move("Tap", power: 1), hp: 1000, speed: 1));

                    if (withFang)
                        state.Player1.ActivePokemon.HeldItemId = "Razor Fang";

                    TestKit.Clash(state, engine);

                    if (TestKit.LogContains(state, "Target flinched!"))
                        flinches++;
                }

                return flinches;
            }

            Assert.Equal(0, Count(withFang: false));
            Assert.InRange(Count(withFang: true), 10, 60);
        }

        [Fact]
        public void A_bright_powder_takes_a_tenth_off_the_moves_aimed_at_its_holder()
        {
            PokemonState mon = Mon("Holder");
            mon.HeldItemId = "Bright Powder";
            Assert.Equal(HeldItems.BrightPowderAccuracy, HeldItems.AccuracyAgainstMultiplier(mon));

            int Count(bool withPowder)
            {
                int misses = 0;

                for (int seed = 1; seed <= 300; seed++)
                {
                    (BattleState state, BattleEngine engine) = TestKit.Duel(seed,
                        Mon("Thrower", TestKit.Move("Throw", power: 40, accuracy: 100)),
                        Mon("Holder", TestKit.Move("Tap", power: 1), hp: 1000, speed: 1));

                    if (withPowder)
                        state.Player2.ActivePokemon.HeldItemId = "Bright Powder";

                    TestKit.Clash(state, engine);

                    if (TestKit.LogContains(state, "The attack missed!"))
                        misses++;
                }

                return misses;
            }

            Assert.Equal(0, Count(withPowder: false));
            Assert.InRange(Count(withPowder: true), 10, 60);
        }
    }
}
