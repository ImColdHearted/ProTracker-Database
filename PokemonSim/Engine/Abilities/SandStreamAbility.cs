using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>Section 154: sets Sandstorm - it set Sun.</summary>
    public class SandStreamAbility : IAbility
    {
        public string Id => "sandstream";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield break;
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        {
            if (state.Environment.Weather == WeatherType.Sandstorm)
                return;

            state.Environment.Weather = WeatherType.Sandstorm;
            state.Environment.WeatherTurns = 5;

            state.Log.Write("A sandstorm has begun!");
        }
    }
}