using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PokemonSim.Actions;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;

namespace PokemonSim.Engine
{
    /// <summary>
    /// Runs a singles battle over a BattleState. Section 154 changed its
    /// job: the engine no longer picks anyone's moves (the old
    /// BuildActionQueue called a MonteCarloAI that did not survive the copy
    /// into the repo, and hard-wired a random opponent for Player 2).
    /// Callers - the Simulator window for a human, an IBattleStrategy for a
    /// computer - choose from GetLegalActions and hand both choices to
    /// RunTurn. RunTurn also fixes the old loop's fatal flaw: an
    /// unconditional return inside the action loop meant only the first
    /// action of every turn ever executed and end-of-turn never ran.
    /// Replacements after a faint are the caller's decision too
    /// (NeedsReplacement/Replace); RunBattle is the strategy-vs-strategy
    /// loop the dev console and the tests drive, with a cancellation token
    /// and the finite turn limit ending everything.
    /// </summary>
    public class BattleEngine
    {
        readonly BattleState state;

        public BattleState State => state;

        public BattleEngine(BattleState state)
        {
            this.state = state;
        }

        public IReadOnlyList<BattleAction> GetLegalActions(PlayerState player) =>
            LegalActions.For(state, player);

        public bool NeedsReplacement(PlayerState player) =>
            state.Outcome == BattleOutcome.Unfinished &&
            player.ActivePokemon.Fainted &&
            !player.HasLost();

        /// <summary>Send in a replacement after a faint. Entry hazards and
        /// switch-in abilities apply exactly as for a chosen switch.</summary>
        public void Replace(PlayerState player, PokemonState replacement)
        {
            SwitchResolver.Resolve(state, player, replacement, voluntary: false);
            UpdateOutcome();
        }

        /// <summary>Stops the battle where it stands.</summary>
        public void Cancel()
        {
            if (state.Outcome != BattleOutcome.Unfinished)
                return;

            state.Outcome = BattleOutcome.Cancelled;
            state.Log.Write("The battle was cancelled.");
        }

        /// <summary>One full turn from two chosen actions: switches first,
        /// then moves by priority, speed, and a seeded coin flip on ties;
        /// then the end-of-turn phase (residuals, weather, terrain, traps,
        /// Protect bookkeeping) and the outcome check.</summary>
        public void RunTurn(BattleAction player1Action, BattleAction player2Action)
        {
            if (state.Outcome != BattleOutcome.Unfinished)
                return;

            BattleInitializer.Initialize(state);

            state.TurnNumber++;
            state.Log.Turn(state.TurnNumber);

            // Section 161: mega evolutions resolve before anything else in
            // the turn, faster side first (slower first under Trick Room,
            // like everything else).
            PerformMegaEvolutions(player1Action, player2Action);

            var queue = new ActionQueue();

            Enqueue(queue, state.Player1, player1Action);
            Enqueue(queue, state.Player2, player2Action);

            queue.Sort(state.TrickRoomTurns > 0);

            foreach (var action in queue.Actions)
            {
                if (state.Outcome != BattleOutcome.Unfinished || state.Player1.HasLost() || state.Player2.HasLost())
                    break;

                ExecuteAction(action);
            }

            if (state.Outcome == BattleOutcome.Unfinished &&
                !state.Player1.HasLost() && !state.Player2.HasLost())
            {
                EndOfTurn();
            }

            UpdateOutcome();

            if (state.Outcome == BattleOutcome.Unfinished && state.TurnNumber >= state.MaxTurns)
            {
                state.Outcome = BattleOutcome.Draw;
                state.Log.Write($"The battle ended in a draw after {state.MaxTurns} turns.");
            }
        }

        void Enqueue(ActionQueue queue, PlayerState owner, BattleAction action)
        {
            action.Owner = owner;
            action.TieBreak = state.Rng.Next(1_000_000);
            queue.Actions.Add(action);
        }

        /// <summary>Section 161. Both sides' flagged mega evolutions, in
        /// speed order. MegaEvolutions.Perform re-checks legality, so a
        /// stale or illegal flag simply does nothing. A mega'd action's
        /// stored Speed is refreshed so the queue sorts on the new stats
        /// rather than the value the legal-action layer computed before the
        /// transform.</summary>
        void PerformMegaEvolutions(BattleAction player1Action, BattleAction player2Action)
        {
            var candidates = new List<(BattleAction Action, PlayerState Side)>
            {
                (player1Action, state.Player1),
                (player2Action, state.Player2)
            };

            var ordered = candidates
                .OrderByDescending(c => StatResolver.GetStat(state, c.Side.ActivePokemon, "Speed"))
                .ToList();

            if (state.TrickRoomTurns > 0)
                ordered.Reverse();

            foreach ((BattleAction action, PlayerState side) in ordered)
            {
                if (!action.MegaEvolve || action.Type != BattleActionType.Move)
                    continue;

                if (!ReferenceEquals(action.User, side.ActivePokemon))
                    continue;

                if (MegaEvolutions.Perform(state, side, action.User))
                    action.Speed = (int)StatResolver.GetStat(state, action.User, "Speed");
            }
        }

        /// <summary>Strategy vs strategy to the end - the dev console's and
        /// the tests' loop. Checks the token between turns; cancelling marks
        /// the battle Cancelled rather than throwing. Section 156: a caller
        /// that wants this WHOLE battle observed (a user-marked lab run, a
        /// test) passes the observer explicitly; nothing inside the engine
        /// ever creates or implies one, and Monte Carlo rollouts call
        /// RunTurn directly so they can never reach this hook.</summary>
        public BattleOutcome RunBattle(
            IBattleStrategy player1Strategy,
            IBattleStrategy player2Strategy,
            CancellationToken cancellationToken = default,
            Observation.IBattleTurnObserver? observer = null)
        {
            BattleInitializer.Initialize(state);

            while (state.Outcome == BattleOutcome.Unfinished)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Cancel();
                    break;
                }

                var legal1 = GetLegalActions(state.Player1);
                var action1 = player1Strategy.ChooseAction(state, state.Player1, legal1);
                // Section 175: read the ranking straight after the choice
                // that produced it - that is the whole of ITeacherPolicy's
                // contract, and the observer copies what it is given.
                observer?.OnDecision(state, state.Player1, player1Strategy.Name, legal1, action1,
                    player1Strategy as ITeacherPolicy);

                var legal2 = GetLegalActions(state.Player2);
                var action2 = player2Strategy.ChooseAction(state, state.Player2, legal2);
                observer?.OnDecision(state, state.Player2, player2Strategy.Name, legal2, action2,
                    player2Strategy as ITeacherPolicy);

                RunTurn(action1, action2);

                observer?.OnTurnResolved(state);

                ResolveReplacements(player1Strategy, player2Strategy, observer);
            }

            observer?.OnFinished(state);

            return state.Outcome;
        }

        void ResolveReplacements(
            IBattleStrategy player1Strategy,
            IBattleStrategy player2Strategy,
            Observation.IBattleTurnObserver? observer = null)
        {
            foreach (var (player, strategy) in new[]
            {
                (state.Player1, player1Strategy),
                (state.Player2, player2Strategy)
            })
            {
                while (NeedsReplacement(player))
                {
                    var indexes = new List<int>();

                    for (int i = 0; i < player.Team.Count; i++)
                    {
                        if (!player.Team[i].Fainted)
                            indexes.Add(i);
                    }

                    int chosen = strategy.ChooseReplacement(state, player, indexes);

                    if (!indexes.Contains(chosen))
                        chosen = indexes[0];

                    // §180: the observer sees the decision, not its
                    // aftermath. Called after Replace, the recorded state
                    // already had the chosen Pokemon standing in it - the
                    // answer inside its own question - which is why those
                    // records were excluded from training. Before it, with
                    // the candidates the engine offered, a replacement is
                    // an ordinary switch example.
                    observer?.OnReplacement(state, player, strategy.Name,
                        player.Team[chosen], indexes,
                        strategy as ITeacherPolicy);

                    Replace(player, player.Team[chosen]);
                }
            }
        }

        void UpdateOutcome()
        {
            if (state.Outcome != BattleOutcome.Unfinished)
                return;

            bool lost1 = state.Player1.HasLost();
            bool lost2 = state.Player2.HasLost();

            if (lost1 && lost2)
            {
                state.Outcome = BattleOutcome.Draw;
                state.Log.Write("Both sides are out of Pokemon - it's a draw!");
            }
            else if (lost1)
            {
                state.Outcome = BattleOutcome.Player2Wins;
                state.Log.Write($"{state.Player2.Name} wins!");
            }
            else if (lost2)
            {
                state.Outcome = BattleOutcome.Player1Wins;
                state.Log.Write($"{state.Player1.Name} wins!");
            }
        }

        void ExecuteAction(BattleAction action)
        {
            var owner = action.Owner ?? state.GetOwner(action.User);
            var opponent = state.GetOpponentOf(owner);

            // A Pokemon that fainted earlier this turn loses its action; its
            // replacement comes in after the turn.
            if (action.User.Fainted)
                return;

            if (action.Type == BattleActionType.Switch)
            {
                if (action.SwitchTarget != null)
                {
                    SwitchResolver.Resolve(state, owner, action.SwitchTarget);
                }

                return;
            }

            if (action.Type == BattleActionType.Move && action.Move != null)
            {
                var attacker = action.User;
                var defender = opponent.ActivePokemon;

                if (defender.Fainted)
                {
                    state.Log.Write($"{attacker.Species}'s attack had no target!");
                    return;
                }

                // Psychic Terrain shields grounded Pokemon from priority
                // moves.
                if (state.Environment.Terrain == TerrainType.Psychic &&
                    Grounding.IsGrounded(state, defender) &&
                    action.Move.Priority > 0 &&
                    action.Move.Category != MoveCategory.Status)
                {
                    state.Log.Write($"{defender.Species} is protected by Psychic Terrain!");
                    return;
                }

                // Section 161: the Z-Power conversion. The synthesized
                // Z-Move resolves in the base move's place; the base move
                // pays the PP (the synth is MaxPP 0, like Struggle) and
                // stays the user's "last move" so Encore and Torment keep
                // pointing at something the user actually knows.
                MoveState move = action.Move;

                if (action.UseZMove && ZMoves.CanUse(state, owner, attacker, move))
                {
                    owner.UsedZMove = true;

                    if (action.Move.CurrentPP > 0)
                        action.Move.CurrentPP--;

                    move = ZMoves.Synthesize(action.Move);

                    state.Log.Write($"{attacker.Species} surrounded itself with its Z-Power!");
                }

                MoveResolver.Resolve(state, attacker, defender, move);

                // §197: the Pokemon has now had its go, so Fake Out and First
                // Impression are done for this stay on the field. AFTER the
                // resolve, not before, because the effect that reads this runs
                // inside it - and regardless of whether the move landed, since
                // a flinch or a full paralysis still spends the turn.
                attacker.HasActedSinceEnteringField = true;

                // Only when the Z-Move actually came out (a full paralysis
                // or a flinch leaves the last-move memory alone).
                if (!ReferenceEquals(move, action.Move) &&
                    attacker.LastMoveName == move.Name)
                {
                    attacker.LastMoveName = action.Move.Name;
                }
            }
        }

        void EndOfTurn()
        {
            state.Events.Dispatch(
                new BattleEvent { Type = BattleEventType.Residual },
                state
            );

            Effects.BurnEffect.Apply(state);
            Effects.PoisonEffect.Apply(state);

            WeatherEffects.Apply(state);

            var env = state.Environment;

            // Grassy Terrain heals both grounded actives; the terrain clock
            // ticks once per turn (the old code ticked it inside the heal,
            // so it ran down twice as fast and only while it was Grassy).
            if (env.Terrain == TerrainType.Grassy)
            {
                HealFromGrass(state.Player1.ActivePokemon);
                HealFromGrass(state.Player2.ActivePokemon);
            }

            if (env.Terrain != TerrainType.None && env.TerrainTurns > 0)
            {
                env.TerrainTurns--;

                if (env.TerrainTurns == 0)
                {
                    env.Terrain = TerrainType.None;
                    state.Log.Write("The terrain returned to normal.");
                }
            }

            foreach (var pokemon in new[] { state.Player1.ActivePokemon, state.Player2.ActivePokemon })
            {
                ApplyTrap(pokemon);

                // Protect only lasts the turn it was used; a turn without
                // Protect resets the diminishing-success streak.
                pokemon.Protected = false;

                if (!pokemon.ProtectedThisTurn)
                    pokemon.ConsecutiveProtects = 0;

                pokemon.ProtectedThisTurn = false;
                pokemon.Flinched = false;
            }

            // Section 158: the volatile and side-condition batch; §159
            // adds the held items (Leftovers, the status orbs) before the
            // end-of-turn abilities (Speed Boost, Bad Dreams, Rain Dish,
            // Shed Skin and friends).
            EndOfTurnVolatiles();
            Items.HeldItems.EndOfTurn(state);
            Abilities.AbilityEndOfTurn.Apply(state);

            state.Events.Dispatch(
                new BattleEvent { Type = BattleEventType.EndTurn },
                state
            );
        }

        /// <summary>Section 158. Leech Seed, Curse, Salt Cure, the Ingrain
        /// and Aqua Ring heals, Yawn and Perish Song countdowns, the
        /// Encore/Taunt/Disable/Magnet Rise timers, per-turn counter
        /// bookkeeping, and the side-condition clocks.</summary>
        void EndOfTurnVolatiles()
        {
            foreach (var (side, other) in new[]
            {
                (state.Player1, state.Player2),
                (state.Player2, state.Player1)
            })
            {
                var pokemon = side.ActivePokemon;

                if (!pokemon.Fainted && pokemon.LeechSeeded && !pokemon.HasMagicGuard)
                {
                    int drained = Math.Max(1, pokemon.MaxHP / 8);

                    pokemon.CurrentHP = Math.Max(0, pokemon.CurrentHP - drained);
                    state.Log.Write($"{pokemon.Species}'s health is sapped by Leech Seed!");

                    var receiver = other.ActivePokemon;

                    if (!receiver.Fainted && receiver.CurrentHP < receiver.MaxHP)
                        receiver.CurrentHP = Math.Min(receiver.MaxHP, receiver.CurrentHP + drained);

                    AnnounceFaint(pokemon);
                }

                if (!pokemon.Fainted && pokemon.Cursed && !pokemon.HasMagicGuard)
                {
                    pokemon.CurrentHP = Math.Max(0, pokemon.CurrentHP - Math.Max(1, pokemon.MaxHP / 4));
                    state.Log.Write($"{pokemon.Species} is afflicted by the curse!");
                    AnnounceFaint(pokemon);
                }

                if (!pokemon.Fainted && pokemon.SaltCured && !pokemon.HasMagicGuard)
                {
                    bool stings = pokemon.Types.Contains(PokemonType.Water) ||
                                  pokemon.Types.Contains(PokemonType.Steel);

                    int hurt = Math.Max(1, pokemon.MaxHP / (stings ? 4 : 8));

                    pokemon.CurrentHP = Math.Max(0, pokemon.CurrentHP - hurt);
                    state.Log.Write($"{pokemon.Species} is hurt by Salt Cure!");
                    AnnounceFaint(pokemon);
                }

                if (!pokemon.Fainted && (pokemon.Rooted || pokemon.AquaRing) &&
                    pokemon.CurrentHP < pokemon.MaxHP)
                {
                    pokemon.CurrentHP = Math.Min(pokemon.MaxHP,
                        pokemon.CurrentHP + Math.Max(1, pokemon.MaxHP / 16));

                    state.Log.Write(pokemon.Rooted
                        ? $"{pokemon.Species} absorbed nutrients with its roots!"
                        : $"{pokemon.Species} is healed by Aqua Ring!");
                }

                if (!pokemon.Fainted && pokemon.YawnTurns > 0)
                {
                    pokemon.YawnTurns--;

                    if (pokemon.YawnTurns == 0)
                    {
                        MoveResolver.TryInflictStatus(state, pokemon, StatusCondition.Sleep,
                            announceFailure: false, substituteBlocks: false);
                    }
                }

                if (!pokemon.Fainted && pokemon.PerishCount > 0)
                {
                    pokemon.PerishCount--;
                    state.Log.Write($"{pokemon.Species}'s perish count fell to {pokemon.PerishCount}!");

                    if (pokemon.PerishCount == 0)
                    {
                        pokemon.CurrentHP = 0;
                        AnnounceFaint(pokemon);
                    }
                }

                if (pokemon.EncoreTurns > 0 && --pokemon.EncoreTurns == 0)
                {
                    pokemon.EncoreMoveName = null;
                    state.Log.Write($"{pokemon.Species}'s encore ended!");
                }

                if (pokemon.TauntTurns > 0 && --pokemon.TauntTurns == 0)
                    state.Log.Write($"{pokemon.Species} shook off the taunt!");

                if (pokemon.DisabledTurns > 0 && --pokemon.DisabledTurns == 0)
                {
                    pokemon.DisabledMoveName = null;
                    state.Log.Write($"{pokemon.Species} is no longer disabled!");
                }

                if (pokemon.MagnetRiseTurns > 0 && --pokemon.MagnetRiseTurns == 0)
                    state.Log.Write($"{pokemon.Species}'s Magnet Rise wore off!");

                pokemon.LastPhysicalDamageTaken = 0;
                pokemon.LastSpecialDamageTaken = 0;
                pokemon.ActedThisTurn = false;
                pokemon.Enduring = false;
                pokemon.KingsShieldUp = false;
                pokemon.BanefulBunkerUp = false;
                pokemon.QuickGuardUp = false;
                pokemon.WideGuardUp = false;

                TickSide(ref state.ReflectTurns(side), $"{side.Name}'s Reflect wore off!");
                TickSide(ref state.LightScreenTurns(side), $"{side.Name}'s Light Screen wore off!");
                TickSide(ref state.MistTurns(side), $"The mist around {side.Name}'s team faded.");
                TickSide(ref state.SafeguardTurns(side), $"{side.Name}'s Safeguard faded.");
                TickSide(ref state.TailwindTurns(side), $"{side.Name}'s tailwind petered out.");
            }

            if (state.TrickRoomTurns > 0 && --state.TrickRoomTurns == 0)
                state.Log.Write("The twisted dimensions returned to normal!");

            if (state.GravityTurns > 0 && --state.GravityTurns == 0)
                state.Log.Write("Gravity returned to normal!");
        }

        void TickSide(ref int turns, string message)
        {
            if (turns > 0 && --turns == 0)
                state.Log.Write(message);
        }

        void AnnounceFaint(PokemonState pokemon)
        {
            if (pokemon.Fainted)
                state.Log.Write($"{pokemon.Species} fainted!");
        }

        void HealFromGrass(PokemonState pokemon)
        {
            if (pokemon.Fainted || !Grounding.IsGrounded(pokemon) || pokemon.CurrentHP >= pokemon.MaxHP)
                return;

            int heal = pokemon.MaxHP / 16;

            pokemon.CurrentHP = Math.Min(pokemon.MaxHP, pokemon.CurrentHP + heal);

            state.Log.Write($"{pokemon.Species} is healed by the grassy terrain!");
        }

        void ApplyTrap(PokemonState pokemon)
        {
            if (pokemon.Trap == null || pokemon.Fainted)
                return;

            // Section 155: Mean Look-style traps carry no residual
            // damage - they only pin the target in place.
            if (!pokemon.HasMagicGuard && pokemon.Trap.DamageFraction > 0)
            {
                int damage = Math.Max(1, (int)(pokemon.MaxHP * pokemon.Trap.DamageFraction));

                pokemon.CurrentHP = Math.Max(0, pokemon.CurrentHP - damage);

                state.Log.Write($"{pokemon.Species} is hurt by {pokemon.Trap.SourceMove}!");

                if (pokemon.Fainted)
                    state.Log.Write($"{pokemon.Species} fainted!");
            }

            pokemon.Trap.TurnsRemaining--;

            if (pokemon.Trap.TurnsRemaining <= 0)
            {
                state.Log.Write($"{pokemon.Species} was freed from {pokemon.Trap.SourceMove}!");
                pokemon.Trap = null;
            }
        }
    }
}