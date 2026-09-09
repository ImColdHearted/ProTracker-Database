using System;
using System.Linq;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// Section 184. The nine effects the competitive-set moves needed that
    /// nothing already in the registry could stand in for. Everything else
    /// in that batch of ninety-nine is either plain data or an existing
    /// effect reused - see the guide's table for which moves are exact and
    /// which are approximations, and how.
    /// </summary>
    public class StickyWebEffect : BaseMoveEffect
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
            bool isP1 = state.GetOwner(attacker) == state.Player1;

            bool already = isP1 ? state.StickyWebP2 : state.StickyWebP1;

            if (already)
            {
                state.Log.Write("But it failed!");
                return;
            }

            if (isP1)
                state.StickyWebP2 = true;
            else
                state.StickyWebP1 = true;

            state.Log.Write("A sticky web spreads out beneath the opposing team!");
        }
    }

    /// <summary>Section 184. Aurora Veil: Reflect and Light Screen at once,
    /// and only while it is hailing - the one screen with a weather
    /// condition on setting it.</summary>
    public class AuroraVeilEffect : BaseMoveEffect
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

            if (state.Environment.Weather != WeatherType.Hail)
            {
                state.Log.Write("But it failed! (Aurora Veil only works in hail.)");
                return;
            }

            PlayerState side = state.GetOwner(attacker);

            ref int reflect = ref state.ReflectTurns(side);
            ref int screen = ref state.LightScreenTurns(side);

            if (reflect > 0 && screen > 0)
            {
                state.Log.Write("But it failed!");
                return;
            }

            int turns = Items.HeldItems.ScreenDuration(attacker);

            reflect = turns;
            screen = turns;

            state.Log.Write($"Aurora Veil made {side.Name}'s team stronger against attacks!");
        }
    }

    /// <summary>Section 184. Strength Sap: heal by the target's CURRENT
    /// Attack stat, then drop that Attack a stage. Healing by the stat
    /// rather than by a fraction is the whole character of the move - it
    /// is enormous against a physical attacker and nearly nothing against
    /// a special one.</summary>
    public class StrengthSapEffect : BaseMoveEffect
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

            if (defender.AttackStage <= -6)
            {
                state.Log.Write("But it failed!");
                return;
            }

            int heal = Math.Max(1, (int)StatResolver.GetStat(state, defender, "Attack"));

            if (attacker.CurrentHP < attacker.MaxHP)
            {
                attacker.CurrentHP = Math.Min(attacker.MaxHP, attacker.CurrentHP + heal);
                state.Log.Write($"{attacker.Species} restored HP using {defender.Species}'s Attack!");
            }

            defender.AttackStage = Math.Max(-6, defender.AttackStage - 1);

            state.Log.Write($"{defender.Species}'s Attack fell!");
        }
    }

    /// <summary>Section 184. First Impression only works on the turn its
    /// user came in.
    ///
    /// §197: shared by First Impression and Fake Out, which is why the
    /// failure message names the move rather than hard-coding one, and it is
    /// registered under both "FirstImpression" and "FirstTurnOnly". The gate
    /// reads HasActedSinceEnteringField rather than comparing
    /// EnteredFieldTurn to the turn number - see that field for why the
    /// comparison was the wrong question.</summary>
    public class FirstImpressionEffect : BaseMoveEffect
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
            // §197: was "EnteredFieldTurn == TurnNumber", which is not the
            // same question. A replacement sent in over a faint has its entry
            // stamped with the turn that just ended, and a voluntary switch
            // spends its own turn - so in both cases the numbers already
            // differed by the Pokemon's first real turn and the move was
            // refused on the one turn it should have worked. What the rule
            // actually asks is whether this Pokemon has had a go yet.
            if (!attacker.HasActedSinceEnteringField)
                return;

            cancelled = true;
            state.Log.Write($"But it failed! ({move.Name} only works on the first turn its user is out.)");
        }
    }

    /// <summary>Section 184. Final Gambit: the user's remaining HP becomes
    /// the damage, and the user faints. Type immunity still applies first -
    /// a Ghost takes nothing, exactly as Seismic Toss behaves.</summary>
    public class FinalGambitEffect : BaseMoveEffect
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
                return;   // immune, or already cancelled

            damage = Math.Max(1, attacker.CurrentHP);

            attacker.CurrentHP = 0;

            state.Log.Write($"{attacker.Species} put everything into that attack!");
        }
    }

    /// <summary>Section 184. Refresh and friends: cure the USER's own
    /// status. HealBell already does the whole party; this is the single
    /// Pokemon version several of the newer moves fold in.</summary>
    public class RefreshCureEffect : BaseMoveEffect
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
            if (attacker.Status == StatusCondition.None)
                return;

            attacker.Status = StatusCondition.None;
            attacker.SleepTurns = 0;
            attacker.ToxicCounter = 0;

            state.Log.Write($"{attacker.Species}'s status returned to normal!");
        }
    }

    /// <summary>Section 184. Heart Swap: the two sides trade stat stages
    /// outright. Its point is that a sweeper's boosts become yours, so the
    /// swap is total rather than a copy in one direction.</summary>
    public class HeartSwapEffect : BaseMoveEffect
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

            (attacker.AttackStage, defender.AttackStage) = (defender.AttackStage, attacker.AttackStage);
            (attacker.DefenseStage, defender.DefenseStage) = (defender.DefenseStage, attacker.DefenseStage);
            (attacker.SpAttackStage, defender.SpAttackStage) = (defender.SpAttackStage, attacker.SpAttackStage);
            (attacker.SpDefenseStage, defender.SpDefenseStage) = (defender.SpDefenseStage, attacker.SpDefenseStage);
            (attacker.SpeedStage, defender.SpeedStage) = (defender.SpeedStage, attacker.SpeedStage);
            (attacker.AccuracyStage, defender.AccuracyStage) = (defender.AccuracyStage, attacker.AccuracyStage);
            (attacker.EvasionStage, defender.EvasionStage) = (defender.EvasionStage, attacker.EvasionStage);

            state.Log.Write($"{attacker.Species} switched stat changes with {defender.Species}!");
        }
    }

    /// <summary>Section 184. Shed Tail: pay half the user's HP for a
    /// substitute, then leave, so the replacement arrives behind it. The
    /// cost is half rather than Substitute's quarter, and a user that
    /// cannot pay does not get to switch either.</summary>
    public class ShedTailEffect : BaseMoveEffect
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

            int cost = attacker.MaxHP / 2;

            if (cost <= 0 || attacker.CurrentHP <= cost)
            {
                state.Log.Write($"{attacker.Species} is too weak to shed its tail!");
                return;
            }

            PlayerState player = state.GetOwner(attacker);
            PokemonState? next = player.GetNextAvailablePokemon();

            if (next == null)
            {
                state.Log.Write("But it failed! (There is nobody left to switch to.)");
                return;
            }

            attacker.CurrentHP -= cost;

            // The substitute belongs to whoever is standing behind it, so
            // it moves to the incoming Pokemon rather than leaving with the
            // one that paid for it.
            attacker.SubstituteHP = 0;

            state.Log.Write($"{attacker.Species} shed its tail to make a substitute!");

            SwitchResolver.Resolve(state, player, next);

            next.SubstituteHP = cost;
        }
    }

    /// <summary>Section 184. Chilly Reception: set hail, then leave. The
    /// snow goes up whether or not there is anyone to switch to, which is
    /// what makes it usable as a weather move on a last Pokemon.</summary>
    public class ChillyReceptionEffect : BaseMoveEffect
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

            state.Environment.Weather = WeatherType.Hail;
            state.Environment.WeatherTurns = 5;

            state.Log.Write("It started to snow!");

            PlayerState player = state.GetOwner(attacker);
            PokemonState? next = player.GetNextAvailablePokemon();

            if (next != null)
                SwitchResolver.Resolve(state, player, next);
        }
    }
}