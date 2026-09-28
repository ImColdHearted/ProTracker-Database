using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// §197. Which slot an effect belongs to when it is found in a Pokemon's
    /// passive list.
    ///
    /// MoveResolver runs every passive effect of BOTH the attacker and the
    /// defender through the same Apply(state, attacker, defender, ...) call,
    /// and until this existed nothing said which of those two the effect was
    /// speaking for. Every defensive ability was therefore also live on the
    /// ATTACKER: a Jellicent with Water Absorb that used Scald ran its own
    /// absorb, which heals the "defender" parameter, so the opposing Pikachu
    /// was healed and the attack was cancelled. That is the bug this was
    /// added for, reported from one battle. Levitate had the same shape - a
    /// Levitate user attacking with a Ground move made its target immune -
    /// and so, in the other direction, did every damage booster: Adaptability
    /// on the DEFENDER boosted the attacker's move, because the booster reads
    /// the attacker either way.
    ///
    /// Either is the default, so an effect that has not been classified
    /// behaves exactly as it did before.
    /// </summary>
    public enum EffectSide
    {
        /// <summary>Runs from either slot - the state before §197, and still
        /// right for anything that reads only the move or the field.</summary>
        Either,

        /// <summary>Only when its owner is the one attacking. Damage and type
        /// boosters live here: they read the attacker.</summary>
        AttackerOnly,

        /// <summary>Only when its owner is the one being hit. Immunities,
        /// absorbs, damage reducers and contact reactions live here: they read
        /// and act on the defender.</summary>
        DefenderOnly
    }

    /// <summary>
    /// A move or ability behaviour hooked into MoveResolver's phases.
    /// Section 154: this file is the interface plus the ability-owned
    /// damage/stat effects only - the Regenerator and Moxie ability classes
    /// that lived (one nested inside the other) at the bottom moved to
    /// Engine/Abilities, every class sits in this namespace now instead of
    /// three different ones, and Chlorophyll's effect no longer PERMANENTLY
    /// doubles the raw Speed stat each time it fires - it is a
    /// CalculateStat-phase modifier like Swift Swim, which is also how the
    /// BeforeDamage effects work now that the resolver feeds them each
    /// hit's real damage (they used to run before any damage existed,
    /// multiplying zero).
    /// </summary>
    /// <summary>
    /// §311. An effect that makes its owner untouchable by one whole type.
    ///
    /// Every one of these already existed and every one of them already
    /// worked; what did not exist was any way to ASK. The observer's feature
    /// encoder computed a move's type effectiveness straight off the type
    /// chart, so it told the network that an Earthquake does double to a
    /// Levitate Bronzong and that a Thunderbolt is a fine idea into a Volt
    /// Absorb Lanturn. The network had no ability input either, so it could
    /// not even learn the correction - it was being taught the wrong number
    /// with nothing to condition it on.
    ///
    /// Answering "which type does this cancel" from the effect itself is what
    /// keeps the encoder from carrying a second list of immunity abilities
    /// that would go stale the moment one was added. A new absorb implements
    /// this and the encoder knows about it without being edited.
    ///
    /// Only for whole-type CANCELLATION. Thick Fat halves and Wonder Guard
    /// reads the chart rather than one type, so neither belongs here.
    /// </summary>
    public interface ITypeNullifier
    {
        PokemonType NullifiedType { get; }
    }

    public interface IMoveEffect
    {
        MovePhase Phase { get; }

        /// <summary>§197. See EffectSide. Defaults to Either on
        /// BaseMoveEffect, which every effect in this engine derives
        /// from.</summary>
        EffectSide Side { get; }

        void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled
        );

        void ApplyStat(StatContext context);
    }

    public class LevitateEffect : BaseMoveEffect, ITypeNullifier
    {
        public PokemonType NullifiedType => PokemonType.Ground;

        public override MovePhase Phase => MovePhase.BeforeMove;

        // §197: the defender's immunity. Run from the attacker's list it made
        // a Levitate user's own Ground move fail against its target.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (move.Type == PokemonType.Ground && !move.Typeless)
            {
                state.Log.Write($"{defender.Species} is immune due to Levitate!");
                cancelled = true;
            }
        }
    }

    public class TechnicianEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: the attacker's boost. Run from the defender's list a
        // Technician wall boosted every weak move aimed at it.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (move.Power <= 60 && move.Power > 0)
            {
                damage = (int)(damage * 1.5);
            }
        }
    }

    public class ChlorophyllEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.CalculateStat;

        public override void ApplyStat(StatContext context)
        {
            if (context.Stat == "Speed" &&
                context.State.Environment.Weather == WeatherType.Sun)
            {
                context.Value *= 2;
            }
        }
    }

    public class HugePowerEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.CalculateStat;

        public override void ApplyStat(StatContext context)
        {
            if (context.Stat == "Attack")
            {
                context.Value *= 2;
            }
        }
    }

    public class GutsEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.CalculateStat;

        public override void ApplyStat(StatContext context)
        {
            if (context.Stat == "Attack" &&
                context.Pokemon.Status != StatusCondition.None)
            {
                context.Value *= 1.5;
            }
        }
    }

    public class SwiftSwimEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.CalculateStat;

        public override void ApplyStat(StatContext context)
        {
            if (context.Stat == "Speed" &&
                context.State.Environment.Weather == WeatherType.Rain)
            {
                context.Value *= 2;
            }
        }
    }

    public class SheerForceEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            // Section 158: any secondary qualifies - the chance-gated
            // status, the flinch, or the opponent-aimed stat change. The
            // secondaries themselves are suppressed in
            // MoveResolver.ApplySecondaryEffects.
            if (move.SecondaryChance > 0 || move.FlinchChance > 0 ||
                (move.StatusChance > 0 && move.Category != MoveCategory.Status))
            {
                damage = (int)(damage * 1.3);
            }
        }
    }

    public class BlazeEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads the attacker's HP, so run from the defender's list
        // it boosted the opponent's move whenever the OPPONENT was in pinch
        // range. Same for Torrent, Overgrow and Swarm.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (attacker.CurrentHP <= attacker.MaxHP / 3 &&
                move.Type == PokemonType.Fire)
            {
                damage = (int)(damage * 1.5);
            }
        }
    }

    public class OvergrowEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (attacker.CurrentHP <= attacker.MaxHP / 3 &&
                move.Type == PokemonType.Grass)
            {
                damage = (int)(damage * 1.5);
            }
        }
    }

    public class TorrentEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (attacker.CurrentHP <= attacker.MaxHP / 3 &&
                move.Type == PokemonType.Water)
            {
                damage = (int)(damage * 1.5);
            }
        }
    }

    public class ThickFatEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: the defender's resistance. Run from the attacker's list a
        // Thick Fat user halved the damage of its OWN Fire and Ice moves.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (move.Type == PokemonType.Fire ||
                move.Type == PokemonType.Ice)
            {
                damage = (int)(damage * 0.5);
            }
        }
    }

    public class MarvelScaleEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.CalculateStat;

        public override void ApplyStat(StatContext context)
        {
            if (context.Stat == "Defense" &&
                context.Pokemon.Status != StatusCondition.None)
            {
                context.Value *= 1.5;
            }
        }
    }

    public class FlashFireEffect : BaseMoveEffect, ITypeNullifier
    {
        public PokemonType NullifiedType => PokemonType.Fire;

        public override MovePhase Phase => MovePhase.BeforeMove;

        // §197: the defender's absorb - the Water Absorb bug in its
        // Fire form. Run from the attacker's list, a Flash Fire user's own
        // Fire move was absorbed by whatever it was aimed at.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (move.Type == PokemonType.Fire && !move.Typeless)
            {
                defender.AbilityState["flashfire"] = true;

                state.Log.Write($"{defender.Species} absorbed the fire!");

                cancelled = true;
            }
        }
    }

    public class FlashFireBoostEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (attacker.AbilityState.TryGetValue("flashfire", out var active) &&
                active is bool boosted && boosted &&
                move.Type == PokemonType.Fire)
            {
                damage = (int)(damage * 1.5);
            }
        }
    }

    public class MoldBreakerEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        // §197: the attacker's. Run from the defender's list it set the
        // ignore-abilities flag while its owner was the one being hit, which
        // switched OFF that owner's own abilities for the rest of the move.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            state.IgnoreDefenderAbilities = true;
        }
    }

    public class VoltAbsorbEffect : BaseMoveEffect, ITypeNullifier
    {
        public PokemonType NullifiedType => PokemonType.Electric;

        public override MovePhase Phase => MovePhase.BeforeMove;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (move.Type == PokemonType.Electric && !move.Typeless)
            {
                int heal = defender.MaxHP / 4;

                defender.CurrentHP = System.Math.Min(
                    defender.MaxHP,
                    defender.CurrentHP + heal
                );

                state.Log.Write($"{defender.Species} absorbed the electricity!");

                cancelled = true;
            }
        }
    }

    public class RapidSpinEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            var player = state.GetOwner(attacker);

            HazardResolver.ClearSide(state, player);

            state.Log.Write($"{attacker.Species} cleared hazards with Rapid Spin!");

            MoveResolver.ApplyStatChange(state, attacker, "Speed", 1);
        }
    }

    public class DefogEffect : BaseMoveEffect
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
            HazardResolver.ClearSide(state, state.Player1);
            HazardResolver.ClearSide(state, state.Player2);

            state.Log.Write("All hazards were blown away!");

            MoveResolver.ApplyStatChange(state, defender, "Evasion", -1);
        }
    }

    /// <summary>Section 154. Struggle's quarter-max-HP recoil - the move
    /// itself is synthesized by LegalActions when a Pokemon has no PP left
    /// anywhere.</summary>
    public class StruggleRecoilEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (damage <= 0 || attacker.HasMagicGuard)
                return;

            int recoil = System.Math.Max(1, attacker.MaxHP / 4);

            attacker.CurrentHP = System.Math.Max(0, attacker.CurrentHP - recoil);

            state.Log.Write($"{attacker.Species} is damaged by recoil!");
        }
    }
}
