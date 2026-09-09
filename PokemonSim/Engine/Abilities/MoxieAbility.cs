using System;
using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>Attack rises when this Pokemon's hit faints the target.
    /// Section 154: extracted from where it sat NESTED INSIDE
    /// RegeneratorAbility (reachable only through a "using static"), and it
    /// listens for faints it caused (evt.Source, set by MoveResolver's
    /// faint event) rather than its own faint, which is what the old check
    /// rewarded.</summary>
    public class MoxieAbility : IAbility
    {
        public string Id => "moxie";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield break;
        }

        public void OnAttach(PokemonState pokemon, BattleState state) =>
            Subscribe(pokemon, state);

        internal static void Subscribe(PokemonState pokemon, BattleState state)
        {
            if (pokemon.AbilityState.ContainsKey("moxie.subscribed"))
                return;

            pokemon.AbilityState["moxie.subscribed"] = true;

            state.Events.Subscribe((evt, s) =>
            {
                if (evt.Type == BattleEventType.PokemonFainted &&
                    evt.Source == pokemon &&
                    evt.Target != pokemon &&
                    !pokemon.Fainted)
                {
                    MoveResolver.ApplyStatChange(s, pokemon, "Attack", 1);
                    s.Log.Write($"{pokemon.Species}'s Moxie kicked in!");
                }
            });
        }
    }
}