using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    // Section 154: namespace fixed (was lowercase "pokemonsim"), lines to
    // the battle's log, and a burn faint is announced.
    public static class BurnEffect
    {
        public static void Apply(BattleState state)
        {
            ApplyTo(state, state.Player1.ActivePokemon);
            ApplyTo(state, state.Player2.ActivePokemon);
        }

        static void ApplyTo(BattleState state, PokemonState pokemon)
        {
            if (pokemon.Status != StatusCondition.Burn)
                return;

            if (pokemon.HasMagicGuard) return;

            if (pokemon.Fainted) return;

            int damage = System.Math.Max(1, pokemon.MaxHP / 16);

            pokemon.CurrentHP -= damage;

            if (pokemon.CurrentHP < 0)
                pokemon.CurrentHP = 0;

            state.Log.Write(
                $"{pokemon.Species} is hurt by its burn! ({pokemon.CurrentHP}/{pokemon.MaxHP})"
            );

            if (pokemon.Fainted)
                state.Log.Write($"{pokemon.Species} fainted!");
        }
    }
}