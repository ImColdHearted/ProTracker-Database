using System.Collections.Generic;

namespace PokemonSim.Observation
{
    /// <summary>
    /// Section 156. The versioned shape of one recorded observation line.
    /// Records are JSON Lines: one DecisionRecord per acting side per turn
    /// (plus one per forced replacement), then one FinalRecord when the
    /// battle ends. Everything here is plain data about the simulated
    /// battle - no UI state, no sprites, no screenshots, no credentials,
    /// no hunting history, and never a Monte Carlo rollout (rollouts run
    /// on clones that no observer is ever attached to).
    ///
    /// The feature layout deliberately stays the author's own
    /// BattleStateEncoder (preserved verbatim in ai-lab/): TEN floats,
    /// self-perspective - self active HP fraction, opponent active HP
    /// fraction, self Attack/Defense/Speed stages, opponent
    /// Attack/Defense/Speed stages, weather enum, terrain enum - so the
    /// existing pokemon_ai.onnx (10 inputs, 4 move outputs) and
    /// Train_Model.py (input_size = 10, output_size = 4) stay compatible.
    /// A later offline training run turns each decision line into the
    /// author's TrainingDataWriter triple: state = State, move =
    /// Action.MoveIndex (move decisions only), outcome = the battle's
    /// FinalRecord score from that side's perspective.
    /// </summary>
    public static class ObservationSchema
    {
        /// <summary>Section 175 raised this from 1 to 2 (ten floats to a
        /// move-aware seventy-six); section 176 to 3 (the bench, hazards,
        /// per-move effect flags and a SWITCH output); section 177 to 4,
        /// which adds the field conditions and closes a hole open since
        /// section 156 - the special-attack and special-defense stages
        /// were never recorded at all. Files of any version are readable,
        /// but they cannot train one model together - the trainer
        /// partitions by width and says what it left out.</summary>
        ///
        /// Section 182 raised it to 5, which appends ONE feature: the risk
        /// appetite the teacher was playing at when the state was recorded.
        /// That one is different in kind from the other 202 - it does not
        /// describe the battle, it describes which of several teachers is
        /// being imitated - and it is what lets a single model carry a
        /// difficulty dial rather than shipping one file per personality.
        /// A version 4 record is a version 5 record with k = 0, because
        /// every teacher before section 182 maximised the plain mean,
        /// which IS k = 0; the trainer therefore widens old states rather
        /// than bucketing them apart.
        ///
        /// Section 184 raised it to 6 for the same kind of reason: Sticky
        /// Web became a real hazard, and a hazard the model cannot see is
        /// one it cannot play around. The two entries go on the END rather
        /// than into the hazard block, because widening that block in
        /// place would shift every field and active feature after it and
        /// break the prefix rule the whole scheme rests on. A version 5
        /// record is a version 6 record with no web on either side, which
        /// is exactly true - the move did not exist when it was recorded.
        public const int Version = 8;

        /// <summary>
        /// §319. The comparison encoder's own id, deliberately nowhere near
        /// the fair one.
        ///
        /// The omniscient vector is §311's - the one that reads the
        /// opponent's stats, ability and item - and it is kept so the cost of
        /// honesty can be MEASURED rather than argued about: train both, play
        /// them against each other, and the gap is the value of the
        /// information the fair agent gave up. It must never be trained
        /// together with a fair corpus or shipped by accident, so it carries
        /// an id of its own rather than a flag on a shared one. A bucket is a
        /// stronger separation than a boolean.
        /// </summary>
        public const int OmniscientVersion = 108;

        /// <summary>The engine's mechanics generation the states were
        /// produced under (the MIGRATION_GUIDE section that last changed
        /// battle mechanics). Training data from different mechanics
        /// generations should not be silently mixed.
        ///
        /// §311 moved it off "156" for the first time. §309 gave an
        /// unpopped Air Balloon a real Ground immunity, which is a change to
        /// what a battle DOES, not only to what the observer writes down.
        /// </summary>
        public const string MechanicsVersion = "319";

        /// <summary>
        /// §311's vector - see ObserverEncoder for the layout.
        ///
        /// The prefix rule is gone. Every width before this one was a prefix
        /// of the next, so a model trained on any of them could be fed the
        /// front of a current state and behave as it always had. §311 re-laid
        /// the vector in blocks, so column 8 no longer means what it meant
        /// and no earlier width is a prefix of anything. That is the point:
        /// the old layout had no concept of a type, an ability, an item or a
        /// stat, and there is no way to add those by appending that does not
        /// also leave weather encoded as an ordinal for ever.
        /// </summary>
        public static readonly int FeatureCount = ObservationV8.Map.Count;

        /// <summary>
        /// §311. The widths that used to be accepted, kept so a refusal can
        /// NAME the generation a model came from instead of just declining a
        /// number. 10 was §156, 76 §175, 160 §176, 202 §177, 203 §182, 205
        /// §184.
        ///
        /// They are history, not compatibility. Feeding 205 of §311's columns
        /// to a 205-input model would not throw - it would score nonsense,
        /// because those 205 columns are a different 205 things - and a
        /// silent wrong answer is worse than a refusal.
        ///
        /// §319 retired 483 - every model trained on it learned with the
        /// opponent's stats, ability and item in front of it, so it is not
        /// merely a different width, it is a different game.
        /// </summary>
        public static readonly int[] RetiredFeatureCounts = { 10, 76, 160, 202, 203, 205, 483 };

        /// <summary>Every input width a shipped model may declare.
        /// §311: exactly one. See RetiredFeatureCounts.</summary>
        public static readonly int[] AcceptedFeatureCounts = { FeatureCount };

        /// <summary>The default risk appetite - plain expected value, the
        /// only thing any teacher did before section 182. Written into
        /// every state whose caller does not ask for something else.</summary>
        public const float NeutralRisk = 0f;

        /// <summary>The four move slots. Unchanged since section 156, and
        /// still the first four outputs of every model.</summary>
        public const int MoveSlots = 4;

        /// <summary>Section 176. Team positions the model may switch to,
        /// indexed as PlayerState.Team is - so output MoveSlots + i means
        /// "send in Team[i]", and the active's own position is simply never
        /// legal.</summary>
        public const int TeamSlots = 6;

        /// <summary>Section 176's output width: four moves then six team
        /// positions. Before it, a model had no way to say "switch", so
        /// NeuralStrategy could never leave a bad matchup while the Monte
        /// Carlo brain it was being measured against could.</summary>
        public const int ActionSlots = MoveSlots + TeamSlots;

        /// <summary>Every output width a shipped model may declare, newest
        /// first. A four-output model is a move-only model from before
        /// section 176 and still works.</summary>
        public static readonly int[] AcceptedActionCounts = { ActionSlots, MoveSlots };
    }

    public sealed class DecisionRecord
    {
        public int Schema { get; set; } = ObservationSchema.Version;
        public string Mechanics { get; set; } = ObservationSchema.MechanicsVersion;
        public string BattleId { get; set; } = "";
        public string Kind { get; set; } = "";          // Primary | Lab
        public int Seed { get; set; }
        public int Turn { get; set; }
        public string Side { get; set; } = "";          // P1 | P2
        public string Strategy { get; set; } = "";      // Human | Random moves | Monte Carlo
        public string Record { get; set; } = "decision";

        /// <summary>The ten-float self-perspective state BEFORE the action
        /// (the author's encoder layout - see ObservationSchema).</summary>
        public float[] State { get; set; } = System.Array.Empty<float>();

        /// <summary>1 for each of the four move slots that was a legal
        /// choice this turn, else 0. All zero when the only move was the
        /// synthesized Struggle. Kept at MoveSlots wide for every reader
        /// written before section 176.</summary>
        public float[] LegalMoves { get; set; } = System.Array.Empty<float>();

        /// <summary>Section 176. The same thing over the full action space:
        /// the four move slots, then one entry per team position the side
        /// could legally switch to this turn. Its first MoveSlots entries
        /// are LegalMoves.</summary>
        public float[] LegalActions { get; set; } = System.Array.Empty<float>();

        public ObservedAction Action { get; set; } = new();

        /// <summary>Facts observed AFTER the whole turn resolved.</summary>
        public TurnAftermath? After { get; set; }

        /// <summary>The shadow evaluation, when a model was available.
        /// Never consulted by anything that picks a real action.</summary>
        public ShadowRecord? Shadow { get; set; }

        /// <summary>
        /// Section 175. The acting strategy's own value for each move slot,
        /// when it has one - Monte Carlo's mean rollout score per slot,
        /// which it computes for every candidate and then throws away in
        /// favour of the argmax. Recording it turns each decision from one
        /// bit ("it chose slot 2") into a ranking over all four, which is
        /// far more to learn from per battle: the trainer's --soft-targets
        /// mode fits this distribution instead of the one-hot choice.
        /// Null for strategies that do not rank (the human, random moves).
        /// A slot the strategy did not evaluate is null rather than NaN -
        /// System.Text.Json refuses NaN under default options, and the
        /// observer's own try/catch would turn that refusal into a
        /// silently dropped record.
        ///
        /// Section 176 widened this to ActionSlots so it covers switches
        /// too. A switch entry carries the score Monte Carlo actually
        /// ranked it by, AFTER its overswitching brakes - because what is
        /// being distilled is the teacher's policy, not its raw rollouts,
        /// and a student trained on the raw numbers would switch far more
        /// than the teacher ever does.
        /// </summary>
        public float?[]? TeacherScores { get; set; }

        /// <summary>
        /// Section 182. The action the TEACHER would have taken, as an
        /// index into the action space, or -1 when no teacher was watching.
        ///
        /// This is what makes a DAgger record different from every record
        /// before it. Until now the acting strategy and the strategy being
        /// learned from were the same thing, so Action was both "what
        /// happened" and "what to imitate". Under section 182 the model
        /// drives and the Monte Carlo brain rides along saying what it
        /// would have done, which splits those apart: Action stays an
        /// honest log of the battle, and this is the label.
        ///
        /// It is deliberately NOT derived from the argmax of TeacherScores.
        /// Monte Carlo picks with a mega-evolution step and a switch brake
        /// applied on top of the numbers, so the argmax and the choice can
        /// differ; recording the choice means a reader never has to
        /// reimplement the teacher to find out what it wanted.
        /// </summary>
        public int TeacherActionIndex { get; set; } = -1;

        /// <summary>§319. Which encoder wrote State: "fair" or "omniscient".
        /// Written on every record so a corpus can be sorted without
        /// guessing from its width.</summary>
        public string Encoder { get; set; } = "fair";

        /// <summary>
        /// §319. WHAT THE OPPONENT ACTUALLY DID on this same turn, as an index
        /// into their own action space, or -1 when it is not known.
        ///
        /// This is a LABEL, not an observation. It is written after the turn
        /// resolves, it is never encoded into State, and the network cannot
        /// see it while choosing - which is the whole point. Pokemon is
        /// simultaneous, so "what are they doing right now" IS the prediction
        /// problem, and this is the answer, recorded for the head that will
        /// one day learn to guess it.
        ///
        /// What they do on the turn AFTER is deliberately not written here.
        /// The corpus already contains it - every decision is a line carrying
        /// its battle, its turn and its side - so the trainer derives it with
        /// a join rather than the observer holding records back for a turn
        /// and risking losing the last one of every cancelled battle.
        /// </summary>
        public int OpponentActionIndex { get; set; } = -1;

        /// <summary>§319. Whether the opponent switched on this turn -
        /// the same label, reduced to the one bit a switch-prediction head
        /// needs. Also written after the fact, also never in State.</summary>
        public bool OpponentSwitched { get; set; }
    }

    public sealed class ObservedAction
    {
        /// <summary>Section 180: Replacement joined Move and Switch as a
        /// trainable kind. Its records used to be written after the engine
        /// had already sent the Pokemon in - state containing its own
        /// answer, empty mask - and are written before the swap now, with
        /// the candidates the engine offered and, from a ranking strategy,
        /// TeacherScores over the switch half. Records from before that
        /// change still carry the empty mask, which is what keeps a reader
        /// from training on them: the chosen index is not marked legal.
        /// Struggle stays untrainable - it has no slot to name.</summary>
        public string Kind { get; set; } = "";          // Move | Switch | Struggle | Replacement
        public int MoveIndex { get; set; } = -1;        // 0-3 for Kind == Move
        public string? MoveName { get; set; }
        public int SwitchTeamIndex { get; set; } = -1;  // for Kind == Switch/Replacement
        public string? SwitchSpecies { get; set; }
    }

    public sealed class TurnAftermath
    {
        public float SelfHpFraction { get; set; }
        public float OpponentHpFraction { get; set; }
        public int DamageDealt { get; set; }            // opponent active HP lost this turn
        public int DamageTaken { get; set; }            // own active HP lost this turn
        public string SelfStatus { get; set; } = "";
        public string OpponentStatus { get; set; } = "";
        public bool SelfActiveFainted { get; set; }
        public bool OpponentActiveFainted { get; set; }
        public bool SelfSwitched { get; set; }
        public bool OpponentSwitched { get; set; }
        public string Weather { get; set; } = "";
        public string Terrain { get; set; } = "";
    }

    public sealed class ShadowRecord
    {
        public bool Available { get; set; }

        /// <summary>Section 176. The model's pick over the whole action
        /// space - a move slot, or MoveSlots + a team position.</summary>
        public int PreferredActionIndex { get; set; } = -1;

        /// <summary>The team position it wanted to send in, or -1 when it
        /// preferred a move.</summary>
        public int PreferredTeamIndex { get; set; } = -1;

        public int PreferredMoveIndex { get; set; } = -1;
        public string? PreferredMoveName { get; set; }
        public float Score { get; set; }
        public float[]? Scores { get; set; }

        /// <summary>True/false for any decision the model could score.
        /// Section 176: a model with a switch head is now judged on switch
        /// turns too; a move-only model still returns null on them, as it
        /// always did, because it had no way to have an opinion.</summary>
        public bool? Agreed { get; set; }
    }

    public sealed class FinalRecord
    {
        public int Schema { get; set; } = ObservationSchema.Version;
        public string Mechanics { get; set; } = ObservationSchema.MechanicsVersion;
        public string BattleId { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Seed { get; set; }
        public string Record { get; set; } = "final";
        public int Turns { get; set; }
        public string Outcome { get; set; } = "";       // Player1Wins | Player2Wins | Draw | Cancelled

        /// <summary>+1 P1 won, -1 P2 won, 0 draw/cancelled - the author's
        /// TrainingDataWriter outcome convention, P1 perspective; negate
        /// for P2-side examples.</summary>
        public int Player1Score { get; set; }

        public int DecisionRecords { get; set; }
        public int DroppedRecords { get; set; }
    }
}