using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Models;

namespace PokemonSim.Engine
{
    /// <summary>
    /// Section 154. What a player may actually do this turn - the single
    /// source both the Simulator window's buttons and every computer
    /// strategy draw from, so an illegal click is impossible by
    /// construction. Rules: a fainted active only switches; a charging
    /// two-turn move locks the user into finishing it; moves need PP; a
    /// Pokemon with no usable moves gets Struggle; switching offers every
    /// healthy benched teammate, except while charging or trapped.
    /// </summary>
    public static class LegalActions
    {
        public static List<BattleAction> For(BattleState state, PlayerState player)
        {
            var actions = new List<BattleAction>();
            var active = player.ActivePokemon;
            var opposing = state.GetOpponentOf(player).ActivePokemon;

            if (!active.Fainted)
            {
                // §301: a recharge turn holds exactly one action, the same
                // shape as a charge turn - the move is offered back and
                // MoveResolver spends the turn on the recharge rather than
                // on the move. Before the charge test because the two can
                // never both be owed, and this one is the cheaper read.
                if (active.MustRecharge && active.RechargeMove != null)
                {
                    actions.Add(MoveAction(state, active, active.RechargeMove, opposing));
                }
                // §304: a lock-in is the third turn that is already spoken
                // for. Outrage, Thrash, Petal Dance, Uproar and Rollout all
                // take the next turns and spend them on themselves, so the
                // menu holds the one move - and holds it even with no PP
                // left, because the lock is not paying for it again.
                else if (active.LockedMove != null && active.LockedTurns > 0)
                {
                    actions.Add(MoveAction(state, active, active.LockedMove, opposing));
                }
                else if (active.Charging && active.ChargingMove != null)
                {
                    actions.Add(MoveAction(state, active, active.ChargingMove, opposing));
                }
                else
                {
                    foreach (var move in active.Moves)
                    {
                        if (move.CurrentPP <= 0)
                            continue;

                        // Section 158: the move-restriction volatiles.
                        if (active.EncoreTurns > 0 && move.Name != active.EncoreMoveName)
                            continue;

                        if (active.TauntTurns > 0 && move.Category == MoveCategory.Status)
                            continue;

                        if (active.DisabledTurns > 0 && move.Name == active.DisabledMoveName)
                            continue;

                        if (active.Tormented && move.Name == active.LastMoveName)
                            continue;

                        // Section 158: Imprison seals moves the opponent
                        // also knows.
                        if (!opposing.Fainted && opposing.ImprisonActive &&
                            opposing.Moves.Any(m => string.Equals(m.Name, move.Name, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        // Section 159: a Choice item locks its holder in;
                        // an Assault Vest bars status moves.
                        if (!Items.HeldItems.AllowsMove(active, move))
                            continue;

                        actions.Add(MoveAction(state, active, move, opposing));
                    }

                    if (actions.Count == 0)
                        actions.Add(MoveAction(state, active, Struggle(), opposing));
                }
            }

            // Section 158: Ingrain roots the user in place, and a trapping
            // ability across the field (Shadow Tag, Magnet Pull) pins too.
            // §301: a Pokemon that owes a recharge cannot leave the field
            // either - the turn is already spoken for.
            // §304: and a Pokemon in the middle of an Outrage cannot leave
            // the field either, for the same reason as the other two.
            // §375: a Shed Shell opens the two doors a trap closes - the
            // trapping move and the trapping ability - and none of the
            // others: roots, a charge, a recharge and a lock-in are the
            // holder's own doing.
            bool escapes = Items.HeldItems.LetsHolderEscape(active);

            bool maySwitch = !active.Charging && !active.MustRecharge &&
                active.LockedMove == null && !active.Rooted &&
                (escapes || (active.Trap == null && !OpponentTrapsUs(active, opposing)));

            if (active.Fainted)
                maySwitch = true;

            if (maySwitch)
            {
                foreach (var teammate in player.Team)
                {
                    if (teammate.Fainted || teammate == active)
                        continue;

                    actions.Add(new BattleAction
                    {
                        Type = BattleActionType.Switch,
                        User = active,
                        SwitchTarget = teammate,
                        // Switches resolve before any move - modeled as a
                        // priority above every move's (max real priority +5).
                        Priority = 10,
                        Speed = (int)StatResolver.GetStat(state, active, "Speed")
                    });
                }
            }

            return actions;
        }

        static bool OpponentTrapsUs(PokemonState active, PokemonState opposing)
        {
            if (opposing.Fainted)
                return false;

            string ability = Abilities.AbilityFactory.Normalize(opposing.AbilityId);

            if (ability == "shadowtag")
                return !active.Types.Contains(PokemonType.Ghost) &&
                       Abilities.AbilityFactory.Normalize(active.AbilityId) != "shadowtag";

            if (ability == "magnetpull")
                return active.Types.Contains(PokemonType.Steel);

            return false;
        }

        /// <summary>§305: the action now carries what it is aimed at.
        /// In singles there is one answer and the engine used to work it out
        /// for itself at resolution time; naming it here is the same answer
        /// from the side that actually chose it, and it is what a doubles
        /// menu will have to offer one entry per target of.</summary>
        private static BattleAction MoveAction(
            BattleState state,
            PokemonState user,
            MoveState move,
            PokemonState? target = null,
            int targetSlot = 0)
        {
            int priority = move.Priority;

            // Section 158: Grassy Glide jumps the queue on its terrain.
            if (move.Effects != null && move.Effects.Contains("GrassyGlidePriority") &&
                state.Environment.Terrain == TerrainType.Grassy &&
                Grounding.IsGrounded(state, user))
            {
                priority += 1;
            }

            return new BattleAction
            {
                Type = BattleActionType.Move,
                User = user,
                Move = move,
                Target = target,
                TargetSlot = targetSlot,
                Priority = priority,
                Speed = (int)StatResolver.GetStat(state, user, "Speed")
            };
        }

        /// <summary>The out-of-PP fallback: typeless 50-power physical hit
        /// that cannot miss, with quarter-max-HP recoil (see
        /// StruggleRecoilEffect). Synthesized here because the data files
        /// do not carry Struggle.</summary>
        public static MoveState Struggle()
        {
            return new MoveState
            {
                Name = "Struggle",
                Power = 50,
                Accuracy = 0,
                Typeless = true,
                MaxPP = 0,
                CurrentPP = 0,
                Type = PokemonType.Normal,
                Category = MoveCategory.Physical,
                IsContact = true,
                Effects = new List<string> { "StruggleRecoil" }
            };
        }
    }
}