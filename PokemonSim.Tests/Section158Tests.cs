using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Abilities;
using PokemonSim.Models;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>Section 158: the move-completion machinery - screens,
    /// Rest and Sleep Talk, the counter family, the volatile batch, and
    /// the field states.</summary>
    public class MoveMachineryTests158
    {
        static void Give(BattleState state, PokemonState pokemon, string ability)
        {
            pokemon.AbilityId = ability;
            AbilityFactory.Restore(pokemon, state);
        }

        static MoveState WithEffects(MoveState move, params string[] effects)
        {
            move.Effects = effects.ToList();
            return move;
        }

        /// <summary>Screens let crits through, so the halving comparisons
        /// need a seed whose opening exchange does not crit.</summary>
        static int CritlessSeed(MoveCategory category)
        {
            for (int seed = 1; seed < 500; seed++)
            {
                var (probe, probeEngine) = Duel(seed,
                    Mon("Hitter", moves: Move("Probe", category: category)),
                    Mon("Wall", speed: 1));

                Clash(probe, probeEngine);

                if (!LogContains(probe, "critical hit"))
                    return seed;
            }

            throw new InvalidOperationException("no critless seed found");
        }

        [Fact]
        public void Reflect_HalvesPhysicalDamage_OnTheSameRolls()
        {
            int seed = CritlessSeed(MoveCategory.Physical);

            var (plain, plainEngine) = Duel(seed, Mon("Hitter", moves: Move("Probe")), Mon("Wall", speed: 1));
            Clash(plain, plainEngine);
            int unscreened = plain.Player2.ActivePokemon.MaxHP - plain.Player2.ActivePokemon.CurrentHP;

            var (walled, walledEngine) = Duel(seed, Mon("Hitter", moves: Move("Probe")), Mon("Wall", speed: 1));
            walled.ReflectTurnsP2 = 5;
            Clash(walled, walledEngine);
            int screened = walled.Player2.ActivePokemon.MaxHP - walled.Player2.ActivePokemon.CurrentHP;

            Assert.True(unscreened > 0);
            Assert.Equal(unscreened / 2, screened);
        }

        [Fact]
        public void LightScreen_HalvesSpecialOnly()
        {
            int seed = CritlessSeed(MoveCategory.Special);

            var (plain, plainEngine) = Duel(seed, Mon("Hitter", moves: Move("Probe", category: MoveCategory.Special)), Mon("Wall", speed: 1));
            Clash(plain, plainEngine);
            int unscreened = plain.Player2.ActivePokemon.MaxHP - plain.Player2.ActivePokemon.CurrentHP;

            var (walled, walledEngine) = Duel(seed, Mon("Hitter", moves: Move("Probe", category: MoveCategory.Special)), Mon("Wall", speed: 1));
            walled.LightScreenTurnsP2 = 5;
            Clash(walled, walledEngine);
            int screened = walled.Player2.ActivePokemon.MaxHP - walled.Player2.ActivePokemon.CurrentHP;

            Assert.Equal(unscreened / 2, screened);
        }

        [Fact]
        public void Infiltrator_IgnoresTheScreen()
        {
            int seed = CritlessSeed(MoveCategory.Physical);

            var (plain, plainEngine) = Duel(seed, Mon("Hitter", moves: Move("Probe")), Mon("Wall", speed: 1));
            Clash(plain, plainEngine);
            int unscreened = plain.Player2.ActivePokemon.MaxHP - plain.Player2.ActivePokemon.CurrentHP;

            var (walled, walledEngine) = Duel(seed, Mon("Hitter", moves: Move("Probe")), Mon("Wall", speed: 1));
            walled.ReflectTurnsP2 = 5;
            Give(walled, walled.Player1.ActivePokemon, "infiltrator");
            Clash(walled, walledEngine);
            int through = walled.Player2.ActivePokemon.MaxHP - walled.Player2.ActivePokemon.CurrentHP;

            Assert.Equal(unscreened, through);
        }

        [Fact]
        public void Tailwind_DoublesTheSidesSpeed()
        {
            var (state, _) = Duel(7, Mon("Runner", speed: 100), Mon("Other"));

            double before = StatResolver.GetStat(state, state.Player1.ActivePokemon, "Speed");
            state.TailwindTurnsP1 = 4;
            double after = StatResolver.GetStat(state, state.Player1.ActivePokemon, "Speed");

            Assert.Equal(before * 2, after);
        }

        [Fact]
        public void TrickRoom_LetsTheSlowerActFirst()
        {
            var slow = Mon("Slowpoke", speed: 10);
            var fast = Mon("Zippy", speed: 200);

            var (state, engine) = Duel(11, slow, fast);
            state.TrickRoomTurns = 5;

            Clash(state, engine);

            var lines = state.Log.Lines.ToList();

            int slowLine = lines.FindIndex(l => l.Contains("Slowpoke used"));
            int fastLine = lines.FindIndex(l => l.Contains("Zippy used"));

            Assert.True(slowLine >= 0 && (fastLine < 0 || slowLine < fastLine),
                "the slower Pokemon should have moved first under Trick Room");
        }

        [Fact]
        public void Rest_FullHealsAndSleepsTwoTurns()
        {
            var rest = WithEffects(Move("Rest", category: MoveCategory.Status, power: 0), "Rest");
            var sleeper = Mon("Napper", hp: 300, moves: rest);
            var (state, engine) = Duel(3, sleeper, Mon("Watcher", speed: 1));

            sleeper.CurrentHP = 50;
            engine.RunTurn(
                MoveAction(state, sleeper, rest),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.True(sleeper.CurrentHP > 250, "Rest should have healed before the slower hit landed");
            Assert.Equal(StatusCondition.Sleep, sleeper.Status);
            Assert.Equal(2, sleeper.SleepTurns);
        }

        [Fact]
        public void SleepTalk_ActsFromWithinSleep()
        {
            var talk = WithEffects(Move("Sleep Talk", category: MoveCategory.Status, power: 0), "SleepTalk");
            var strike = Move("Strike");
            var sleeper = Mon("Napper", moves: new[] { talk, strike });
            var target = Mon("Target", speed: 1);

            var (state, engine) = Duel(5, sleeper, target);
            sleeper.Status = StatusCondition.Sleep;
            sleeper.SleepTurns = 2;

            engine.RunTurn(
                MoveAction(state, sleeper, talk),
                MoveAction(state, target, target.Moves[0]));

            Assert.True(LogContains(state, "used Sleep Talk"));
            Assert.True(target.CurrentHP < target.MaxHP, "the called move should have hit");
            Assert.Equal(StatusCondition.Sleep, sleeper.Status);
        }

        [Fact]
        public void ConfuseRay_SetsTheConfusionClock()
        {
            var ray = WithEffects(Move("Confuse Ray", category: MoveCategory.Status, power: 0, accuracy: 100), "InflictConfusion");
            var (state, engine) = Duel(9, Mon("Ghost", moves: ray), Mon("Victim", speed: 1));

            Clash(state, engine);

            int turns = state.Player2.ActivePokemon.ConfusionTurns;
            Assert.InRange(turns, 1, 5);
            Assert.True(LogContains(state, "became confused"));
        }

        [Fact]
        public void LeechSeed_DrainsIntoTheOpposingActive()
        {
            var seed = WithEffects(Move("Leech Seed", category: MoveCategory.Status, power: 0, accuracy: 100), "LeechSeed");
            var seeder = Mon("Seeder", moves: seed);
            var victim = Mon("Victim", type: PokemonType.Water, hp: 160, speed: 1,
                moves: Move("Wait", category: MoveCategory.Status, power: 0));

            var (state, engine) = Duel(13, seeder, victim);
            seeder.CurrentHP = 100;

            Clash(state, engine);

            Assert.True(victim.LeechSeeded);
            Assert.Equal(160 - 160 / 8, victim.CurrentHP);
            Assert.Equal(100 + 160 / 8, seeder.CurrentHP);
        }

        [Fact]
        public void Yawn_PutsTheTargetToSleepATurnLater()
        {
            var yawn = WithEffects(Move("Yawn", category: MoveCategory.Status, power: 0), "Yawn");
            var wait = Move("Wait", category: MoveCategory.Status, power: 0);
            var (state, engine) = Duel(17, Mon("Yawner", moves: yawn), Mon("Drowsy", speed: 1, moves: wait));

            Clash(state, engine);
            Assert.Equal(StatusCondition.None, state.Player2.ActivePokemon.Status);

            Clash(state, engine);
            Assert.Equal(StatusCondition.Sleep, state.Player2.ActivePokemon.Status);
        }

        [Fact]
        public void Encore_LocksTheLegalActionsToOneMove()
        {
            var strike = Move("Strike");
            var guard = Move("Guard", category: MoveCategory.Status, power: 0);
            var victim = Mon("Victim", moves: new[] { strike, guard });
            var encore = WithEffects(Move("Encore", category: MoveCategory.Status, power: 0, accuracy: 100), "Encore");

            var (state, engine) = Duel(19, Mon("Fan", speed: 1, moves: encore), victim);

            // The victim (faster) strikes; the encore lands after.
            Clash(state, engine);

            var legal = engine.GetLegalActions(state.Player2)
                .Where(a => a.Move != null)
                .Select(a => a.Move!.Name)
                .ToList();

            Assert.Equal(new List<string> { "Strike" }, legal);
        }

        [Fact]
        public void Taunt_FiltersStatusMoves()
        {
            var victim = Mon("Victim", moves: new[]
            {
                Move("Strike"),
                Move("Plot", category: MoveCategory.Status, power: 0)
            });

            var (state, engine) = Duel(23, Mon("Bully"), victim);
            victim.TauntTurns = 2;

            var names = engine.GetLegalActions(state.Player2)
                .Where(a => a.Move != null)
                .Select(a => a.Move!.Name)
                .ToList();

            Assert.Contains("Strike", names);
            Assert.DoesNotContain("Plot", names);
        }

        [Fact]
        public void Counter_ReturnsDoubleThePhysicalDamage()
        {
            var counter = WithEffects(Move("Counter", type: PokemonType.Fighting, power: 1, accuracy: 100, priority: -5), "Counter");
            var puncher = Mon("Puncher", hp: 400);
            var counterer = Mon("Counterer", hp: 400, speed: 1, moves: counter);

            var (state, engine) = Duel(29, puncher, counterer);
            Clash(state, engine);

            int taken = 400 - counterer.CurrentHP;
            int returned = 400 - puncher.CurrentHP;

            Assert.True(taken > 0, "the physical hit should land first");
            Assert.Equal(taken * 2, returned);
        }

        [Fact]
        public void SuperFang_HalvesCurrentHp()
        {
            var fang = WithEffects(Move("Super Fang", power: 1, accuracy: 0), "SuperFang");
            var (state, engine) = Duel(31, Mon("Rat", moves: fang),
                Mon("Tank", hp: 300, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            Clash(state, engine);

            Assert.Equal(150, state.Player2.ActivePokemon.CurrentHP);
        }

        [Fact]
        public void PainSplit_AveragesTheTwoSides()
        {
            var split = WithEffects(Move("Pain Split", category: MoveCategory.Status, power: 0), "PainSplit");
            var hurt = Mon("Hurt", hp: 200, moves: split);
            var whole = Mon("Whole", hp: 200, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0));

            var (state, engine) = Duel(37, hurt, whole);
            hurt.CurrentHP = 20;

            Clash(state, engine);

            Assert.Equal(110, hurt.CurrentHP);
            Assert.Equal(110, whole.CurrentHP);
        }

        [Fact]
        public void BellyDrum_TradesHalfTheHpForMaxAttack()
        {
            var drum = WithEffects(Move("Belly Drum", category: MoveCategory.Status, power: 0), "BellyDrum");
            var drummer = Mon("Drummer", hp: 200, moves: drum);
            var (state, engine) = Duel(43, drummer, Mon("Watcher", speed: 1,
                moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            Clash(state, engine);

            Assert.Equal(100, drummer.CurrentHP);
            Assert.Equal(6, drummer.AttackStage);
        }

        [Fact]
        public void DestinyBond_TakesTheKillerDown()
        {
            var bond = WithEffects(Move("Destiny Bond", category: MoveCategory.Status, power: 0, priority: 1), "DestinyBond");
            var martyr = Mon("Martyr", hp: 10, moves: bond);
            var killer = Mon("Killer", attack: 400, speed: 1);

            var (state, engine) = Duel(47, martyr, killer);
            Clash(state, engine);

            Assert.True(martyr.Fainted, "the martyr should have been knocked out");
            Assert.True(killer.Fainted, "Destiny Bond should have taken the killer down too");
        }

        [Fact]
        public void Roar_DragsARandomTeammateIn()
        {
            var roar = WithEffects(Move("Roar", category: MoveCategory.Status, power: 0, priority: -6), "ForcedSwitch");
            var lead = Mon("Lead", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0));
            var bench = Mon("Bench");

            var (state, engine) = Battle(53,
                new List<PokemonState> { Mon("Roarer", moves: roar) },
                new List<PokemonState> { lead, bench });

            Clash(state, engine);

            Assert.Same(bench, state.Player2.ActivePokemon);
            Assert.True(LogContains(state, "dragged out"));
        }

        [Fact]
        public void PerishSong_FaintsBothInThreeTurns()
        {
            var song = WithEffects(Move("Perish Song", category: MoveCategory.Status, power: 0), "PerishSong");
            var wait = Move("Wait", category: MoveCategory.Status, power: 0);
            var (state, engine) = Duel(59, Mon("Singer", moves: song), Mon("Listener", speed: 1, moves: wait));

            Clash(state, engine);
            Assert.Equal(2, state.Player1.ActivePokemon.PerishCount);

            Clash(state, engine);
            Clash(state, engine);
            Clash(state, engine);

            Assert.True(state.Player1.ActivePokemon.Fainted);
            Assert.True(state.Player2.ActivePokemon.Fainted);
            Assert.Equal(BattleOutcome.Draw, state.Outcome);
        }

        [Fact]
        public void Ohko_KillsOutright_ButSturdyShrugsItOff()
        {
            var cold = WithEffects(Move("Sheer Cold", type: PokemonType.Ice, category: MoveCategory.Special, power: 1, accuracy: 0), "Ohko");

            var (plain, plainEngine) = Duel(61, Mon("Glacier", moves: cold), Mon("Soft", speed: 1));
            Clash(plain, plainEngine);
            Assert.True(plain.Player2.ActivePokemon.Fainted);

            var (guarded, guardedEngine) = Duel(61, Mon("Glacier", moves: WithEffects(Move("Sheer Cold", type: PokemonType.Ice, category: MoveCategory.Special, power: 1, accuracy: 0), "Ohko")), Mon("Rock", speed: 1));
            Give(guarded, guarded.Player2.ActivePokemon, "sturdy");
            Clash(guarded, guardedEngine);

            Assert.False(guarded.Player2.ActivePokemon.Fainted);
            Assert.True(LogContains(guarded, "protected by Sturdy"));
        }

        [Fact]
        public void Endure_HangsOnAtOneHp()
        {
            var endure = WithEffects(Move("Endure", category: MoveCategory.Status, power: 0, priority: 4), "Endure");
            var survivor = Mon("Survivor", hp: 60, moves: endure);
            var crusher = Mon("Crusher", attack: 500, speed: 1);

            var (state, engine) = Duel(67, survivor, crusher);
            Clash(state, engine);

            Assert.Equal(1, survivor.CurrentHP);
            Assert.True(LogContains(state, "endured the hit"));
        }

        [Fact]
        public void KingsShield_PunishesContact_AndLetsStatusThrough()
        {
            var shield = WithEffects(Move("King's Shield", category: MoveCategory.Status, power: 0, priority: 4), "KingsShield");
            var blade = Mon("Blade", moves: shield);
            var contact = Move("Slam");
            contact.IsContact = true;
            var puncher = Mon("Puncher", speed: 1, moves: contact);

            var (state, engine) = Duel(71, blade, puncher);
            Clash(state, engine);

            Assert.Equal(blade.MaxHP, blade.CurrentHP);
            Assert.Equal(-2, puncher.AttackStage);
        }

        [Fact]
        public void BatonPass_HandsTheBoostsOver()
        {
            var pass = WithEffects(Move("Baton Pass", category: MoveCategory.Status, power: 0), "BatonPass");
            var runner = Mon("Runner", moves: pass);
            var heir = Mon("Heir");

            var (state, engine) = Battle(73,
                new List<PokemonState> { runner, heir },
                new List<PokemonState> { Mon("Watcher", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)) });

            runner.SpAttackStage = 4;
            Clash(state, engine);

            Assert.Same(heir, state.Player1.ActivePokemon);
            Assert.Equal(4, heir.SpAttackStage);
        }

        [Fact]
        public void WeatherBall_TakesTheWeathersType()
        {
            var ball = WithEffects(Move("Weather Ball", category: MoveCategory.Special, power: 50, accuracy: 100), "WeatherBall");
            var (state, engine) = Duel(79, Mon("Caster", moves: ball), Mon("Target", speed: 1));

            state.Environment.Weather = WeatherType.Rain;
            state.Environment.WeatherTurns = 5;

            Clash(state, engine);

            Assert.Equal(PokemonType.Water, ball.Type);
            Assert.Equal(100, ball.Power);
        }

        [Fact]
        public void StoredPower_ScalesWithPositiveStages()
        {
            var stored = WithEffects(Move("Stored Power", category: MoveCategory.Special, power: 20, accuracy: 100), "StoredPower");
            var (plain, plainEngine) = Duel(83, Mon("Psion", moves: stored), Mon("Target", hp: 800, speed: 1));
            Clash(plain, plainEngine);
            int baseline = 800 - plain.Player2.ActivePokemon.CurrentHP;

            var boosted = WithEffects(Move("Stored Power", category: MoveCategory.Special, power: 20, accuracy: 100), "StoredPower");
            var (state, engine) = Duel(83, Mon("Psion", moves: boosted), Mon("Target", hp: 800, speed: 1));
            state.Player1.ActivePokemon.SpeedStage = 6;   // does not touch special damage
            Clash(state, engine);
            int scaled = 800 - state.Player2.ActivePokemon.CurrentHP;

            Assert.Equal(baseline * 7, scaled);
        }

        [Fact]
        public void BodyPress_SwingsWithTheUsersDefense()
        {
            var press = Move("Body Press", type: PokemonType.Fighting, power: 80, accuracy: 100);
            press.UsesDefenseAsOffense = true;

            var weakArms = Mon("Presser", attack: 10, defense: 300, moves: press);
            var (state, engine) = Duel(89, weakArms, Mon("Target", hp: 500, speed: 1));

            Clash(state, engine);

            int dealt = 500 - state.Player2.ActivePokemon.CurrentHP;
            Assert.True(dealt > 100, $"Body Press should have hit off 300 Defense, dealt {dealt}");
        }

        [Fact]
        public void Gravity_GroundsALevitator()
        {
            var (state, _) = Duel(91, Mon("Floaty"), Mon("Other"));
            var floaty = state.Player1.ActivePokemon;
            Give(state, floaty, "levitate");

            Assert.False(Grounding.IsGrounded(state, floaty));

            state.GravityTurns = 5;
            Assert.True(Grounding.IsGrounded(state, floaty));
        }

        [Fact]
        public void Imprison_SealsSharedMoves()
        {
            var jailer = Mon("Jailer", moves: new[] { Move("Shared"), Move("Own") });
            var victim = Mon("Victim", moves: new[] { Move("Shared"), Move("Different") });

            var (state, engine) = Duel(101, jailer, victim);
            jailer.ImprisonActive = true;

            var names = engine.GetLegalActions(state.Player2)
                .Where(a => a.Move != null)
                .Select(a => a.Move!.Name)
                .ToList();

            Assert.DoesNotContain("Shared", names);
            Assert.Contains("Different", names);
        }
    }

    /// <summary>Section 158: the ability completion - the full catalog and
    /// a behaviour check for each mechanism family.</summary>
    public class AbilityTests158
    {
        static void Give(BattleState state, PokemonState pokemon, string ability)
        {
            pokemon.AbilityId = ability;
            AbilityFactory.Restore(pokemon, state);
        }

        [Fact]
        public void EveryCatalogAndBossAbility_IsSupported()
        {
            // The whole species-catalog pool plus the §158 boss-file batch.
            foreach (string name in new[]
            {
                "Bad Dreams", "Clear Body", "Compound Eyes", "Contrary", "Flame Body",
                "Good as Gold", "Infiltrator", "Libero", "Magic Bounce", "Mega Launcher",
                "Multiscale", "Poison Heal", "Pressure", "Protosynthesis", "Purifying Salt",
                "Quark Drive", "Rain Dish", "Reckless", "Sand Rush", "Shed Skin",
                "Sheer Force", "Solar Power", "Tinted Lens", "Tough Claws", "Toxic Debris",
                "Unaware", "Weak Armor", "Serene Grace", "Sturdy", "Analytic", "Iron Fist",
                "Natural Cure", "Water Absorb", "Justified", "Speed Boost", "Inner Focus",
                "Poison Touch", "Sap Sipper", "Sand Force", "Pixilate", "Storm Drain",
                "Turboblaze", "Lightning Rod", "Rough Skin", "Scrappy", "Insomnia",
                "Cursed Body", "No Guard", "Synchronize", "Protean", "Iron Barbs",
                "Teravolt", "Skill Link", "Fairy Aura", "Own Tempo", "Mummy",
                "Adaptability", "Aerilate", "Magnet Pull", "Vital Spirit", "Steadfast",
                "Download", "Leaf Guard", "Unnerve", "Dry Skin", "Defiant", "Poison Point",
                "Shadow Tag", "Static", "Slush Rush", "Minus", "Swarm", "Trace",
                "Competitive", "Effect Spore", "Filter", "Snow Cloak", "Victory Star",
                "Stance Change", "Multitype", "Truant", "Pure Power", "Aftermath",
                "Shell Armor", "Pick Up", "Slow Start", "Run Away", "Super Luck",
                "Rock Head", "Oblivious", "Air Lock", "Hydration", "Cheek Pouch",
                "Unburden", "Soundproof", "Quick Feet", "Flare Boost", "Sniper",
                "Flower Veil"
            })
            {
                Assert.True(AbilityFactory.IsSupported(name), $"{name} should be supported");
                Assert.True(AbilityFactory.TryCreate(name, out var ability), $"{name} should construct");
                Assert.Equal(AbilityFactory.Normalize(name), ability.Id);
            }

            Assert.False(AbilityFactory.IsSupported("Wonder Guard"));
        }

        [Fact]
        public void ToughClaws_BoostsContactByThirtyPercent()
        {
            var contact = Move("Claw");
            contact.IsContact = true;

            var (plain, plainEngine) = Duel(103, Mon("Cat", moves: contact), Mon("Target", hp: 600, speed: 1));
            Clash(plain, plainEngine);
            int baseline = 600 - plain.Player2.ActivePokemon.CurrentHP;

            var clawed = Move("Claw");
            clawed.IsContact = true;
            var (state, engine) = Duel(103, Mon("Cat", moves: clawed), Mon("Target", hp: 600, speed: 1));
            Give(state, state.Player1.ActivePokemon, "toughclaws");
            Clash(state, engine);
            int boosted = 600 - state.Player2.ActivePokemon.CurrentHP;

            Assert.Equal((int)(baseline * 1.3), boosted);
        }

        [Fact]
        public void Multiscale_HalvesTheFirstHit()
        {
            var (plain, plainEngine) = Duel(107, Mon("Hitter"), Mon("Dragon", hp: 600, speed: 1));
            Clash(plain, plainEngine);
            int baseline = 600 - plain.Player2.ActivePokemon.CurrentHP;

            var (state, engine) = Duel(107, Mon("Hitter"), Mon("Dragon", hp: 600, speed: 1));
            Give(state, state.Player2.ActivePokemon, "multiscale");
            Clash(state, engine);
            int reduced = 600 - state.Player2.ActivePokemon.CurrentHP;

            Assert.Equal(baseline / 2, reduced);
        }

        [Fact]
        public void Unaware_IgnoresTheAttackersBoosts()
        {
            var (plain, plainEngine) = Duel(109, Mon("Hitter"), Mon("Wall", hp: 600, speed: 1));
            Clash(plain, plainEngine);
            int unboosted = 600 - plain.Player2.ActivePokemon.CurrentHP;

            var (state, engine) = Duel(109, Mon("Hitter"), Mon("Wall", hp: 600, speed: 1));
            state.Player1.ActivePokemon.AttackStage = 6;
            Give(state, state.Player2.ActivePokemon, "unaware");
            Clash(state, engine);
            int against = 600 - state.Player2.ActivePokemon.CurrentHP;

            Assert.Equal(unboosted, against);
        }

        [Fact]
        public void Contrary_FlipsIntimidatesDrop()
        {
            var (state, _) = Duel(113, Mon("Bully"), Mon("Snake"));
            var snake = state.Player2.ActivePokemon;
            Give(state, snake, "contrary");

            // Intimidate's entry drop rides the guarded opponent-sourced
            // path (ApplyStatChangeAgainst is internal to the engine).
            new IntimidateAbility().OnAttach(state.Player1.ActivePokemon, state);

            Assert.Equal(1, snake.AttackStage);
        }

        [Fact]
        public void ClearBody_RefusesTheDrop_AndDefiantPunishesIt()
        {
            var (state, _) = Duel(127, Mon("Bully"), Mon("Steelix"));
            var target = state.Player2.ActivePokemon;

            Give(state, target, "clearbody");
            new IntimidateAbility().OnAttach(state.Player1.ActivePokemon, state);
            Assert.Equal(0, target.AttackStage);

            Give(state, target, "defiant");
            new IntimidateAbility().OnAttach(state.Player1.ActivePokemon, state);
            Assert.Equal(1, target.AttackStage);   // -1 from the drop, +2 from Defiant
        }

        [Fact]
        public void SheerForce_TradesTheSecondaryForPower()
        {
            // Seed 131 procs the 30% burn without Sheer Force.
            int burnSeed = -1;
            for (int seed = 1; seed < 200; seed++)
            {
                var ember = Move("Singe", type: PokemonType.Fire, category: MoveCategory.Special);
                ember.SecondaryChance = 0.3;
                ember.InflictStatus = StatusCondition.Burn;

                var (probe, probeEngine) = Duel(seed, Mon("Torch", moves: ember), Mon("Log", type: PokemonType.Grass, hp: 600, speed: 1));
                Clash(probe, probeEngine);

                if (probe.Player2.ActivePokemon.Status == StatusCondition.Burn)
                {
                    burnSeed = seed;
                    break;
                }
            }

            Assert.True(burnSeed > 0, "some seed should proc the burn");

            var forced = Move("Singe", type: PokemonType.Fire, category: MoveCategory.Special);
            forced.SecondaryChance = 0.3;
            forced.InflictStatus = StatusCondition.Burn;

            var (state, engine) = Duel(burnSeed, Mon("Torch", moves: forced), Mon("Log", type: PokemonType.Grass, hp: 600, speed: 1));
            Give(state, state.Player1.ActivePokemon, "sheerforce");
            Clash(state, engine);

            Assert.Equal(StatusCondition.None, state.Player2.ActivePokemon.Status);
        }

        [Fact]
        public void Pressure_DrainsAnExtraPp()
        {
            var strike = Move("Strike", pp: 10);
            var (state, engine) = Duel(137, Mon("Hitter", moves: strike), Mon("Wailer", speed: 1));
            Give(state, state.Player2.ActivePokemon, "pressure");

            Clash(state, engine);

            Assert.Equal(8, strike.CurrentPP);
        }

        [Fact]
        public void MagicBounce_ReflectsTheStatusMove()
        {
            var wave = Move("Zap Wave", type: PokemonType.Electric, category: MoveCategory.Status, power: 0, accuracy: 100);
            wave.InflictStatus = StatusCondition.Paralysis;

            var (state, engine) = Duel(139, Mon("Caster", moves: wave), Mon("Mirror", speed: 1));
            Give(state, state.Player2.ActivePokemon, "magicbounce");

            Clash(state, engine);

            Assert.Equal(StatusCondition.Paralysis, state.Player1.ActivePokemon.Status);
            Assert.Equal(StatusCondition.None, state.Player2.ActivePokemon.Status);
        }

        [Fact]
        public void WaterAbsorb_HealsInsteadOfHurting()
        {
            var jet = Move("Jet", type: PokemonType.Water, category: MoveCategory.Special);
            var (state, engine) = Duel(149, Mon("Squirt", moves: jet), Mon("Sponge", speed: 1));
            var sponge = state.Player2.ActivePokemon;
            Give(state, sponge, "waterabsorb");
            sponge.CurrentHP = 100;

            Clash(state, engine);

            Assert.Equal(100 + sponge.MaxHP / 4, sponge.CurrentHP);
        }

        [Fact]
        public void PoisonHeal_TurnsPoisonIntoRecovery()
        {
            var wait = Move("Wait", category: MoveCategory.Status, power: 0);
            var (state, engine) = Duel(151, Mon("Idler", moves: wait), Mon("Gliscor", speed: 1, moves: Move("Rest2", category: MoveCategory.Status, power: 0)));
            var gliscor = state.Player2.ActivePokemon;
            Give(state, gliscor, "poisonheal");
            gliscor.Status = StatusCondition.Poison;
            gliscor.CurrentHP = 100;

            Clash(state, engine);

            Assert.Equal(100 + gliscor.MaxHP / 8, gliscor.CurrentHP);
        }

        [Fact]
        public void SpeedBoost_ClimbsEachTurn()
        {
            var wait = Move("Wait", category: MoveCategory.Status, power: 0);
            var (state, engine) = Duel(157, Mon("Ninja", moves: wait), Mon("Other", speed: 1, moves: Move("Wait2", category: MoveCategory.Status, power: 0)));
            Give(state, state.Player1.ActivePokemon, "speedboost");

            Clash(state, engine);
            Clash(state, engine);

            Assert.Equal(2, state.Player1.ActivePokemon.SpeedStage);
        }

        [Fact]
        public void BadDreams_TormentsTheSleeper()
        {
            var wait = Move("Wait", category: MoveCategory.Status, power: 0);
            var (state, engine) = Duel(163, Mon("Darkrai", moves: wait), Mon("Sleeper", hp: 160, speed: 1, moves: Move("Snooze", category: MoveCategory.Status, power: 0)));
            Give(state, state.Player1.ActivePokemon, "baddreams");
            var sleeper = state.Player2.ActivePokemon;
            sleeper.Status = StatusCondition.Sleep;
            sleeper.SleepTurns = 3;

            Clash(state, engine);

            Assert.Equal(160 - 160 / 8, sleeper.CurrentHP);
        }

        [Fact]
        public void ShadowTag_PinsTheOpponentInPlace()
        {
            var runner = Mon("Runner");
            var bench = Mon("Bench");

            var (state, engine) = Battle(167,
                new List<PokemonState> { Mon("Wobbuffet") },
                new List<PokemonState> { runner, bench });

            Give(state, state.Player1.ActivePokemon, "shadowtag");

            var actions = engine.GetLegalActions(state.Player2);

            Assert.DoesNotContain(actions, a => a.Type == BattleActionType.Switch);
        }

        [Fact]
        public void Truant_LoafsEveryOtherTurn()
        {
            var (state, engine) = Duel(173, Mon("Slaking", hp: 800), Mon("Punchbag", hp: 800, speed: 1,
                moves: Move("Wait", category: MoveCategory.Status, power: 0)));
            Give(state, state.Player1.ActivePokemon, "truant");

            Clash(state, engine);
            Clash(state, engine);

            Assert.True(LogContains(state, "loafing around"));
            int hits = state.Log.Lines.Count(l => l.Contains("Slaking used"));
            Assert.Equal(1, hits);
        }

        [Fact]
        public void NaturalCure_HealsOnTheWayOut()
        {
            var sick = Mon("Sick");
            var healthy = Mon("Healthy");

            var (state, engine) = Battle(179,
                new List<PokemonState> { sick, healthy },
                new List<PokemonState> { Mon("Watcher", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)) });

            Give(state, sick, "naturalcure");
            sick.Status = StatusCondition.Poison;

            engine.RunTurn(
                SwitchAction(state, sick, healthy),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.Equal(StatusCondition.None, sick.Status);
        }

        [Fact]
        public void MoveFlags_ClassifyTheCompletedData()
        {
            MoveDex.EnsureLoaded();

            Assert.True(MoveDex.TryGet("Fire Punch", out var punch));
            Assert.True(punch.IsContact);
            Assert.True(punch.IsPunch);

            Assert.True(MoveDex.TryGet("Earthquake", out var quake));
            Assert.False(quake.IsContact);
            Assert.True(quake.IsSpread);

            Assert.True(MoveDex.TryGet("Grass Knot", out var knot));
            Assert.True(knot.IsContact);

            Assert.True(MoveDex.TryGet("Dark Pulse", out var pulse));
            Assert.True(pulse.IsPulse);
            Assert.False(pulse.IsContact);

            // The §158 data additions load with their machinery attached.
            Assert.True(MoveDex.TryGet("Sheer Cold", out var cold));
            Assert.Contains("Ohko", cold.Effects!);

            Assert.True(MoveDex.TryGet("Rest", out var rest));
            Assert.Contains("Rest", rest.Effects!);

            Assert.True(MoveDex.TryGet("Trick Room", out var room));
            Assert.Equal(-7, room.Priority);

            Assert.True(MoveDex.TryGet("Body Press", out var press));
            Assert.True(press.UsesDefenseAsOffense);

            Assert.True(MoveDex.TryGet("Foul Play", out var foul));
            Assert.True(foul.UsesTargetAttack);

            Assert.True(MoveDex.TryGet("Tera Blast", out var tera));
            Assert.True(tera.UsesHigherOffense);
        }

        [Fact]
        public void EveryMoveFlagListName_ExistsInTheData()
        {
            MoveDex.EnsureLoaded();

            foreach (string name in MoveFlags.AllListedNames())
            {
                Assert.True(MoveDex.TryGet(name, out _),
                    $"MoveFlags lists \"{name}\" but moves.json has no such move");
            }
        }
    }
}