using System;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    // Section 154: moved into PokemonSim.Engine.Effects (was global), HP
    // clamps at zero, faints are announced.
    public static class PoisonEffect
    {
        public static void Apply(BattleState state)
        {
            ApplyToPokemon(state, state.Player1.ActivePokemon);
            ApplyToPokemon(state, state.Player2.ActivePokemon);
        }

        static void ApplyToPokemon(BattleState state, PokemonState p)
        {
            if (p.HasMagicGuard) return;
            if (p.Fainted) return;

            // Section 158: Poison Heal turns the poison into an eighth of
            // healing (the toxic clock stays untouched).
            if ((p.Status == StatusCondition.Poison || p.Status == StatusCondition.Toxic) &&
                Abilities.AbilityFactory.Normalize(p.AbilityId) == "poisonheal")
            {
                if (p.CurrentHP < p.MaxHP)
                {
                    p.CurrentHP = Math.Min(p.MaxHP, p.CurrentHP + Math.Max(1, p.MaxHP / 8));
                    state.Log.Write($"{p.Species} is healed by Poison Heal!");
                }

                return;
            }

            if (p.Status == StatusCondition.Poison)
            {
                int damage = Math.Max(1, p.MaxHP / 8);
                p.CurrentHP = Math.Max(0, p.CurrentHP - damage);

                state.Log.Write($"{p.Species} is hurt by poison!");
            }
            else if (p.Status == StatusCondition.Toxic)
            {
                p.ToxicCounter++;

                int damage = Math.Max(1, (p.MaxHP / 16) * p.ToxicCounter);
                p.CurrentHP = Math.Max(0, p.CurrentHP - damage);

                state.Log.Write($"{p.Species} is badly poisoned!");
            }
            else
            {
                return;
            }

            if (p.Fainted)
                state.Log.Write($"{p.Species} fainted!");
        }
    }
}