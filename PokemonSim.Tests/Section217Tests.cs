using System.Collections.Generic;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Models;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 217. BattleEventType.BeforeMove, which existed in the enum
    /// from the beginning and which nothing ever dispatched.
    ///
    /// That mattered the moment anything wanted to watch a battle rather
    /// than play one. DamageDealt is reported, so a watcher could see a hit
    /// land - but a status move deals no damage, so Thunder Wave, Swords
    /// Dance and Toxic went past completely unseen. There was no event that
    /// meant "this Pokemon just used this move".
    ///
    /// There is one now, dispatched at the single point where a move use is
    /// certain: after Truant, flinch, sleep, freeze and paralysis have all
    /// been passed, after the PP has been spent, alongside the announcement
    /// in the log. Nothing in the engine subscribes to it and the resolver
    /// reads nothing back from it, so a battle plays out identically whether
    /// anyone is listening.
    ///
    /// The last test is the one that keeps the tracker's frame rate: the
    /// Monte Carlo strategy clones a battle thousands of times per turn, and
    /// a clone must not inherit the UI's subscription.
    /// </summary>
    public class Section217Tests
    {
        static Section217Tests()
        {
            MoveDex.EnsureLoaded();
        }

        /// <summary>Collects every event a battle dispatches, in order.</summary>
        static List<BattleEvent> Watch(BattleState state)
        {
            var seen = new List<BattleEvent>();

            state.Events.Subscribe((battleEvent, _) => seen.Add(battleEvent));

            return seen;
        }

        static List<BattleEvent> Moves(List<BattleEvent> seen) =>
            seen.FindAll(e => e.Type == BattleEventType.BeforeMove);

        [Fact]
        public void BeforeMove_is_dispatched_when_a_damaging_move_is_used()
        {
            MoveState tackle = Move("Tackle");
            PokemonState attacker = Mon("Rattata", moves: tackle);
            PokemonState defender = Mon("Pidgey");

            (BattleState state, _) = Duel(1, attacker, defender);

            List<BattleEvent> seen = Watch(state);

            MoveResolver.Resolve(state, attacker, defender, tackle);

            Assert.Single(Moves(seen));
        }

        /// <summary>The gap this section closes. A status move deals no
        /// damage, so before now it produced no event at all.</summary>
        [Fact]
        public void BeforeMove_is_dispatched_for_a_status_move_too()
        {
            MoveState growl = Move("Growl", category: MoveCategory.Status, power: 0);
            PokemonState attacker = Mon("Rattata", moves: growl);
            PokemonState defender = Mon("Pidgey");

            (BattleState state, _) = Duel(1, attacker, defender);

            List<BattleEvent> seen = Watch(state);

            MoveResolver.Resolve(state, attacker, defender, growl);

            Assert.Single(Moves(seen));
            Assert.DoesNotContain(seen, e => e.Type == BattleEventType.DamageDealt);
        }

        [Fact]
        public void BeforeMove_carries_the_attacker_the_defender_and_the_move()
        {
            MoveState ember = Move("Ember", type: PokemonType.Fire,
                category: MoveCategory.Special, power: 40);

            PokemonState attacker = Mon("Charmander", PokemonType.Fire, moves: ember);
            PokemonState defender = Mon("Bulbasaur", PokemonType.Grass);

            (BattleState state, _) = Duel(2, attacker, defender);

            List<BattleEvent> seen = Watch(state);

            MoveResolver.Resolve(state, attacker, defender, ember);

            BattleEvent used = Assert.Single(Moves(seen));

            Assert.Same(attacker, used.Source);
            Assert.Same(defender, used.Target);
            Assert.Same(ember, used.Move);
        }

        /// <summary>A move that never happens is not reported. The event
        /// sits below every gate for exactly this reason.</summary>
        [Fact]
        public void BeforeMove_is_not_dispatched_when_a_flinch_stops_the_move()
        {
            MoveState tackle = Move("Tackle");
            PokemonState attacker = Mon("Rattata", moves: tackle);
            PokemonState defender = Mon("Pidgey");

            (BattleState state, _) = Duel(3, attacker, defender);

            List<BattleEvent> seen = Watch(state);

            attacker.Flinched = true;

            MoveResolver.Resolve(state, attacker, defender, tackle);

            Assert.Empty(Moves(seen));
        }

        [Fact]
        public void BeforeMove_arrives_before_the_damage_it_causes()
        {
            MoveState tackle = Move("Tackle", accuracy: 0);
            PokemonState attacker = Mon("Rattata", moves: tackle);
            PokemonState defender = Mon("Pidgey");

            (BattleState state, _) = Duel(4, attacker, defender);

            List<BattleEvent> seen = Watch(state);

            MoveResolver.Resolve(state, attacker, defender, tackle);

            int used = seen.FindIndex(e => e.Type == BattleEventType.BeforeMove);
            int hit = seen.FindIndex(e => e.Type == BattleEventType.DamageDealt);

            Assert.True(used >= 0, "the move was never reported");
            Assert.True(hit > used, "damage was reported before the move that caused it");
        }

        [Fact]
        public void Both_sides_of_a_turn_are_reported()
        {
            PokemonState first = Mon("Rattata", speed: 200, moves: Move("Tackle", accuracy: 0));
            PokemonState second = Mon("Pidgey", speed: 10, moves: Move("Gust", accuracy: 0));

            (BattleState state, BattleEngine engine) = Duel(5, first, second);

            List<BattleEvent> seen = Watch(state);

            Clash(state, engine);

            List<BattleEvent> used = Moves(seen);

            Assert.Equal(2, used.Count);
            Assert.Same(first, used[0].Source);
            Assert.Same(second, used[1].Source);
        }

        /// <summary>Section 217. The property that keeps the simulator
        /// usable: BattleState.Clone gives the copy a fresh EventManager, so
        /// the thousands of battles the Monte Carlo strategy plays out in
        /// its head never reach a subscriber attached to the real one.</summary>
        [Fact]
        public void A_cloned_battle_does_not_reach_the_original_subscribers()
        {
            MoveState tackle = Move("Tackle");
            PokemonState attacker = Mon("Rattata", moves: tackle);
            PokemonState defender = Mon("Pidgey");

            (BattleState state, _) = Duel(6, attacker, defender);

            List<BattleEvent> seen = Watch(state);

            BattleState clone = state.Clone();

            MoveResolver.Resolve(
                clone,
                clone.Player1.ActivePokemon,
                clone.Player2.ActivePokemon,
                clone.Player1.ActivePokemon.Moves[0]);

            Assert.Empty(seen);
        }
    }
}