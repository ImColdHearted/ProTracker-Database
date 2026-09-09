using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// Section 155. Doubles a hit's damage when a status condition is
    /// present on one side: registered as FacadeBoost (the ATTACKER is
    /// statused - Facade) and HexBoost (the DEFENDER is statused - Hex).
    /// Runs per hit at BeforeDamage, the phase the resolver already feeds
    /// each hit's damage through by ref.
    /// </summary>
    public class StatusPowerEffect : BaseMoveEffect
    {
        readonly bool checksAttacker;
        readonly bool requiresPoison;

        /// <summary>Section 158: requiresPoison narrows the trigger to
        /// poison and toxic - Venoshock's condition, against Hex's
        /// any-status one.</summary>
        public StatusPowerEffect(bool checksAttacker, bool requiresPoison = false)
        {
            this.checksAttacker = checksAttacker;
            this.requiresPoison = requiresPoison;
        }

        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0)
                return;

            var subject = checksAttacker ? attacker : defender;

            bool met = requiresPoison
                ? subject.Status == StatusCondition.Poison || subject.Status == StatusCondition.Toxic
                : subject.Status != StatusCondition.None;

            if (met)
                damage *= 2;
        }
    }
}