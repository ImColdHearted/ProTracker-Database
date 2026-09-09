using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Engine;
using PokemonSim.Models;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    public class CoreBattleTests
    {
        [Fact]
        public void BasicDamage_ReducesHpAndIsLogged()
        {
            var (state, engine) = Duel(1,
                Mon("Hitter", moves: Move("Slam", power: 50)),
                Mon("Target", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            Clash(state, engine);

            var target = state.Player2.ActivePokemon;

            Assert.True(target.CurrentHP < target.MaxHP);
            Assert.True(LogContains(state, "Hitter used Slam!"));
            Assert.True(LogContains(state, "lost"));

            // Equal attack/defense, power 50, level 50: base 24 before
            // variance/crit - the applied damage stays in a tight band.
            int dealt = target.MaxHP - target.CurrentHP;
            Assert.InRange(dealt, 15, 40);
        }

        [Fact]
        public void SeededBattle_IsExactlyReproducible()
        {
            static (BattleState, List<string>) Run()
            {
                var (state, engine) = Duel(777,
                    Mon("A", attack: 130, moves: Move("Hit A", power: 60, accuracy: 90)),
                    Mon("B", attack: 130, moves: Move("Hit B", power: 60, accuracy: 90)));

                for (int i = 0; i < 12 && state.Outcome == BattleOutcome.Unfinished; i++)
                    Clash(state, engine);

                return (state, state.Log.Lines.ToList());
            }

            var (state1, log1) = Run();
            var (state2, log2) = Run();

            Assert.Equal(log1, log2);
            Assert.Equal(state1.Player1.ActivePokemon.CurrentHP, state2.Player1.ActivePokemon.CurrentHP);
            Assert.Equal(state1.Player2.ActivePokemon.CurrentHP, state2.Player2.ActivePokemon.CurrentHP);
        }

        [Fact]
        public void Accuracy_MissesAndHitsBothHappen()
        {
            bool sawMiss = false, sawHit = false;

            for (int seed = 0; seed < 40 && (!sawMiss || !sawHit); seed++)
            {
                var (state, engine) = Duel(seed,
                    Mon("Gunner", moves: Move("Wild Swing", power: 40, accuracy: 50)),
                    Mon("Dodger", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

                Clash(state, engine);

                if (LogContains(state, "The attack missed!")) sawMiss = true;
                if (LogContains(state, "lost")) sawHit = true;
            }

            Assert.True(sawMiss, "a 50-accuracy move never missed across 40 seeds");
            Assert.True(sawHit, "a 50-accuracy move never hit across 40 seeds");
        }

        [Fact]
        public void SureHitMoves_NeverMiss()
        {
            var (state, engine) = Duel(3,
                Mon("Sniper", hp: 5000, moves: Move("Aim", power: 10, accuracy: 0)),
                Mon("Ghost", hp: 5000, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            state.Player2.ActivePokemon.EvasionStage = 6;

            for (int i = 0; i < 20; i++)
                Clash(state, engine);

            Assert.False(LogContains(state, "missed"));
        }

        [Fact]
        public void Priority_BeatsSpeed()
        {
            var slow = Mon("Slowpoke", speed: 5, moves: Move("Sucker Jab", power: 40, priority: 1));
            var fast = Mon("Speedy", speed: 200, moves: Move("Plain Hit", power: 40));

            var (state, engine) = Duel(9, slow, fast);

            Clash(state, engine);

            int jabIndex = state.Log.Lines.ToList().FindIndex(l => l.Contains("Sucker Jab"));
            int plainIndex = state.Log.Lines.ToList().FindIndex(l => l.Contains("Plain Hit"));

            Assert.True(jabIndex >= 0 && plainIndex >= 0);
            Assert.True(jabIndex < plainIndex, "the +1 priority move should act before the faster Pokemon");
        }

        [Fact]
        public void SpeedOrder_FasterActsFirst()
        {
            var fast = Mon("Speedy", speed: 200, moves: Move("Fast Hit", power: 10));
            var slow = Mon("Slowpoke", speed: 5, moves: Move("Slow Hit", power: 10));

            var (state, engine) = Duel(11, fast, slow);

            Clash(state, engine);

            var lines = state.Log.Lines.ToList();
            Assert.True(lines.FindIndex(l => l.Contains("Fast Hit")) < lines.FindIndex(l => l.Contains("Slow Hit")));
        }

        [Fact]
        public void SpeedTie_IsACoinFlipAcrossSeeds()
        {
            bool firstWentFirst = false, secondWentFirst = false;

            for (int seed = 0; seed < 60 && (!firstWentFirst || !secondWentFirst); seed++)
            {
                var (state, engine) = Duel(seed,
                    Mon("Twin One", speed: 100, moves: Move("One", power: 1)),
                    Mon("Twin Two", speed: 100, moves: Move("Two", power: 1)));

                Clash(state, engine);

                var lines = state.Log.Lines.ToList();
                int one = lines.FindIndex(l => l.Contains("used One"));
                int two = lines.FindIndex(l => l.Contains("used Two"));

                if (one < two) firstWentFirst = true; else secondWentFirst = true;
            }

            Assert.True(firstWentFirst && secondWentFirst, "a speed tie always resolved the same way across 60 seeds");
        }

        [Fact]
        public void TypeEffectiveness_ScalesDamageAndAnnounces()
        {
            static int Dealt(int seed, PokemonType defenderType)
            {
                var (state, engine) = Duel(seed,
                    Mon("Zapper", moves: Move("Zap", type: PokemonType.Electric, category: MoveCategory.Special, power: 60)),
                    Mon("Wall", type: defenderType, hp: 2000, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

                Clash(state, engine);
                return state.Player2.ActivePokemon.MaxHP - state.Player2.ActivePokemon.CurrentHP;
            }

            int vsWater = Dealt(21, PokemonType.Water);
            int vsGrass = Dealt(21, PokemonType.Grass);

            // Same seed = same variance and crit rolls, so the ratio is the
            // chart's 2.0 / 0.5 = 4, give or take integer truncation.
            Assert.InRange(vsWater / (double)vsGrass, 3.0, 5.0);

            var (immuneState, immuneEngine) = Duel(4,
                Mon("Normal Guy", moves: Move("Body Check", type: PokemonType.Normal, power: 60)),
                Mon("Spooky", type: PokemonType.Ghost, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            Clash(immuneState, immuneEngine);

            Assert.Equal(immuneState.Player2.ActivePokemon.MaxHP, immuneState.Player2.ActivePokemon.CurrentHP);
            Assert.True(LogContains(immuneState, "doesn't affect"));
        }

        [Fact]
        public void Stab_Boosts_SameTypeMoves()
        {
            static int Dealt(int seed, PokemonType attackerType)
            {
                var (state, engine) = Duel(seed,
                    Mon("Striker", type: attackerType, moves: Move("Flame", type: PokemonType.Fire, power: 60)),
                    Mon("Dummy", type: PokemonType.Normal, hp: 2000, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

                Clash(state, engine);
                return state.Player2.ActivePokemon.MaxHP - state.Player2.ActivePokemon.CurrentHP;
            }

            int withStab = Dealt(33, PokemonType.Fire);
            int without = Dealt(33, PokemonType.Normal);

            Assert.InRange(withStab / (double)without, 1.3, 1.7);
        }

        [Fact]
        public void Burn_HalvesPhysicalDamage_AndTicksAtEndOfTurn()
        {
            static (int dealt, PokemonState attacker) Round(int seed, bool burned)
            {
                var (state, engine) = Duel(seed,
                    Mon("Bruiser", hp: 320, moves: Move("Pound", power: 60)),
                    Mon("Bag", hp: 2000, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

                if (burned)
                    state.Player1.ActivePokemon.Status = StatusCondition.Burn;

                Clash(state, engine);
                return (state.Player2.ActivePokemon.MaxHP - state.Player2.ActivePokemon.CurrentHP, state.Player1.ActivePokemon);
            }

            var (healthyDamage, _) = Round(5, burned: false);
            var (burnedDamage, burnedMon) = Round(5, burned: true);

            Assert.InRange(healthyDamage / (double)burnedDamage, 1.7, 2.3);

            // The burn residual: 320/16 = 20 HP at end of turn.
            Assert.Equal(320 - 20, burnedMon.CurrentHP);
        }

        [Fact]
        public void Paralysis_HalvesSpeed_AndCanFullyParalyze()
        {
            var (state, _) = Duel(2,
                Mon("Runner", speed: 100, moves: Move("Hit")),
                Mon("Other", moves: Move("Hit")));

            var runner = state.Player1.ActivePokemon;

            double before = StatResolver.GetStat(state, runner, "Speed");
            runner.Status = StatusCondition.Paralysis;
            double after = StatResolver.GetStat(state, runner, "Speed");

            Assert.Equal(before / 2, after, 3);

            bool sawFullParalysis = false;

            for (int seed = 0; seed < 60 && !sawFullParalysis; seed++)
            {
                var (s2, e2) = Duel(seed,
                    Mon("Stuck", moves: Move("Try", power: 1)),
                    Mon("Bystander", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

                s2.Player1.ActivePokemon.Status = StatusCondition.Paralysis;
                Clash(s2, e2);

                sawFullParalysis = LogContains(s2, "It can't move!");
            }

            Assert.True(sawFullParalysis, "25% full paralysis never happened across 60 seeds");
        }

        [Fact]
        public void Sleep_CountsDownThenWakes()
        {
            var (state, engine) = Duel(6,
                Mon("Dozer", hp: 4000, moves: Move("Yawnless", power: 10)),
                Mon("Bag", hp: 4000, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            var dozer = state.Player1.ActivePokemon;
            dozer.Status = StatusCondition.Sleep;
            dozer.SleepTurns = 2;

            Clash(state, engine);
            Clash(state, engine);
            Clash(state, engine);

            Assert.Equal(2, state.Log.Lines.Count(l => l.Contains("fast asleep")));
            Assert.True(LogContains(state, "woke up"));
            Assert.Equal(StatusCondition.None, dozer.Status);
        }

        [Fact]
        public void StatStages_ChangeRealDamage()
        {
            static int Dealt(int seed, int attackStage)
            {
                var (state, engine) = Duel(seed,
                    Mon("Dancer", moves: Move("Cut", power: 60)),
                    Mon("Bag", hp: 4000, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

                state.Player1.ActivePokemon.AttackStage = attackStage;

                Clash(state, engine);
                return state.Player2.ActivePokemon.MaxHP - state.Player2.ActivePokemon.CurrentHP;
            }

            int plainDamage = Dealt(13, 0);
            int boosted = Dealt(13, 2);

            Assert.InRange(boosted / (double)plainDamage, 1.7, 2.3);
        }

        [Fact]
        public void StatusMove_AppliesItsStatChange()
        {
            var dance = Move("Sword Twirl", category: MoveCategory.Status, power: 0);
            dance.StatChanges = new List<StatChange> { new StatChange { Stat = "Attack", Stages = 2, Target = "self" } };

            var (state, engine) = Duel(8,
                Mon("Dancer", moves: dance),
                Mon("Bag", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            Clash(state, engine);

            Assert.Equal(2, state.Player1.ActivePokemon.AttackStage);
            Assert.True(LogContains(state, "rose"));
        }

        [Fact]
        public void Protect_Blocks_AndFailsWhenSpammed()
        {
            var protect = Move("Guard", category: MoveCategory.Status, power: 0, priority: 4);
            protect.Effects = new List<string> { "Protect" };

            var (state, engine) = Duel(1,
                Mon("Turtle", moves: protect),
                Mon("Puncher", moves: Move("Punch", power: 80)));

            Clash(state, engine);

            Assert.True(LogContains(state, "protected itself"));
            Assert.Equal(state.Player1.ActivePokemon.MaxHP, state.Player1.ActivePokemon.CurrentHP);

            bool everFailed = false;

            for (int i = 0; i < 6 && !everFailed; i++)
            {
                Clash(state, engine);
                everFailed = LogContains(state, "But it failed!");
            }

            Assert.True(everFailed, "consecutive Protects never failed");
        }

        [Fact]
        public void Substitute_AbsorbsUntilItBreaks()
        {
            var sub = Move("Decoy", category: MoveCategory.Status, power: 0);
            sub.Effects = new List<string> { "Substitute" };

            var (state, engine) = Duel(10,
                Mon("Puppeteer", hp: 400, speed: 200, moves: sub),
                Mon("Puncher", moves: Move("Punch", power: 30)));

            Clash(state, engine);

            var puppeteer = state.Player1.ActivePokemon;

            // Substitute costs a quarter of max HP; the punch afterwards hit
            // the substitute, not the Pokemon.
            Assert.Equal(300, puppeteer.CurrentHP);
            Assert.True(puppeteer.SubstituteHP > 0 || LogContains(state, "substitute faded"));
            Assert.True(LogContains(state, "took the hit"));
        }

        [Fact]
        public void TwoTurnMove_ChargesThenHits_AndDodgesWhileGone()
        {
            var dive = Move("Sky Dive", power: 80);
            dive.Effects = new List<string> { "TwoTurn" };

            var (state, engine) = Duel(14,
                Mon("Bird", speed: 200, moves: dive),
                Mon("Archer", moves: Move("Arrow", power: 60, accuracy: 100)));

            Clash(state, engine);

            // Turn 1: vanished; the arrow found nobody.
            Assert.True(LogContains(state, "vanished"));
            Assert.True(LogContains(state, "avoided the attack"));
            Assert.Equal(state.Player2.ActivePokemon.MaxHP, state.Player2.ActivePokemon.CurrentHP);

            var legal = engine.GetLegalActions(state.Player1);
            Assert.Single(legal);
            Assert.Equal("Sky Dive", legal[0].Move!.Name);

            engine.RunTurn(legal[0], MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.True(state.Player2.ActivePokemon.CurrentHP < state.Player2.ActivePokemon.MaxHP);
            Assert.False(state.Player1.ActivePokemon.Charging);
        }

        [Fact]
        public void Switching_SwapsActive_AndResetsStages()
        {
            var lead = Mon("Lead", moves: Move("Hit", power: 10));
            var bench = Mon("Bench", moves: Move("Hit", power: 10));
            lead.AttackStage = 4;

            var (state, engine) = Battle(17,
                new List<PokemonState> { lead, bench },
                new List<PokemonState> { Mon("Foe", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)) });

            engine.RunTurn(
                SwitchAction(state, lead, bench),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.Same(bench, state.Player1.ActivePokemon);
            Assert.True(LogContains(state, "withdrew Lead"));
            Assert.True(LogContains(state, "sent out Bench"));
            Assert.Equal(0, lead.AttackStage);
        }

        [Fact]
        public void Faint_ForcesReplacement_AndBattleContinues()
        {
            var lead = Mon("Fragile", hp: 10, moves: Move("Tap", power: 1));
            var backup = Mon("Backup", moves: Move("Tap", power: 1));

            var (state, engine) = Battle(19,
                new List<PokemonState> { lead, backup },
                new List<PokemonState> { Mon("Bully", attack: 400, speed: 300, moves: Move("Crush", power: 120)) });

            Clash(state, engine);

            Assert.True(lead.Fainted);
            Assert.True(LogContains(state, "Fragile fainted"));
            Assert.True(engine.NeedsReplacement(state.Player1));
            Assert.Equal(BattleOutcome.Unfinished, state.Outcome);

            engine.Replace(state.Player1, backup);

            Assert.Same(backup, state.Player1.ActivePokemon);
            Assert.True(LogContains(state, "sent out Backup"));
        }

        [Fact]
        public void Weather_Damages_RespectsImmunity_AndExpires()
        {
            var (state, engine) = Duel(23,
                Mon("Sandy", type: PokemonType.Rock, hp: 320, moves: Move("Wait", category: MoveCategory.Status, power: 0)),
                Mon("Softy", type: PokemonType.Normal, hp: 320, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            state.Environment.Weather = WeatherType.Sandstorm;
            state.Environment.WeatherTurns = 2;

            Clash(state, engine);

            Assert.Equal(320, state.Player1.ActivePokemon.CurrentHP);       // Rock: immune
            Assert.Equal(320 - 20, state.Player2.ActivePokemon.CurrentHP);  // 320/16
            Assert.True(LogContains(state, "buffeted"));

            Clash(state, engine);

            Assert.True(LogContains(state, "The weather returned to normal."));
            Assert.Equal(WeatherType.None, state.Environment.Weather);
        }

        [Fact]
        public void Hazards_HurtOnSwitchIn()
        {
            var lead = Mon("Lead", moves: Move("Wait", category: MoveCategory.Status, power: 0));
            var moth = Mon("Moth", hp: 320, moves: Move("Wait", category: MoveCategory.Status, power: 0));
            moth.Types = new List<PokemonType> { PokemonType.Bug, PokemonType.Flying };

            var (state, engine) = Battle(29,
                new List<PokemonState> { lead, moth },
                new List<PokemonState> { Mon("Foe", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)) });

            state.StealthRockP1 = true;

            engine.RunTurn(
                SwitchAction(state, lead, moth),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            // Rock vs Bug/Flying = 4x: an eighth of max HP becomes half.
            Assert.Equal(320 - 160, moth.CurrentHP);
            Assert.True(LogContains(state, "hurt by Stealth Rock"));
        }

        [Fact]
        public void ToxicSpikes_Poison_AndPoisonTypeAbsorbs()
        {
            var lead = Mon("Lead", moves: Move("Wait", category: MoveCategory.Status, power: 0));
            var victim = Mon("Victim", moves: Move("Wait", category: MoveCategory.Status, power: 0));
            var absorber = Mon("Absorber", type: PokemonType.Poison, moves: Move("Wait", category: MoveCategory.Status, power: 0));

            var (state, engine) = Battle(31,
                new List<PokemonState> { lead, victim, absorber },
                new List<PokemonState> { Mon("Foe", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)) });

            state.ToxicSpikesP1 = 1;

            engine.RunTurn(
                SwitchAction(state, lead, victim),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.Equal(StatusCondition.Poison, victim.Status);

            engine.RunTurn(
                SwitchAction(state, victim, absorber),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.Equal(0, state.ToxicSpikesP1);
            Assert.True(LogContains(state, "absorbed the Toxic Spikes"));
        }

        [Fact]
        public void PpExhaustion_ForcesStruggle_WithRecoil_ThatHitsGhosts()
        {
            var onlyMove = Move("Last Word", power: 10, pp: 1);

            var (state, engine) = Duel(37,
                Mon("Tired", hp: 400, moves: onlyMove),
                Mon("Spooky", type: PokemonType.Ghost, hp: 4000, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            Clash(state, engine);

            Assert.Equal(0, onlyMove.CurrentPP);

            var legal = engine.GetLegalActions(state.Player1);
            Assert.Single(legal);
            Assert.Equal("Struggle", legal[0].Move!.Name);

            engine.RunTurn(legal[0], MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            // Typeless Struggle hits the Ghost, and the recoil (400/4) hurts.
            Assert.True(state.Player2.ActivePokemon.CurrentHP < state.Player2.ActivePokemon.MaxHP);
            Assert.True(LogContains(state, "damaged by recoil"));
            Assert.Equal(300, state.Player1.ActivePokemon.CurrentHP);
        }

        [Fact]
        public void WinAndLoss_AreDetected()
        {
            var (state, engine) = Duel(41,
                Mon("Champ", attack: 600, moves: Move("Finisher", power: 200)),
                Mon("Glass", hp: 10, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            Clash(state, engine);

            Assert.Equal(BattleOutcome.Player1Wins, state.Outcome);
            Assert.True(LogContains(state, "P1 wins!"));
        }

        [Fact]
        public void TurnLimit_EndsInDraw()
        {
            var (state, engine) = Duel(43,
                Mon("Stall One", moves: Move("Wait", category: MoveCategory.Status, power: 0)),
                Mon("Stall Two", moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            state.MaxTurns = 3;

            for (int i = 0; i < 5; i++)
                Clash(state, engine);

            Assert.Equal(BattleOutcome.Draw, state.Outcome);
            Assert.Equal(3, state.TurnNumber);
            Assert.True(LogContains(state, "draw"));
        }

        [Fact]
        public void Cancellation_StopsTheBattle()
        {
            var (state, engine) = Duel(47,
                Mon("One", moves: Move("Hit", power: 1)),
                Mon("Two", moves: Move("Hit", power: 1)));

            engine.Cancel();

            Assert.Equal(BattleOutcome.Cancelled, state.Outcome);
            Assert.True(LogContains(state, "cancelled"));

            int hpBefore = state.Player2.ActivePokemon.CurrentHP;
            Clash(state, engine);

            // A cancelled battle refuses further turns.
            Assert.Equal(hpBefore, state.Player2.ActivePokemon.CurrentHP);
        }

        [Fact]
        public void Intimidate_LowersTheActiveOpponentOnEntry()
        {
            var grr = Mon("Growler", moves: Move("Hit", power: 1));
            grr.AbilityId = "intimidate";
            PokemonSim.Engine.Abilities.AbilityFactory.TryCreate("intimidate", out var ability);
            grr.Ability = ability;

            var victim = Mon("Victim", moves: Move("Hit", power: 1));
            var bench = Mon("Bench", moves: Move("Hit", power: 1));

            var (state, _) = Battle(53,
                new List<PokemonState> { grr },
                new List<PokemonState> { victim, bench });

            Assert.Equal(-1, victim.AttackStage);
            Assert.Equal(0, bench.AttackStage);
        }
    }
}