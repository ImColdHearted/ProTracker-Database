using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PokemonSim.Actions;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Abilities;
using PokemonSim.Engine.Items;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>Section 161: mega evolutions and Z-Moves - the transform
    /// math, the once-per-side latches, the sticky items, and the computer
    /// players' triggers.</summary>
    public class MegaZTests161
    {
        static void Hold(PokemonState pokemon, string item) =>
            pokemon.HeldItemId = HeldItems.Normalize(item);

        static MoveState WithEffects(MoveState move, params string[] effects)
        {
            move.Effects = effects.ToList();
            return move;
        }

        static BattleAction MegaAction(BattleState state, PokemonState user, MoveState move)
        {
            BattleAction action = MoveAction(state, user, move);
            action.MegaEvolve = true;
            return action;
        }

        // ---- mega evolution ----

        [Fact]
        public void Mega_TransformsSpeciesTypesStatsAndAbility_KeepingHp()
        {
            var gengar = Mon("Gengar", type: PokemonType.Ghost, hp: 200, speed: 60,
                moves: Move("Shade", category: MoveCategory.Status, power: 0));
            Hold(gengar, "Gengarite");

            var (state, engine) = Duel(161, gengar,
                Mon("Wall", hp: 400, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            engine.RunTurn(
                MegaAction(state, gengar, gengar.Moves[0]),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.Equal("Mega Gengar", gengar.Species);
            Assert.True(gengar.MegaEvolved);
            Assert.True(state.Player1.UsedMegaEvolution);

            Assert.Contains(PokemonType.Ghost, gengar.Types);
            Assert.Contains(PokemonType.Poison, gengar.Types);

            Assert.Equal("shadowtag", gengar.AbilityId);

            // Level 50, IVs 31, EVs 0, Hardy - recomputed from Mega
            // Gengar's 60/65/80/170/95/130 base line; HP untouched.
            Assert.Equal(200, gengar.MaxHP);
            Assert.Equal(85, gengar.Stats.Attack);
            Assert.Equal(100, gengar.Stats.Defense);
            Assert.Equal(190, gengar.Stats.SpAttack);
            Assert.Equal(115, gengar.Stats.SpDefense);
            Assert.Equal(150, gengar.Stats.Speed);

            Assert.True(LogContains(state, "Gengar has Mega Evolved into Mega Gengar!"));
            Assert.True(LogContains(state, "reacting to the Key Stone"));

            // The stone stays held - mega evolution does not consume it.
            Assert.Equal("gengarite", gengar.HeldItemId);
        }

        [Fact]
        public void Mega_IsOncePerSide()
        {
            var gengar = Mon("Gengar", type: PokemonType.Ghost,
                moves: Move("Shade", category: MoveCategory.Status, power: 0));
            var alakazam = Mon("Alakazam", type: PokemonType.Psychic,
                moves: Move("Bend", category: MoveCategory.Status, power: 0));
            Hold(gengar, "Gengarite");
            Hold(alakazam, "Alakazite");

            var (state, engine) = Battle(162,
                new List<PokemonState> { gengar, alakazam },
                new List<PokemonState> { Mon("Wall", hp: 500, moves: Move("Wait", category: MoveCategory.Status, power: 0)) });

            engine.RunTurn(
                MegaAction(state, gengar, gengar.Moves[0]),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.True(state.Player1.UsedMegaEvolution);
            Assert.False(MegaEvolutions.CanMegaEvolve(state, state.Player1, alakazam));

            // A stale flag on a later action does nothing.
            engine.RunTurn(
                SwitchAction(state, gengar, alakazam),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            engine.RunTurn(
                MegaAction(state, alakazam, alakazam.Moves[0]),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.Equal("Alakazam", alakazam.Species);
            Assert.False(alakazam.MegaEvolved);
        }

        [Fact]
        public void Mega_NeedsTheHoldersOwnStone()
        {
            var pikachu = Mon("Pikachu", type: PokemonType.Electric);
            Hold(pikachu, "Gengarite");

            var (state, _) = Duel(163, pikachu, Mon("Wall"));

            Assert.False(MegaEvolutions.CanMegaEvolve(state, state.Player1, pikachu));

            Assert.True(MegaEvolutions.StoneMatches("Gengar", "gengarite"));
            Assert.True(MegaEvolutions.StoneMatches("Mega Gengar", "gengarite"));
            Assert.False(MegaEvolutions.StoneMatches("Pikachu", "gengarite"));
        }

        [Fact]
        public void Mega_PersistsAcrossSwitching()
        {
            var gengar = Mon("Gengar", type: PokemonType.Ghost,
                moves: Move("Shade", category: MoveCategory.Status, power: 0));
            var partner = Mon("Partner", moves: Move("Idle", category: MoveCategory.Status, power: 0));
            Hold(gengar, "Gengarite");

            var (state, engine) = Battle(164,
                new List<PokemonState> { gengar, partner },
                new List<PokemonState> { Mon("Wall", hp: 500, moves: Move("Wait", category: MoveCategory.Status, power: 0)) });

            engine.RunTurn(
                MegaAction(state, gengar, gengar.Moves[0]),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            engine.RunTurn(
                SwitchAction(state, gengar, partner),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            engine.RunTurn(
                SwitchAction(state, partner, gengar),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.Equal("Mega Gengar", gengar.Species);
            Assert.True(gengar.MegaEvolved);
            Assert.Equal(150, gengar.Stats.Speed);
            Assert.False(MegaEvolutions.CanMegaEvolve(state, state.Player1, gengar));
        }

        [Fact]
        public void Mega_NewSpeedCountsTheSameTurn()
        {
            // 60 base would move second against 100; Mega Gengar's
            // recomputed 150 moves first on the very turn it transforms.
            var gengar = Mon("Gengar", type: PokemonType.Ghost, speed: 60,
                moves: Move("Shade", category: MoveCategory.Status, power: 0));
            Hold(gengar, "Gengarite");

            var roadblock = Mon("Roadblock", speed: 100,
                moves: Move("Wait", category: MoveCategory.Status, power: 0));

            var (state, engine) = Duel(165, gengar, roadblock);

            engine.RunTurn(
                MegaAction(state, gengar, gengar.Moves[0]),
                MoveAction(state, roadblock, roadblock.Moves[0]));

            var lines = state.Log.Lines.ToList();
            int megaMoved = lines.FindIndex(l => l.Contains("Mega Gengar used"));
            int wallMoved = lines.FindIndex(l => l.Contains("Roadblock used"));

            Assert.True(megaMoved >= 0);
            Assert.True(wallMoved >= 0);
            Assert.True(megaMoved < wallMoved, "the mega should act first on its transform turn");
        }

        [Fact]
        public void Mega_AbilityActivatesOnTransform()
        {
            var charizard = Mon("Charizard", type: PokemonType.Fire,
                moves: Move("Pose", category: MoveCategory.Status, power: 0));
            Hold(charizard, "Charizardite Y");

            var (state, engine) = Duel(166, charizard,
                Mon("Wall", hp: 400, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            engine.RunTurn(
                MegaAction(state, charizard, charizard.Moves[0]),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.Equal("Mega Charizard Y", charizard.Species);
            Assert.Equal("drought", charizard.AbilityId);
            Assert.Equal(WeatherType.Sun, state.Environment.Weather);
        }

        [Fact]
        public void Mega_AllTwentyFourStonesHaveFormeDataAndAbilities()
        {
            Assert.Equal(24, MegaEvolutions.All.Count);

            foreach (MegaEvolutions.MegaForm form in MegaEvolutions.All)
            {
                Assert.True(HeldItems.IsMegaStone(form.StoneId), form.StoneId);
                Assert.True(PokemonDex.TryGet(form.MegaSpecies, out _),
                    $"{form.MegaSpecies} missing from the engine pokedex");
                Assert.True(AbilityFactory.IsSupported(form.AbilityId),
                    $"{form.AbilityId} not implemented");
            }
        }

        [Fact]
        public void Mega_ComputerPlayersFlagItAutomatically()
        {
            var gengar = Mon("Gengar", type: PokemonType.Ghost,
                moves: Move("Shade", category: MoveCategory.Status, power: 0));
            Hold(gengar, "Gengarite");

            var (state, engine) = Duel(167, gengar, Mon("Wall"));

            var random = new RandomMoveStrategy();
            BattleAction chosen = random.ChooseAction(state, state.Player1,
                engine.GetLegalActions(state.Player1));

            Assert.True(chosen.MegaEvolve);

            var monteCarlo = new MonteCarloStrategy(
                new MonteCarloConfig { SimulationsPerAction = 2, MaxRolloutTurns = 2 }, seed: 9);
            BattleAction brainy = monteCarlo.ChooseAction(state, state.Player1,
                engine.GetLegalActions(state.Player1));

            Assert.True(brainy.MegaEvolve);
        }

        // ---- Z-Moves ----

        [Fact]
        public void Z_PowerTableMatchesTheGames()
        {
            foreach ((int basePower, int zPower) in new[]
            {
                (40, 100), (55, 100), (60, 120), (65, 120), (70, 140),
                (75, 140), (80, 160), (85, 160), (90, 175), (95, 175),
                (100, 180), (110, 185), (120, 190), (125, 190), (130, 195),
                (140, 200), (250, 200)
            })
            {
                Assert.Equal(zPower, ZMoves.ZPower(basePower));
            }
        }

        [Fact]
        public void Z_SynthesisShape()
        {
            MoveState fireBase = Move("Flame Hit", type: PokemonType.Fire, power: 80, pp: 16, priority: 1);
            fireBase.Category = MoveCategory.Special;

            MoveState z = ZMoves.Synthesize(fireBase);

            Assert.Equal("Inferno Overdrive", z.Name);
            Assert.Equal(PokemonType.Fire, z.Type);
            Assert.Equal(MoveCategory.Special, z.Category);
            Assert.Equal(160, z.Power);
            Assert.Equal(0, z.Accuracy);
            Assert.Equal(0, z.MaxPP);
            Assert.Equal(1, z.Priority);
            Assert.True(z.IsZMove);
            Assert.False(z.IsContact);
            Assert.Null(z.Effects);
        }

        [Fact]
        public void Z_FiresOncePerBattle_AndSpendsTheBaseMovesPP()
        {
            var attacker = Mon("Zapper", type: PokemonType.Electric, attack: 120, speed: 90,
                moves: Move("Spark Hit", type: PokemonType.Electric, power: 60, pp: 16));
            Hold(attacker, "Electrium Z");

            var wall = Mon("Wall", hp: 600, speed: 10,
                moves: Move("Wait", category: MoveCategory.Status, power: 0));

            var (state, engine) = Duel(168, attacker, wall);

            BattleAction first = MoveAction(state, attacker, attacker.Moves[0]);
            first.UseZMove = true;

            engine.RunTurn(first, MoveAction(state, wall, wall.Moves[0]));

            Assert.True(state.Player1.UsedZMove);
            Assert.Equal(15, attacker.Moves[0].CurrentPP);
            Assert.True(LogContains(state, "surrounded itself with its Z-Power!"));
            Assert.True(LogContains(state, "used Gigavolt Havoc!"));

            // The base move stays the "last move" for Encore and friends.
            Assert.Equal("Spark Hit", attacker.LastMoveName);

            int hpAfterZ = wall.CurrentHP;

            // A stale flag on a later turn resolves as the plain move.
            BattleAction second = MoveAction(state, attacker, attacker.Moves[0]);
            second.UseZMove = true;

            engine.RunTurn(second, MoveAction(state, wall, wall.Moves[0]));

            Assert.Equal(14, attacker.Moves[0].CurrentPP);
            Assert.True(LogContains(state, "used Spark Hit!"));
            Assert.True(hpAfterZ - wall.CurrentHP < 600 - hpAfterZ,
                "the plain follow-up should hit for less than the Z-Move");
        }

        [Fact]
        public void Z_NeedsTheMatchingCrystalAndARealDamagingMove()
        {
            var mon = Mon("Zapper", type: PokemonType.Electric,
                moves: Move("Spark Hit", type: PokemonType.Electric, power: 60, pp: 16));
            var (state, _) = Duel(169, mon, Mon("Wall"));

            // No crystal at all.
            Assert.False(ZMoves.CanUse(state, state.Player1, mon, mon.Moves[0]));

            // The wrong crystal.
            Hold(mon, "Firium Z");
            Assert.False(ZMoves.CanUse(state, state.Player1, mon, mon.Moves[0]));

            // The right crystal.
            Hold(mon, "Electrium Z");
            Assert.True(ZMoves.CanUse(state, state.Player1, mon, mon.Moves[0]));
            Assert.True(ZMoves.AvailableFor(state, state.Player1, mon));

            // Status and zero-power moves never Z.
            MoveState status = Move("Stance", type: PokemonType.Electric,
                category: MoveCategory.Status, power: 0);
            Assert.False(ZMoves.CanUse(state, state.Player1, mon, status));

            // Spent latch kills it side-wide.
            state.Player1.UsedZMove = true;
            Assert.False(ZMoves.CanUse(state, state.Player1, mon, mon.Moves[0]));
            Assert.False(ZMoves.AvailableFor(state, state.Player1, mon));
        }

        [Fact]
        public void Z_AQuarterLeaksThroughProtect()
        {
            int LossAgainst(bool protectedDefender)
            {
                var attacker = Mon("Zapper", type: PokemonType.Electric, attack: 130, speed: 90,
                    moves: Move("Spark Hit", type: PokemonType.Electric, power: 80, pp: 16));
                Hold(attacker, "Electrium Z");

                var wall = Mon("Wall", hp: 600, speed: 10,
                    moves: Move("Wait", category: MoveCategory.Status, power: 0));

                var (state, engine) = Duel(170, attacker, wall);

                wall.Protected = protectedDefender;

                BattleAction z = MoveAction(state, attacker, attacker.Moves[0]);
                z.UseZMove = true;

                engine.RunTurn(z, MoveAction(state, wall, wall.Moves[0]));

                if (protectedDefender)
                    Assert.True(LogContains(state, "couldn't fully protect itself!"));

                return 600 - wall.CurrentHP;
            }

            int full = LossAgainst(protectedDefender: false);
            int leaked = LossAgainst(protectedDefender: true);

            Assert.True(full > 0);
            Assert.Equal(Math.Max(1, full / 4), leaked);
        }

        [Fact]
        public void Z_AiFiresItWhenItLooksLethal()
        {
            var attacker = Mon("Zapper", type: PokemonType.Electric, attack: 130,
                moves: Move("Spark Hit", type: PokemonType.Electric, power: 80, pp: 16));
            Hold(attacker, "Electrium Z");

            var target = Mon("Wall", hp: 400, moves: Move("Wait", category: MoveCategory.Status, power: 0));

            var (state, engine) = Duel(171, attacker, target);

            // Full health: nothing looks lethal, play normally.
            Assert.Null(ZMoves.TryPickLethalZ(state, state.Player1, attacker, target,
                engine.GetLegalActions(state.Player1)));

            // On the ropes: the Z finishes it.
            target.CurrentHP = 10;

            BattleAction? pick = ZMoves.TryPickLethalZ(state, state.Player1, attacker, target,
                engine.GetLegalActions(state.Player1));

            Assert.NotNull(pick);
            Assert.Equal("Spark Hit", pick!.Move!.Name);
        }

        [Fact]
        public void Z_AiEstimateLeavesTheBattleRngAlone()
        {
            (BattleState State, BattleEngine Engine) Build()
            {
                var attacker = Mon("Zapper", type: PokemonType.Electric,
                    moves: Move("Spark Hit", type: PokemonType.Electric, power: 80, pp: 16));
                Hold(attacker, "Electrium Z");
                return Duel(172, attacker, Mon("Wall", hp: 40));
            }

            var probed = Build();
            var control = Build();

            ZMoves.TryPickLethalZ(probed.State, probed.State.Player1,
                probed.State.Player1.ActivePokemon, probed.State.Player2.ActivePokemon,
                probed.Engine.GetLegalActions(probed.State.Player1));

            Assert.Equal(control.State.Rng.Next(1_000_000), probed.State.Rng.Next(1_000_000));
        }

        [Fact]
        public async Task Z_SessionOpponentFiresTheLethalZ()
        {
            var player = Mon("Hero", hp: 300, moves: Move("Poke", power: 40, pp: 16));

            var foe = Mon("Zapper", type: PokemonType.Electric, attack: 130, speed: 200,
                moves: Move("Spark Hit", type: PokemonType.Electric, power: 80, pp: 16));
            Hold(foe, "Electrium Z");

            var session = new SimulatorSession(
                "You", new List<PokemonState> { player },
                "Foe", new List<PokemonState> { foe },
                seed: 173);

            player.CurrentHP = 5;

            await session.PlayTurnAsync(session.PlayerLegalActions().First(a => a.Move != null));

            Assert.True(LogContains(session.State, "surrounded itself with its Z-Power!"));
            Assert.True(session.Opponent.UsedZMove);
        }

        // ---- sticky items ----

        [Fact]
        public void Sticky_CrystalsAndOwnedStones()
        {
            var zapper = Mon("Zapper");
            Hold(zapper, "Firium Z");
            Assert.True(HeldItems.IsSticky(zapper));

            var tyranitar = Mon("Tyranitar", type: PokemonType.Rock);
            Hold(tyranitar, "Tyranitarite");
            Assert.True(HeldItems.IsSticky(tyranitar));

            var snorlax = Mon("Snorlax");
            Hold(snorlax, "Tyranitarite");
            Assert.False(HeldItems.IsSticky(snorlax));

            Hold(snorlax, "Leftovers");
            Assert.False(HeldItems.IsSticky(snorlax));
        }

        [Fact]
        public void Sticky_KnockOffCannotRemoveThem_AndSkipsItsBoost()
        {
            var attacker = Mon("Batter", attack: 100, speed: 90,
                moves: WithEffects(Move("Knock Away", power: 65, pp: 16), "KnockOff", "KnockOffRemove"));

            var holder = Mon("Holder", hp: 500, speed: 10,
                moves: Move("Wait", category: MoveCategory.Status, power: 0));
            Hold(holder, "Ghostium Z");

            var (state, engine) = Duel(174, attacker, holder);

            Clash(state, engine);

            Assert.Equal("ghostiumz", holder.HeldItemId);
            Assert.False(holder.LostItem);
            Assert.True(LogContains(state, "held on to its Ghostium Z!"));
        }

        [Fact]
        public void Sticky_TrickAndThiefAndFlingAllFail()
        {
            // Trick against an owned mega stone: no swap.
            var trickster = Mon("Trickster", speed: 90,
                moves: WithEffects(Move("Swap", category: MoveCategory.Status, power: 0), "TrickItem"));
            Hold(trickster, "Leftovers");

            var tyranitar = Mon("Tyranitar", type: PokemonType.Rock, hp: 400, speed: 10,
                moves: Move("Wait", category: MoveCategory.Status, power: 0));
            Hold(tyranitar, "Tyranitarite");

            var (state, engine) = Duel(175, trickster, tyranitar);
            Clash(state, engine);

            Assert.Equal("leftovers", trickster.HeldItemId);
            Assert.Equal("tyranitarite", tyranitar.HeldItemId);

            // Thief against a crystal: damage lands, nothing is stolen.
            var thief = Mon("Thief", attack: 100, speed: 90,
                moves: WithEffects(Move("Grab", power: 60, pp: 16), "StealItem"));

            var holder = Mon("Holder", hp: 500, speed: 10,
                moves: Move("Wait", category: MoveCategory.Status, power: 0));
            Hold(holder, "Fairium Z");

            var (state2, engine2) = Duel(176, thief, holder);
            Clash(state2, engine2);

            Assert.Null(thief.HeldItemId);
            Assert.Equal("fairiumz", holder.HeldItemId);

            // Fling with a crystal in hand: the throw fails outright.
            var flinger = Mon("Flinger", speed: 90,
                moves: WithEffects(Move("Hurl", power: 30, pp: 16), "FlingItem", "FlingThrow"));
            Hold(flinger, "Normalium Z");

            var wall = Mon("Wall", hp: 400, speed: 10,
                moves: Move("Wait", category: MoveCategory.Status, power: 0));

            var (state3, engine3) = Duel(177, flinger, wall);
            Clash(state3, engine3);

            Assert.Equal("normaliumz", flinger.HeldItemId);
            Assert.False(flinger.LostItem);
            Assert.Equal(400, wall.CurrentHP);
        }

        // ---- team building ----

        sealed class OneSpeciesSource : ISpeciesSource
        {
            readonly SpeciesInfo info;

            public OneSpeciesSource(string name, string type, params string[] learnset)
            {
                info = new SpeciesInfo
                {
                    Name = name,
                    Types = new List<string> { type },
                    BaseStats = new Stats
                    {
                        HP = 100, Attack = 100, Defense = 100,
                        SpAttack = 100, SpDefense = 100, Speed = 100
                    },
                    LearnsetMoveNames = learnset.ToList()
                };
            }

            public IReadOnlyList<string> AllSpeciesNames => new List<string> { info.Name };

            public SpeciesInfo? Find(string name) =>
                name.Equals(info.Name, StringComparison.OrdinalIgnoreCase) ? info : null;
        }

        [Fact]
        public void Builder_MatchingStoneIsQuiet_MismatchAndIdleCrystalWarn()
        {
            MoveDex.EnsureLoaded();

            var source = new OneSpeciesSource("Gengar", "Ghost", "Shadow Ball");

            TeamBuildResult Build(string itemName)
            {
                var plan = new TeamSlotPlan
                {
                    SpeciesName = "Gengar",
                    Level = 50,
                    ItemName = itemName,
                    MoveNames = { "Shadow Ball" }
                };

                return TeamBuilder.Build(new[] { plan }, source);
            }

            // Its own stone: attached, and no warning at all - it works now.
            TeamBuildResult own = Build("Gengarite");
            Assert.True(own.Ok);
            Assert.Equal("gengarite", own.Team[0].HeldItemId);
            Assert.DoesNotContain(own.Warnings, w => w.Contains("rides along") || w.Contains("belongs to"));

            // Somebody else's stone: honest note.
            TeamBuildResult wrong = Build("Tyranitarite");
            Assert.True(wrong.Ok);
            Assert.Contains(wrong.Warnings, w => w.Contains("belongs to a different Pokemon"));

            // A crystal with no matching damaging move will sit idle.
            TeamBuildResult idle = Build("Firium Z");
            Assert.True(idle.Ok);
            Assert.Contains(idle.Warnings, w => w.Contains("sit idle"));

            // The matching crystal: quiet.
            TeamBuildResult ready = Build("Ghostium Z");
            Assert.True(ready.Ok);
            Assert.Equal("ghostiumz", ready.Team[0].HeldItemId);
            Assert.DoesNotContain(ready.Warnings, w => w.Contains("sit idle"));
        }

        [Fact]
        public void Catalog_KnowsTheEighteenCrystals()
        {
            foreach (string name in new[]
            {
                "Normalium Z", "Firium Z", "Waterium Z", "Electrium Z",
                "Grassium Z", "Icium Z", "Fightinium Z", "Poisonium Z",
                "Groundium Z", "Flyinium Z", "Psychium Z", "Buginium Z",
                "Rockium Z", "Ghostium Z", "Dragonium Z", "Darkinium Z",
                "Steelium Z", "Fairium Z"
            })
            {
                Assert.True(HeldItems.IsSupported(name), name);
                Assert.True(HeldItems.IsZCrystal(name), name);
                Assert.False(HeldItems.IsMegaStone(name), name);
            }

            Assert.Equal(PokemonType.Fire, HeldItems.ZCrystalType("Firium Z"));
            Assert.Equal(PokemonType.Fairy, HeldItems.ZCrystalType("fairiumz"));
            Assert.Null(HeldItems.ZCrystalType("Leftovers"));
            Assert.False(HeldItems.IsZCrystal("Gengarite"));
        }
    }
}