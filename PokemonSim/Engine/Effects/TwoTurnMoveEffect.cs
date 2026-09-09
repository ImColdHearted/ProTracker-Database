using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>Fly and friends. First press: vanish, remember the move,
    /// stop resolving (the charge turn deals nothing). Second press (the
    /// engine's legal actions lock the user into it): come down and let the
    /// move run. While charging, MoveResolver makes the user
    /// semi-invulnerable to targeted moves.</summary>
    public class TwoTurnMoveEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (!attacker.Charging)
            {
                attacker.Charging = true;
                attacker.ChargingMove = move;

                state.Log.Write($"{attacker.Species} vanished!");

                cancelled = true;
                return;
            }

            attacker.Charging = false;
            attacker.ChargingMove = null;
        }
    }
}