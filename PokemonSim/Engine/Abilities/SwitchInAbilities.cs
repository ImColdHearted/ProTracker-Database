using System.Collections.Generic;
using System.Linq;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>
    /// Section 158. Switch-in abilities in the Intimidate tradition -
    /// their whole life is OnAttach, which BattleInitializer and
    /// SwitchResolver already run on entry.
    /// </summary>
    public class DownloadAbility : IAbility
    {
        public string Id => "download";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield break;
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        {
            var opponent = state.GetOpponents(pokemon).FirstOrDefault();

            if (opponent == null)
                return;

            string boosted = opponent.Stats.Defense < opponent.Stats.SpDefense
                ? "Attack"
                : "SpAttack";

            state.Log.Write($"{pokemon.Species} downloaded its opponent's data!");
            MoveResolver.ApplyStatChange(state, pokemon, boosted, 1);
        }
    }

    /// <summary>Trace: copies the opponent's ability and runs its entry
    /// behaviour (a traced Intimidate still lowers Attack).</summary>
    public class TraceAbility : IAbility
    {
        public string Id => "trace";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield break;
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        {
            var opponent = state.GetOpponents(pokemon).FirstOrDefault();

            if (opponent == null || string.IsNullOrWhiteSpace(opponent.AbilityId) ||
                AbilityFactory.Normalize(opponent.AbilityId) == "trace")
            {
                return;
            }

            pokemon.AbilityId = opponent.AbilityId;
            pokemon.HasMagicGuard = false;
            AbilityFactory.Restore(pokemon, state);

            state.Log.Write($"{pokemon.Species} traced {opponent.Species}'s {opponent.AbilityId}!");

            pokemon.Ability?.OnAttach(pokemon, state);
        }
    }

    /// <summary>Air Lock / Cloud Nine, simplified: entering clears the
    /// running weather (true suppression - weather resuming when the
    /// holder leaves - is not modeled; the §158 guide says so).</summary>
    public class AirLockAbility : IAbility
    {
        public string Id => "airlock";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield break;
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        {
            if (state.Environment.Weather == WeatherType.None)
                return;

            state.Environment.Weather = WeatherType.None;
            state.Environment.WeatherTurns = 0;
            state.Log.Write("The effects of the weather disappeared!");
        }
    }
}