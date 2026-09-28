using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Models;
using PokemonSim.Observation;

namespace PokemonSim.Engine.Strategies
{
    /// <summary>
    /// §171. The deep-learning player. Sections 156 to 170 kept the trained
    /// network strictly observer-only - it scored decisions after the fact
    /// and never chose anything. This strategy is the explicit, deliberately
    /// scoped exception: it lets the network actually PLAY, and the admin
    /// console's Battle Lab is the only place that builds one. The
    /// Simulator's own battles are untouched - a boss is still the Monte
    /// Carlo brain, a random opponent is still the baseline - so nothing a
    /// user sees changes because this class exists.
    ///
    /// It reaches the model through the same IShadowEvaluator seam the
    /// observer uses, so the engine still never references the onnx
    /// runtime, and it reads its state through the same encoder the
    /// observer records with - which means the network plays on exactly
    /// the features it was trained on. Section 175 widened that encoder,
    /// so the legal actions now go in too: slot legality is one of the
    /// features, and passing them is what makes the state the network sees
    /// identical to the one written into the training corpus.
    ///
    /// Section 176 gave the model a switch head, so it can now leave a bad
    /// matchup and can pick who comes in after a faint - which is most of
    /// what the Monte Carlo brain was doing that this player could not.
    /// A model from before that still works and simply never proposes a
    /// switch: its four outputs are still the first four, and
    /// Status.CanChooseSwitches says which kind is loaded.
    ///
    /// Anything the model cannot express falls through to the preserved
    /// baseline behaviour: a turn with nothing legal to score (Struggle
    /// with no bench), a state the evaluator declines to score, an
    /// unavailable model, or a replacement a move-only model cannot rank.
    /// Those fallbacks draw from the battle's own seeded rng, so a lab
    /// battle stays reproducible.
    /// </summary>
    public sealed class NeuralStrategy : IBattleStrategy
    {
        readonly IShadowEvaluator evaluator;
        readonly RandomMoveStrategy fallback = new();

        /// <summary>§185. The same book section 183 gave the Monte Carlo
        /// brain, or null. It matters here for the first time: until this
        /// section the network only ever played in the Battle Lab, where a
        /// book is deliberately never built, so a model opponent that
        /// repeated itself forever was nobody's problem. It is somebody's
        /// problem the moment it plays a person.</summary>
        readonly BattleRecall? recall;

        public NeuralStrategy(IShadowEvaluator evaluator, BattleRecall? recall = null)
        {
            this.evaluator = evaluator;
            this.recall = recall;
        }

        /// <summary>
        /// Section 182. Which teacher this player should behave like, from
        /// 0 (the expected-value brain) to 1 (the gambler). It is written
        /// into the last feature of every state the model is shown, so a
        /// model trained across the range answers as the teacher at that
        /// setting would have - one file, a difficulty dial rather than a
        /// personality baked in at training time.
        ///
        /// A model trained before section 182 has 202 inputs and never
        /// sees this feature at all, so setting it simply does nothing.
        /// </summary>
        public float RiskAppetite { get; set; } = ObservationSchema.NeutralRisk;

        /// <summary>
        /// §322. Which observation this player reads the battle through -
        /// the same switch, with the same default, that §319 gave
        /// BattleObserver.
        ///
        /// False is the fair V8 vector: the opponent only as far as it has
        /// shown itself. True is §311's omniscient V7, kept so the cost of
        /// honesty can be measured by playing one against the other rather
        /// than argued about.
        ///
        /// WHY THIS EXISTS. §319 moved the observer to FairEncoder and left
        /// this class - the one that actually PLAYS - still encoding with
        /// ObserverEncoder. The two had been the same call for so long that
        /// changing one and not the other looked like one change. It was
        /// not, and the way it failed is the part worth remembering: a fair
        /// model asks for 1206 features, the omniscient vector is 483, so
        /// OnnxShadowEvaluator.Predict took its `state.Length < inputWidth`
        /// exit and returned null - which this class treats as "no opinion"
        /// and answers by falling through to the baseline. Every decision,
        /// silently, with a model that had loaded perfectly and a lab report
        /// that said the run was a deep-learning run. See Incompatibility
        /// for the refusal that now stands in the way of that.
        /// </summary>
        public bool Omniscient { get; init; }

        /// <summary>§322. How wide the vector this player writes is. The
        /// model must want exactly this many.</summary>
        public int EncoderWidth =>
            Omniscient ? ObserverEncoder.TotalFeatures : FairEncoder.FeatureCount;

        /// <summary>§322. One encode, one place to be wrong.</summary>
        float[] Observe(
            BattleState state, PlayerState self, IReadOnlyList<BattleAction>? legalActions) =>
            Omniscient
                ? ObserverEncoder.Encode(state, self, legalActions, RiskAppetite)
                : FairEncoder.Encode(state, self, legalActions, RiskAppetite);

        /// <summary>
        /// §322. Why this model cannot play on this observation, in words
        /// for the person who chose it - or null when it can.
        ///
        /// A caller that puts the network on the field is expected to ask
        /// this FIRST and refuse the run, because the alternative is not an
        /// error: it is a full run of baseline play reported as the model's.
        /// A silent fallback is right for one turn the model cannot score
        /// and catastrophic for every turn, and nothing in the fallback
        /// itself can tell those two apart - only the width can, and only
        /// before the run starts.
        /// </summary>
        public static string? Incompatibility(IShadowEvaluator? evaluator, bool omniscient = false)
        {
            if (evaluator == null)
                return "no evaluator was supplied";

            if (!evaluator.Status.Available)
                return evaluator.Status.Description;

            int wanted = omniscient ? ObserverEncoder.TotalFeatures : FairEncoder.FeatureCount;

            if (evaluator.Status.InputWidth != wanted)
            {
                return $"the model reads {evaluator.Status.InputWidth} features but the " +
                       (omniscient ? "omniscient" : "fair") +
                       $" observation it would be shown is {wanted} wide, so it could not " +
                       "score a single turn. Train a model on the current observation, or " +
                       "install one that matches.";
            }

            return null;
        }

        int decisions;
        int modelChoices;
        int switchChoices;

        public string Name => "Deep learning";

        /// <summary>How often the network actually decided, rather than
        /// falling through to the baseline - the honest measure of how
        /// much of a run it really played.</summary>
        public int DecisionCount => decisions;
        public int ModelChoiceCount => modelChoices;

        /// <summary>Section 176. How many of those choices were switches -
        /// zero for a move-only model, and the honest measure of whether
        /// the switch head is doing anything.</summary>
        public int SwitchChoiceCount => switchChoices;

        public BattleAction ChooseAction(BattleState state, PlayerState self, IReadOnlyList<BattleAction> legalActions)
        {
            System.Threading.Interlocked.Increment(ref decisions);

            // §176: the mask covers moves AND the team positions this side
            // may switch to. A move-only model reads its first four.
            float[] mask = ObserverEncoder.LegalActionMask(legalActions, self);

            // Nothing this model could score (a Struggle turn with no
            // bench to switch to).
            if (mask.All(slot => slot <= 0))
                return fallback.ChooseAction(state, self, legalActions);

            ShadowPrediction? prediction = evaluator.Predict(
                Observe(state, self, legalActions), mask);

            if (prediction == null)
                return fallback.ChooseAction(state, self, legalActions);

            BattleAction? chosen = Match(legalActions, self, Prefer(
                BattleMemory.Situation(self.ActivePokemon, state.GetOpponentOf(self).ActivePokemon),
                prediction, mask));

            if (chosen == null)
                return fallback.ChooseAction(state, self, legalActions);

            System.Threading.Interlocked.Increment(ref modelChoices);

            recall?.Note(
                BattleMemory.Situation(self.ActivePokemon, state.GetOpponentOf(self).ActivePokemon),
                ObserverEncoder.ActionIndex(self, chosen));

            if (chosen.Type == BattleActionType.Switch)
            {
                System.Threading.Interlocked.Increment(ref switchChoices);
                return chosen;      // no mega on the turn you switch out
            }

            // §161: same rule every computer player follows - mega evolve
            // the first turn it legally can, with no rng draw.
            if (MegaEvolutions.CanMegaEvolve(state, self, self.ActivePokemon))
                chosen.MegaEvolve = true;

            return chosen;
        }

        /// <summary>
        /// §185. Which output the network prefers once its book has had a
        /// word - the model's own pick when there is no book, or nothing
        /// in it about this matchup.
        ///
        /// The nudge has to be applied to COMPARABLE numbers. Section 183
        /// sized it against Monte Carlo's rollout means, which run from 0
        /// to about 1.1 and are win probabilities; a network's raw outputs
        /// are logits on whatever scale training left them, so the same
        /// 0.14 could be overwhelming or invisible. Softmax first and the
        /// two are the same currency again: a share of the decision.
        /// </summary>
        int Prefer(string situation, ShadowPrediction prediction, float[] mask)
        {
            if (recall == null || prediction.Scores.Length == 0)
                return prediction.PreferredActionIndex;

            int width = Math.Min(prediction.Scores.Length, mask.Length);

            int legal = 0;

            for (int i = 0; i < width; i++)
            {
                if (mask[i] > 0)
                    legal++;
            }

            if (legal <= 1)
                return prediction.PreferredActionIndex;

            double[] share = Softmax(prediction.Scores, mask, width);

            int best = -1;
            double bestScore = double.NegativeInfinity;

            for (int i = 0; i < width; i++)
            {
                if (mask[i] <= 0)
                    continue;

                double score = share[i] + recall.Adjust(situation, i, legal);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            return best < 0 ? prediction.PreferredActionIndex : best;
        }

        /// <summary>§185. The model's outputs as shares of one, over the
        /// legal actions only - an illegal action is not competing for any
        /// of it. Shifted by the maximum first, which is the standard way
        /// to keep the exponentials from overflowing.</summary>
        static double[] Softmax(float[] scores, float[] mask, int width)
        {
            var share = new double[width];

            double max = double.NegativeInfinity;

            for (int i = 0; i < width; i++)
            {
                if (mask[i] > 0 && scores[i] > max)
                    max = scores[i];
            }

            if (!double.IsFinite(max))
                return share;

            double total = 0;

            for (int i = 0; i < width; i++)
            {
                if (mask[i] <= 0)
                    continue;

                double value = Math.Exp(scores[i] - max);

                share[i] = value;
                total += value;
            }

            if (total <= 0)
                return new double[width];

            for (int i = 0; i < width; i++)
                share[i] /= total;

            return share;
        }

        /// <summary>§176. The legal action an output index names: a move
        /// slot below MoveSlots, otherwise the switch that sends in that
        /// team position. Null when nothing legal matches, which is the
        /// caller's cue to fall back.</summary>
        static BattleAction? Match(
            IReadOnlyList<BattleAction> legalActions, PlayerState self, int actionIndex)
        {
            if (actionIndex < 0)
                return null;

            if (actionIndex < ObservationSchema.MoveSlots)
            {
                return legalActions.FirstOrDefault(a =>
                    a.Type == BattleActionType.Move &&
                    a.Move != null &&
                    a.Move.Index == actionIndex);
            }

            int teamIndex = actionIndex - ObservationSchema.MoveSlots;

            if (teamIndex < 0 || teamIndex >= self.Team.Count)
                return null;

            PokemonState wanted = self.Team[teamIndex];

            return legalActions.FirstOrDefault(a =>
                a.Type == BattleActionType.Switch &&
                ReferenceEquals(a.SwitchTarget, wanted));
        }

        /// <summary>§176. After a faint the switch head picks who comes in,
        /// scoring only the team positions the engine offered. A model
        /// without one falls through to the preserved baseline pick.</summary>
        public int ChooseReplacement(BattleState state, PlayerState self, IReadOnlyList<int> legalTeamIndexes)
        {
            if (!evaluator.Status.CanChooseSwitches || legalTeamIndexes.Count == 0)
                return fallback.ChooseReplacement(state, self, legalTeamIndexes);

            float[] replacements = ObserverEncoder.ReplacementMask(legalTeamIndexes);

            ShadowPrediction? prediction = evaluator.Predict(
                Observe(state, self, null), replacements);

            string situation = BattleMemory.ReplacementSituation(
                state.GetOpponentOf(self).ActivePokemon);

            int preferred = prediction == null ? -1 : Prefer(situation, prediction, replacements);

            int teamIndex = preferred >= ObservationSchema.MoveSlots
                ? preferred - ObservationSchema.MoveSlots
                : -1;

            if (teamIndex < 0 || !legalTeamIndexes.Contains(teamIndex))
                return fallback.ChooseReplacement(state, self, legalTeamIndexes);

            System.Threading.Interlocked.Increment(ref switchChoices);

            recall?.Note(situation, ObservationSchema.MoveSlots + teamIndex);

            return teamIndex;
        }
    }
}