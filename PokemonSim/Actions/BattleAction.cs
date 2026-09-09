using PokemonSim.Models;

namespace PokemonSim.Actions
{
    public class BattleAction
    {
        public BattleActionType Type;

        public required PokemonState User;

        public MoveState? Move;

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
    }
}