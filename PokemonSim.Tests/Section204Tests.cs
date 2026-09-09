using System.Collections.Generic;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Abilities;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 204. Disguise, which was reported as not working and was not:
    /// nothing implemented it.
    ///
    /// "Disguise" appeared in exactly one place in this codebase - a list of
    /// form-changing ability NAMES in the card importer - and AbilityFactory
    /// had never heard of it, so a Mimikyu built with it got no ability
    /// object at all and battled with nothing. That is the first test here,
    /// and it is the one that would have caught this: an ability a roster
    /// can name should be an ability the factory can build.
    ///
    /// The rule: the first damaging move to CONNECT does nothing except
    /// break the disguise, which costs an eighth of the maximum. Everything
    /// after lands normally, for the rest of the battle - the disguise does
    /// not come back when it switches out.
    ///
    /// The phase is the subtle part. BeforeMove runs before the accuracy
    /// roll, so a disguise there would break on a move that then missed;
    /// BeforeDamage runs once the move has connected.
    /// </summary>
    public class Section204Tests
    {
        static Section204Tests()
        {
            MoveDex.EnsureLoaded();
        }

        static void Give(BattleState state, PokemonState pokemon, string ability)
        {
            pokemon.AbilityId = ability;
            AbilityFactory.Restore(pokemon, state);
        }

        [Fact]
        public void TheFactoryKnowsDisguise()
        {
            Assert.Contains("disguise", AbilityFactory.SupportedIds);
            Assert.True(AbilityFactory.TryCreate("Disguise", out IAbility ability));
            Assert.IsType<DisguiseAbility>(ability);
            Assert.Contains(ability.GetEffects(), e => e is DisguiseEffect);
        }

        [Fact]
        public void ItSpeaksForTheDefender()
        {
            Assert.Equal(EffectSide.DefenderOnly, new DisguiseEffect().Side);
        }

        [Fact]
        public void ItRunsOnceTheMoveHasConnected_NotBeforeTheAccuracyRoll()
        {
            // BeforeMove happens before accuracy in MoveResolver, so a
            // disguise there would be spent by a move that then missed.
            Assert.Equal(MovePhase.BeforeDamage, new DisguiseEffect().Phase);
        }

        [Fact]
        public void TheFirstHitIsBlockedAndCostsAnEighth()
        {
            var (state, engine) = Duel(2040, Mon("Hitter"), Mon("Mimikyu", speed: 1));

            PokemonState mimikyu = state.Player2.ActivePokemon;
            Give(state, mimikyu, "disguise");

            Clash(state, engine);

            // No damage from the move at all - only the disguise breaking.
            Assert.Equal(mimikyu.MaxHP - mimikyu.MaxHP / 8, mimikyu.CurrentHP);
            Assert.True(DisguiseEffect.IsBusted(mimikyu));
            Assert.True(LogContains(state, "disguise served as a decoy"));
        }

        [Fact]
        public void TheSecondHitLandsNormally()
        {
            var (state, engine) = Duel(2041, Mon("Hitter"), Mon("Mimikyu", hp: 600, speed: 1));

            PokemonState mimikyu = state.Player2.ActivePokemon;
            Give(state, mimikyu, "disguise");

            Clash(state, engine);
            int afterTheDisguise = mimikyu.CurrentHP;

            Clash(state, engine);

            Assert.True(mimikyu.CurrentHP < afterTheDisguise);
        }

        [Fact]
        public void AStatusMoveGoesStraightThrough()
        {
            var wave = Move("Zap Wave", type: PokemonType.Electric,
                category: MoveCategory.Status, power: 0, accuracy: 100);
            wave.InflictStatus = StatusCondition.Paralysis;

            var (state, engine) = Duel(2042, Mon("Caster", moves: wave), Mon("Mimikyu", speed: 1));

            PokemonState mimikyu = state.Player2.ActivePokemon;
            Give(state, mimikyu, "disguise");

            Clash(state, engine);

            Assert.Equal(StatusCondition.Paralysis, mimikyu.Status);

            // A status move neither spends the disguise nor costs it a point.
            Assert.False(DisguiseEffect.IsBusted(mimikyu));
            Assert.Equal(mimikyu.MaxHP, mimikyu.CurrentHP);
        }

        [Fact]
        public void MagicGuardStopsTheCostButNotTheBlock()
        {
            var (state, engine) = Duel(2043, Mon("Hitter"), Mon("Mimikyu", speed: 1));

            PokemonState mimikyu = state.Player2.ActivePokemon;
            Give(state, mimikyu, "disguise");
            mimikyu.HasMagicGuard = true;

            Clash(state, engine);

            Assert.Equal(mimikyu.MaxHP, mimikyu.CurrentHP);
            Assert.True(DisguiseEffect.IsBusted(mimikyu));
        }

        [Fact]
        public void ABustedDisguiseDoesNotComeBackOnASwitch()
        {
            var mimikyu = Mon("Mimikyu", hp: 600);
            var bench = Mon("Bench", hp: 600);

            var (state, engine) = Battle(2044,
                new List<PokemonState> { mimikyu, bench },
                new List<PokemonState> { Mon("Hitter", speed: 1) });

            Give(state, mimikyu, "disguise");

            PokemonState hitter = state.Player2.ActivePokemon;

            // Turn 1: the disguise takes the hit.
            engine.RunTurn(
                MoveAction(state, mimikyu, mimikyu.Moves[0]),
                MoveAction(state, hitter, hitter.Moves[0]));

            Assert.True(DisguiseEffect.IsBusted(mimikyu));

            // Out and back again.
            engine.RunTurn(
                SwitchAction(state, mimikyu, bench),
                MoveAction(state, hitter, hitter.Moves[0]));

            engine.RunTurn(
                SwitchAction(state, bench, mimikyu),
                MoveAction(state, hitter, hitter.Moves[0]));

            Assert.Same(mimikyu, state.Player1.ActivePokemon);
            Assert.True(DisguiseEffect.IsBusted(mimikyu));
        }
    }
}