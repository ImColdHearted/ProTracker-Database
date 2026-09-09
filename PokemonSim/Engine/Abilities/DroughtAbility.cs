using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    public class DroughtAbility : IAbility
    {
        public string Id => "drought";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield break;
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        {
            if (state.Environment.Weather == WeatherType.Sun)
                return;

            state.Environment.Weather = WeatherType.Sun;
            state.Environment.WeatherTurns = 5;

            state.Log.Write("Drought made the sun shine brightly!");
        }
    }
}