using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PokemonSim.Actions;
using PokemonSim.Models;

namespace PokemonSim.Observation
{
    /// <summary>
    /// Section 156. The observer / shadow evaluator: a passive recorder a
    /// battle driver may attach to ONE whole battle. It receives immutable
    /// facts (state before a decision, the legal actions, the chosen
    /// action, the resolved state, the final outcome), encodes them with
    /// the author's ten-float layout, optionally asks the shadow model for
    /// its preferred move, and appends JSON Lines to one file per battle
    /// in the given directory. It never chooses anything, never mutates
    /// battle state, never draws from the battle's rng, and never throws
    /// out of a callback: every failure is folded into LastStatus and the
    /// battle plays on exactly as if the observer were not there.
    ///
    /// Writes go through a bounded channel drained by one background task,
    /// so turn resolution never waits on the disk; when the channel is
    /// full the record is dropped and counted rather than blocking.
    /// DisposeAsync flushes what is queued; Cancel abandons it.
    /// </summary>
    public sealed class BattleObserver : IBattleTurnObserver, IAsyncDisposable
    {
        readonly string filePath;
        readonly string kind;
        readonly int seed;
        readonly IShadowEvaluator shadow;
        readonly Channel<string> lines;
        readonly CancellationTokenSource writerCts = new();
        readonly List<DecisionRecord> pending = new(2);
        readonly Dictionary<DecisionRecord, (int SelfHp, int OppHp, PokemonState SelfActive, PokemonState OppActive)> pendingBefore = new(2);
        readonly object gate = new();

        Task? writer;
        bool finished;

        int recorded;
        int dropped;
        int moveDecisions;
        int shadowAgreed;
        int ranked;
        string lastStatus = "recording";

        public string BattleId { get; }

        public int RecordedCount => Volatile.Read(ref recorded);
        public int DroppedCount => Volatile.Read(ref dropped);
        public int MoveDecisionCount => Volatile.Read(ref moveDecisions);

        /// <summary>Section 180. How many recorded decisions carried a
        /// ranking from the acting strategy. Zero for a whole run means the
        /// corpus cannot use the trainer's --soft-targets, which is worth
        /// finding out when the run ends rather than hours later.</summary>
        public int RankedCount => Volatile.Read(ref ranked);
        public int ShadowAgreedCount => Volatile.Read(ref shadowAgreed);
        public string LastStatus => lastStatus;
        public ShadowEvaluatorStatus ShadowStatus => shadow.Status;

        public BattleObserver(
            string directory,
            string battleKind,
            int battleSeed,
            IShadowEvaluator? shadowEvaluator = null,
            int queueCapacity = 256)
        {
            kind = battleKind;
            seed = battleSeed;
            shadow = shadowEvaluator ?? new NullShadowEvaluator("no shadow model attached - observation only");

            BattleId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{unchecked((uint)battleSeed):x8}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";

            Directory.CreateDirectory(directory);
            filePath = Path.Combine(directory, $"obs-{BattleId}.jsonl");

            lines = Channel.CreateBounded<string>(new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropWrite
            });
        }

        // ---- IBattleTurnObserver ----

        public void OnDecision(BattleState state, PlayerState actor, string strategyName,
                               IReadOnlyList<BattleAction> legalActions, BattleAction chosen,
                               Engine.Strategies.ITeacherPolicy? teacher = null)
        {
            try
            {
                lock (gate)
                {
                    if (finished)
                        return;

                    var record = new DecisionRecord
                    {
                        BattleId = BattleId,
                        Kind = kind,
                        Seed = seed,
                        Turn = state.TurnNumber + 1,
                        Side = ReferenceEquals(actor, state.Player1) ? "P1" : "P2",
                        Strategy = strategyName,
                        // Section 175: the encoder needs what was on offer
                        // this turn to say which slots were usable.
                        State = ObserverEncoder.Encode(state, actor, legalActions, RiskOf(teacher)),
                        LegalMoves = ObserverEncoder.LegalMoveMask(legalActions),
                        // Section 176: the same legality over moves AND the
                        // team positions this side could switch to, which is
                        // what a model with a switch head has to be masked
                        // with.
                        LegalActions = ObserverEncoder.LegalActionMask(legalActions, actor),
                        Action = Describe(actor, chosen),
                        TeacherScores = TeacherVector(teacher?.LastMoveScores),
                        TeacherActionIndex = TeacherChoice(teacher)
                    };

                    if (record.TeacherScores != null)
                        Interlocked.Increment(ref ranked);

                    ApplyShadow(record, actor);

                    PokemonState opponentActive = state.GetOpponentOf(actor).ActivePokemon;

                    pending.Add(record);
                    pendingBefore[record] = (
                        actor.ActivePokemon.CurrentHP,
                        opponentActive.CurrentHP,
                        actor.ActivePokemon,
                        opponentActive);
                }
            }
            catch (Exception ex)
            {
                lastStatus = "observer error: " + ex.Message;
            }
        }

        public void OnTurnResolved(BattleState state)
        {
            try
            {
                lock (gate)
                {
                    if (finished)
                        return;

                    foreach (DecisionRecord record in pending)
                    {
                        PlayerState actor = record.Side == "P1" ? state.Player1 : state.Player2;
                        PlayerState opponent = state.GetOpponentOf(actor);
                        (int selfHpBefore, int oppHpBefore, PokemonState selfBefore, PokemonState oppBefore) = pendingBefore[record];

                        record.After = new TurnAftermath
                        {
                            SelfHpFraction = Fraction(actor.ActivePokemon),
                            OpponentHpFraction = Fraction(opponent.ActivePokemon),
                            DamageTaken = Math.Max(0, selfHpBefore - Math.Max(0, selfBefore.CurrentHP)),
                            DamageDealt = Math.Max(0, oppHpBefore - Math.Max(0, oppBefore.CurrentHP)),
                            SelfStatus = actor.ActivePokemon.Status.ToString(),
                            OpponentStatus = opponent.ActivePokemon.Status.ToString(),
                            SelfActiveFainted = selfBefore.Fainted,
                            OpponentActiveFainted = oppBefore.Fainted,
                            SelfSwitched = !ReferenceEquals(actor.ActivePokemon, selfBefore),
                            OpponentSwitched = !ReferenceEquals(opponent.ActivePokemon, oppBefore),
                            Weather = state.Environment.Weather.ToString(),
                            Terrain = state.Environment.Terrain.ToString()
                        };

                        Enqueue(record);
                    }

                    pending.Clear();
                    pendingBefore.Clear();
                }
            }
            catch (Exception ex)
            {
                lastStatus = "observer error: " + ex.Message;
            }
        }

        public void OnReplacement(BattleState state, PlayerState actor, string strategyName,
                                  PokemonState sentIn,
                                  IReadOnlyList<int>? legalTeamIndexes = null,
                                  Engine.Strategies.ITeacherPolicy? teacher = null)
        {
            try
            {
                lock (gate)
                {
                    if (finished)
                        return;

                    var record = new DecisionRecord
                    {
                        BattleId = BattleId,
                        Kind = kind,
                        Seed = seed,
                        Turn = state.TurnNumber,
                        Side = ReferenceEquals(actor, state.Player1) ? "P1" : "P2",
                        Strategy = strategyName,
                        // Section 180: this now fires BEFORE the swap, so
                        // the state is the one the side actually decided
                        // from - its active still the fainted Pokemon - and
                        // the mask carries the team positions the engine
                        // offered. That makes a replacement an ordinary
                        // switch example rather than one whose state gives
                        // away its own answer. A caller that has not been
                        // updated passes no candidates, and the empty mask
                        // still tells the trainer to skip it.
                        State = ObserverEncoder.Encode(state, actor, null, RiskOf(teacher)),
                        LegalMoves = new float[ObservationSchema.MoveSlots],
                        LegalActions = legalTeamIndexes == null
                            ? new float[ObservationSchema.ActionSlots]
                            : ObserverEncoder.ReplacementMask(legalTeamIndexes),
                        TeacherScores = TeacherVector(teacher?.LastMoveScores),
                        TeacherActionIndex = TeacherChoice(teacher),
                        Action = new ObservedAction
                        {
                            Kind = "Replacement",
                            SwitchTeamIndex = actor.Team.IndexOf(sentIn),
                            SwitchSpecies = sentIn.Species
                        }
                    };

                    if (record.TeacherScores != null)
                        Interlocked.Increment(ref ranked);

                    Enqueue(record);
                }
            }
            catch (Exception ex)
            {
                lastStatus = "observer error: " + ex.Message;
            }
        }

        /// <summary>Section 175. A defensive copy of the acting strategy's
        /// per-slot scores, sized to the schema. A slot the strategy never
        /// evaluated arrives as NaN and is written as null, because the
        /// record writer uses System.Text.Json's default options and those
        /// throw on NaN - which this class's own try/catch would turn into
        /// a quietly missing record. A vector with nothing usable in it at
        /// all is null rather than four nulls.</summary>
        /// <summary>Section 182. The risk appetite to stamp on this
        /// state. No teacher means nobody was judging it, which is the
        /// same thing every record before section 182 said implicitly:
        /// plain expected value.</summary>
        static float RiskOf(Engine.Strategies.ITeacherPolicy? teacher)
        {
            if (teacher == null)
                return ObservationSchema.NeutralRisk;

            float risk = teacher.RiskAppetite;

            return float.IsFinite(risk) ? Math.Clamp(risk, 0f, 1f) : ObservationSchema.NeutralRisk;
        }

        /// <summary>Section 182. Which action the teacher wanted, kept
        /// only when it names a real slot - so a reader never has to guess
        /// whether -1 means "no teacher" or "a teacher with no opinion".
        /// Both are -1, and both mean the same thing to a trainer.</summary>
        static int TeacherChoice(Engine.Strategies.ITeacherPolicy? teacher)
        {
            if (teacher == null)
                return -1;

            int index = teacher.LastTeacherAction;

            return index >= 0 && index < ObservationSchema.ActionSlots ? index : -1;
        }

        static float?[]? TeacherVector(IReadOnlyList<float>? scores)
        {
            if (scores == null)
                return null;

            var copy = new float?[ObservationSchema.ActionSlots];
            bool any = false;

            for (int i = 0; i < copy.Length; i++)
            {
                float value = i < scores.Count ? scores[i] : float.NaN;

                if (float.IsNaN(value) || float.IsInfinity(value))
                    continue;

                copy[i] = value;
                any = true;
            }

            return any ? copy : null;
        }

        public void OnFinished(BattleState state)
        {
            try
            {
                lock (gate)
                {
                    if (finished)
                        return;

                    finished = true;
                    pending.Clear();
                    pendingBefore.Clear();

                    var final = new FinalRecord
                    {
                        BattleId = BattleId,
                        Kind = kind,
                        Seed = seed,
                        Turns = state.TurnNumber,
                        Outcome = state.Outcome.ToString(),
                        Player1Score = state.Outcome switch
                        {
                            BattleOutcome.Player1Wins => 1,
                            BattleOutcome.Player2Wins => -1,
                            _ => 0
                        },
                        DecisionRecords = RecordedCount,
                        DroppedRecords = DroppedCount
                    };

                    Enqueue(final, countIt: false);
                    lines.Writer.TryComplete();
                    lastStatus = $"battle finished ({state.Outcome}) - {RecordedCount} records";
                }
            }
            catch (Exception ex)
            {
                lastStatus = "observer error: " + ex.Message;
            }
        }

        // ---- plumbing ----

        void ApplyShadow(DecisionRecord record, PlayerState actor)
        {
            if (!shadow.Status.Available)
                return;

            ShadowPrediction? prediction;

            try
            {
                // Section 176: the full action mask, so a model with a
                // switch head is told which team positions were legal. A
                // move-only model reads its first four entries.
                prediction = shadow.Predict(record.State, record.LegalActions);
            }
            catch (Exception ex)
            {
                // A misbehaving evaluator costs its opinion, never the
                // record and never the battle.
                lastStatus = "shadow inference failed: " + ex.Message;
                record.Shadow = new ShadowRecord { Available = false };
                return;
            }

            if (prediction == null)
            {
                record.Shadow = new ShadowRecord { Available = false };
                return;
            }

            if (prediction.PreferredMoveIndex >= 0 &&
                prediction.PreferredMoveIndex < actor.ActivePokemon.Moves.Count)
            {
                prediction.PreferredMoveName = actor.ActivePokemon.Moves[prediction.PreferredMoveIndex].Name;
            }

            bool? agreed = null;

            if (record.Action.Kind == "Move")
            {
                agreed = prediction.PreferredMoveIndex == record.Action.MoveIndex;
            }
            else if (record.Action.Kind == "Switch" && shadow.Status.CanChooseSwitches)
            {
                // Section 176: only a model that HAS a switch head gets
                // marked right or wrong on a switch turn. A move-only one
                // could not express the answer, so it stays null rather
                // than scoring a miss it was never able to avoid.
                agreed = prediction.PreferredTeamIndex == record.Action.SwitchTeamIndex;
            }

            if (agreed != null)
            {
                Interlocked.Increment(ref moveDecisions);

                if (agreed == true)
                    Interlocked.Increment(ref shadowAgreed);
            }

            record.Shadow = new ShadowRecord
            {
                Available = true,
                PreferredActionIndex = prediction.PreferredActionIndex,
                PreferredTeamIndex = prediction.PreferredTeamIndex,
                PreferredMoveIndex = prediction.PreferredMoveIndex,
                PreferredMoveName = prediction.PreferredMoveName,
                Score = prediction.Score,
                Scores = prediction.Scores,
                Agreed = agreed
            };
        }

        static ObservedAction Describe(PlayerState actor, BattleAction chosen)
        {
            if (chosen.Type == BattleActionType.Switch && chosen.SwitchTarget != null)
            {
                return new ObservedAction
                {
                    Kind = "Switch",
                    SwitchTeamIndex = actor.Team.IndexOf(chosen.SwitchTarget),
                    SwitchSpecies = chosen.SwitchTarget.Species
                };
            }

            if (chosen.Move != null && chosen.Move.Name == "Struggle")
                return new ObservedAction { Kind = "Struggle", MoveName = "Struggle" };

            return new ObservedAction
            {
                Kind = "Move",
                MoveIndex = chosen.Move?.Index ?? -1,
                MoveName = chosen.Move?.Name
            };
        }

        static float Fraction(PokemonState pokemon) =>
            pokemon.MaxHP <= 0 ? 0f : Math.Max(0, pokemon.CurrentHP) / (float)pokemon.MaxHP;

        void Enqueue(object record, bool countIt = true)
        {
            string line = JsonSerializer.Serialize(record, record.GetType());

            if (lines.Writer.TryWrite(line))
            {
                if (countIt)
                    Interlocked.Increment(ref recorded);

                EnsureWriter();
            }
            else if (countIt)
            {
                Interlocked.Increment(ref dropped);
                lastStatus = $"buffer full - {DroppedCount} record(s) dropped";
            }
        }

        void EnsureWriter()
        {
            if (writer != null)
                return;

            writer = Task.Run(async () =>
            {
                var batch = new StringBuilder();

                try
                {
                    // Section 186: the token is read ONCE, here, and never
                    // again. Reading writerCts.Token per iteration is what
                    // produced 78,422 log lines in one day: DisposeAsync
                    // disposes the source, and CancellationTokenSource.Token
                    // THROWS ObjectDisposedException rather than returning an
                    // already-cancelled token, so the loop faulted instead of
                    // stopping. A token that has been handed out stays valid
                    // after its source is disposed; only the property does not.
                    CancellationToken token = writerCts.Token;

                    while (await lines.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                    {
                        batch.Clear();

                        while (lines.Reader.TryRead(out string? line))
                            batch.Append(line).Append('\n');

                        if (batch.Length > 0)
                        {
                            try
                            {
                                await File.AppendAllTextAsync(filePath, batch.ToString(), token)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                lastStatus = "observation write failed: " + ex.Message;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancelled: abandon whatever is still queued.
                }
                catch (ObjectDisposedException)
                {
                    // Section 186: the same thing said a different way -
                    // the source went away while this loop was between
                    // awaits. There is nothing left to write to.
                }
            });
        }

        /// <summary>Abandon queued records immediately (the cancellation
        /// path). Safe to call from any thread.</summary>
        public void Cancel()
        {
            lock (gate)
            {
                finished = true;
                pending.Clear();
                pendingBefore.Clear();
            }

            lines.Writer.TryComplete();
            writerCts.Cancel();
            lastStatus = "observation cancelled";
        }

        /// <summary>Flush and stop. Bounded wait so a stuck disk can never
        /// wedge the app.</summary>
        /// <summary>
        /// Section 186. How long a flush may take before it is treated as a
        /// hung disk rather than a busy one.
        ///
        /// It was two seconds, chosen when observers were disposed one at a
        /// time after a whole run. Section 181 started disposing them as
        /// each battle ends, so at fifty thousand battles a great many
        /// flushes contend for the disk at once and two seconds is routinely
        /// not enough. The timeout then became the NORMAL path, which is how
        /// a safety valve turned into a fault generator. Thirty seconds is
        /// still a bound on shutdown, and the flushes wait in parallel, so
        /// raising it costs nothing but a longer worst case.
        /// </summary>
        const int FlushBudgetMs = 30000;

        public async ValueTask DisposeAsync()
        {
            lines.Writer.TryComplete();

            Task? pendingWriter = writer;

            if (pendingWriter != null)
            {
                Task done = await Task.WhenAny(pendingWriter, Task.Delay(FlushBudgetMs)).ConfigureAwait(false);

                if (!ReferenceEquals(done, pendingWriter))
                    writerCts.Cancel();

                // Section 186: wait for the writer to actually stop before
                // disposing what it reads from, and observe its fault here.
                // Both halves matter. Disposing underneath a running loop is
                // what threw; leaving the fault unobserved is what handed it
                // to the finalizer, which logged every single one.
                try
                {
                    await pendingWriter.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lastStatus = "observation write failed: " + ex.Message;
                }
            }

            writerCts.Dispose();
        }
    }
}