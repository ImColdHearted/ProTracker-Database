using System;
using System.Collections.Generic;
using PokemonSim.Models;

namespace PokemonSim.Engine
{
    /// <summary>
    /// Section 154. Battle start: announce the match, send out both actives,
    /// and run their switch-in abilities (Intimidate, the weather setters,
    /// the event subscribers) in speed order - the step the old code
    /// referenced as a missing "BattleInitializer" class and never had, so
    /// no ability ever attached at battle start.
    /// </summary>
    public static class BattleInitializer
    {
        public static void Initialize(BattleState state)
        {
            if (state.Initialized)
                return;

            if (state.Player1.Team.Count == 0 || state.Player2.Team.Count == 0)
                throw new InvalidOperationException("Both players need at least one Pokemon.");

            state.Initialized = true;

            state.Log.Write($"Battle started: {state.Player1.Name} vs {state.Player2.Name}!");
            state.Log.Write($"{state.Player1.Name} sent out {state.Player1.ActivePokemon.Species}!");
            state.Log.Write($"{state.Player2.Name} sent out {state.Player2.ActivePokemon.Species}!");

            // Section 158: Slow Start counts from field entry.
            state.Player1.ActivePokemon.EnteredFieldTurn = state.TurnNumber;
            state.Player2.ActivePokemon.EnteredFieldTurn = state.TurnNumber;

            // §197: and neither starter has moved yet, so both may open with
            // Fake Out - the one case where "started in battle" has to count
            // the same as having just switched in.
            state.Player1.ActivePokemon.HasActedSinceEnteringField = false;
            state.Player2.ActivePokemon.HasActedSinceEnteringField = false;

            // Switch-in abilities, faster Pokemon first (ties: Player1).
            var order = new List<PokemonState> { state.Player1.ActivePokemon, state.Player2.ActivePokemon };

            if (StatResolver.GetStat(state, order[1], "Speed") > StatResolver.GetStat(state, order[0], "Speed"))
                (order[0], order[1]) = (order[1], order[0]);

            foreach (var pokemon in order)
                pokemon.Ability?.OnAttach(pokemon, state);
        }
    }
}