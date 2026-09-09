using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>U-turn-style switch. Section 154: it runs AFTER the hit
    /// (it used to switch before dealing damage), and goes through
    /// SwitchResolver so the incoming Pokemon meets the hazards and its
    /// switch-in ability fires. The replacement is the next healthy
    /// teammate - a chosen target needs the strategy/UI hook a later phase
    /// adds.</summary>
    public class SwitchAfterEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (attacker.Fainted)
                return;

            var player = state.GetOwner(attacker);

            var next = player.GetNextAvailablePokemon();

            if (next != null)
            {
                SwitchResolver.Resolve(state, player, next);
            }
        }
    }
}