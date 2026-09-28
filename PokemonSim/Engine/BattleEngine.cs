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

        /// <summary>§305: any empty slot, not only the first. In singles
        /// there is one slot and this is the question it always asked.</summary>
        public bool NeedsReplacement(PlayerState player) =>
            EmptySlot(player) >= 0;

        /// <summary>§305. The first slot waiting for a replacement, or -1 if
        /// none is.</summary>
        public int EmptySlot(PlayerState player)
        {
            if (state.Outcome != BattleOutcome.Unfinished || player.HasLost())
                return -1;

            for (int i = 0; i < player.Active.Count; i++)
            {
                if (player.Active[i].Fainted)
                    return i;
            }

            return -1;
        }

        /// <summary>Send in a replacement after a faint. Entry hazards and
        /// switch-in abilities apply exactly as for a chosen switch.</summary>
        public void Replace(PlayerState player, PokemonState replacement) =>
            Replace(player, replacement, EmptySlot(player));

        /// <summary>§305: into a named slot. The slot-less form above fills
        /// the first empty one, which in singles is the only one.</summary>
        public void Replace(PlayerState player, PokemonState replacement, int slot)
        {
            SwitchResolver.Resolve(state, player, replacement, voluntary: false,
                slot: slot < 0 ? 0 : slot);

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
        /// <summary>§305. The two-action form, which is every caller there
        /// has ever been: the tests, the dev console, the strategy loop and
        /// the tracker's Simulator window. It hands both actions to the list
        /// form below, so there is one turn and it is written once.</summary>
        public void RunTurn(BattleAction player1Action, BattleAction player2Action) =>
            RunTurn(new[] { player1Action, player2Action });

        /// <summary>
        /// §305. One turn from the actions every side chose.
        ///
        /// A list rather than a pair because a doubles turn is four actions
        /// and a singles turn is two, and the only difference between them
        /// should be how many there are. Everything inside already worked
        /// off the queue rather than off the two arguments - the arguments
        /// were only ever used to build it - so this is the same turn with
        /// the pair taken out of its signature.
        /// </summary>
        public void RunTurn(IReadOnlyList<BattleAction> actions)
        {
            if (state.Outcome != BattleOutcome.Unfinished)
                return;

            BattleInitializer.Initialize(state);

            state.TurnNumber++;
            state.Log.Turn(state.TurnNumber);

            // §319: the picture the end of this turn will be measured
            // against. The knowledge layer learns by DIFFING the visible
            // field, so it needs a before as well as an after - and it has
            // to be taken here, after the turn counter moves and before any
            // action resolves.
            state.Knowledge.BeginTurn(state);

            // §305: every action knows whose it is before anything else
            // reads it, so the two passes below do not have to guess.
            foreach (var action in actions)
                action.Owner ??= state.GetOwner(action.User);

            // Section 161: mega evolutions resolve before anything else in
            // the turn, faster side first (slower first under Trick Room,
            // like everything else).
            PerformMegaEvolutions(actions);

            // §304: what each side chose, recorded before a single action
            // resolves. Upper Hand asks whether its target is about to use
            // a priority move and Pursuit whether its target is about to
            // leave; both questions are about the future, which is only
            // answerable from here. Cleared at end of turn.
            //
            // §305: the FIRST action each side declared. In singles that is
            // the only one; in doubles a side declares two and this will
            // have to become a list of its own, which is a change to what
            // the two moves reading it mean rather than to the plumbing.
            state.DeclaredP1 = actions.FirstOrDefault(a => a.Owner == state.Player1);
            state.DeclaredP2 = actions.FirstOrDefault(a => a.Owner == state.Player2);

            var queue = new ActionQueue();

            foreach (var action in actions)
                Enqueue(queue, action.Owner!, action);

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

            // §319: and the after. Everything the observation encoder is
            // allowed to know about the opponent is written here, out of the
            // difference between the two pictures - never out of the
            // opponent's own state.
            state.Knowledge.EndTurn(state);

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

            // §375: a Quick Claw is rolled here, once per move action, so
            // the sort below can read the answer.
            action.QuickClaw = action.Type == BattleActionType.Move &&
                Items.HeldItems.QuickClawTriggers(state, action.User);

            queue.Actions.Add(action);
        }

        /// <summary>
        /// §305. Who this move is aimed at.
        ///
        /// A move is aimed at a POSITION, not at a Pokemon. If the thing
        /// that was standing there when the action was chosen has switched
        /// out in the meantime, the move hits whatever took its place -
        /// which is how the games play it, and which is exactly what the old
        /// line did by reading the opposing active at the moment of
        /// resolution rather than at the moment of choosing.
        ///
        /// So the SLOT is the answer and BattleAction.Target is only the
        /// name of what was standing in it at the time. Reading the stored
        /// Pokemon instead would have quietly changed a switch from a dodge
        /// into a way of dragging the attack onto the bench.
        ///
        /// TargetSlot is 0 unless somebody set it, and slot 0 is the
        /// opposing active, so every caller that builds an action by hand
        /// gets the behaviour it has always had.
        /// </summary>
        PokemonState DefenderFor(BattleAction action, PlayerState opponent)
        {
            PokemonState? inSlot = state.InSlot(opponent, action.TargetSlot);

            if (inSlot != null && !inSlot.Fainted)
                return inSlot;

            // The slot is empty - somebody has fainted and not been replaced
            // yet. Anything else still standing on that side will do.
            foreach (PokemonState standing in opponent.Standing())
                return standing;

            return opponent.ActivePokemon;
        }

        /// <summary>Section 161. Both sides' flagged mega evolutions, in
        /// speed order. MegaEvolutions.Perform re-checks legality, so a
        /// stale or illegal flag simply does nothing. A mega'd action's
        /// stored Speed is refreshed so the queue sorts on the new stats
        /// rather than the value the legal-action layer computed before the
        /// transform.</summary>
        /// <summary>§305: over the turn's actions rather than over a pair.
        /// The ordering is by the ACTING Pokemon's speed now, which in
        /// singles is the same Pokemon as the side's active and so the same
        /// order - but in doubles a side has two of them and "the side's
        /// speed" would have stopped meaning anything.</summary>
        void PerformMegaEvolutions(IReadOnlyList<BattleAction> actions)
        {
            var ordered = actions
                .OrderByDescending(a => StatResolver.GetStat(state, a.User, "Speed"))
                .ToList();

            if (state.TrickRoomTurns > 0)
                ordered.Reverse();

            foreach (BattleAction action in ordered)
            {
                PlayerState side = action.Owner ?? state.GetOwner(action.User);

                if (!action.MegaEvolve || action.Type != BattleActionType.Move)
                    continue;

                if (side.SlotOf(action.User) < 0)
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

            // §375: so does one that LEFT the field earlier this turn - hit
            // by a Roar, or sent off by its own Eject Button. Its
            // replacement chose nothing, and the bench does not act. (Before
            // this, a Pokemon Roared out ahead of its own move still made
            // that move, from the bench.)
            if (owner.SlotOf(action.User) < 0)
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
                var defender = DefenderFor(action, opponent);

                // §375: said when the action comes up, whether or not the
                // claw changed anything - the games announce it either way.
                if (action.QuickClaw)
                    state.Log.Write($"{attacker.Species}'s Quick Claw let it move first!");

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

                // §319: the move is out, so it has been announced. Said
                // here rather than worked out by the knowledge layer's diff
                // because LastMoveName persists across turns - a Pokemon that
                // never got to act still carries the last name it used, and a
                // diff cannot tell that from a genuine repeat.
                state.Knowledge.NoteMoveUsed(state, attacker, move.Name);

                MoveResolver.Resolve(state, attacker, defender, move);

                // §304: the move did not go off - it missed, was blocked,
                // hit something it could not touch, or its user never got
                // to act. Any lock-in ends there, and it ends WITHOUT the
                // confusion, which is the price of finishing an Outrage
                // rather than of starting one.
                //
                // Here rather than inside the resolver because the resolver
                // has a dozen ways to give up and this needs to run after
                // every one of them; the resolver marks the failure in one
                // place and clears it in one place, and this reads the
                // answer.
                if (attacker.MoveFailedThisTurn)
                    Effects.LockIn.Break(state, attacker);

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

                // §304: this turn's answer becomes last turn's, which is
                // the one Stomping Tantrum and Temper Flare read. A
                // Pokemon that never acted counts as having failed -
                // being frozen solid or sent out over a faint is exactly
                // the situation those two moves are angry about.
                pokemon.MoveFailedLastTurn = pokemon.MoveFailedThisTurn;
                pokemon.MoveFailedThisTurn = false;

                // §304: Roost gives the Flying type back at the end of the
                // turn it was surrendered on.
                pokemon.RoostedThisTurn = false;
            }

            // §304: Plasma Fists' ion deluge is over before anything else
            // ticks - it lasts the turn it was used and no longer.
            state.IonDelugeTurns = 0;

            // §304: the wishes, which land on whoever is standing in the
            // slot now rather than on whoever made them.
            ResolveWishes();

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

            // §304: nobody has declared anything for next turn yet, and a
            // stale declaration would answer Upper Hand and Pursuit with
            // last turn's news.
            state.DeclaredP1 = null;
            state.DeclaredP2 = null;
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

                // §304: Psychic Noise's two turns.
                if (pokemon.HealBlockTurns > 0 && --pokemon.HealBlockTurns == 0)
                    state.Log.Write($"{pokemon.Species} can heal again!");

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

        /// <summary>§304. Wish: made on one turn, landing at the end of
        /// the next, on whoever is standing in the slot by then. The amount
        /// was fixed when the wish was made - half the WISHER's maximum HP
        /// - so a Blissey's wish is worth having whoever collects it.</summary>
        void ResolveWishes()
        {
            foreach (var side in new[] { state.Player1, state.Player2 })
            {
                if (state.WishTurns(side) <= 0)
                    continue;

                if (--state.WishTurns(side) > 0)
                    continue;

                int heal = state.WishHeal(side);

                state.WishHeal(side) = 0;

                var pokemon = side.ActivePokemon;

                if (pokemon.Fainted || pokemon.CurrentHP >= pokemon.MaxHP)
                    continue;

                // §304: a heal-blocked Pokemon collects nothing, and the
                // wish is spent either way.
                if (pokemon.HealBlockTurns > 0)
                {
                    state.Log.Write($"{pokemon.Species} cannot heal!");
                    continue;
                }

                pokemon.CurrentHP = Math.Min(pokemon.MaxHP, pokemon.CurrentHP + heal);

                state.Log.Write($"{pokemon.Species}'s wish came true!");
            }
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