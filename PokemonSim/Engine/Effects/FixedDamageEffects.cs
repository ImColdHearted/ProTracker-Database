using System;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// Section 155. Seismic Toss-style fixed damage: the hit deals exactly
    /// the attacker's level, replacing whatever the damage formula said
    /// (the move's listed power is a placeholder). Type immunity still
    /// applies first - a Ghost takes nothing from Seismic Toss because the
    /// resolver's effectiveness check zeroes the hit before this phase
    /// doubles as its override.
    /// </summary>
    public class LevelDamageEffect : BaseMoveEffect
    {
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
                return;   // immune (or already cancelled) - leave it at zero

            damage = Math.Max(1, attacker.Level);
        }
    }

    /// <summary>
    /// Section 155. Explosion and Self-Destruct: after the hit resolves the
    /// user's own HP drops to zero. Fainted is computed from HP, so the
    /// engine's normal faint handling takes over from there; Magic Guard
    /// deliberately does NOT prevent this - it is the move's cost, not
    /// indirect damage.
    /// </summary>
    public class SelfFaintEffect : BaseMoveEffect
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

            attacker.CurrentHP = 0;

            state.Log.Write($"{attacker.Species} fainted from its own attack!");
        }
    }

    /// <summary>
    /// Section 155. Binding moves reuse the trap machinery the engine
    /// already had (PokemonState.TrapEffect + BattleEngine.ApplyTrap +
    /// LegalActions' no-switching-while-trapped rule) - nothing set a trap
    /// until now. Registered two ways: BindTrap (Infestation, Magma Storm:
    /// an eighth of max HP per turn for 4-5 turns) and TrapNoDamage (Mean
    /// Look: no residual damage, no practical turn limit - it just pins the
    /// target in place). A fresh trap does not overwrite a running one,
    /// matching how binding moves stack in practice.
    /// </summary>
    public class BindTrapEffect : BaseMoveEffect
    {
        readonly bool dealsDamage;

        public BindTrapEffect(bool dealsDamage)
        {
            this.dealsDamage = dealsDamage;
        }

        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (cancelled || defender.Fainted || defender.Trap != null)
                return;

            // A damaging bind that never connected traps nothing.
            if (dealsDamage && damage <= 0)
                return;

            defender.Trap = new PokemonState.TrapEffect
            {
                SourceMove = move.Name,
                DamageFraction = dealsDamage ? 1.0 / 8.0 : 0.0,
                TurnsRemaining = dealsDamage ? state.Rng.Next(4, 6) : 999
            };

            state.Log.Write(dealsDamage
                ? $"{defender.Species} was trapped by {move.Name}!"
                : $"{defender.Species} can no longer escape!");
        }
    }
}