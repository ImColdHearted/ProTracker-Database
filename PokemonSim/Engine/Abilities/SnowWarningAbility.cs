using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>Section 154: sets Hail with its own message - it set Sun and announced a sandstorm.</summary>
    public class SnowWarningAbility : IAbility
    {
        public string Id => "snowwarning";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield break;
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        {
            if (state.Environment.Weather == WeatherType.Hail)
                return;

            state.Environment.Weather = WeatherType.Hail;
            state.Environment.WeatherTurns = 5;

            state.Log.Write("It started to hail!");
        }
    }
}