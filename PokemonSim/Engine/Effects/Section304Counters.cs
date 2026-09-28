using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Data;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// §304. The power formulas that needed a counter before they could be
    /// written. §303 built the family and cleared seventeen moves; these
    /// are the ones it had to leave behind because the engine had no way to
    /// answer their question. The answers are the new PokemonState and
    /// BattleState fields, and every class here is still just a PowerNow.
    /// </summary>

    /// <summary>Rage Fist: fifty more power for every hit this Pokemon has
    /// taken, up to 350.</summary>
    public class PowerFromTimesAttackedEffect : PowerFormulaEffect
    {
        protected override int PowerNow(
            BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
            => Math.Min(350, move.Power + 50 * attacker.TimesAttacked);
    }

    /// <summary>Stomping Tantrum and Temper Flare: twice as strong after a
    /// move that failed. LAST turn's failure, not this turn's - the flag
    /// this reads is the one the end-of-turn pass rolled over, so a move
    /// cannot double off its own failure.</summary>
    public class PowerIfLastMoveFailedEffect : PowerDoublerEffect
    {
        protected override bool Doubles(
            BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
            => attacker.MoveFailedLastTurn;
    }

    /// <summary>Triple Kick and Triple Axel: each hit is stronger than the
    /// last, by the move's listed power again. The listed power is
    /// therefore the FIRST hit's - 10 and 20 - not the third's.</summary>
    public class PowerFromHitNumberEffect : PowerFormulaEffect
    {
        protected override int PowerNow(
            BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
            => move.Power * Math.Max(1, state.CurrentHitNumber);
    }

    /// <summary>Pursuit: twice as strong against a target that has declared
    /// a switch and not yet taken it. The declaration is BattleState's,
    /// made as the turn's queue is built; a turn nobody declared anything
    /// in leaves Pursuit at its listed power rather than guessing.</summary>
    public class PowerIfTargetSwitchingEffect : PowerDoublerEffect
    {
        protected override bool Doubles(
            BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
        {
            Actions.BattleAction? declared = state.DeclaredFor(defender);

            return declared != null &&
                   declared.Type == BattleActionType.Switch &&
                   !defender.ActedThisTurn;
        }
    }

    /// <summary>
    /// Return and Frustration: power from how much the Pokemon likes its
    /// trainer, which is the one thing in this section that is not a battle
    /// state at all. Ten happiness to four power, floored, and never less
    /// than one.
    ///
    /// The data listed both at 102, which is Return's value at maximum
    /// happiness and Frustration's at minimum - each was right for one of
    /// them and wrong for the other. Happiness defaults to 255, so Return
    /// keeps the number it has always had and Frustration drops to the 1 it
    /// should have been.
    /// </summary>
    public class PowerFromHappinessEffect : PowerFormulaEffect
    {
        readonly bool inverted;

        /// <summary>inverted: Frustration, which reads the distance from
        /// maximum happiness instead of the happiness itself.</summary>
        public PowerFromHappinessEffect(bool inverted)
        {
            this.inverted = inverted;
        }

        protected override int PowerNow(
            BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
        {
            int happiness = Math.Clamp(attacker.Happiness, 0, 255);
            int value = inverted ? 255 - happiness : happiness;

            return Math.Max(1, value * 10 / 25);
        }
    }

    /// <summary>Water Shuriken: five more power in Ash-Greninja's hands,
    /// and only its. The data listed the move at 20 - the boosted number -
    /// so every Greninja threw it as hard as that one does; it is listed at
    /// its real 15 now.</summary>
    public class PowerIfAshGreninjaEffect : PowerFormulaEffect
    {
        protected override int PowerNow(
            BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
        {
            bool ash = string.Equals(attacker.Species, "Ash-Greninja", StringComparison.OrdinalIgnoreCase) &&
                       Abilities.AbilityFactory.Normalize(attacker.AbilityId) == "battlebond";

            return ash ? move.Power + 5 : move.Power;
        }
    }

    /// <summary>
    /// §304. Beat Up: one hit for every party member well enough to join
    /// in, each as strong as that member's own Attack.
    ///
    /// It is the one move in this section whose HIT COUNT is not a number
    /// in the data file - a full healthy party throws six punches and a
    /// lone survivor throws one. MinHits and MaxHits cannot say that, so
    /// this effect answers it in BeforeMove through BattleState's hit-count
    /// override, which the resolver reads in place of the move's own range
    /// and clears as the loop ends.
    ///
    /// Whose punches they are is fixed here too, in party order, so the
    /// power effect below and the count can never disagree about who is
    /// swinging.
    /// </summary>
    public class BeatUpEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        /// <summary>The party members that join in: awake, unstatused and
        /// standing, plus the user itself whatever state it is in - which
        /// is the rule the games use.</summary>
        public static List<PokemonState> Allies(BattleState state, PokemonState attacker)
        {
            PlayerState owner = state.GetOwner(attacker);

            return owner.Team
                .Where(p => ReferenceEquals(p, attacker) ||
                            (!p.Fainted && p.Status == StatusCondition.None))
                .ToList();
        }

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            state.HitCountOverride = Math.Max(1, Allies(state, attacker).Count);
        }
    }

    /// <summary>Beat Up's power, one party member at a time: five plus a
    /// tenth of that member's base Attack. The hit number picks which
    /// member, out of the same list the count came from.</summary>
    public class PowerFromBeatUpAllyEffect : PowerFormulaEffect
    {
        protected override int PowerNow(
            BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
        {
            List<PokemonState> allies = BeatUpEffect.Allies(state, attacker);

            if (allies.Count == 0)
                return move.Power;

            int index = Math.Clamp(state.CurrentHitNumber - 1, 0, allies.Count - 1);

            PokemonState ally = allies[index];

            int baseAttack = PokemonDex.TryGet(ally.Species, out PokemonSpecies species)
                ? species.BaseStats.Attack
                : ally.Stats.Attack;

            return 5 + baseAttack / 10;
        }
    }
}
