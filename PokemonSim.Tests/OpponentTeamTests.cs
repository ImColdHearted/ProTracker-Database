using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>A tiny hand-built species catalog for the resolver and
    /// builder tests - real catalog spellings, no data files.</summary>
    sealed class StubSpeciesSource : ISpeciesSource
    {
        readonly Dictionary<string, SpeciesInfo> byName = new(StringComparer.OrdinalIgnoreCase);

        public StubSpeciesSource(params string[] names)
        {
            foreach (string name in names)
            {
                byName[name] = new SpeciesInfo
                {
                    Name = name,
                    Types = new List<string> { "Normal" },
                    BaseStats = new Stats { HP = 80, Attack = 80, Defense = 80, SpAttack = 80, SpDefense = 80, Speed = 80 },
                    LearnsetMoveNames = new List<string>(),   // deliberately empty: boss moves must not care
                    AbilityNames = new List<string> { "Intimidate", "Serene Grace" }
                };
            }
        }

        public IReadOnlyList<string> AllSpeciesNames => byName.Keys.OrderBy(n => n).ToList();

        public SpeciesInfo? Find(string name) =>
            byName.TryGetValue(name.Trim(), out SpeciesInfo? info) ? info : null;
    }

    /// <summary>Section 155. The trusted-roster builder that turns boss
    /// data into battle teams, and the name bridges between the boss files'
    /// spellings and the species catalog's.</summary>
    public class OpponentTeamTests
    {
        static OpponentTeamTests()
        {
            MoveDex.EnsureLoaded();
        }

        static OpponentSlotPlan Slot(string species, params string[] moves) => new()
        {
            SpeciesName = species,
            NatureName = "Adamant",
            AbilityName = "Intimidate",
            ItemName = "None",
            MoveNames = moves.ToList()
        };

        [Fact]
        public void BossMoves_AreTrusted_NoLearnsetVeto()
        {
            // The stub's learnset is EMPTY; a boss still fights with the
            // moves its file lists, because the boss file is authoritative.
            var result = OpponentTeams.Build(
                new[] { Slot("Snorlax", "Fire Blast", "Toxic") },
                new StubSpeciesSource("Snorlax"),
                new BattleRng(1));

            Assert.True(result.Ok);
            Assert.Equal(2, result.Team[0].Moves.Count);
            Assert.DoesNotContain(result.Errors, e => e.Contains("learn"));
        }

        [Fact]
        public void UnknownMove_IsNamedAndSkipped_TheRestSurvive()
        {
            var result = OpponentTeams.Build(
                new[] { Slot("Snorlax", "Fire Blast", "Completely Made Up") },
                new StubSpeciesSource("Snorlax"),
                new BattleRng(1));

            Assert.True(result.Ok);
            Assert.Single(result.Team[0].Moves);
            Assert.Contains(result.Warnings, w => w.Contains("Completely Made Up"));
        }

        [Fact]
        public void UnknownSpecies_LosesTheSlotWithAClearError_NotTheFight()
        {
            var result = OpponentTeams.Build(
                new[] { Slot("Munchlax", "Fire Blast"), Slot("Snorlax", "Fire Blast") },
                new StubSpeciesSource("Snorlax"),
                new BattleRng(1));

            Assert.True(result.Ok);
            Assert.Single(result.Team);
            Assert.Contains(result.Errors, e => e.Contains("Munchlax"));
        }

        [Fact]
        public void NothingBuildable_FailsWithAClearError()
        {
            var result = OpponentTeams.Build(
                new[] { Slot("Munchlax", "Fire Blast") },
                new StubSpeciesSource("Snorlax"),
                new BattleRng(1));

            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.Contains("cannot start"));
        }

        [Fact]
        public void MissingLevel_DefaultsWithAWarning()
        {
            var result = OpponentTeams.Build(
                new[] { Slot("Snorlax", "Fire Blast") },
                new StubSpeciesSource("Snorlax"),
                new BattleRng(1));

            Assert.Equal(OpponentTeams.DefaultLevel, result.Team[0].Level);
            Assert.Contains(result.Warnings, w => w.Contains("no level"));
        }

        [Fact]
        public void RandomNature_IsRolledFromTheSeededRng_SoItIsReproducible()
        {
            var plan = Slot("Snorlax", "Fire Blast");
            plan.NatureName = "Random";

            var first = OpponentTeams.Build(new[] { plan }, new StubSpeciesSource("Snorlax"), new BattleRng(77));
            var second = OpponentTeams.Build(new[] { plan }, new StubSpeciesSource("Snorlax"), new BattleRng(77));

            Assert.Equal(first.Team[0].Nature, second.Team[0].Nature);
        }

        [Fact]
        public void HeldItems_AttachForReal_Section159()
        {
            // §155 reported items as not simulated; §159 attaches them.
            var slot = Slot("Snorlax", "Fire Blast");
            slot.ItemName = "Leftovers";

            var result = OpponentTeams.Build(
                new[] { slot }, new StubSpeciesSource("Snorlax"), new BattleRng(1));

            Assert.True(result.Ok);
            Assert.Equal("leftovers", result.Team[0].HeldItemId);
            Assert.DoesNotContain(result.Warnings, w => w.Contains("Leftovers"));
        }

        [Fact]
        public void UnknownItem_WarnsAndIsLeftOff()
        {
            var slot = Slot("Snorlax", "Fire Blast");
            slot.ItemName = "Master Ball";

            var result = OpponentTeams.Build(
                new[] { slot }, new StubSpeciesSource("Snorlax"), new BattleRng(1));

            Assert.True(result.Ok);
            Assert.Null(result.Team[0].HeldItemId);
            Assert.Contains(result.Warnings, w => w.Contains("Master Ball") && w.Contains("not simulated"));
        }

        [Fact]
        public void MegaStone_OnTheWrongHolder_RidesAlongWithAnHonestNote()
        {
            // §161: a MATCHING stone mega evolves for real now (see
            // MegaZTests161); only a mismatched one is still dead weight.
            var slot = Slot("Snorlax", "Fire Blast");
            slot.ItemName = "Tyranitarite";

            var result = OpponentTeams.Build(
                new[] { slot }, new StubSpeciesSource("Snorlax"), new BattleRng(1));

            Assert.True(result.Ok);
            Assert.Equal("tyranitarite", result.Team[0].HeldItemId);
            Assert.Contains(result.Warnings, w => w.Contains("belongs to a different Pokemon"));
        }

        [Fact]
        public void UnsupportedAbility_WarnsAndStillFights()
        {
            // Section 158 implemented Serene Grace (the §155 example), so
            // this test pins a genuinely unimplemented id instead.
            var slot = Slot("Snorlax", "Fire Blast");
            slot.AbilityName = "Wonder Guard";

            var result = OpponentTeams.Build(
                new[] { slot }, new StubSpeciesSource("Snorlax"), new BattleRng(1));

            Assert.True(result.Ok);
            Assert.Contains(result.Warnings, w => w.Contains("Wonder Guard"));
        }

        [Fact]
        public void NameBridges_FindTheCatalogSpellings()
        {
            var source = new StubSpeciesSource(
                "Wash Rotom", "Alolan Ninetales", "Shaymin: Sky", "Mega Charizard X",
                "Galarian Weezing", "Hisuian Arcanine", "Aegislash Shield Forme");

            foreach ((string bossSpelling, string catalogSpelling) in new[]
            {
                ("Rotom-Wash", "Wash Rotom"),
                ("Ninetales-Alolan", "Alolan Ninetales"),
                ("Shaymin-Sky", "Shaymin: Sky"),
                ("Mega Charizard-X", "Mega Charizard X"),
                ("Weezing-Galarian", "Galarian Weezing"),
                ("Arcanine-Hisui", "Hisuian Arcanine"),
                ("Aegislash", "Aegislash Shield Forme"),
            })
            {
                (SpeciesInfo? info, _) = OpponentTeams.Resolve(bossSpelling, source);

                Assert.NotNull(info);
                Assert.Equal(catalogSpelling, info!.Name);
            }
        }

        [Fact]
        public void MegaWithoutCatalogData_FallsBackToTheBaseSpecies()
        {
            var source = new StubSpeciesSource("Tyranitar");

            (SpeciesInfo? info, string? note) = OpponentTeams.Resolve("Mega Tyranitar", source);

            Assert.NotNull(info);
            Assert.Equal("Tyranitar", info!.Name);
            Assert.NotNull(note);
            Assert.Contains("Mega", note!);
        }

        [Fact]
        public void PairedRosters_BuildIndependentTeams()
        {
            var source = new StubSpeciesSource("Snorlax");

            var jessie = OpponentTeams.Build(new[] { Slot("Snorlax", "Fire Blast") }, source, new BattleRng(5));
            var james = OpponentTeams.Build(new[] { Slot("Snorlax", "Fire Blast") }, source, new BattleRng(5));

            jessie.Team[0].CurrentHP = 1;

            Assert.NotSame(jessie.Team[0], james.Team[0]);
            Assert.NotEqual(jessie.Team[0].CurrentHP, james.Team[0].CurrentHP);
            Assert.NotSame(jessie.Team[0].Moves[0], james.Team[0].Moves[0]);
        }

        [Fact]
        public void ZeroImplementedMoves_MeansAStruggleWarning_NotACrash()
        {
            var result = OpponentTeams.Build(
                new[] { Slot("Snorlax", "Completely Made Up") },
                new StubSpeciesSource("Snorlax"),
                new BattleRng(1));

            Assert.True(result.Ok);
            Assert.Empty(result.Team[0].Moves);
            Assert.Contains(result.Warnings, w => w.Contains("Struggle"));
        }

        [Fact]
        public void TheBossMoveBatch_IsInTheMoveData()
        {
            Assert.True(MoveDex.TryGet("Fire Blast", out MoveState fireBlast));
            Assert.Equal(StatusCondition.Burn, fireBlast.InflictStatus);

            Assert.True(MoveDex.TryGet("Toxic", out MoveState toxic));
            Assert.Equal(StatusCondition.Toxic, toxic.InflictStatus);
            Assert.Equal(MoveCategory.Status, toxic.Category);

            Assert.True(MoveDex.TryGet("Psyshock", out MoveState psyshock));
            Assert.True(psyshock.UsesTargetDefense);

            Assert.True(MoveDex.TryGet("Seismic Toss", out MoveState toss));
            Assert.Contains("LevelDamage", toss.Effects!);

            Assert.True(MoveDex.TryGet("Knock Off", out _));
            Assert.True(MoveDex.TryGet("Hidden Power (Ice)", out MoveState hp));
            Assert.Equal(PokemonType.Ice, hp.Type);

            Assert.False(MoveDex.TryGet("Leech Seed", out _));   // deliberately still unknown
        }
    }

    /// <summary>Section 155. The new move effects behind the boss batch.</summary>
    public class BossMoveEffectTests
    {
        [Fact]
        public void Recoil_HurtsTheAttackerByAFractionOfDamageDealt()
        {
            var hitter = TestKit.Mon("Hitter", hp: 300, speed: 200,
                moves: TestKit.Move("Reckless Hit", power: 80));
            hitter.Moves[0].Effects = new List<string> { "Recoil33" };

            var wall = TestKit.Mon("Wall", hp: 300, speed: 1, moves: TestKit.Move("Tap", power: 0));

            var (state, engine) = TestKit.Duel(7, hitter, wall);
            TestKit.Clash(state, engine);

            int dealt = 300 - wall.CurrentHP;
            int taken = 300 - hitter.CurrentHP;

            Assert.True(dealt > 0);
            Assert.InRange(taken, Math.Max(1, dealt / 3 - 2), dealt / 3 + 2);
            Assert.True(TestKit.LogContains(state, "recoil"));
        }

        [Fact]
        public void Explosion_FaintsItsUser()
        {
            var bomber = TestKit.Mon("Bomber", hp: 300, speed: 200, moves: TestKit.Move("Boom", power: 250));
            bomber.Moves[0].Effects = new List<string> { "SelfFaint" };

            var victim = TestKit.Mon("Victim", hp: 500, speed: 1, moves: TestKit.Move("Tap", power: 1));

            var (state, engine) = TestKit.Duel(8, bomber, victim);
            TestKit.Clash(state, engine);

            Assert.True(bomber.Fainted);
            Assert.True(victim.CurrentHP < 500);
        }

        [Fact]
        public void SeismicToss_DealsExactlyTheUsersLevel()
        {
            var tosser = TestKit.Mon("Tosser", hp: 300, speed: 200, moves: TestKit.Move("Toss", power: 1));
            tosser.Moves[0].Effects = new List<string> { "LevelDamage" };
            tosser.Level = 50;

            var target = TestKit.Mon("Target", hp: 300, speed: 1, moves: TestKit.Move("Tap", power: 0));

            var (state, engine) = TestKit.Duel(9, tosser, target);
            TestKit.Clash(state, engine);

            Assert.Equal(250, target.CurrentHP);
        }

        [Fact]
        public void FacadeBoost_DoublesWhenTheAttackerIsStatused()
        {
            int DamageWithStatus(bool poisoned)
            {
                var user = TestKit.Mon("Facader", hp: 400, speed: 200, moves: TestKit.Move("Grudge Hit", power: 70));
                user.Moves[0].Effects = new List<string> { "FacadeBoost" };

                if (poisoned)
                    user.Status = StatusCondition.Poison;

                var target = TestKit.Mon("Punchbag", hp: 400, speed: 1, moves: TestKit.Move("Tap", power: 0));

                var (state, engine) = TestKit.Duel(10, user, target);
                TestKit.Clash(state, engine);

                return 400 - target.CurrentHP;
            }

            int clean = DamageWithStatus(false);
            int statused = DamageWithStatus(true);

            Assert.Equal(clean * 2, statused);
        }

        [Fact]
        public void BindTrap_PinsTheTarget_NoSwitchingOut()
        {
            var trapper = TestKit.Mon("Trapper", hp: 300, speed: 200, moves: TestKit.Move("Bind Hit", power: 40));
            trapper.Moves[0].Effects = new List<string> { "BindTrap" };

            var prey = TestKit.Mon("Prey", hp: 300, speed: 1, moves: TestKit.Move("Tap", power: 1));
            var bench = TestKit.Mon("Bench", hp: 300, moves: TestKit.Move("Tap 2", power: 1));

            var (state, engine) = TestKit.Battle(11,
                new List<PokemonState> { trapper },
                new List<PokemonState> { prey, bench });

            TestKit.Clash(state, engine);

            Assert.NotNull(prey.Trap);
            Assert.DoesNotContain(engine.GetLegalActions(state.Player2),
                a => a.Type == BattleActionType.Switch);
            Assert.True(TestKit.LogContains(state, "trapped"));
        }

        [Fact]
        public void MeanLookStyleTrap_PinsWithoutResidualDamage()
        {
            var pinner = TestKit.Mon("Pinner", hp: 300, speed: 200,
                moves: TestKit.Move("Stare", category: MoveCategory.Status, power: 0));
            pinner.Moves[0].Effects = new List<string> { "TrapNoDamage" };

            var prey = TestKit.Mon("Stuck", hp: 300, speed: 1, moves: TestKit.Move("Tap", power: 0));
            var bench = TestKit.Mon("Waiting", hp: 300, moves: TestKit.Move("Tap 2", power: 0));

            var (state, engine) = TestKit.Battle(12,
                new List<PokemonState> { pinner },
                new List<PokemonState> { prey, bench });

            TestKit.Clash(state, engine);

            Assert.NotNull(prey.Trap);
            Assert.Equal(300, prey.CurrentHP);   // pinned, never hurt by it
            Assert.DoesNotContain(engine.GetLegalActions(state.Player2),
                a => a.Type == BattleActionType.Switch);
        }
    }
}