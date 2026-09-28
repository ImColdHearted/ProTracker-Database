using System;
using System.Collections.Generic;
using System.Threading;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;

namespace PokemonSim.Evaluation
{
    /// <summary>
    /// §326. A brain with a tally-counter on it.
    ///
    /// It exists because §325's first real results said the installed model
    /// loses to random legal moves 133-267 over 400 battles - 33.3%, with a
    /// 95% interval of 28.8 to 38.0. That is not an undertrained model. A
    /// model that had learned nothing at all would sit at 50%, because that
    /// is what random scores against random. Landing two thirds of the way
    /// to zero means it is systematically choosing WORSE than chance, and
    /// the only way to find out how is to watch what it chooses.
    ///
    /// The cheapest thing worth watching is switching. The baseline never
    /// volunteers a switch - §154's RandomMoveStrategy picks a random MOVE
    /// and only switches when forced - so a brain that switches often is
    /// spending turns the baseline is spending on damage. Nothing else in
    /// this engine converts "plays badly" into "loses to random" so
    /// directly.
    ///
    /// A decorator rather than counters inside each brain, for two reasons:
    /// every brain gets counted the same way, so the numbers are comparable
    /// across Monte Carlo, baseline and model; and the brains themselves
    /// stay untouched, which matters because one of them is the teacher and
    /// this must not perturb it.
    /// </summary>
    public sealed class CountingStrategy : IBattleStrategy
    {
        readonly IBattleStrategy inner;

        int decisions;
        int voluntarySwitches;
        int replacements;

        // §335. What it picks when it does attack.
        int moveChoices;
        int statusChoices;
        int damagingChoices;
        int wastedStatus;
        int noEffectChoices;
        int weakerThanAvailable;

        readonly int[] slotChoices = new int[MoveSlots];

        public CountingStrategy(IBattleStrategy strategy)
        {
            inner = strategy;
        }

        /// <summary>The brain underneath, for a caller that wants something
        /// only that brain knows - the model's own count of how often it
        /// answered, say.</summary>
        public IBattleStrategy Inner => inner;

        public string Name => inner.Name;

        /// <summary>Turns this brain was asked to act on.</summary>
        public int Decisions => Volatile.Read(ref decisions);

        /// <summary>Turns it chose to switch INSTEAD of attacking. Forced
        /// replacements are counted separately and are nobody's decision -
        /// counting them here would make every brain that loses a Pokemon
        /// look like a switcher.</summary>
        public int VoluntarySwitches => Volatile.Read(ref voluntarySwitches);

        /// <summary>Times it picked who comes in after a faint.</summary>
        public int Replacements => Volatile.Read(ref replacements);

        /// <summary>§335. How many move slots a Pokemon can have, and so how
        /// many buckets the slot histogram has.</summary>
        public const int MoveSlots = 4;

        /// <summary>Turns it used a move rather than switching.</summary>
        public int MoveChoices => Volatile.Read(ref moveChoices);

        /// <summary>Of those, how many were a Status move - no damage at
        /// all. Counted apart from the no-effect count below, because a
        /// status move is a legitimate play and a 0x attack never is.</summary>
        public int StatusChoices => Volatile.Read(ref statusChoices);

        /// <summary>
        /// §341. Status moves that could not change anything on the board
        /// they were played into.
        ///
        /// §335 gave an attack two ways to be wasted - it cannot touch the
        /// target, or a better one was legal - and gave a status move none
        /// at all. That is the hole the model fell into: it plays a status
        /// move on 48.5% of its turns against Monte Carlo's 33.3%, and every
        /// one of them is invisible to both of §335's figures because they
        /// are measured over DAMAGING choices only. A brain that answers
        /// every hard question with Swords Dance scores perfectly on a
        /// metric that only grades attacks.
        ///
        /// Four cases, and only four, each one certain:
        ///
        ///   - every stat the move would raise on its user is already at
        ///     +6, so there is nothing left to raise;
        ///   - it inflicts a status on a target that already has one;
        ///   - it sets the weather that is already blowing;
        ///   - it sets the terrain that is already down.
        ///
        /// Everything else is left alone. Protect, Substitute, Roost, hazard
        /// moves, phazing - whether those were worth a turn is a judgement
        /// this cannot make, so it does not pretend to. Like §335's type
        /// chart, the number UNDERSTATES, and that is the right direction
        /// for a figure somebody is going to act on.
        /// </summary>
        public int WastedStatus => Volatile.Read(ref wastedStatus);

        /// <summary>Damaging moves whose target was known, which is the
        /// denominator the two counts below belong over. Counted here rather
        /// than worked out as MoveChoices minus StatusChoices, because a turn
        /// whose target could not be read is counted in neither and a
        /// subtraction would quietly put it in both.</summary>
        public int DamagingChoices => Volatile.Read(ref damagingChoices);

        /// <summary>Damaging moves aimed at something the TYPE CHART says
        /// they cannot touch: Normal into Ghost, Electric into Ground.
        ///
        /// Deliberately the type chart alone, not the engine's own answer.
        /// Abilities that grant an immunity - Levitate, Flash Fire, Volt
        /// Absorb - are not in here, so this UNDERSTATES. A number that
        /// understates a defect is safe; one that needs the whole damage
        /// pipeline to reproduce would be a second implementation of the
        /// engine living inside the measurement system, which is the one
        /// thing a harness must not have.</summary>
        public int NoEffectChoices => Volatile.Read(ref noEffectChoices);

        /// <summary>Damaging moves chosen while a damaging move with a
        /// STRICTLY BETTER type multiplier was legal on the same turn.
        ///
        /// This is "is it choosing the worst option", asked as cheaply as it
        /// can be asked. It is NOT "was this the best play" - power,
        /// accuracy, speed, hazards and the rest all matter and none of them
        /// are here. It says only that a more effective move was in front of
        /// it and it did not take it.</summary>
        public int WeakerThanAvailable => Volatile.Read(ref weakerThanAvailable);

        /// <summary>How often it chose the move in this slot. A policy that
        /// has collapsed onto one output shows up here and nowhere
        /// else.</summary>
        public int SlotChoices(int slot) =>
            slot >= 0 && slot < MoveSlots ? Volatile.Read(ref slotChoices[slot]) : 0;

        public BattleAction ChooseAction(
            BattleState state, PlayerState self, IReadOnlyList<BattleAction> legalActions)
        {
            Interlocked.Increment(ref decisions);

            BattleAction chosen = inner.ChooseAction(state, self, legalActions);

            if (chosen.Type == BattleActionType.Switch)
            {
                Interlocked.Increment(ref voluntarySwitches);

                return chosen;
            }

            CountMove(state, self, legalActions, chosen);

            return chosen;
        }

        /// <summary>
        /// §335. Everything the harness can learn from ONE chosen move
        /// without re-running the engine.
        ///
        /// §326 asked whether the model loses to random by switching, and
        /// the answer was no: it switches on 0.2-0.4% of turns against Monte
        /// Carlo's 6-10%. So it attacks on almost every turn and still
        /// scores 30% against random legal moves, which leaves exactly one
        /// place for the difference to be - in WHICH attack.
        ///
        /// Three questions, in rising order of how much they would explain:
        /// which slot, how often the move cannot touch the target at all,
        /// and how often a strictly more effective move was sitting there.
        /// </summary>
        void CountMove(
            BattleState state,
            PlayerState self,
            IReadOnlyList<BattleAction> legalActions,
            BattleAction chosen)
        {
            MoveState? move = chosen.Move;

            if (move is null)
                return;

            Interlocked.Increment(ref moveChoices);

            int slot = SlotOf(chosen.User, move);

            if (slot >= 0 && slot < MoveSlots)
                Interlocked.Increment(ref slotChoices[slot]);

            if (move.Category == MoveCategory.Status)
            {
                Interlocked.Increment(ref statusChoices);

                if (ChangesNothing(state, self, chosen, move))
                    Interlocked.Increment(ref wastedStatus);

                return;
            }

            PokemonState? target = chosen.Target ?? Opponent(state, self)?.ActivePokemon;

            if (target is null)
                return;

            Interlocked.Increment(ref damagingChoices);

            double mine = Effectiveness(move, target);

            if (mine <= 0)
                Interlocked.Increment(ref noEffectChoices);

            double best = mine;

            for (int i = 0; i < legalActions.Count; i++)
            {
                BattleAction option = legalActions[i];

                if (option.Type == BattleActionType.Switch || option.Move is null)
                    continue;

                if (option.Move.Category == MoveCategory.Status)
                    continue;

                double here = Effectiveness(option.Move, option.Target ?? target);

                if (here > best)
                    best = here;
            }

            if (best > mine)
                Interlocked.Increment(ref weakerThanAvailable);
        }

        /// <summary>§341. Whether this status move had anything left to do
        /// on this board. See WastedStatus for the four cases and for why
        /// there are only four.</summary>
        static bool ChangesNothing(
            BattleState state, PlayerState self, BattleAction chosen, MoveState move)
        {
            if (move.SetWeather != WeatherType.None)
                return state.Environment.Weather == move.SetWeather;

            if (move.SetTerrain != TerrainType.None)
                return state.Environment.Terrain == move.SetTerrain;

            if (move.InflictStatus != StatusCondition.None)
            {
                PokemonState? target = chosen.Target ?? Opponent(state, self)?.ActivePokemon;

                return target is not null && target.Status != StatusCondition.None;
            }

            List<StatChange>? changes = move.StatChanges;

            if (changes is null || changes.Count == 0)
                return false;

            // Only the raises this move puts on its OWN user can be judged
            // from here. A drop aimed at the opponent might miss, might be
            // blocked, might be at -6 already - and the move may do several
            // things at once, so a single unmaxed stat is enough to make the
            // turn worth something.
            bool sawSelfRaise = false;

            for (int i = 0; i < changes.Count; i++)
            {
                StatChange change = changes[i];

                if (change.Stages <= 0 || !IsSelf(change.Target))
                    return false;

                sawSelfRaise = true;

                if (StageOf(chosen.User, change.Stat) < MaxStage)
                    return false;
            }

            return sawSelfRaise;
        }

        /// <summary>The stage ceiling the engine clamps to.</summary>
        const int MaxStage = 6;

        static bool IsSelf(string? target) =>
            string.IsNullOrEmpty(target) ||
            string.Equals(target, "self", StringComparison.OrdinalIgnoreCase);

        /// <summary>The same names StatResolver reads, and the same fields.
        /// An unknown name answers with a stage below the ceiling, so a move
        /// this does not understand is never called wasted.</summary>
        static int StageOf(PokemonState pokemon, string stat) => stat switch
        {
            "Attack" => pokemon.AttackStage,
            "Defense" => pokemon.DefenseStage,
            "SpAttack" => pokemon.SpAttackStage,
            "SpDefense" => pokemon.SpDefenseStage,
            "Speed" => pokemon.SpeedStage,
            "Accuracy" => pokemon.AccuracyStage,
            "Evasion" => pokemon.EvasionStage,
            _ => 0
        };

        /// <summary>Which of the user's four slots this move is, by IDENTITY
        /// - the move object in the action is the one in the team, so
        /// nothing here depends on move names or on MoveState.Index staying
        /// in step with the list it came from.</summary>
        static int SlotOf(PokemonState user, MoveState move)
        {
            List<MoveState> moves = user.Moves;

            for (int i = 0; i < moves.Count; i++)
            {
                if (ReferenceEquals(moves[i], move))
                    return i;
            }

            return -1;
        }

        /// <summary>The type chart's multiplier for this move against this
        /// target, and nothing else - see NoEffectChoices for why the
        /// engine's own answer is deliberately not used.</summary>
        static double Effectiveness(MoveState move, PokemonState target)
        {
            if (move.Typeless)
                return 1;

            double multiplier = 1;

            List<PokemonType> types = target.Types;

            for (int i = 0; i < types.Count; i++)
                multiplier *= TypeChart.GetMultiplier(move.Type, types[i]);

            return multiplier;
        }

        /// <summary>The other side, whoever this brain is playing.</summary>
        static PlayerState? Opponent(BattleState state, PlayerState self) =>
            ReferenceEquals(state.Player1, self) ? state.Player2
                : ReferenceEquals(state.Player2, self) ? state.Player1
                    : null;

        public int ChooseReplacement(
            BattleState state, PlayerState self, IReadOnlyList<int> legalTeamIndexes)
        {
            Interlocked.Increment(ref replacements);

            return inner.ChooseReplacement(state, self, legalTeamIndexes);
        }
    }
}
