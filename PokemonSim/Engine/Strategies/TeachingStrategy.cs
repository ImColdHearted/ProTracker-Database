using System;
using System.Collections.Generic;
using PokemonSim.Actions;
using PokemonSim.Models;

namespace PokemonSim.Engine.Strategies
{
    /// <summary>
    /// Section 182. The DAgger wrapper: one side of a battle where the
    /// STUDENT drives and the TEACHER says, at every decision, what it
    /// would have done instead.
    ///
    /// This exists because plain imitation learns the wrong distribution.
    /// A corpus collected while the Monte Carlo brain plays contains the
    /// positions Monte Carlo steers into. The model is then asked to play,
    /// reaches a slightly different position on turn three, and from there
    /// every position is one the training data never covered - so its
    /// mistakes compound, and a model agreeing with its teacher on nearly
    /// six decisions in ten can still lose nearly every battle. That gap
    /// is not a training bug, it is what behavioural cloning does.
    ///
    /// The fix is to collect on the STUDENT's distribution and label with
    /// the TEACHER's answer. The student plays a whole battle, going
    /// wherever its own mistakes take it, and every position it reaches is
    /// recorded with what the brain would have played there. The next
    /// model is trained on the places it actually goes.
    ///
    /// Two details make it honest rather than merely plausible.
    ///
    /// The teacher must not touch the battle. It ranks through
    /// MonteCarloStrategy.Advise, which is ChooseAction without the two
    /// things that commit to a choice: it never applies mega evolution
    /// (WithMega SETS MegaEvolve on the very BattleAction objects the
    /// student is about to choose from, so a teacher calling ChooseAction
    /// to "just have a look" would be reaching into the battle and
    /// evolving the student's Pokemon), and it never records having
    /// chosen. Its rollouts run on state.Clone() with its own rng, so a
    /// battle with a teacher watching plays out identically to one
    /// without.
    ///
    /// The teacher must not lose track of the battle either. Its
    /// overswitching brake asks "did I switch last turn", and a teacher
    /// that never plays would answer no forever and happily advise
    /// switching every turn. So it is told what actually happened.
    /// </summary>
    public sealed class TeachingStrategy : IBattleStrategy, ITeacherPolicy
    {
        readonly IBattleStrategy student;
        readonly MonteCarloStrategy teacher;

        public TeachingStrategy(IBattleStrategy student, MonteCarloStrategy teacher)
        {
            this.student = student ?? throw new ArgumentNullException(nameof(student));
            this.teacher = teacher ?? throw new ArgumentNullException(nameof(teacher));
        }

        /// <summary>The student's name with a mark, because the battle log
        /// should say who actually played while a corpus reader can still
        /// tell these records apart from ordinary ones - they are the only
        /// records whose label is not the action that was taken.</summary>
        public string Name => student.Name + " (taught)";

        public IReadOnlyList<float>? LastMoveScores => teacher.LastMoveScores;
        public int LastTeacherAction => teacher.LastTeacherAction;
        public float RiskAppetite => teacher.RiskAppetite;

        public BattleAction ChooseAction(BattleState state, PlayerState self, IReadOnlyList<BattleAction> legalActions)
        {
            // The teacher forms its opinion FIRST, so that when the driver
            // reads LastMoveScores immediately after this returns, the
            // numbers belong to this decision.
            teacher.Advise(state, self, legalActions);

            BattleAction played = student.ChooseAction(state, self, legalActions);

            teacher.NoteChoice(played);

            return played;
        }

        public int ChooseReplacement(BattleState state, PlayerState self, IReadOnlyList<int> legalTeamIndexes)
        {
            // ChooseReplacement is safe to call outright: it applies no
            // mega evolution and every rollout runs on a clone, so unlike
            // ChooseAction it has nothing to strip out.
            teacher.ChooseReplacement(state, self, legalTeamIndexes);

            return student.ChooseReplacement(state, self, legalTeamIndexes);
        }
    }
}