using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// Section 158. Reflect and Light Screen - five-turn side conditions
    /// that halve incoming physical or special damage. The halving itself
    /// lives in MoveResolver's hit loop (crits and Infiltrator go through);
    /// this effect only raises the wall. Registered as Reflect and
    /// LightScreen.
    /// </summary>
    public class ScreenEffect : BaseMoveEffect
    {
        readonly bool physical;

        public ScreenEffect(bool physical)
        {
            this.physical = physical;
        }

        public override MovePhase Phase => MovePhase.BeforeMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            cancelled = true;

            var side = state.GetOwner(attacker);

            ref int turns = ref physical ? ref state.ReflectTurns(side) : ref state.LightScreenTurns(side);

            if (turns > 0)
            {
                state.Log.Write("But it failed!");
                return;
            }

            // Section 159: Light Clay stretches the wall to eight turns.
            turns = Items.HeldItems.ScreenDuration(attacker);

            state.Log.Write(physical
                ? $"Reflect raised {side.Name}'s team's Defense!"
                : $"Light Screen raised {side.Name}'s team's Special Defense!");
        }
    }

    /// <summary>Section 158. Brick Break / Psychic Fangs: shatter the
    /// defender's screens before the hit lands.</summary>
    public class ScreenBreakEffect : BaseMoveEffect
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
            var side = state.GetOwner(defender);

            if (state.ReflectTurns(side) > 0 || state.LightScreenTurns(side) > 0)
            {
                state.ReflectTurns(side) = 0;
                state.LightScreenTurns(side) = 0;
                state.Log.Write("The wall shattered!");
            }
        }
    }

    /// <summary>Section 158. Mist: five turns of protection from
    /// opponent-inflicted stat drops (see ApplyStatChangeAgainst).</summary>
    public class MistEffect : BaseMoveEffect
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
            cancelled = true;

            var side = state.GetOwner(attacker);

            if (state.MistTurns(side) > 0)
            {
                state.Log.Write("But it failed!");
                return;
            }

            state.MistTurns(side) = 5;
            state.Log.Write($"{side.Name}'s team became shrouded in mist!");
        }
    }

    /// <summary>Section 158. Safeguard: five turns of protection from
    /// opponent-inflicted status conditions (see TryInflictStatus).</summary>
    public class SafeguardEffect : BaseMoveEffect
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
            cancelled = true;

            var side = state.GetOwner(attacker);

            if (state.SafeguardTurns(side) > 0)
            {
                state.Log.Write("But it failed!");
                return;
            }

            state.SafeguardTurns(side) = 5;
            state.Log.Write($"{side.Name}'s team is protected by Safeguard!");
        }
    }

    /// <summary>Section 158. Tailwind: the side's Speed doubles for four
    /// turns (see StatResolver).</summary>
    public class TailwindEffect : BaseMoveEffect
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
            cancelled = true;

            var side = state.GetOwner(attacker);

            if (state.TailwindTurns(side) > 0)
            {
                state.Log.Write("But it failed!");
                return;
            }

            state.TailwindTurns(side) = 4;
            state.Log.Write($"The tailwind blew from behind {side.Name}'s team!");
        }
    }

    /// <summary>Section 158. Court Change: both sides' hazards, screens,
    /// mist, safeguard and tailwind swap owners.</summary>
    public class CourtChangeEffect : BaseMoveEffect
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
            if (cancelled)
                return;

            (state.SpikesP1, state.SpikesP2) = (state.SpikesP2, state.SpikesP1);
            (state.ToxicSpikesP1, state.ToxicSpikesP2) = (state.ToxicSpikesP2, state.ToxicSpikesP1);
            (state.StealthRockP1, state.StealthRockP2) = (state.StealthRockP2, state.StealthRockP1);
            (state.StickyWebP1, state.StickyWebP2) = (state.StickyWebP2, state.StickyWebP1);
            (state.ReflectTurnsP1, state.ReflectTurnsP2) = (state.ReflectTurnsP2, state.ReflectTurnsP1);
            (state.LightScreenTurnsP1, state.LightScreenTurnsP2) = (state.LightScreenTurnsP2, state.LightScreenTurnsP1);
            (state.MistTurnsP1, state.MistTurnsP2) = (state.MistTurnsP2, state.MistTurnsP1);
            (state.SafeguardTurnsP1, state.SafeguardTurnsP2) = (state.SafeguardTurnsP2, state.SafeguardTurnsP1);
            (state.TailwindTurnsP1, state.TailwindTurnsP2) = (state.TailwindTurnsP2, state.TailwindTurnsP1);

            state.Log.Write($"{attacker.Species} swapped the battle effects affecting each side!");
        }
    }
}