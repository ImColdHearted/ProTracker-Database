using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Models;

namespace PokemonSim.Observation
{
    /// <summary>What kind of thing a side did on one turn, as a spectator
    /// would classify it.</summary>
    public enum ObservedActionKind
    {
        None = 0,
        Move = 1,
        Switch = 2,
        Replacement = 3,
    }

    /// <summary>
    /// §319. Everything ONE side has legitimately let the other see about
    /// one of its Pokemon.
    ///
    /// The rule this type exists to enforce: a field here may only be filled
    /// in by something that actually happened on the field. There is no
    /// constructor that takes a PokemonState and copies it, because that is
    /// the shape of the mistake - it is far too easy to write
    /// <c>Item = mon.HeldItemId</c> and never notice that nobody ever saw it.
    /// Everything is written by BattleKnowledge's own observation pass, from
    /// a BEFORE/AFTER diff of the visible battle.
    /// </summary>
    public sealed class ObservedPokemon
    {
        /// <summary>Has this Pokemon ever stood on the field? Until it has,
        /// the opponent does not know it exists.</summary>
        public bool Seen;

        public string Species = "";
        public int Level;
        public List<PokemonType> Types { get; set; } = new();

        public bool Fainted;
        public float HpFraction;
        public StatusCondition Status = StatusCondition.None;

        /// <summary>Currently standing on the field.</summary>
        public bool Active;

        /// <summary>The seven stat stages, in the engine's own order: Attack,
        /// Defense, Sp. Attack, Sp. Defense, Speed, accuracy, evasion.
        ///
        /// These are PUBLIC. Every stage change is announced out loud in a
        /// real battle - "its Attack rose!" - so a player watching knows them
        /// exactly. They live here rather than being read off the opposing
        /// PokemonState so that the encoder has ONE source for everything
        /// about the opponent and can be checked never to touch the other.
        /// </summary>
        public int[] Stages { get; set; } = new int[7];

        /// <summary>The eight volatiles the observation carries: confused,
        /// seeded, behind a substitute, taunted, encored, disabled, trapped,
        /// charging. Announced, like the stages.</summary>
        public bool[] Volatiles { get; set; } = new bool[8];

        /// <summary>It came in on this turn.</summary>
        public bool JustArrived;

        /// <summary>How many turns ago it was last on the field, or -1 if it
        /// never has been.</summary>
        public int TurnsSinceSeen = -1;

        /// <summary>The moves it has been SEEN to use, in the order it first
        /// used them. Never its moveset - only its history.</summary>
        public List<string> MovesSeen { get; set; } = new();

        /// <summary>How many times each seen move has been used.</summary>
        public Dictionary<string, int> MoveUses { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The last move it was seen to use.</summary>
        public string? LastMove;

        /// <summary>How many times in a row it has now used LastMove.</summary>
        public int LastMoveStreak;

        /// <summary>It was holding something and it is visibly gone - a berry
        /// eaten, a Balloon popped, a Sash spent. A player sees this; they do
        /// not necessarily learn WHAT it was, which is why this is a
        /// different fact from ItemName.</summary>
        public bool ItemSpent;

        /// <summary>The item's name, once the battle has actually named it.
        /// Null is the honest answer for "still holding something unknown"
        /// AND for "holding nothing" - which is why HasItemEvidence below is
        /// a separate question.</summary>
        public string? ItemName;

        /// <summary>The ability's name, once the battle has named it.</summary>
        public string? AbilityName;

        /// <summary>Types this Pokemon has been OBSERVED to take no damage
        /// from when a damaging move of that type hit it. This is how a
        /// player learns about Levitate: not by being told, but by watching
        /// an Earthquake do nothing. It is evidence, not the ability.</summary>
        public List<PokemonType> ObservedImmunities { get; set; } = new();

        /// <summary>The largest fraction of ITS OWN maximum it has been seen
        /// to lose to one hit, and the largest fraction of a target's maximum
        /// it has been seen to take off. Bulk and power, as evidence.</summary>
        public float WorstHitTaken;
        public float BestHitDealt;

        /// <summary>Times it has been seen to move before, and after, the
        /// observer's own Pokemon. Speed, as evidence rather than as a
        /// number nobody could know.</summary>
        public int TimesMovedFirst;
        public int TimesMovedSecond;

        /// <summary>Turns it has spent on the field, and how many of them it
        /// spent raising its own stats. Setup, as a count.</summary>
        public int TurnsOnField;
        public int TurnsBoosting;

        public ObservedPokemon Clone() => new()
        {
            Seen = Seen,
            Species = Species,
            Level = Level,
            Types = new List<PokemonType>(Types),
            Fainted = Fainted,
            HpFraction = HpFraction,
            Status = Status,
            Active = Active,
            Stages = (int[])Stages.Clone(),
            Volatiles = (bool[])Volatiles.Clone(),
            JustArrived = JustArrived,
            TurnsSinceSeen = TurnsSinceSeen,
            MovesSeen = new List<string>(MovesSeen),
            MoveUses = new Dictionary<string, int>(MoveUses, StringComparer.OrdinalIgnoreCase),
            LastMove = LastMove,
            LastMoveStreak = LastMoveStreak,
            ItemSpent = ItemSpent,
            ItemName = ItemName,
            AbilityName = AbilityName,
            ObservedImmunities = new List<PokemonType>(ObservedImmunities),
            WorstHitTaken = WorstHitTaken,
            BestHitDealt = BestHitDealt,
            TimesMovedFirst = TimesMovedFirst,
            TimesMovedSecond = TimesMovedSecond,
            TurnsOnField = TurnsOnField,
            TurnsBoosting = TurnsBoosting,
        };
    }

    /// <summary>
    /// §319. One side of the battle as the OTHER side can see it: six slots,
    /// of which only the ones that have been on the field carry anything.
    ///
    /// The slots are indexed as PlayerState.Team is, which is a deliberate
    /// and slightly uncomfortable choice: it means slot 4 is "their fourth
    /// Pokemon" even before anyone has seen it. Nothing readable is stored
    /// there until it is seen, so no information crosses - but the INDEX
    /// carries a little, in that a team of six always shows six slots. That
    /// is true of a real battle too: both players know a team is six.
    /// </summary>
    public sealed class SideKnowledge
    {
        public List<ObservedPokemon> Slots { get; set; } = new();

        /// <summary>How many of this side's Pokemon have ever been seen.</summary>
        public int SeenCount => Slots.Count(s => s.Seen);

        /// <summary>How many have been seen to faint. A faint is always
        /// visible, so this is knowable even for a Pokemon that was never
        /// otherwise interesting.</summary>
        public int FaintedCount => Slots.Count(s => s.Fainted);

        /// <summary>Slots that have never been on the field - the unknowns
        /// the model is supposed to reason about rather than be told.</summary>
        public int UnseenCount => Slots.Count(s => !s.Seen);

        // ---- behaviour, counted over the whole battle -------------------
        public int Switches;
        public int MovesUsed;
        public int AttacksUsed;
        public int StatusMovesUsed;
        public int BoostMovesUsed;
        public int ProtectsUsed;

        /// <summary>Turn number of the last switch this side made, or 0.</summary>
        public int LastSwitchTurn;

        public SideKnowledge Clone()
        {
            var copy = new SideKnowledge
            {
                Slots = Slots.Select(s => s.Clone()).ToList(),
                Switches = Switches,
                MovesUsed = MovesUsed,
                AttacksUsed = AttacksUsed,
                StatusMovesUsed = StatusMovesUsed,
                BoostMovesUsed = BoostMovesUsed,
                ProtectsUsed = ProtectsUsed,
                LastSwitchTurn = LastSwitchTurn,
            };

            return copy;
        }
    }

    /// <summary>
    /// §319. One turn, as both players saw it. This is the row the history
    /// window is made of, and it is deliberately SYMMETRIC - stored from no
    /// perspective at all - because the encoder writes it from the acting
    /// side's, and a row that had already picked a side could only be read
    /// one way.
    ///
    /// Nothing in here is hidden information. An action is recorded once it
    /// has resolved, which is the moment both players have seen it.
    /// </summary>
    public sealed class TurnEvent
    {
        public int Turn;

        public string P1Species = "";
        public string P2Species = "";

        public ObservedActionKind P1Kind;
        public ObservedActionKind P2Kind;

        public string? P1Move;
        public string? P2Move;

        /// <summary>Fraction of the TARGET's maximum HP removed. Percentages
        /// rather than points, so a Blissey and a Deoxys are on one scale.</summary>
        public float P1DamageDealt;
        public float P2DamageDealt;

        public float P1HpAfter;
        public float P2HpAfter;

        public bool P1Fainted;
        public bool P2Fainted;

        public StatusCondition P1Status;
        public StatusCondition P2Status;

        /// <summary>Sum of the seven stat stages, so a turn spent setting up
        /// is visible as a jump rather than having to carry seven columns
        /// per side per turn of history.</summary>
        public int P1StageSum;
        public int P2StageSum;

        public WeatherType Weather;
        public TerrainType Terrain;

        public bool WeatherChanged;
        public bool TerrainChanged;
        public bool HazardsChanged;

        public TurnEvent Clone() => (TurnEvent)MemberwiseClone();
    }

    /// <summary>
    /// §319. THE VISIBILITY BOUNDARY.
    ///
    /// Before this section the observation encoder read BattleState directly,
    /// and so it encoded the opponent's exact computed stats, its ability and
    /// its held item - none of which a player can know - along with an
    /// effectiveness, a damage estimate and a "nullified" flag all three of
    /// which are computed FROM those hidden things. Fifty-four of §311's four
    /// hundred and eighty-three features carried information no human could
    /// have. Every model trained here learned to play with the opponent's
    /// sheet face up.
    ///
    /// The fix is not a checklist of features to stop writing. It is this
    /// object. The engine maintains it; the encoder reads it INSTEAD of the
    /// battle; and a fact that nobody observed has nowhere to live. A leak
    /// stops being something to remember not to write and becomes something
    /// that cannot be expressed.
    ///
    /// HOW IT LEARNS THINGS. By DIFFING the visible battle before and after
    /// each turn, not by being told. That is not a shortcut - it is the
    /// definition. A spectator sees hit points move, a Pokemon come in, a
    /// move announced, a berry vanish, a Ground move do nothing. A spectator
    /// does NOT see an ability that quietly changed a damage roll, and
    /// neither does this, which is exactly right.
    ///
    /// WHAT IT IS NOT. It is not a place for the engine to deposit answers.
    /// Nothing in this file reads AbilityId, HeldItemId, Stats, IVs, EVs,
    /// Nature, or a MoveState the Pokemon has not used - and a static check
    /// in the §319 battery fails the build if that changes.
    /// </summary>
    public sealed class BattleKnowledge
    {
        /// <summary>§319: sixteen, chosen with the user. Long enough to hold
        /// a Protect / switch / attack / switch-back pattern several times
        /// over; short enough that the flat network the trainer builds can
        /// still see the whole window as input.</summary>
        public const int HistoryTurns = 16;

        /// <summary>What Player 2 can see about Player 1.</summary>
        public SideKnowledge OfPlayer1 { get; set; } = new();

        /// <summary>What Player 1 can see about Player 2.</summary>
        public SideKnowledge OfPlayer2 { get; set; } = new();

        /// <summary>The most recent turns, oldest first, capped at
        /// HistoryTurns.</summary>
        public List<TurnEvent> History { get; set; } = new();

        /// <summary>The last snapshot taken, so the next one can be diffed
        /// against it. Not observation - bookkeeping.</summary>
        private Snapshot? previous;

        /// <summary>
        /// The move each side was ANNOUNCED to use this turn, or null if it
        /// did not get to act. Cleared at the start of every turn.
        ///
        /// §319: this is the one thing the diff cannot answer and the engine
        /// has to say out loud. PokemonState.LastMoveName persists across
        /// turns, so a Pokemon that was asleep, flinched or fully paralysed
        /// still carries the name of the move it used three turns ago - and a
        /// diff that read it would record a move nobody used, inflating every
        /// streak and use count built on top of it. It would also be unable
        /// to tell a genuine repeat from no action at all, because in both
        /// cases the name does not change.
        ///
        /// Announcing it is not a leak. "Pikachu used Thunderbolt!" is said
        /// out loud in every battle ever played; it is the most public thing
        /// that happens on a turn.
        /// </summary>
        private string? announcedP1;
        private string? announcedP2;

        public SideKnowledge For(BattleState state, PlayerState side) =>
            ReferenceEquals(side, state.Player1) ? OfPlayer1 : OfPlayer2;

        /// <summary>What the given side can see about the other one.</summary>
        public SideKnowledge Against(BattleState state, PlayerState side) =>
            ReferenceEquals(side, state.Player1) ? OfPlayer2 : OfPlayer1;

        public BattleKnowledge Clone()
        {
            var copy = new BattleKnowledge
            {
                OfPlayer1 = OfPlayer1.Clone(),
                OfPlayer2 = OfPlayer2.Clone(),
                History = History.Select(h => h.Clone()).ToList(),
            };

            copy.previous = previous?.Clone();
            copy.announcedP1 = announcedP1;
            copy.announcedP2 = announcedP2;

            return copy;
        }

        /// <summary>
        /// Called by the engine when a move actually comes out. The only
        /// thing in this file the engine TELLS it rather than it working out
        /// by looking - and the only thing it could not have worked out.
        /// </summary>
        public void NoteMoveUsed(BattleState state, PokemonState user, string? moveName)
        {
            if (string.IsNullOrWhiteSpace(moveName))
                return;

            if (state.Player1.Active.Contains(user))
                announcedP1 = moveName;
            else if (state.Player2.Active.Contains(user))
                announcedP2 = moveName;
        }

        // =============================================== the observation pass

        /// <summary>
        /// Called by the engine at the START of a turn, before anything
        /// resolves. Takes the picture the diff at the end of the turn will
        /// be measured against, and makes sure every Pokemon standing on the
        /// field counts as seen - a Pokemon on the field is visible whether
        /// or not it ever does anything.
        /// </summary>
        public void BeginTurn(BattleState state)
        {
            EnsureSlots(state);

            // "It came in" is a fact about the turn just gone, so it is
            // cleared at the start of the next one rather than carried.
            foreach (ObservedPokemon slot in OfPlayer1.Slots.Concat(OfPlayer2.Slots))
                slot.JustArrived = false;

            announcedP1 = null;
            announcedP2 = null;

            NoteActives(state);

            previous = Snapshot.Of(state);
        }

        /// <summary>
        /// Called by the engine at the END of a turn, after everything has
        /// resolved. Everything this writes is a difference between two
        /// pictures of the field.
        /// </summary>
        public void EndTurn(BattleState state)
        {
            EnsureSlots(state);

            Snapshot now = Snapshot.Of(state);
            Snapshot before = previous ?? now;

            var row = new TurnEvent
            {
                Turn = state.TurnNumber,
                P1Species = now.P1.Species,
                P2Species = now.P2.Species,
                P1HpAfter = now.P1.HpFraction,
                P2HpAfter = now.P2.HpFraction,
                P1Fainted = now.P1.Fainted,
                P2Fainted = now.P2.Fainted,
                P1Status = now.P1.Status,
                P2Status = now.P2.Status,
                P1StageSum = now.P1.StageSum,
                P2StageSum = now.P2.StageSum,
                Weather = now.Weather,
                Terrain = now.Terrain,
                WeatherChanged = now.Weather != before.Weather,
                TerrainChanged = now.Terrain != before.Terrain,
                HazardsChanged = now.HazardMask != before.HazardMask,
            };

            Record(state, state.Player1, OfPlayer1, before.P1, now.P1, before.P2, now.P2, row, first: true);
            Record(state, state.Player2, OfPlayer2, before.P2, now.P2, before.P1, now.P1, row, first: false);

            History.Add(row);

            while (History.Count > HistoryTurns)
                History.RemoveAt(0);

            NoteActives(state);

            previous = now;
        }

        /// <summary>One side's half of the turn, written from the difference
        /// between its before and after.</summary>
        private void Record(
            BattleState state,
            PlayerState side,
            SideKnowledge known,
            Side before,
            Side after,
            Side themBefore,
            Side themAfter,
            TurnEvent row,
            bool first)
        {
            bool switched = !string.Equals(before.Species, after.Species, StringComparison.Ordinal)
                            && before.Species.Length > 0;

            // What the engine announced this turn, and nothing else.
            string? move = switched ? null : (first ? announcedP1 : announcedP2);

            ObservedActionKind kind = switched
                ? (before.Fainted ? ObservedActionKind.Replacement : ObservedActionKind.Switch)
                : move != null ? ObservedActionKind.Move : ObservedActionKind.None;

            // Damage is measured on the OTHER side's bar, as a fraction of
            // its own maximum.
            float dealt = Math.Max(0f, themBefore.HpFraction - themAfter.HpFraction);

            if (first)
            {
                row.P1Kind = kind;
                row.P1Move = move;
                row.P1DamageDealt = dealt;
            }
            else
            {
                row.P2Kind = kind;
                row.P2Move = move;
                row.P2DamageDealt = dealt;
            }

            if (kind == ObservedActionKind.Switch || kind == ObservedActionKind.Replacement)
            {
                if (kind == ObservedActionKind.Switch)
                {
                    known.Switches++;
                    known.LastSwitchTurn = state.TurnNumber;
                }

                foreach (ObservedPokemon other in known.Slots)
                    other.JustArrived = false;

                SlotFor(known, side, after.Index).JustArrived = true;
            }

            ObservedPokemon slot = SlotFor(known, side, after.Index);

            slot.TurnsOnField++;

            if (after.StageSum > before.StageSum && !switched)
            {
                slot.TurnsBoosting++;
                known.BoostMovesUsed++;
            }

            if (move != null)
            {
                known.MovesUsed++;

                if (dealt > 0f)
                    known.AttacksUsed++;
                else
                    known.StatusMovesUsed++;

                if (after.Protected)
                    known.ProtectsUsed++;

                if (!slot.MovesSeen.Contains(move, StringComparer.OrdinalIgnoreCase))
                    slot.MovesSeen.Add(move);

                slot.MoveUses[move] = slot.MoveUses.TryGetValue(move, out int n) ? n + 1 : 1;

                slot.LastMoveStreak = string.Equals(slot.LastMove, move, StringComparison.OrdinalIgnoreCase)
                    ? slot.LastMoveStreak + 1
                    : 1;

                slot.LastMove = move;

                if (dealt > slot.BestHitDealt)
                    slot.BestHitDealt = dealt;
            }

            float taken = Math.Max(0f, before.HpFraction - after.HpFraction);

            if (taken > slot.WorstHitTaken && !switched)
                slot.WorstHitTaken = taken;

            // A held item that visibly went away. What it WAS stays unknown
            // unless the battle named it; that it is gone is plain to see.
            if (before.HasItem && !after.HasItem && !switched)
                slot.ItemSpent = true;
        }

        /// <summary>Marks everything standing on the field as seen, and
        /// refreshes the visible facts about it. A Pokemon on the field is
        /// visible: its species, its level, its types, its bar, its status.
        /// </summary>
        private void NoteActives(BattleState state)
        {
            Note(state, state.Player1, OfPlayer1);
            Note(state, state.Player2, OfPlayer2);

            void Note(BattleState s, PlayerState side, SideKnowledge known)
            {
                for (int i = 0; i < known.Slots.Count && i < side.Team.Count; i++)
                {
                    ObservedPokemon slot = known.Slots[i];
                    PokemonState mon = side.Team[i];

                    bool active = side.Active.Contains(mon);

                    slot.Active = active;

                    // A faint is visible wherever it happens.
                    if (mon.Fainted && slot.Seen)
                        slot.Fainted = true;

                    if (!active)
                    {
                        if (slot.Seen && slot.TurnsSinceSeen >= 0)
                            slot.TurnsSinceSeen = Math.Min(slot.TurnsSinceSeen + 1, 99);

                        continue;
                    }

                    slot.Seen = true;
                    slot.TurnsSinceSeen = 0;
                    slot.Species = mon.Species;
                    slot.Level = mon.Level;
                    slot.Types = new List<PokemonType>(mon.Types);
                    slot.Fainted = mon.Fainted;
                    slot.HpFraction = Fraction(mon.CurrentHP, mon.MaxHP);
                    slot.Status = mon.Status;

                    // Announced out loud in a real battle, so knowable.
                    slot.Stages[0] = mon.AttackStage;
                    slot.Stages[1] = mon.DefenseStage;
                    slot.Stages[2] = mon.SpAttackStage;
                    slot.Stages[3] = mon.SpDefenseStage;
                    slot.Stages[4] = mon.SpeedStage;
                    slot.Stages[5] = mon.AccuracyStage;
                    slot.Stages[6] = mon.EvasionStage;

                    slot.Volatiles[0] = mon.ConfusionTurns > 0;
                    slot.Volatiles[1] = mon.LeechSeeded;
                    slot.Volatiles[2] = mon.SubstituteHP > 0;
                    slot.Volatiles[3] = mon.TauntTurns > 0;
                    slot.Volatiles[4] = mon.EncoreTurns > 0;
                    slot.Volatiles[5] = mon.DisabledTurns > 0;
                    slot.Volatiles[6] = mon.Trap != null;
                    slot.Volatiles[7] = mon.Charging;
                }
            }
        }

        private static ObservedPokemon SlotFor(SideKnowledge known, PlayerState side, int index)
        {
            if (index >= 0 && index < known.Slots.Count)
                return known.Slots[index];

            return known.Slots.Count > 0 ? known.Slots[0] : new ObservedPokemon();
        }

        private void EnsureSlots(BattleState state)
        {
            Fill(OfPlayer1, state.Player1.Team.Count);
            Fill(OfPlayer2, state.Player2.Team.Count);

            static void Fill(SideKnowledge known, int count)
            {
                while (known.Slots.Count < count)
                    known.Slots.Add(new ObservedPokemon());
            }
        }

        internal static float Fraction(int part, int whole) =>
            whole <= 0 ? 0f : Math.Clamp(part / (float)whole, 0f, 1f);

        // ============================================== the snapshot, private

        /// <summary>One side's visible facts at an instant. Everything here
        /// is something a spectator can read off the screen.</summary>
        private sealed class Side
        {
            public int Index;
            public string Species = "";
            public float HpFraction;
            public bool Fainted;
            public StatusCondition Status;
            public int StageSum;
            public bool HasItem;
            public bool Protected;

            public Side Clone() => (Side)MemberwiseClone();
        }

        /// <summary>
        /// The whole field at an instant.
        ///
        /// HasItem is the one line in this file that touches HeldItemId, and
        /// it takes only whether the string is empty - never the string. A
        /// player watching sees a berry get eaten; they do not see which
        /// berry it was unless the battle says so.
        /// </summary>
        private sealed class Snapshot
        {
            public Side P1 = new();
            public Side P2 = new();
            public WeatherType Weather;
            public TerrainType Terrain;
            public int HazardMask;

            public static Snapshot Of(BattleState state)
            {
                return new Snapshot
                {
                    P1 = SideOf(state.Player1),
                    P2 = SideOf(state.Player2),
                    Weather = state.Environment.Weather,
                    Terrain = state.Environment.Terrain,
                    HazardMask = Hazards(state),
                };
            }

            private static Side SideOf(PlayerState side)
            {
                PokemonState mon = side.ActivePokemon;

                return new Side
                {
                    Index = side.Team.IndexOf(mon),
                    Species = mon.Species,
                    HpFraction = Fraction(mon.CurrentHP, mon.MaxHP),
                    Fainted = mon.Fainted,
                    Status = mon.Status,
                    StageSum = mon.AttackStage + mon.DefenseStage + mon.SpAttackStage
                               + mon.SpDefenseStage + mon.SpeedStage
                               + mon.AccuracyStage + mon.EvasionStage,
                    HasItem = !string.IsNullOrWhiteSpace(mon.HeldItemId),
                    Protected = mon.Protected,
                };
            }

            private static int Hazards(BattleState s) =>
                (s.StealthRockP1 ? 1 : 0) | (s.StealthRockP2 ? 2 : 0)
                | (s.SpikesP1 << 2) | (s.SpikesP2 << 5)
                | (s.ToxicSpikesP1 << 8) | (s.ToxicSpikesP2 << 10)
                | (s.StickyWebP1 ? 4096 : 0) | (s.StickyWebP2 ? 8192 : 0);

            public Snapshot Clone() => new()
            {
                P1 = P1.Clone(),
                P2 = P2.Clone(),
                Weather = Weather,
                Terrain = Terrain,
                HazardMask = HazardMask,
            };
        }
    }
}
