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
                if (active.Charging && active.ChargingMove != null)
                {
                    actions.Add(MoveAction(state, active, active.ChargingMove));
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

                        actions.Add(MoveAction(state, active, move));
                    }

                    if (actions.Count == 0)
                        actions.Add(MoveAction(state, active, Struggle()));
                }
            }

            // Section 158: Ingrain roots the user in place, and a trapping
            // ability across the field (Shadow Tag, Magnet Pull) pins too.
            bool maySwitch = !active.Charging && active.Trap == null && !active.Rooted &&
                !OpponentTrapsUs(active, opposing);

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

        private static BattleAction MoveAction(BattleState state, PokemonState user, MoveState move)
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