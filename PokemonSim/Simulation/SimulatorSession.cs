using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;

namespace PokemonSim.Simulation
{
    /// <summary>
    /// Section 154. One human-vs-computer battle as the Simulator window
    /// drives it, kept UI-free so the window stays a thin binding layer
    /// and this whole flow is testable headlessly. The player's chosen
    /// action and the opponent strategy's reply run through the engine off
    /// the caller's thread (PlayTurnAsync), a cancellation token stops the
    /// battle as Cancelled, and after a faint the session says who must
    /// replace: the opponent replaces via its strategy automatically, the
    /// player through ReplacePlayerPokemon once the window asked.
    /// </summary>
    public sealed class SimulatorSession
    {
        readonly IBattleStrategy opponentStrategy;

        /// <summary>§183. The opponent's notes on this battle, folded into
        /// its book when the battle ends. The session owns the folding
        /// because it is the only thing here that knows the battle is over
        /// and who won; the strategy only knows it made a choice.</summary>
        readonly BattleRecall? recall;

        // Section 156: the optional passive observer. Only THIS session -
        // the primary visible battle - and an explicitly-marked lab run
        // ever hold one; Monte Carlo's internal rollouts run on clones no
        // observer is attached to, so they can never be recorded.
        public IBattleTurnObserver? Observer { get; }
        bool observerFinished;

        public BattleState State { get; }
        public BattleEngine Engine { get; }

        public PlayerState Player => State.Player1;
        public PlayerState Opponent => State.Player2;

        public BattleOutcome Outcome => State.Outcome;

        public bool PlayerMustReplace => Engine.NeedsReplacement(Player);

        public SimulatorSession(
            string playerName,
            List<PokemonState> playerTeam,
            string opponentName,
            List<PokemonState> opponentTeam,
            IBattleStrategy? strategy = null,
            int? seed = null,
            IBattleTurnObserver? observer = null,
            BattleRecall? recall = null)
        {
            opponentStrategy = strategy ?? new RandomMoveStrategy();
            Observer = observer;
            this.recall = recall;

            State = new BattleState
            {
                Player1 = new PlayerState
                {
                    Name = playerName,
                    Team = playerTeam,
                    ActivePokemon = playerTeam[0]
                },
                Player2 = new PlayerState
                {
                    Name = opponentName,
                    Team = opponentTeam,
                    ActivePokemon = opponentTeam[0]
                },
                Rng = new BattleRng(seed ?? Environment.TickCount)
            };

            Engine = new BattleEngine(State);

            BattleInitializer.Initialize(State);
        }

        public IReadOnlyList<BattleAction> PlayerLegalActions() =>
            Engine.GetLegalActions(Player);

        /// <summary>One turn: the player's chosen action against the
        /// opponent strategy's pick. Runs on a worker thread so the UI
        /// thread never waits on the engine; the opponent also auto-replaces
        /// a fainted Pokemon here. Cancellation marks the battle Cancelled.</summary>
        public Task PlayTurnAsync(BattleAction playerAction, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Engine.Cancel();
                    NotifyFinishedIfOver();
                    return;
                }

                // Section 156: the observer sees each committed decision
                // BEFORE the turn resolves, then the resolved turn. It is
                // handed the live state read-only and can neither change
                // an action nor delay resolution (its writes are queued).
                Observer?.OnDecision(State, Player, "Human",
                    Engine.GetLegalActions(Player), playerAction);

                var opponentLegal = Engine.GetLegalActions(Opponent);

                var opponentAction = opponentStrategy.ChooseAction(
                    State, Opponent, opponentLegal);

                // Section 161: an opponent holding a Z-Crystal trades its
                // chosen move for the Z-Move the first time one looks
                // lethal. The estimate runs on a clone with its own fixed
                // rng, so the battle's own rng position never moves.
                var lethalZ = ZMoves.TryPickLethalZ(
                    State, Opponent, Opponent.ActivePokemon,
                    Player.ActivePokemon, opponentLegal);

                if (lethalZ != null)
                {
                    lethalZ.MegaEvolve = opponentAction.MegaEvolve;
                    opponentAction = lethalZ;
                    opponentAction.UseZMove = true;
                }

                // Section 175: the boss brain's own per-slot rollout means
                // ride along, so a battle you actually played teaches the
                // trainer as much as a lab battle does. A Z-Move swap above
                // does not change what the slots were worth.
                Observer?.OnDecision(State, Opponent, opponentStrategy.Name,
                    opponentLegal, opponentAction,
                    opponentStrategy as ITeacherPolicy);

                Engine.RunTurn(playerAction, opponentAction);

                Observer?.OnTurnResolved(State);

                ReplaceOpponentIfNeeded();

                NotifyFinishedIfOver();
            }, CancellationToken.None);
        }

        /// <summary>The player's replacement after a faint (a team index
        /// from PlayerReplacementChoices).</summary>
        public void ReplacePlayerPokemon(int teamIndex)
        {
            if (!PlayerMustReplace)
                return;

            if (teamIndex < 0 || teamIndex >= Player.Team.Count)
                return;

            var replacement = Player.Team[teamIndex];

            if (replacement.Fainted)
                return;

            // §180: before the swap, with the choices the player was
            // offered - the same shape the engine's own path records.
            Observer?.OnReplacement(State, Player, "Human", replacement,
                PlayerReplacementChoices());

            Engine.Replace(Player, replacement);
        }

        public IReadOnlyList<int> PlayerReplacementChoices()
        {
            var choices = new List<int>();

            for (int i = 0; i < Player.Team.Count; i++)
            {
                if (!Player.Team[i].Fainted)
                    choices.Add(i);
            }

            return choices;
        }

        void ReplaceOpponentIfNeeded()
        {
            while (Engine.NeedsReplacement(Opponent))
            {
                var indexes = new List<int>();

                for (int i = 0; i < Opponent.Team.Count; i++)
                {
                    if (!Opponent.Team[i].Fainted)
                        indexes.Add(i);
                }

                int chosen = opponentStrategy.ChooseReplacement(State, Opponent, indexes);

                if (!indexes.Contains(chosen))
                    chosen = indexes[0];

                Observer?.OnReplacement(State, Opponent, opponentStrategy.Name,
                    Opponent.Team[chosen], indexes,
                    opponentStrategy as ITeacherPolicy);

                Engine.Replace(Opponent, Opponent.Team[chosen]);
            }
        }

        void NotifyFinishedIfOver()
        {
            if (observerFinished || State.Outcome == BattleOutcome.Unfinished)
                return;

            observerFinished = true;
            Observer?.OnFinished(State);

            // §183: the opponent is Player 2, so "won" is its own result,
            // not the player's. A draw or a cancellation is not a win, and
            // it still counts as having played those lines - which is what
            // stops the brain repeating a line that keeps drawing.
            recall?.Settle(State.Outcome == BattleOutcome.Player2Wins);
        }

        public void Cancel()
        {
            Engine.Cancel();
            NotifyFinishedIfOver();
        }
    }
}