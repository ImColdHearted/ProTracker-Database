using System.Collections.Generic;
using PokemonSim.Actions;
using PokemonSim.Models;

namespace PokemonSim.Engine.Strategies
{
    /// <summary>
    /// Section 154. A computer opponent. The engine no longer chooses
    /// anyone's actions (the old BuildActionQueue hard-wired an AI into the
    /// engine itself, referencing a MonteCarloAI class that did not survive
    /// the copy into this repo); instead the engine reports legal actions
    /// and a strategy - or the Simulator window's human - picks one. The
    /// user's Monte Carlo opponent and the boss brains plug back in here in
    /// a later phase by implementing this interface.
    /// </summary>
    public interface IBattleStrategy
    {
        string Name { get; }

        /// <summary>Pick one of the legal actions (never empty while the
        /// battle is unfinished).</summary>
        BattleAction ChooseAction(BattleState state, PlayerState self, IReadOnlyList<BattleAction> legalActions);

        /// <summary>Pick a replacement after a faint: one of the offered
        /// team indexes (into self.Team, all non-fainted).</summary>
        int ChooseReplacement(BattleState state, PlayerState self, IReadOnlyList<int> legalTeamIndexes);
    }

    /// <summary>
    /// Section 175. A strategy that does not merely choose, but RANKS -
    /// it can say what each move slot was worth to it. Monte Carlo already
    /// computes exactly that (a mean rollout score per candidate action)
    /// and then discards everything but the winner; publishing it costs
    /// nothing and gives the observer a teaching signal with four numbers
    /// in it instead of one label.
    ///
    /// Contract: valid only immediately after the ChooseAction call that
    /// produced it, on the same thread, and only for that decision. A
    /// strategy that could not rank this turn (one legal action, budget
    /// exhausted, a forced replacement) returns null rather than a
    /// misleading vector of zeroes.
    /// </summary>
    public interface ITeacherPolicy
    {
        /// <summary>One score per move slot for the decision just made,
        /// or null. Slots the strategy did not evaluate are NaN, which is
        /// how a reader tells "worth nothing" from "never looked at".</summary>
        IReadOnlyList<float>? LastMoveScores { get; }

        /// <summary>
        /// Section 182. Which action the teacher settled on, as an index
        /// into the action space, or -1.
        ///
        /// Until section 182 this was redundant - the strategy doing the
        /// ranking was the strategy doing the playing, so the chosen
        /// action already said it. Under DAgger they come apart: the model
        /// drives while this brain says what it would have done, and that
        /// recommendation is the training label. The argmax of
        /// LastMoveScores is not a substitute, because a choice is made
        /// with the switch brake and mega evolution applied on top of the
        /// numbers.
        /// </summary>
        int LastTeacherAction { get; }

        /// <summary>Section 182. The risk appetite these scores were
        /// produced at, 0 for plain expected value. A recorder writes it
        /// into the state so one model can learn the whole range and be
        /// told at play time which end of it to behave like.</summary>
        float RiskAppetite { get; }
    }
}