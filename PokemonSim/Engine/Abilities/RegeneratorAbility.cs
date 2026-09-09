using System;
using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>Heals a third of max HP on switching out. Section 154:
    /// extracted from the bottom of IMoveEffect.cs, and the event
    /// subscription moved into Subscribe so AbilityFactory.Restore can
    /// re-attach it on a CLONED battle - the old shared EventManager kept
    /// listeners bound to the original battle's Pokemon objects, so clones
    /// either healed the real battle's Regenerator or nobody's.</summary>
    public class RegeneratorAbility : IAbility
    {
        public string Id => "regenerator";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield break;
        }

        public void OnAttach(PokemonState pokemon, BattleState state) =>
            Subscribe(pokemon, state);

        internal static void Subscribe(PokemonState pokemon, BattleState state)
        {
            if (pokemon.AbilityState.ContainsKey("regenerator.subscribed"))
                return;

            pokemon.AbilityState["regenerator.subscribed"] = true;

            state.Events.Subscribe((evt, s) =>
            {
                if (evt.Type == BattleEventType.SwitchOut &&
                    evt.Source == pokemon &&
                    !pokemon.Fainted &&
                    pokemon.CurrentHP < pokemon.MaxHP)
                {
                    int heal = pokemon.MaxHP / 3;

                    pokemon.CurrentHP = Math.Min(
                        pokemon.MaxHP,
                        pokemon.CurrentHP + heal
                    );

                    s.Log.Write($"{pokemon.Species} restored HP with Regenerator!");
                }
            });
        }
    }
}