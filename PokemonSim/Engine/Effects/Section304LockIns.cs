using System;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// §304. The multi-turn lock, and the counters that ride on it.
    ///
    /// Five moves take the user's next turns and spend them on themselves:
    /// Outrage, Thrash and Petal Dance for two or three turns and then
    /// confuse the user; Uproar for three, keeping everybody awake; Rollout
    /// for five, getting stronger each time. They are one mechanic with
    /// three numbers, so they are one class with three constructor
    /// arguments.
    ///
    /// HOW A LOCK IS SPENT. The effect runs AfterMove, which the resolver
    /// only reaches when the move actually went off, and it does two
    /// things: it starts the lock if there is not one already, and it
    /// leaves the counting to MoveResolver. The counting lives there rather
    /// than here because the interesting case is the one this class never
    /// sees - the move that missed, was blocked, or was never used at all.
    /// A lock that only ever ticked on success would never run out, and
    /// LegalActions would offer that one move forever. So MoveResolver
    /// calls Spend on the way out of a move that worked and Break on the
    /// way out of one that did not, and the rule "a broken lock never
    /// confuses" is stated once, in Break.
    ///
    /// THE TURN COUNT is rolled when the lock starts and stored, rather
    /// than re-rolled: the games decide the length up front, and a stored
    /// number is also the only version a cloned battle can replay.
    /// </summary>
    public class LockInEffect : BaseMoveEffect
    {
        readonly int minTurns;
        readonly int maxTurns;
        readonly bool confuses;

        public LockInEffect(int minTurns, int maxTurns, bool confuses)
        {
            this.minTurns = minTurns;
            this.maxTurns = maxTurns;
            this.confuses = confuses;
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
            if (cancelled || attacker.Fainted)
                return;

            // Already locked into this move - MoveResolver does the
            // counting; there is nothing to start.
            if (attacker.LockedMove != null)
                return;

            attacker.LockedMove = move;
            attacker.LockConfusesOnEnd = confuses;

            // Rng.Next's upper bound is exclusive, so a 2-3 turn lock is
            // Next(2, 4).
            attacker.LockedTurns = minTurns >= maxTurns
                ? minTurns
                : state.Rng.Next(minTurns, maxTurns + 1);
        }
    }

    /// <summary>
    /// §304. Uproar's second half: nobody sleeps while it is going on, and
    /// anybody already asleep wakes up. Separate from the lock because the
    /// lock is shared with four other moves and this is Uproar's alone.
    ///
    /// The "nobody may fall asleep" half is enforced where sleep is
    /// inflicted (MoveResolver.TryInflictStatus), which is the only place
    /// that can refuse it; this effect handles the waking.
    /// </summary>
    public class UproarWakeEffect : BaseMoveEffect
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

            foreach (PokemonState pokemon in new[] { attacker, defender })
            {
                if (pokemon.Fainted || pokemon.Status != StatusCondition.Sleep)
                    continue;

                pokemon.Status = StatusCondition.None;
                pokemon.SleepTurns = 0;

                state.Log.Write($"{pokemon.Species} woke up in the uproar!");
            }
        }
    }

    /// <summary>
    /// §304. The consecutive-use ramp: Fury Cutter doubles each turn to
    /// four times its power, Echoed Voice adds its own power again each
    /// turn up to five times, Rollout doubles each turn for five turns and
    /// doubles again if the user has curled up.
    ///
    /// The counter itself is one field on the Pokemon rather than one per
    /// move, because the rule all three share is "in a row, and only this
    /// move": using anything else resets it, which a per-move counter would
    /// have to be told about. MoveResolver keeps it - it advances the count
    /// as the move is announced (before any power is read) and clears it
    /// when a move fails, the same two places that spend and break a lock.
    ///
    /// This class only reads it. Being a PowerFormulaEffect (§303) it says
    /// what the power is and the family rescales the damage; it never does
    /// the damage arithmetic itself.
    /// </summary>
    public class PowerFromConsecutiveUsesEffect : PowerFormulaEffect
    {
        readonly bool doubles;
        readonly int maxSteps;
        readonly bool defenseCurlDoubles;

        /// <summary>doubles: each turn multiplies the power (Fury Cutter,
        /// Rollout) rather than adding the listed power again (Echoed
        /// Voice). maxSteps counts the first use, so Fury Cutter's three
        /// steps are 40, 80, 160.</summary>
        public PowerFromConsecutiveUsesEffect(bool doubles, int maxSteps, bool defenseCurlDoubles = false)
        {
            this.doubles = doubles;
            this.maxSteps = maxSteps;
            this.defenseCurlDoubles = defenseCurlDoubles;
        }

        protected override int PowerNow(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move)
        {
            int steps = Math.Clamp(attacker.ConsecutiveMoveUses, 1, maxSteps);

            int power = doubles
                ? move.Power * (1 << (steps - 1))
                : move.Power * steps;

            if (defenseCurlDoubles && attacker.DefenseCurled)
                power *= 2;

            return power;
        }
    }

    /// <summary>§304. Defense Curl's second half - the latch Rollout reads.
    /// The stat change itself is in the data, the way every other pure
    /// stat move's is.</summary>
    public class DefenseCurlEffect : BaseMoveEffect
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
            if (!cancelled)
                attacker.DefenseCurled = true;
        }
    }

    /// <summary>§304. Minimize's second half - the latch that makes the
    /// flattening moves sure hits at double damage. Same shape as Defense
    /// Curl: the evasion boost is data, this is the marker.</summary>
    public class MinimizeEffect : BaseMoveEffect
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
            if (!cancelled)
                attacker.Minimized = true;
        }
    }

    /// <summary>
    /// §304. The lock's bookkeeping, called by MoveResolver at the two
    /// moments that matter. Static rather than an effect because both
    /// moments are outside any one move's effect list - in particular
    /// Break, whose whole job is to run for a move that never reached its
    /// effects at all.
    /// </summary>
    public static class LockIn
    {
        /// <summary>A move went off. Counts a turn off any lock the user is
        /// under, confuses it if the lock has run its course and that lock
        /// confuses, and advances the consecutive-use counter for whatever
        /// move was used.</summary>
        public static void Spend(BattleState state, PokemonState attacker, MoveState move)
        {
            attacker.MoveFailedThisTurn = false;

            if (attacker.LockedMove == null)
                return;

            attacker.LockedTurns--;

            if (attacker.LockedTurns > 0)
                return;

            bool confuses = attacker.LockConfusesOnEnd;

            Clear(attacker);

            if (!confuses || attacker.Fainted)
                return;

            state.Log.Write($"{attacker.Species} tired itself out!");

            // Own Tempo still refuses it, as it refuses every other
            // confusion; and a Pokemon already confused stays as confused
            // as it was rather than having its clock restarted.
            if (attacker.ConfusionTurns > 0 ||
                Abilities.AbilityFactory.Normalize(attacker.AbilityId) == "owntempo")
            {
                return;
            }

            attacker.ConfusionTurns = state.Rng.Next(2, 6);
            state.Log.Write($"{attacker.Species} became confused!");
        }

        /// <summary>A move did not go off - it missed, was blocked, hit
        /// nothing, or was never used because its user could not act. The
        /// lock ends where it stands and the user is NOT confused: the
        /// confusion is the price of finishing, not of starting. The
        /// consecutive-use ramp resets for the same reason.</summary>
        public static void Break(BattleState state, PokemonState attacker)
        {
            attacker.MoveFailedThisTurn = true;

            attacker.ConsecutiveMoveName = null;
            attacker.ConsecutiveMoveUses = 0;

            if (attacker.LockedMove == null)
                return;

            Clear(attacker);

            state.Log.Write($"{attacker.Species} came out of its rampage.");
        }

        /// <summary>Advance the consecutive-use counter as a move is
        /// announced, before anything reads its power. A different move
        /// starts the count again at one.</summary>
        public static void CountUse(PokemonState attacker, MoveState move)
        {
            if (!string.Equals(attacker.ConsecutiveMoveName, move.Name, StringComparison.OrdinalIgnoreCase))
            {
                attacker.ConsecutiveMoveName = move.Name;
                attacker.ConsecutiveMoveUses = 0;
            }

            attacker.ConsecutiveMoveUses++;
        }

        public static void Clear(PokemonState pokemon)
        {
            pokemon.LockedMove = null;
            pokemon.LockedTurns = 0;
            pokemon.LockConfusesOnEnd = false;
        }
    }
}
