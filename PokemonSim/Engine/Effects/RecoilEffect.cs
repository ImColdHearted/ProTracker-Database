using System;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// Section 155. Classic recoil: after a damaging hit the attacker takes
    /// a fraction of the damage it dealt. Registered three ways (Recoil25,
    /// Recoil33, Recoil50) for Take Down-, Double-Edge- and Head Smash-class
    /// moves. Magic Guard blocks it, matching how the engine already treats
    /// indirect damage; Struggle's fixed quarter-max-HP recoil stays its own
    /// StruggleRecoilEffect, exactly as before. Fainting is not announced
    /// here - Fainted is computed from HP and the engine's own faint
    /// handling picks it up, the same as StruggleRecoilEffect.
    /// </summary>
    public class RecoilEffect : BaseMoveEffect
    {
        readonly double fraction;

        public RecoilEffect(double fraction)
        {
            this.fraction = fraction;
        }

        public override MovePhase Phase => MovePhase.AfterDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0 || attacker.Fainted || attacker.HasMagicGuard)
                return;

            // Section 158: Rock Head feels no recoil (Struggle's fixed
            // recoil stays its own effect and is deliberately exempt).
            if (Abilities.AbilityFactory.Normalize(attacker.AbilityId) == "rockhead")
                return;

            int recoil = Math.Max(1, (int)(damage * fraction));

            attacker.CurrentHP = Math.Max(0, attacker.CurrentHP - recoil);

            state.Log.Write($"{attacker.Species} is damaged by recoil!");
        }
    }
}