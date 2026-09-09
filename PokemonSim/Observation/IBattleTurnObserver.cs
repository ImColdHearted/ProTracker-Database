using System.Collections.Generic;
using PokemonSim.Actions;
using PokemonSim.Models;

namespace PokemonSim.Observation
{
    /// <summary>
    /// Section 156. What a battle DRIVER may show an observer. Only the two
    /// drivers of real, whole battles call this - SimulatorSession (the
    /// primary visible battle) and BattleEngine.RunBattle when its caller
    /// explicitly passes an observer (the user-marked lab run, the dev
    /// console, a test). Monte Carlo's rollouts call RunTurn directly on
    /// clones and never see an observer, so no hypothetical rollout can
    /// ever masquerade as a genuine observed battle. Implementations must
    /// treat every argument as read-only and must never throw.
    /// </summary>
    public interface IBattleTurnObserver
    {
        /// <summary>A side has committed to an action (called before the
        /// turn resolves). Section 175 added the last parameter: when the
        /// acting strategy implements ITeacherPolicy, the driver passes
        /// what it thought each move slot was worth, so the recorded
        /// decision carries a ranking and not merely a label. Null from
        /// anything that does not rank.</summary>
        void OnDecision(BattleState state, PlayerState actor, string strategyName,
                        IReadOnlyList<BattleAction> legalActions, BattleAction chosen,
                        Engine.Strategies.ITeacherPolicy? teacher = null);

        /// <summary>The whole turn has resolved.</summary>
        void OnTurnResolved(BattleState state);

        /// <summary>
        /// A fainted Pokemon is being replaced outside a turn.
        ///
        /// Section 180 moved this call BEFORE the swap and gave it the
        /// candidates. It used to fire afterwards with nothing but the
        /// Pokemon that had already come in, which made the recorded state
        /// contain its own answer - so section 176 had to exclude those
        /// records from training, and the switch head was left learning
        /// from the 0.2 percent of decisions that were voluntary switches.
        /// Called before the swap, with the team indexes the engine
        /// offered, a replacement is an ordinary switch decision with an
        /// honest state and a real label - about five per battle.
        /// </summary>
        void OnReplacement(BattleState state, PlayerState actor, string strategyName,
                           PokemonState sentIn,
                           IReadOnlyList<int>? legalTeamIndexes = null,
                           Engine.Strategies.ITeacherPolicy? teacher = null);

        /// <summary>The battle reached its outcome (win, loss, draw or
        /// cancellation). May be called once per battle.</summary>
        void OnFinished(BattleState state);
    }
}