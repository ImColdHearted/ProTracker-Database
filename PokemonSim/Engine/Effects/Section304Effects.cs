using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// §304. The leftovers: everything in the audit's gap list that is not
    /// a lock-in and not a power formula. They have nothing in common with
    /// each other, which is why they were left until last - each is its own
    /// small rule, and the work was in finding it somewhere real to hook
    /// onto rather than in the rule itself.
    /// </summary>

    /// <summary>
    /// High Jump Kick, Jump Kick and Supercell Slam: half the user's
    /// maximum HP for missing.
    ///
    /// Not a phase effect, because there is no phase for it: a move that
    /// misses returns from MoveResolver before BeforeDamage, so nothing in
    /// a move's effect list ever runs. MoveResolver calls this from the two
    /// places a kick can come up empty instead, and the effect name is a
    /// marker that says which moves it applies to.
    ///
    /// Missing is the case; being blocked by Protect is not. Modern games
    /// stopped charging crash damage for that and this follows them.
    /// </summary>
    public class CrashDamageEffect : MarkerMoveEffect
    {
        public const string Name = "CrashDamage";

        /// <summary>Called by MoveResolver when a move that carries the
        /// marker misses or hits a target it cannot affect.</summary>
        public static void OnMiss(BattleState state, PokemonState attacker, MoveState move)
        {
            if (move.Effects == null ||
                !move.Effects.Any(e => string.Equals(e, Name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (attacker.Fainted || attacker.HasMagicGuard)
                return;

            int crash = Math.Max(1, attacker.MaxHP / 2);

            attacker.CurrentHP = Math.Max(0, attacker.CurrentHP - crash);

            state.Log.Write($"{attacker.Species} kept going and crashed!");
        }
    }

    /// <summary>Smack Down and Thousand Arrows: the target comes down, and
    /// Ground reaches it from then on. Grounding reads the flag.</summary>
    public class SmackDownEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (cancelled || defender.Fainted || damage <= 0 || defender.SmackedDown)
                return;

            // Nothing to bring down if it was never up. Ingrain and Gravity
            // already hold it down, so the move changes nothing there
            // either - Grounding is the single authority on all of it.
            if (Grounding.IsGrounded(state, defender))
                return;

            defender.SmackedDown = true;
            defender.MagnetRiseTurns = 0;

            state.Log.Write($"{defender.Species} fell straight down!");
        }
    }

    /// <summary>Psychic Noise: two turns with no healing of any kind.</summary>
    public class HealBlockEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (cancelled || defender.Fainted || damage <= 0 || defender.HealBlockTurns > 0)
                return;

            defender.HealBlockTurns = 2;

            state.Log.Write($"{defender.Species} was prevented from healing!");
        }
    }

    /// <summary>Sparkling Aria: the target's burn is washed off. The one
    /// attacking move in the game that helps its target, and it helps it
    /// whether or not the attacker wanted to.</summary>
    public class CureTargetBurnEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (cancelled || defender.Fainted || damage <= 0)
                return;

            if (defender.Status != StatusCondition.Burn)
                return;

            defender.Status = StatusCondition.None;

            state.Log.Write($"{defender.Species}'s burn was healed!");
        }
    }

    /// <summary>Glaive Rush: the user hits hard and then stands there. Until
    /// it moves again, attacks against it cannot miss and land double. The
    /// resolver reads the flag in its accuracy roll and the damage
    /// calculator in its modifier, and the resolver clears it as the user's
    /// next move begins.</summary>
    public class GlaiveRushEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (!cancelled && !attacker.Fainted)
                attacker.GlaiveRushActive = true;
        }
    }

    /// <summary>Rage: while it stands, every damaging hit the user takes
    /// raises its Attack. Cleared when the user next moves, so it only
    /// covers the gap between this turn and the next.</summary>
    public class RageEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (!cancelled && !attacker.Fainted)
                attacker.RageActive = true;
        }
    }

    /// <summary>Charge: the user's next Electric move hits twice as hard.
    /// The Sp. Def boost beside it is in the data, like every other pure
    /// stat change. The damage calculator spends the flag.</summary>
    public class ChargeEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (cancelled)
                return;

            attacker.ChargeActive = true;

            state.Log.Write($"{attacker.Species} began charging power!");
        }
    }

    /// <summary>Roost: the price of the heal is the user's Flying type for
    /// the rest of the turn, which is how a Ground move gets to touch a
    /// Skarmory. The heal itself is the ordinary Heal effect beside this
    /// one in the data.</summary>
    public class RoostEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (cancelled || !attacker.Types.Contains(PokemonType.Flying))
                return;

            attacker.RoostedThisTurn = true;

            state.Log.Write($"{attacker.Species} landed on the ground!");
        }
    }

    /// <summary>Plasma Fists: for the rest of the turn every Normal move is
    /// an Electric one. One turn, both sides, and the only field state in
    /// the game that is over before the next Pokemon acts twice.</summary>
    public class IonDelugeEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (cancelled || damage <= 0)
                return;

            state.IonDelugeTurns = 1;

            state.Log.Write("A deluge of ions showers the battlefield!");
        }
    }

    /// <summary>
    /// Spectral Thief: the target's stat boosts become the attacker's, and
    /// then it takes the hit anyway.
    ///
    /// BeforeMove, because "before dealing damage" is the whole point - the
    /// stolen Attack has to be swinging the very hit that follows. Only the
    /// RAISED stages move; the target keeps its own drops, which is what
    /// makes this a theft rather than a Haze.
    /// </summary>
    public class SpectralThiefEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        public override void Apply(
            BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (cancelled || defender.Fainted)
                return;

            int stolen = 0;

            stolen += Steal(ref defender.AttackStage, ref attacker.AttackStage);
            stolen += Steal(ref defender.DefenseStage, ref attacker.DefenseStage);
            stolen += Steal(ref defender.SpAttackStage, ref attacker.SpAttackStage);
            stolen += Steal(ref defender.SpDefenseStage, ref attacker.SpDefenseStage);
            stolen += Steal(ref defender.SpeedStage, ref attacker.SpeedStage);
            stolen += Steal(ref defender.AccuracyStage, ref attacker.AccuracyStage);
            stolen += Steal(ref defender.EvasionStage, ref attacker.EvasionStage);

            if (stolen > 0)
                state.Log.Write($"{attacker.Species} stole {defender.Species}'s boosted power!");
        }

        /// <summary>Move one raised stage across, and answer how much moved.
        /// A stage at or below zero stays where it is - the target keeps
        /// its own drops, which is what makes this a theft and not a
        /// Haze.</summary>
        static int Steal(ref int from, ref int to)
        {
            if (from <= 0)
                return 0;

            int taken = from;

            to = Math.Clamp(to + taken, -6, 6);
            from = 0;

            return taken;
        }
    }

    /// <summary>
    /// Wish: half the WISHER's maximum HP, handed to whoever is standing in
    /// its slot at the end of the following turn.
    ///
    /// The amount is fixed here rather than when it lands, which is what
    /// makes a Blissey's wish worth having on a Shedinja. The side's slot
    /// holds it - BattleState, beside Healing Wish, which is the same idea
    /// without the wait - and BattleEngine's end-of-turn pass spends it.
    /// </summary>
    public class WishEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        public override void Apply(
            BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            PlayerState side = state.GetOwner(attacker);

            if (state.WishTurns(side) > 0)
            {
                state.Log.Write("But it failed!");
                cancelled = true;
                return;
            }

            state.WishTurns(side) = 2;
            state.WishHeal(side) = Math.Max(1, attacker.MaxHP / 2);

            state.Log.Write($"{attacker.Species} made a wish!");
        }
    }

    /// <summary>Revival Blessing: one fainted party member comes back with
    /// half its health. The earliest fainted one - party order - because
    /// there is nobody to ask which.</summary>
    public class RevivalBlessingEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        public override void Apply(
            BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            PlayerState side = state.GetOwner(attacker);

            PokemonState? fallen = side.Team.FirstOrDefault(p => p.Fainted);

            if (fallen == null)
            {
                state.Log.Write("But it failed!");
                cancelled = true;
                return;
            }

            fallen.CurrentHP = Math.Max(1, fallen.MaxHP / 2);
            fallen.Status = StatusCondition.None;
            fallen.SleepTurns = 0;
            fallen.ToxicCounter = 0;

            state.Log.Write($"{fallen.Species} was revived!");
        }
    }

    /// <summary>
    /// Upper Hand: a priority punch that only lands on somebody winding up
    /// a priority move of their own, and flinches them out of it.
    ///
    /// The flinch is 100% and lives in the data. What could not live
    /// anywhere before §304 is the condition, which asks what the target is
    /// ABOUT to do - so the move sat in the audit with a flinch nobody
    /// dared give it, because a certain flinch without the condition is a
    /// better move than the real one. BattleState's declared actions answer
    /// it now.
    ///
    /// A turn where nothing was declared (a caller driving the resolver
    /// directly) lets the move through rather than failing it: the gate
    /// exists to stop a free flinch, and refusing a move because the engine
    /// was not asked would be its own kind of wrong.
    /// </summary>
    public class UpperHandGateEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        public override void Apply(
            BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            Actions.BattleAction? declared = state.DeclaredFor(defender);

            if (declared == null)
                return;

            bool priorityAttack =
                declared.Type == BattleActionType.Move &&
                declared.Move != null &&
                declared.Move.Priority > 0 &&
                declared.Move.Category != MoveCategory.Status;

            if (priorityAttack && !defender.ActedThisTurn)
                return;

            state.Log.Write("But it failed!");
            cancelled = true;
        }
    }
}
