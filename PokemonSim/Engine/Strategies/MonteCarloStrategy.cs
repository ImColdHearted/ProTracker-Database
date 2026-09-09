using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using PokemonSim.Actions;
using PokemonSim.Models;

namespace PokemonSim.Engine.Strategies
{
    /// <summary>Tuning for MonteCarloStrategy. One shared configuration is
    /// safe to hand to many strategies - it is read-only after construction
    /// in practice (nothing mutates it mid-battle), and each strategy keeps
    /// its own rng and per-battle state.</summary>
    public sealed class MonteCarloConfig
    {
        /// <summary>Rollouts per candidate action. The original engine
        /// called MonteCarloAI.ChooseMove(state, 20); the default stays in
        /// that spirit, big enough to rank moves and small enough that a
        /// decision costs a fraction of a second and never fights the
        /// tracker's live capture loop for CPU.</summary>
        public int SimulationsPerAction { get; init; } = 24;

        /// <summary>How many turns past the current one a rollout may run
        /// before it is scored as it stands (rollout termination even when
        /// neither random side can close a battle).</summary>
        public int MaxRolloutTurns { get; init; } = 40;

        /// <summary>Wall-clock budget for one decision. When it runs out,
        /// the best fully-evaluated action so far is used.</summary>
        public int MaxDecisionMilliseconds { get; init; } = 1500;

        /// <summary>Subtracted from a voluntary switch's score so that
        /// switching must actually look better than staying in, which is
        /// what prevents two cautious sides from switching forever.</summary>
        public double SwitchPenalty { get; init; } = 0.08;

        /// <summary>Extra penalty when the previous chosen action was
        /// already a switch - the overswitching brake.</summary>
        public double ConsecutiveSwitchPenalty { get; init; } = 0.25;

        /// <summary>
        /// Section 182. How much this brain prefers upside to expectation,
        /// from 0 (rank by the plain mean, which is every version of this
        /// strategy before section 182) to 1 (rank purely by how good an
        /// action is when it goes well).
        ///
        /// The rollouts already contain this information and it was being
        /// thrown away. A move that wins outright three times in ten and
        /// loses the rest has a mean of 0.3; a move that grinds to a
        /// capped 0.55 every time has a mean of 0.55. The mean says take
        /// the grind. But the first move's good outcomes are WINS and the
        /// second's are never better than a draw, and a player who is
        /// behind, or who simply wants the battle to be interesting,
        /// should take the gamble knowingly.
        ///
        /// At 0 the arithmetic short-circuits to exactly the old mean, so
        /// a default-configured brain plays the same battles it always
        /// did, roll for roll.
        /// </summary>
        public double RiskAppetite { get; init; } = 0;

        /// <summary>Section 182. Which slice of the rollouts counts as
        /// "when it goes well" - the top quarter by default. A single
        /// quantile would be too blunt here: rollout scores pile up near 0
        /// and 1, so a percentile jumps between them and ties half the
        /// candidates together. The MEAN of the best slice stays smooth
        /// and keeps ranking them.</summary>
        public double UpsideFraction { get; init; } = 0.25;
    }

    /// <summary>
    /// Section 155. The completed Monte Carlo opponent. The original engine
    /// hard-wired "MonteCarloAI.ChooseMove(state, 20)" into its turn loop,
    /// but the class itself did not survive the copy into the repo; this is
    /// that missing piece rebuilt on the section-154 machinery it always
    /// implied: BattleState.Clone for a private sandbox, LegalActions for
    /// candidate enumeration, seeded BattleRng for reproducible rollouts.
    ///
    /// For every legal action it plays that action on a clone against a
    /// random opponent, finishes the clone battle with both sides random up
    /// to a turn cap, and scores the outcome (win 1, draw 0.5, loss 0, plus
    /// a small HP-difference term so even capped rollouts rank). The real
    /// battle is never touched: every rollout runs on a clone whose rng is
    /// replaced from this strategy's own seeded stream, so the visible
    /// battle's rng position, log and state stay byte-for-byte identical
    /// while the strategy thinks - a test fingerprints exactly that.
    ///
    /// Determinism: same construction seed + same battle = same choices.
    /// Rollouts run sequentially on the calling thread (the Simulator calls
    /// strategies from a worker thread already), so no parallel rng
    /// hazards exist inside a decision; separate battles use separate
    /// strategy instances and share nothing mutable. Cancellation is
    /// honored between rollouts through <see cref="CancellationToken"/>,
    /// falling back to the first legal action. Any unexpected evaluation
    /// failure falls back the same way instead of crashing the battle.
    /// </summary>
    public sealed class MonteCarloStrategy : IBattleStrategy, ITeacherPolicy
    {
        readonly MonteCarloConfig config;
        readonly BattleRng rng;

        // Section 175: the last decision's per-slot rollout means, for the
        // observer to record as a soft training target. One battle per
        // strategy instance and one decision at a time within it, so a
        // plain field is safe; it is cleared at the top of every decision
        // so a stale vector can never be attributed to the wrong turn.
        float[]? lastMoveScores;
        int lastTeacherAction = -1;

        // Per-battle memory (one strategy instance = one battle): the
        // overswitching brake needs to know what it chose last turn.
        bool lastChoiceWasSwitch;

        public string Name => "Monte Carlo";

        /// <summary>Section 175, widened by section 176. The mean rollout
        /// score this strategy gave each ACTION on the decision just made -
        /// the four move slots, then the six team positions - which is the
        /// ranking behind the choice, not merely the choice. NaN marks an
        /// action that was never evaluated (not offered this turn, or the
        /// decision budget ran out first). Null when the turn involved no
        /// ranking at all.
        ///
        /// A switch entry is the score AFTER the overswitching brakes,
        /// because those brakes are part of what makes this strategy worth
        /// copying: a student fitted to the raw rollout means would switch
        /// far more often than the teacher ever does.</summary>
        public IReadOnlyList<float>? LastMoveScores => lastMoveScores;

        /// <summary>Section 182. The action space index this brain settled
        /// on for the decision just ranked, or -1. Under DAgger the brain
        /// advises a battle it is not playing, so what it WOULD have done
        /// is the label and cannot be read off the action that was taken.
        /// </summary>
        public int LastTeacherAction => lastTeacherAction;

        /// <summary>Section 182. The risk appetite this brain ranks by, so
        /// a recorder can write it into the state it is labelling.</summary>
        public float RiskAppetite => (float)Math.Clamp(config.RiskAppetite, 0.0, 1.0);

        /// <summary>Set by the battle owner (SimulatorSession's creator)
        /// so a cancelled battle also stops mid-decision rollouts.</summary>
        public CancellationToken CancellationToken { get; set; }

        /// <summary>Section 183. What this brain remembers from earlier
        /// battles, or null for one that starts every battle from nothing -
        /// which is what the Battle Lab always builds, deliberately. A
        /// teacher whose choices depend on a book the student cannot see
        /// would be labelling states with an unlearnable answer.</summary>
        readonly BattleRecall? recall;

        public MonteCarloStrategy(MonteCarloConfig? config = null, int seed = 987654321,
                                  BattleRecall? recall = null)
        {
            this.config = config ?? new MonteCarloConfig();
            rng = new BattleRng(seed);
            this.recall = recall;
        }

        public BattleAction ChooseAction(BattleState state, PlayerState self, IReadOnlyList<BattleAction> legalActions)
        {
            BattleAction chosen = Advise(state, self, legalActions);

            lastChoiceWasSwitch = chosen.Type == BattleActionType.Switch;

            // Section 183: only a brain that PLAYED writes to the book.
            // Advise is what a teacher calls, and a teacher's opinion about
            // a battle it is not playing is not a battle it played.
            recall?.Note(
                BattleMemory.Situation(self.ActivePokemon,
                                       state.GetOpponentOf(self).ActivePokemon),
                Observation.ObserverEncoder.ActionIndex(self, chosen));

            return WithMega(state, self, chosen);
        }

        /// <summary>
        /// Section 182. Everything ChooseAction does EXCEPT committing to
        /// it: the rollouts, the ranking, and which action came out on top,
        /// with no mega evolution applied and no memory of having chosen.
        ///
        /// That separation is what makes a teacher possible. WithMega sets
        /// MegaEvolve on the BattleAction object it is handed, and those
        /// objects come from the engine's own legal-action list - the same
        /// list the student is about to pick from. A teacher that called
        /// ChooseAction merely to read its opinion would therefore reach
        /// into the battle and flip mega evolution on a move the student
        /// might play, which is not an opinion, it is a move. Advise
        /// touches nothing: the rollouts run on state.Clone() with the
        /// brain's own rng, so a battle advised is byte-for-byte a battle
        /// unadvised.
        ///
        /// lastChoiceWasSwitch is left alone for the same reason - the
        /// brain did not choose anything here. A caller running this as a
        /// teacher should call NoteChoice with what actually happened, so
        /// the overswitching brake stays anchored to the real battle.
        /// </summary>
        public BattleAction Advise(BattleState state, PlayerState self, IReadOnlyList<BattleAction> legalActions)
        {
            // Section 175: a fresh decision, so no scores until this one
            // produces some. Every early return below therefore reports
            // "did not rank" rather than the previous turn's numbers.
            lastMoveScores = null;
            lastTeacherAction = -1;

            if (legalActions.Count == 0)
                throw new InvalidOperationException("ChooseAction called with no legal actions.");

            BattleAction chosen;

            if (legalActions.Count == 1)
            {
                chosen = legalActions[0];
            }
            else
            {
                try
                {
                    chosen = Evaluate(state, self, legalActions);
                }
                catch
                {
                    // Fail safely with a legal action: prefer a move, else
                    // the first thing offered (a forced switch, typically).
                    chosen = legalActions[0];

                    foreach (var action in legalActions)
                    {
                        if (action.Type == BattleActionType.Move)
                        {
                            chosen = action;
                            break;
                        }
                    }
                }
            }

            lastTeacherAction = Observation.ObserverEncoder.ActionIndex(self, chosen);

            return chosen;
        }

        /// <summary>Section 182. Tell a brain that is advising rather than
        /// playing what the side it advises actually did, so its
        /// overswitching brake tracks the real battle instead of a run of
        /// hypotheticals it was never allowed to act on.</summary>
        public void NoteChoice(BattleAction played) =>
            lastChoiceWasSwitch = played.Type == BattleActionType.Switch;

        /// <summary>Section 161: the boss brain mega evolves the first
        /// turn it legally can, whatever move it picked. No rng draw, so
        /// decision fingerprints on stoneless teams are untouched.</summary>
        static BattleAction WithMega(BattleState state, PlayerState self, BattleAction chosen)
        {
            if (chosen.Type == BattleActionType.Move &&
                MegaEvolutions.CanMegaEvolve(state, self, self.ActivePokemon))
            {
                chosen.MegaEvolve = true;
            }

            return chosen;
        }

        public int ChooseReplacement(BattleState state, PlayerState self, IReadOnlyList<int> legalTeamIndexes)
        {
            // Section 180: a replacement is ranked like any other decision
            // now, so the same contract applies - cleared first, published
            // only if something was actually scored.
            lastMoveScores = null;
            lastTeacherAction = -1;

            if (legalTeamIndexes.Count == 0)
                throw new InvalidOperationException("ChooseReplacement called with no candidates.");

            if (legalTeamIndexes.Count == 1)
            {
                // Section 182: no judgement was exercised, but a reader
                // still wants to know which slot this was.
                lastTeacherAction = TeacherSlot(legalTeamIndexes[0]);
                return legalTeamIndexes[0];
            }

            try
            {
                var stopwatch = Stopwatch.StartNew();

                // Fewer rollouts than a move decision - replacements are
                // rarer and the candidate space is small.
                int perCandidate = Math.Max(4, config.SimulationsPerAction / 2);

                double bestScore = double.NegativeInfinity;
                int best = legalTeamIndexes[0];

                // Section 180: the same NaN-until-scored vector the action
                // path uses, over the switch half of the action space.
                var slotScores = new float[Observation.ObservationSchema.ActionSlots];
                bool anySlotScored = false;

                for (int i = 0; i < slotScores.Length; i++)
                    slotScores[i] = float.NaN;

                var samples = new double[Math.Max(1, perCandidate)];

                foreach (int index in legalTeamIndexes)
                {
                    if (OutOfBudget(stopwatch))
                        break;

                    double total = 0;
                    int runs = 0;

                    for (int i = 0; i < perCandidate; i++)
                    {
                        if (OutOfBudget(stopwatch))
                            break;

                        var clone = state.Clone();
                        clone.Rng = new BattleRng(rng.Next(int.MaxValue));

                        PlayerState cloneSelf = SameSide(state, clone, self);
                        var replacement = cloneSelf.Team[index];

                        if (replacement.Fainted)
                            break;

                        new BattleEngine(clone).Replace(cloneSelf, replacement);

                        double sample = Rollout(clone, cloneSelf, firstAction: null);

                        samples[runs] = sample;
                        total += sample;
                        runs++;
                    }

                    if (runs == 0)
                        continue;

                    // Section 182: a gambler gambles about who comes in too.
                    double score = RiskAdjusted(samples, runs, total);

                    // Section 183: "who do I send in against this" is its
                    // own question with its own book - keyed on what is
                    // standing opposite, since what just fainted is not
                    // what the decision is about.
                    if (recall != null)
                    {
                        score += recall.Adjust(
                            BattleMemory.ReplacementSituation(state.GetOpponentOf(self).ActivePokemon),
                            TeacherSlot(index),
                            legalTeamIndexes.Count);
                    }

                    // Section 180: no switch penalty applies here - the
                    // side has no choice about switching, only about who.
                    if (index >= 0 && index < Observation.ObservationSchema.TeamSlots)
                    {
                        slotScores[Observation.ObservationSchema.MoveSlots + index] = (float)score;
                        anySlotScored = true;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = index;
                    }
                }

                if (anySlotScored)
                    lastMoveScores = slotScores;

                lastTeacherAction = TeacherSlot(best);

                recall?.Note(
                    BattleMemory.ReplacementSituation(state.GetOpponentOf(self).ActivePokemon),
                    TeacherSlot(best));

                return best;
            }
            catch
            {
                lastTeacherAction = TeacherSlot(legalTeamIndexes[0]);
                return legalTeamIndexes[0];
            }
        }

        // ---- the evaluation core ----

        BattleAction Evaluate(BattleState state, PlayerState self, IReadOnlyList<BattleAction> legalActions)
        {
            var stopwatch = Stopwatch.StartNew();

            double bestScore = double.NegativeInfinity;
            BattleAction? best = null;

            // Section 175: NaN until an action is actually scored, so an
            // unevaluated one is never read as a rollout mean of zero.
            // Section 176 widened this to the whole action space.
            var slotScores = new float[Observation.ObservationSchema.ActionSlots];
            bool anySlotScored = false;

            for (int i = 0; i < slotScores.Length; i++)
                slotScores[i] = float.NaN;

            // Section 182: the individual rollout scores are kept now,
            // not just their running total. The mean throws away exactly
            // the thing a risk appetite needs - whether an action's good
            // outcomes are wins or merely survivals.
            var samples = new double[Math.Max(1, config.SimulationsPerAction)];

            foreach (var action in legalActions)
            {
                double total = 0;
                int runs = 0;

                for (int i = 0; i < config.SimulationsPerAction; i++)
                {
                    if (OutOfBudget(stopwatch))
                        break;

                    var clone = state.Clone();
                    clone.Rng = new BattleRng(rng.Next(int.MaxValue));

                    PlayerState cloneSelf = SameSide(state, clone, self);
                    BattleAction? cloneAction = MatchAction(state, clone, self, cloneSelf, action);

                    if (cloneAction == null)
                        break;

                    double sample = Rollout(clone, cloneSelf, cloneAction);

                    samples[runs] = sample;
                    total += sample;
                    runs++;
                }

                if (runs == 0)
                    continue;   // budget gone before this action got a look

                double score = RiskAdjusted(samples, runs, total);

                // Section 175: a move slot's worth is what the rollouts
                // said it was, and no penalty applies to it.
                if (action.Type == BattleActionType.Move &&
                    action.Move != null &&
                    action.Move.Index >= 0 &&
                    action.Move.Index < Observation.ObservationSchema.MoveSlots)
                {
                    slotScores[action.Move.Index] = (float)score;
                    anySlotScored = true;
                }

                if (action.Type == BattleActionType.Switch)
                {
                    score -= config.SwitchPenalty;

                    if (lastChoiceWasSwitch)
                        score -= config.ConsecutiveSwitchPenalty;

                    // Section 176: recorded AFTER the brakes, which is the
                    // number this strategy actually ranks by - see
                    // LastMoveScores for why the student wants that one.
                    if (action.SwitchTarget != null)
                    {
                        int index = self.Team.IndexOf(action.SwitchTarget);

                        if (index >= 0 && index < Observation.ObservationSchema.TeamSlots)
                        {
                            slotScores[Observation.ObservationSchema.MoveSlots + index] = (float)score;
                            anySlotScored = true;
                        }
                    }
                }

                // Section 183: last, on top of everything else, because
                // it is a nudge between options this brain already rates
                // as close - never a reason to play something it rates as
                // bad. Scores here run about 0 to 1.1, and the two weights
                // together cannot move one by more than about 0.14.
                if (recall != null)
                {
                    score += recall.Adjust(
                        BattleMemory.Situation(self.ActivePokemon,
                                               state.GetOpponentOf(self).ActivePokemon),
                        Observation.ObserverEncoder.ActionIndex(self, action),
                        legalActions.Count);
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = action;
                }
            }

            if (anySlotScored)
                lastMoveScores = slotScores;

            if (best != null)
                return best;

            // Budget or cancellation before anything was evaluated.
            foreach (var action in legalActions)
            {
                if (action.Type == BattleActionType.Move)
                    return action;
            }

            return legalActions[0];
        }

        /// <summary>Section 182. A team position as the action space
        /// numbers it.</summary>
        static int TeacherSlot(int teamIndex) =>
            teamIndex >= 0 && teamIndex < Observation.ObservationSchema.TeamSlots
                ? Observation.ObservationSchema.MoveSlots + teamIndex
                : -1;

        /// <summary>
        /// Section 182. What a candidate is worth, at this brain's risk
        /// appetite: the plain rollout mean at 0, the mean of its best
        /// outcomes at 1, a blend in between.
        ///
        /// The zero case returns the mean by the same arithmetic it always
        /// used and allocates nothing, so a default brain is not merely
        /// close to its pre-182 self - it is identical, roll for roll.
        /// </summary>
        internal double RiskAdjusted(double[] samples, int runs, double total)
        {
            double mean = total / runs;
            double appetite = Math.Clamp(config.RiskAppetite, 0.0, 1.0);

            if (appetite <= 0 || runs <= 1)
                return mean;

            return (1 - appetite) * mean + appetite * UpperTailMean(samples, runs);
        }

        /// <summary>Section 182. The mean of the best slice of the
        /// rollouts - "how good is this when it goes well". A percentile
        /// would be the obvious choice and is the wrong one: a rollout
        /// scores near 1 for a win, near 0 for a loss and near 0.5 for a
        /// battle the turn cap cut short, so scores pile up in three
        /// places and a percentile snaps between them, tying candidates
        /// that are not tied. Averaging the top slice stays smooth.
        /// </summary>
        internal double UpperTailMean(double[] samples, int runs)
        {
            int take = (int)Math.Ceiling(Math.Clamp(config.UpsideFraction, 0.0, 1.0) * runs);

            take = Math.Clamp(take, 1, runs);

            var sorted = new double[runs];

            Array.Copy(samples, sorted, runs);
            Array.Sort(sorted);

            double sum = 0;

            for (int i = runs - take; i < runs; i++)
                sum += sorted[i];

            return sum / take;
        }

        bool OutOfBudget(Stopwatch stopwatch) =>
            CancellationToken.IsCancellationRequested ||
            stopwatch.ElapsedMilliseconds >= config.MaxDecisionMilliseconds;

        /// <summary>Plays the clone battle to an outcome (or the rollout
        /// turn cap) with both sides random, optionally scripting this
        /// side's first action, and scores it for cloneSelf.</summary>
        double Rollout(BattleState clone, PlayerState cloneSelf, BattleAction? firstAction)
        {
            var engine = new BattleEngine(clone);
            var random = new RandomMoveStrategy();

            int cap = Math.Min(clone.MaxTurns, clone.TurnNumber + config.MaxRolloutTurns);

            while (clone.Outcome == BattleOutcome.Unfinished && clone.TurnNumber < cap)
            {
                PlayerState opponent = clone.GetOpponentOf(cloneSelf);

                var selfLegal = engine.GetLegalActions(cloneSelf);
                var otherLegal = engine.GetLegalActions(opponent);

                if (selfLegal.Count == 0 || otherLegal.Count == 0)
                    break;

                BattleAction selfAction = firstAction ?? random.ChooseAction(clone, cloneSelf, selfLegal);
                firstAction = null;

                BattleAction otherAction = random.ChooseAction(clone, opponent, otherLegal);

                engine.RunTurn(
                    ReferenceEquals(cloneSelf, clone.Player1) ? selfAction : otherAction,
                    ReferenceEquals(cloneSelf, clone.Player1) ? otherAction : selfAction);

                ReplaceAllFainted(engine, clone, random);
            }

            return Score(clone, cloneSelf);
        }

        static void ReplaceAllFainted(BattleEngine engine, BattleState clone, RandomMoveStrategy random)
        {
            foreach (var player in new[] { clone.Player1, clone.Player2 })
            {
                while (engine.NeedsReplacement(player))
                {
                    var candidates = new List<int>();

                    for (int i = 0; i < player.Team.Count; i++)
                    {
                        if (!player.Team[i].Fainted)
                            candidates.Add(i);
                    }

                    if (candidates.Count == 0)
                        break;

                    int pick = random.ChooseReplacement(clone, player, candidates);

                    if (!candidates.Contains(pick))
                        pick = candidates[0];

                    engine.Replace(player, player.Team[pick]);
                }
            }
        }

        double Score(BattleState clone, PlayerState cloneSelf)
        {
            PlayerState opponent = clone.GetOpponentOf(cloneSelf);

            bool selfWon = clone.Outcome == BattleOutcome.Player1Wins
                ? ReferenceEquals(cloneSelf, clone.Player1)
                : clone.Outcome == BattleOutcome.Player2Wins && ReferenceEquals(cloneSelf, clone.Player2);

            double outcomeScore =
                clone.Outcome == BattleOutcome.Player1Wins || clone.Outcome == BattleOutcome.Player2Wins
                    ? (selfWon ? 1.0 : 0.0)
                    : 0.5;   // draw, cancelled, or rollout cap

            // Small HP-difference term so rollouts the cap cut short still
            // rank: +-0.1 at most, never enough to outweigh a real win.
            double hpDelta = TeamHpFraction(cloneSelf) - TeamHpFraction(opponent);

            return outcomeScore + 0.1 * hpDelta;
        }

        static double TeamHpFraction(PlayerState player)
        {
            double total = 0;
            int count = 0;

            foreach (var pokemon in player.Team)
            {
                total += pokemon.MaxHP <= 0 ? 0 : Math.Max(0, pokemon.CurrentHP) / (double)pokemon.MaxHP;
                count++;
            }

            return count == 0 ? 0 : total / count;
        }

        // ---- mapping the real battle onto a clone ----

        static PlayerState SameSide(BattleState real, BattleState clone, PlayerState self) =>
            ReferenceEquals(self, real.Player1) ? clone.Player1 : clone.Player2;

        /// <summary>The legal actions reference the REAL battle's objects;
        /// find the clone's equivalent by kind + move slot (or name, for
        /// the synthesized Struggle) + switch target's team index.</summary>
        static BattleAction? MatchAction(
            BattleState real, BattleState clone,
            PlayerState self, PlayerState cloneSelf,
            BattleAction action)
        {
            var cloneLegal = LegalActions.For(clone, cloneSelf);

            foreach (var candidate in cloneLegal)
            {
                if (candidate.Type != action.Type)
                    continue;

                if (action.Type == BattleActionType.Move)
                {
                    if (action.Move == null || candidate.Move == null)
                        continue;

                    if (candidate.Move.Name == action.Move.Name &&
                        candidate.Move.Index == action.Move.Index)
                    {
                        // Section 161: the scripted rollout action keeps
                        // its mega/Z intent.
                        candidate.MegaEvolve = action.MegaEvolve;
                        candidate.UseZMove = action.UseZMove;
                        return candidate;
                    }
                }
                else if (action.Type == BattleActionType.Switch)
                {
                    if (action.SwitchTarget == null || candidate.SwitchTarget == null)
                        continue;

                    if (self.Team.IndexOf(action.SwitchTarget) ==
                        cloneSelf.Team.IndexOf(candidate.SwitchTarget))
                        return candidate;
                }
            }

            return null;
        }
    }
}