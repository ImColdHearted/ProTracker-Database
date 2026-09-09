using System;
using PokemonSim.Models;

namespace PokemonSim.Engine
{
    /// <summary>End-of-turn weather. Section 154: chip damage clamps at
    /// zero and announces the faint, Magic Guard blocks it, lines go to the
    /// battle's own log, and the clock only clears weather that is actually
    /// running (permanent weather with WeatherTurns 0 used to be wiped by
    /// the unconditional check).</summary>
    public static class WeatherEffects
    {
        public static void Apply(BattleState state)
        {
            var env = state.Environment;

            if (env.Weather == WeatherType.None)
                return;

            ApplyTo(state, state.Player1.ActivePokemon, env);
            ApplyTo(state, state.Player2.ActivePokemon, env);

            if (env.WeatherTurns > 0)
            {
                env.WeatherTurns--;

                if (env.WeatherTurns == 0)
                {
                    env.Weather = WeatherType.None;
                    state.Log.Write("The weather returned to normal.");
                }
            }
        }

        static void ApplyTo(BattleState state, PokemonState p, BattleEnvironment env)
        {
            if (p.Fainted || p.HasMagicGuard)
                return;

            // Section 158: the weather-dwelling abilities shrug the chip off.
            string ability = Abilities.AbilityFactory.Normalize(p.AbilityId);

            bool hurt = false;

            if (env.Weather == WeatherType.Sandstorm)
            {
                if (!p.Types.Contains(PokemonType.Rock) &&
                    !p.Types.Contains(PokemonType.Ground) &&
                    !p.Types.Contains(PokemonType.Steel) &&
                    ability != "sandrush" && ability != "sandforce")
                {
                    hurt = true;
                    state.Log.Write($"{p.Species} is buffeted by the sandstorm!");
                }
            }

            if (env.Weather == WeatherType.Hail)
            {
                if (!p.Types.Contains(PokemonType.Ice) &&
                    ability != "snowcloak" && ability != "slushrush")
                {
                    hurt = true;
                    state.Log.Write($"{p.Species} is pelted by hail!");
                }
            }

            if (hurt)
            {
                int damage = Math.Max(1, p.MaxHP / 16);

                p.CurrentHP = Math.Max(0, p.CurrentHP - damage);

                if (p.Fainted)
                    state.Log.Write($"{p.Species} fainted!");
            }
        }
    }
}