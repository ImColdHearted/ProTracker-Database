using PokemonSim.Models;

namespace PokemonSim.Actions
{
    public class BattleAction
    {
        public BattleActionType Type;

        public required PokemonState User;

        public MoveState? Move;

        /// <summary>
        /// §305. What this move is aimed at.
        ///
        /// In singles there is exactly one thing it could be aimed at, and
        /// the engine used to work that out for itself at the moment of
        /// resolution - it read the opposing side's active. That is the same
        /// answer, but it is the ENGINE's answer rather than the chooser's,
        /// and in doubles the chooser is the one who knows. So the target
        /// travels with the action now, filled in by LegalActions when the
        /// action is offered.
        ///
        /// Null means "whatever the engine would have picked", which keeps
        /// every caller that builds a BattleAction by hand - the tests, the
        /// tracker's Simulator window, the dev console - working exactly as
        /// it did.
        /// </summary>
        public PokemonState? Target;

        /// <summary>Which of the opposing side's slots Target is standing
        /// in, or 0 in singles where there is only the one.</summary>
        public int TargetSlot;

        public PokemonState? SwitchTarget;

        public int Priority;

        public int Speed;

        // Section 154: which player this action belongs to (set by the
        // engine when the action is queued) - identifying the owner by
        // comparing User against the actives broke as soon as a switch
        // resolved earlier in the same turn.
        public PlayerState? Owner;

        // Section 154: seeded tie-break for equal priority and speed, so a
        // speed tie is a coin flip instead of always favouring Player 1.
        public int TieBreak;

        // Section 161: "and Mega Evolve" on a move action. The engine
        // re-checks legality when the turn resolves, so a stale flag is
        // harmless.
        public bool MegaEvolve;

        // Section 161: "as its Z-Move" on a move action - the resolver
        // synthesizes the Z-Move from Move when this is set and the side's
        // Z-Power is still unspent.
        public bool UseZMove;

        // §375: the user's Quick Claw went off for this action. Rolled by
        // the engine as the action is queued (HeldItems.QuickClawTriggers),
        // read by ActionQueue.Sort ahead of Speed and behind Priority, and
        // announced when the action comes up.
        public bool QuickClaw;
    }
}