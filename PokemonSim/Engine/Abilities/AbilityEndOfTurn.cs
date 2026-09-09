using System;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>
    /// Section 158. The end-of-turn abilities, run by BattleEngine right
    /// after the volatile batch. A direct id-keyed pass (like BurnEffect
    /// and WeatherEffects) rather than event subscriptions: no wiring to
    /// rebuild on clone, nothing to leak between battles. Poison Heal is
    /// the one exception - it lives inside PoisonEffect, where the poison
    /// it replaces is dealt.
    /// </summary>
    public static class AbilityEndOfTurn
    {
        public static void Apply(BattleState state)
        {
            ApplyFor(state, state.Player1.ActivePokemon, state.Player2.ActivePokemon);
            ApplyFor(state, state.Player2.ActivePokemon, state.Player1.ActivePokemon);
        }

        static void ApplyFor(BattleState state, PokemonState pokemon, PokemonState opponent)
        {
            if (pokemon.Fainted)
                return;

            var weather = state.Environment.Weather;

            switch (AbilityFactory.Normalize(pokemon.AbilityId))
            {
                case "speedboost":
                    if (pokemon.SpeedStage < 6)
                    {
                        state.Log.Write($"{pokemon.Species}'s Speed Boost kicked in!");
                        MoveResolver.ApplyStatChange(state, pokemon, "Speed", 1);
                    }
                    break;

                case "baddreams":
                    if (!opponent.Fainted && opponent.Status == StatusCondition.Sleep &&
                        !opponent.HasMagicGuard)
                    {
                        opponent.CurrentHP = Math.Max(0, opponent.CurrentHP - Math.Max(1, opponent.MaxHP / 8));
                        state.Log.Write($"{opponent.Species} is tormented by bad dreams!");

                        if (opponent.Fainted)
                            state.Log.Write($"{opponent.Species} fainted!");
                    }
                    break;

                case "raindish":
                    if (weather == WeatherType.Rain && pokemon.CurrentHP < pokemon.MaxHP)
                    {
                        pokemon.CurrentHP = Math.Min(pokemon.MaxHP,
                            pokemon.CurrentHP + Math.Max(1, pokemon.MaxHP / 16));
                        state.Log.Write($"{pokemon.Species} is refreshed by Rain Dish!");
                    }
                    break;

                case "dryskin":
                    if (weather == WeatherType.Rain && pokemon.CurrentHP < pokemon.MaxHP)
                    {
                        pokemon.CurrentHP = Math.Min(pokemon.MaxHP,
                            pokemon.CurrentHP + Math.Max(1, pokemon.MaxHP / 8));
                        state.Log.Write($"{pokemon.Species}'s Dry Skin drinks the rain!");
                    }
                    else if (weather == WeatherType.Sun && !pokemon.HasMagicGuard)
                    {
                        pokemon.CurrentHP = Math.Max(0, pokemon.CurrentHP - Math.Max(1, pokemon.MaxHP / 8));
                        state.Log.Write($"{pokemon.Species}'s Dry Skin suffers in the sun!");

                        if (pokemon.Fainted)
                            state.Log.Write($"{pokemon.Species} fainted!");
                    }
                    break;

                case "solarpower":
                    if (weather == WeatherType.Sun && !pokemon.HasMagicGuard)
                    {
                        pokemon.CurrentHP = Math.Max(0, pokemon.CurrentHP - Math.Max(1, pokemon.MaxHP / 8));
                        state.Log.Write($"{pokemon.Species} is worn out by Solar Power!");

                        if (pokemon.Fainted)
                            state.Log.Write($"{pokemon.Species} fainted!");
                    }
                    break;

                case "hydration":
                    if (weather == WeatherType.Rain && pokemon.Status != StatusCondition.None)
                    {
                        pokemon.Status = StatusCondition.None;
                        pokemon.SleepTurns = 0;
                        pokemon.ToxicCounter = 0;
                        state.Log.Write($"{pokemon.Species} was cured by Hydration!");
                    }
                    break;

                case "shedskin":
                    if (pokemon.Status != StatusCondition.None && state.Rng.Next(3) == 0)
                    {
                        pokemon.Status = StatusCondition.None;
                        pokemon.SleepTurns = 0;
                        pokemon.ToxicCounter = 0;
                        state.Log.Write($"{pokemon.Species} shed its skin and its status!");
                    }
                    break;
            }
        }
    }
}