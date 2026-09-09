using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>Section 154: Protect now actually protects. The old version
    /// set a flag nothing read, at a phase status moves never reached. It
    /// runs at BeforeMove on the user's own turn, succeeds with the classic
    /// halving streak (always, then 1/2, 1/4...), and MoveResolver blocks
    /// anything aimed at a Protected Pokemon until end of turn.</summary>
    public class ProtectEffect : BaseMoveEffect
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
            double successChance = 1.0 / (1 << System.Math.Min(attacker.ConsecutiveProtects, 8));

            attacker.ProtectedThisTurn = true;

            if (!state.Rng.Chance(successChance))
            {
                attacker.ConsecutiveProtects = 0;
                state.Log.Write("But it failed!");
                cancelled = true;
                return;
            }

            attacker.Protected = true;
            attacker.ConsecutiveProtects++;

            state.Log.Write($"{attacker.Species} protected itself!");

            // Nothing further for this move.
            cancelled = true;
        }
    }
}