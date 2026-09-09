using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    public class DrizzleAbility : IAbility
    {
        public string Id => "drizzle";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield break;
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        {
            if (state.Environment.Weather == WeatherType.Rain)
                return;

            state.Environment.Weather = WeatherType.Rain;
            state.Environment.WeatherTurns = 5;

            state.Log.Write("It started raining due to Drizzle!");
        }
    }
}