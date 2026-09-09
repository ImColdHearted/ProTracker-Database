using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>Fires on entry (BattleInitializer at the start,
    /// SwitchResolver on every switch-in). Section 154: it lowers the
    /// ACTIVE opponent's Attack - GetOpponents used to hand it the whole
    /// opposing team.</summary>
    public class IntimidateAbility : IAbility
    {
        public string Id => "intimidate";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield break;
        }

        public void OnAttach(PokemonState pokemon, BattleState state)
        {
            foreach (var opponent in state.GetOpponents(pokemon))
            {
                // Section 158: routed as an opponent-sourced drop so Mist,
                // Clear Body and Defiant get their say.
                MoveResolver.ApplyStatChangeAgainst(state, pokemon, opponent, "Attack", -1);
            }
        }
    }
}