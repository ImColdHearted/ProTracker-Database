using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    // Section 154: moved into PokemonSim.Engine.Effects (was global).
    public abstract class BaseMoveEffect : IMoveEffect
    {
        public abstract MovePhase Phase { get; }

        /// <summary>§197. Either unless an effect says otherwise, so nothing
        /// that has not been classified changes behaviour.</summary>
        public virtual EffectSide Side => EffectSide.Either;

        public virtual void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        { }

        public virtual void ApplyStat(StatContext context) { }
    }
}