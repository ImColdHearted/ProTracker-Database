using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Engine;
using PokemonSim.Engine.Items;
using PokemonSim.Models;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>Section 159: held items - the passive numbers, the
    /// consumables, the locks and the theft moves.</summary>
    public class ItemTests159
    {
        static void Hold(PokemonState pokemon, string item) =>
            pokemon.HeldItemId = HeldItems.Normalize(item);

        static MoveState WithEffects(MoveState move, params string[] effects)
        {
            move.Effects = effects.ToList();
            return move;
        }

        [Fact]
        public void Catalog_KnowsThePoolAndTheStones()
        {
            foreach (string name in new[]
            {
                "Leftovers", "Black Sludge", "Choice Band", "Choice Specs", "Choice Scarf",
                "Life Orb", "Focus Sash", "Assault Vest", "Eviolite", "Rocky Helmet",
                "Weakness Policy", "Expert Belt", "Muscle Band", "Wise Glasses",
                "Air Balloon", "Light Ball", "Light Clay", "Scope Lens", "Wide Lens",
                "Sitrus Berry", "Lum Berry", "Flame Orb",
                "Toxic Orb", "Icy Rock", "Damp Rock", "Heat Rock", "Smooth Rock",
                "Silk Scarf", "Charcoal", "Mystic Water", "Magnet", "Miracle Seed",
                "Never-Melt Ice", "Black Belt", "Poison Barb", "Soft Sand", "Sharp Beak",
                "Twisted Spoon", "Silver Powder", "Hard Stone", "Spell Tag", "Dragon Fang",
                "Black Glasses", "Metal Coat", "Pixie Plate", "Occa Berry", "Passho Berry",
                "Shuca Berry", "Chople Berry", "Colbur Berry", "Chilan Berry",
                "Tyranitarite", "Charizardite X"
            })
            {
                Assert.True(HeldItems.IsSupported(name), $"{name} should be supported");
            }

            Assert.True(HeldItems.IsMegaStone("Tyranitarite"));
            Assert.False(HeldItems.IsMegaStone("Eviolite"));
            Assert.False(HeldItems.IsSupported("Master Ball"));
            Assert.Equal("Choice Band", HeldItems.DisplayName("choiceband"));

            // The item picker's pool must be fully engine-backed. The pool
            // itself lives app-side; its names are mirrored here.
            Assert.True(HeldItems.SupportedIds.Count >= 60);
        }

        [Fact]
        public void Leftovers_HealSixteenthEachTurn()
        {
            var wait = Move("Wait", category: MoveCategory.Status, power: 0);
            var (state, engine) = Duel(11, Mon("Snorlax", hp: 320, moves: wait),
                Mon("Watcher", speed: 1, moves: Move("Wait2", category: MoveCategory.Status, power: 0)));

            var snorlax = state.Player1.ActivePokemon;
            Hold(snorlax, "Leftovers");
            snorlax.CurrentHP = 100;

            Clash(state, engine);

            Assert.Equal(100 + 320 / 16, snorlax.CurrentHP);
        }

        [Fact]
        public void BlackSludge_FeedsPoisonTypes_AndBurnsTheRest()
        {
            var wait = Move("Wait", category: MoveCategory.Status, power: 0);
            var (state, engine) = Duel(13,
                Mon("Muk", type: PokemonType.Poison, hp: 320, moves: wait),
                Mon("Thief", hp: 320, speed: 1, moves: Move("Wait2", category: MoveCategory.Status, power: 0)));

            var muk = state.Player1.ActivePokemon;
            var thief = state.Player2.ActivePokemon;
            Hold(muk, "Black Sludge");
            Hold(thief, "Black Sludge");
            muk.CurrentHP = 100;

            Clash(state, engine);

            Assert.Equal(100 + 320 / 16, muk.CurrentHP);
            Assert.Equal(320 - 320 / 8, thief.CurrentHP);
        }

        [Fact]
        public void ChoiceBand_BoostsAttack_AndLocksTheMenu()
        {
            var strike = Move("Strike");
            var other = Move("Other");
            var bander = Mon("Bander", moves: new[] { strike, other });

            var (plain, plainEngine) = Duel(17, Mon("Bander2", moves: Move("Strike")), Mon("Wall", hp: 600, speed: 1));
            Clash(plain, plainEngine);
            int baseline = 600 - plain.Player2.ActivePokemon.CurrentHP;

            var (state, engine) = Duel(17, bander, Mon("Wall", hp: 600, speed: 1));
            Hold(bander, "Choice Band");

            engine.RunTurn(
                MoveAction(state, bander, strike),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            int banded = 600 - state.Player2.ActivePokemon.CurrentHP;
            Assert.True(banded > baseline, $"banded {banded} should beat {baseline}");

            var legal = engine.GetLegalActions(state.Player1)
                .Where(a => a.Move != null)
                .Select(a => a.Move!.Name)
                .ToList();

            Assert.Equal(new List<string> { "Strike" }, legal);
        }

        [Fact]
        public void ChoiceLock_ReleasesOnSwitching()
        {
            var strike = Move("Strike");
            var other = Move("Other");
            var scarfer = Mon("Scarfer", moves: new[] { strike, other });
            var friend = Mon("Friend");

            var (state, engine) = Battle(19,
                new List<PokemonState> { scarfer, friend },
                new List<PokemonState> { Mon("Watcher", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)) });

            Hold(scarfer, "Choice Scarf");

            engine.RunTurn(
                MoveAction(state, scarfer, strike),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.Equal("Strike", scarfer.ChoiceLockedMoveName);

            engine.RunTurn(
                SwitchAction(state, scarfer, friend),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));

            Assert.Null(scarfer.ChoiceLockedMoveName);
        }

        [Fact]
        public void LifeOrb_BoostsAndBites()
        {
            var (plain, plainEngine) = Duel(23, Mon("Orber2", moves: Move("Strike")), Mon("Wall", hp: 600, speed: 1));
            Clash(plain, plainEngine);
            int baseline = 600 - plain.Player2.ActivePokemon.CurrentHP;

            var orber = Mon("Orber", hp: 200, moves: Move("Strike"));
            var (state, engine) = Duel(23, orber, Mon("Wall", hp: 600, speed: 1));
            Hold(orber, "Life Orb");
            Clash(state, engine);

            int boosted = 600 - state.Player2.ActivePokemon.CurrentHP;

            Assert.Equal((int)(baseline * 1.3), boosted);
            Assert.True(orber.CurrentHP <= 200 - 20, "the orb should have taken its tenth");
        }

        [Fact]
        public void FocusSash_SavesFromFull_Once()
        {
            var sasher = Mon("Sasher", hp: 60, moves: Move("Wait", category: MoveCategory.Status, power: 0));
            var crusher = Mon("Crusher", attack: 500, speed: 1);

            var (state, engine) = Duel(29, sasher, crusher);
            Hold(sasher, "Focus Sash");

            Clash(state, engine);

            Assert.Equal(1, sasher.CurrentHP);
            Assert.Null(sasher.HeldItemId);
            Assert.True(sasher.LostItem);
            Assert.True(LogContains(state, "Focus Sash"));

            Clash(state, engine);
            Assert.True(sasher.Fainted, "no second save without the sash");
        }

        [Fact]
        public void SitrusBerry_KicksInAtHalf()
        {
            var berry = Mon("Berry", hp: 200, moves: Move("Wait", category: MoveCategory.Status, power: 0));
            var hitter = Mon("Hitter", attack: 300, speed: 1);

            var (state, engine) = Duel(31, berry, hitter);
            Hold(berry, "Sitrus Berry");
            berry.CurrentHP = 120;

            // The slower hit (57-102 with variance and crits) always lands
            // in the berry's window: under half, never fatal.
            Clash(state, engine);

            Assert.False(berry.Fainted);
            Assert.Null(berry.HeldItemId);
            Assert.True(berry.LostItem);
            Assert.True(LogContains(state, "Sitrus"));
        }

        [Fact]
        public void LumBerry_CuresTheFreshStatus()
        {
            var wave = Move("Wave", category: MoveCategory.Status, power: 0, accuracy: 100);
            wave.InflictStatus = StatusCondition.Paralysis;

            var (state, engine) = Duel(37, Mon("Caster", moves: wave), Mon("Lummed", speed: 1,
                moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            var lummed = state.Player2.ActivePokemon;
            Hold(lummed, "Lum Berry");

            Clash(state, engine);

            Assert.Equal(StatusCondition.None, lummed.Status);
            Assert.Null(lummed.HeldItemId);
            Assert.True(LogContains(state, "Lum Berry"));
        }

        [Fact]
        public void ResistBerry_HalvesTheSuperEffectiveHit()
        {
            var quake = Move("Quake", type: PokemonType.Ground);

            var (plain, plainEngine) = Duel(41, Mon("Shaker", moves: Move("Quake", type: PokemonType.Ground)),
                Mon("Steel", type: PokemonType.Steel, hp: 600, speed: 1));
            Clash(plain, plainEngine);
            int bare = 600 - plain.Player2.ActivePokemon.CurrentHP;

            var (state, engine) = Duel(41, Mon("Shaker", moves: quake),
                Mon("Steel", type: PokemonType.Steel, hp: 600, speed: 1));
            var steel = state.Player2.ActivePokemon;
            Hold(steel, "Shuca Berry");
            Clash(state, engine);

            int cushioned = 600 - steel.CurrentHP;

            Assert.Equal(bare / 2, cushioned);
            Assert.Null(steel.HeldItemId);
        }

        [Fact]
        public void RockyHelmet_PunishesContact()
        {
            var contact = Move("Slam");
            contact.IsContact = true;

            var (state, engine) = Duel(43, Mon("Slammer", hp: 300, moves: contact),
                Mon("Helmed", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            Hold(state.Player2.ActivePokemon, "Rocky Helmet");

            Clash(state, engine);

            Assert.Equal(300 - 300 / 6, state.Player1.ActivePokemon.CurrentHP);
        }

        [Fact]
        public void WeaknessPolicy_TurnsPainIntoPower()
        {
            var quake = Move("Quake", type: PokemonType.Ground);

            var (state, engine) = Duel(47, Mon("Shaker", moves: quake),
                Mon("Steel", type: PokemonType.Steel, hp: 600, speed: 1,
                    moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            var steel = state.Player2.ActivePokemon;
            Hold(steel, "Weakness Policy");

            Clash(state, engine);

            Assert.Equal(2, steel.AttackStage);
            Assert.Equal(2, steel.SpAttackStage);
            Assert.Null(steel.HeldItemId);
        }

        [Fact]
        public void AirBalloon_FloatsUntilPopped()
        {
            var (state, engine) = Duel(53, Mon("Hitter"), Mon("Floater", hp: 400, speed: 1,
                moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            var floater = state.Player2.ActivePokemon;
            Hold(floater, "Air Balloon");

            Assert.False(Grounding.IsGrounded(state, floater));

            Clash(state, engine);

            Assert.True(LogContains(state, "popped"));
            Assert.True(Grounding.IsGrounded(state, floater));
        }

        [Fact]
        public void AssaultVest_BarsStatusMoves()
        {
            var strike = Move("Strike");
            var plot = Move("Plot", category: MoveCategory.Status, power: 0);
            var vested = Mon("Vested", moves: new[] { strike, plot });

            var (state, engine) = Duel(59, vested, Mon("Other"));
            Hold(vested, "Assault Vest");

            var names = engine.GetLegalActions(state.Player1)
                .Where(a => a.Move != null)
                .Select(a => a.Move!.Name)
                .ToList();

            Assert.Contains("Strike", names);
            Assert.DoesNotContain("Plot", names);

            double spd = StatResolver.GetStat(state, vested, "SpDefense");
            Assert.Equal(150, (int)spd);
        }

        [Fact]
        public void TypeBooster_AddsTwentyPercent()
        {
            var jet = Move("Jet", type: PokemonType.Water, category: MoveCategory.Special);

            var (plain, plainEngine) = Duel(61, Mon("Squirt", moves: Move("Jet", type: PokemonType.Water, category: MoveCategory.Special)),
                Mon("Wall", hp: 600, speed: 1));
            Clash(plain, plainEngine);
            int bare = 600 - plain.Player2.ActivePokemon.CurrentHP;

            var squirt = Mon("Squirt", moves: jet);
            var (state, engine) = Duel(61, squirt, Mon("Wall", hp: 600, speed: 1));
            Hold(squirt, "Mystic Water");
            Clash(state, engine);

            int boosted = 600 - state.Player2.ActivePokemon.CurrentHP;
            Assert.Equal((int)(bare * 1.2), boosted);
        }

        [Fact]
        public void FlameOrb_BurnsItsHolder_ForGuts()
        {
            var wait = Move("Wait", category: MoveCategory.Status, power: 0);
            var (state, engine) = Duel(67, Mon("Bruiser", moves: wait),
                Mon("Watcher", speed: 1, moves: Move("Wait2", category: MoveCategory.Status, power: 0)));

            var bruiser = state.Player1.ActivePokemon;
            Hold(bruiser, "Flame Orb");

            Clash(state, engine);

            Assert.Equal(StatusCondition.Burn, bruiser.Status);
        }

        [Fact]
        public void LightClay_StretchesTheScreens()
        {
            var reflect = WithEffects(Move("Wall", category: MoveCategory.Status, power: 0), "Reflect");
            var (state, engine) = Duel(109, Mon("Setter", moves: reflect),
                Mon("Watcher", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            Hold(state.Player1.ActivePokemon, "Light Clay");

            Clash(state, engine);

            Assert.Equal(7, state.ReflectTurnsP1);   // 8, minus this end of turn
        }

        [Fact]
        public void WeatherRock_StretchesTheWeather()
        {
            var dance = Move("Dance", category: MoveCategory.Status, power: 0);
            dance.SetWeather = WeatherType.Rain;

            var (state, engine) = Duel(71, Mon("Dancer", moves: dance),
                Mon("Watcher", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            Hold(state.Player1.ActivePokemon, "Damp Rock");

            Clash(state, engine);

            Assert.Equal(WeatherType.Rain, state.Environment.Weather);
            Assert.Equal(7, state.Environment.WeatherTurns);   // 8, minus this end of turn
        }

        [Fact]
        public void Trick_SwapsItems_BothWays()
        {
            var trick = WithEffects(Move("Trick", category: MoveCategory.Status, power: 0, accuracy: 100), "TrickItem");
            var trickster = Mon("Trickster", moves: trick);
            var victim = Mon("Victim", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0));

            var (state, engine) = Duel(73, trickster, victim);
            Hold(trickster, "Choice Scarf");
            Hold(victim, "Leftovers");

            Clash(state, engine);

            Assert.Equal("leftovers", trickster.HeldItemId);
            Assert.Equal("choicescarf", victim.HeldItemId);
        }

        [Fact]
        public void KnockOff_BoostsAndRemoves()
        {
            var knock = WithEffects(Move("Knock", type: PokemonType.Dark), "KnockOff", "KnockOffRemove");
            var (plain, plainEngine) = Duel(79, Mon("Knocker", moves: WithEffects(Move("Knock", type: PokemonType.Dark), "KnockOff", "KnockOffRemove")),
                Mon("Holder", hp: 600, speed: 1));
            Clash(plain, plainEngine);
            int bare = 600 - plain.Player2.ActivePokemon.CurrentHP;

            var (state, engine) = Duel(79, Mon("Knocker", moves: knock), Mon("Holder", hp: 600, speed: 1));
            var holder = state.Player2.ActivePokemon;
            Hold(holder, "Leftovers");
            Clash(state, engine);

            int boosted = 600 - holder.CurrentHP;

            Assert.Equal(bare * 3 / 2, boosted);
            Assert.Null(holder.HeldItemId);
            Assert.True(holder.LostItem);
            Assert.True(LogContains(state, "knocked off"));
        }

        [Fact]
        public void Thief_StealsWhenEmptyHanded()
        {
            var thief = WithEffects(Move("Mug", type: PokemonType.Dark), "StealItem");
            var (state, engine) = Duel(83, Mon("Mugger", moves: thief),
                Mon("Mark", hp: 400, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            var mark = state.Player2.ActivePokemon;
            Hold(mark, "Leftovers");

            Clash(state, engine);

            Assert.Equal("leftovers", state.Player1.ActivePokemon.HeldItemId);
            Assert.Null(mark.HeldItemId);
        }

        [Fact]
        public void Fling_ThrowsTheItemAway_AndFailsEmpty()
        {
            var fling = WithEffects(Move("Fling", type: PokemonType.Dark, power: 30), "FlingItem", "FlingThrow");
            var flinger = Mon("Flinger", moves: fling);

            var (state, engine) = Duel(89, flinger,
                Mon("Target", hp: 400, speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            Hold(flinger, "Toxic Orb");

            Clash(state, engine);

            Assert.Null(flinger.HeldItemId);
            Assert.Equal(StatusCondition.Toxic, state.Player2.ActivePokemon.Status);

            Clash(state, engine);
            Assert.True(LogContains(state, "But it failed!"));
        }

        [Fact]
        public void Poltergeist_NeedsAnItemToHaunt()
        {
            var polt = WithEffects(Move("Haunt", type: PokemonType.Ghost, power: 110), "PoltergeistGate");
            var (state, engine) = Duel(97, Mon("Ghost", moves: polt),
                Mon("Empty", type: PokemonType.Ghost, hp: 400, speed: 1,
                    moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            Clash(state, engine);

            Assert.Equal(400, state.Player2.ActivePokemon.CurrentHP);
            Assert.True(LogContains(state, "But it failed!"));

            Hold(state.Player2.ActivePokemon, "Leftovers");
            Clash(state, engine);

            Assert.True(state.Player2.ActivePokemon.CurrentHP < 400, "with an item the haunt lands");
        }

        [Fact]
        public void Unburden_DoublesSpeedAfterTheItemGoes()
        {
            var (state, _) = Duel(101, Mon("Acrobat", speed: 80), Mon("Other"));
            var acrobat = state.Player1.ActivePokemon;
            acrobat.AbilityId = "unburden";

            double before = StatResolver.GetStat(state, acrobat, "Speed");

            acrobat.HeldItemId = null;
            acrobat.LostItem = true;

            double after = StatResolver.GetStat(state, acrobat, "Speed");

            Assert.Equal(before * 2, after);
        }

        [Fact]
        public void Eviolite_AndScarf_MultiplyTheRightStats()
        {
            var (state, _) = Duel(103, Mon("Chansey", defense: 100, speed: 100), Mon("Other"));
            var chansey = state.Player1.ActivePokemon;

            Hold(chansey, "Eviolite");
            Assert.Equal(150, (int)StatResolver.GetStat(state, chansey, "Defense"));
            Assert.Equal(150, (int)StatResolver.GetStat(state, chansey, "SpDefense"));

            Hold(chansey, "Choice Scarf");
            Assert.Equal(150, (int)StatResolver.GetStat(state, chansey, "Speed"));
            Assert.Equal(100, (int)StatResolver.GetStat(state, chansey, "Defense"));
        }

        [Fact]
        public void MegaStone_IsInert_ButCarried()
        {
            var (state, engine) = Duel(107, Mon("Tyranitar", moves: Move("Strike")), Mon("Wall", hp: 600, speed: 1));
            var tyranitar = state.Player1.ActivePokemon;
            Hold(tyranitar, "Tyranitarite");

            Clash(state, engine);

            Assert.Equal("tyranitarite", tyranitar.HeldItemId);

            double attack = StatResolver.GetStat(state, tyranitar, "Attack");
            Assert.Equal(100, (int)attack);
        }
    }
}