using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Models;

namespace PokemonSim.Observation
{
    /// <summary>
    /// §319. Writes the version 8 vector - the fair one.
    ///
    /// THE RULE THIS CLASS EXISTS TO KEEP: everything about the opponent
    /// comes from BattleKnowledge, and nothing comes from the opposing
    /// PlayerState. The acting side's own half is read straight off the
    /// battle, because a player does know their own team.
    ///
    /// The battery enforces that literally: this file may not name
    /// GetOpponentOf, and the only PlayerState it may take is the actor's.
    /// That is a stronger guarantee than reviewing each feature, because it
    /// survives the next person adding one.
    ///
    /// ESTIMATES ARE MARKED. Three columns per move slot depend on the
    /// opponent, and under §311 all three were answered out of its hidden
    /// ability and item. Here they are answered from what is visible - its
    /// types - and from what has been watched: whether it has been SEEN to
    /// take nothing from that type. They live in their own block so nobody
    /// has to take my word for which columns those are.
    /// </summary>
    public static class FairEncoder
    {
        private static readonly FeatureMap Map = ObservationV8.Map;

        private static readonly int SelfAt = Map.Start(ObservationV8.Self);
        private static readonly int SelfTypesAt = Map.Start(ObservationV8.SelfTypes);
        private static readonly int SelfStatsAt = Map.Start(ObservationV8.SelfStats);
        private static readonly int SelfAbilityAt = Map.Start(ObservationV8.SelfAbility);
        private static readonly int SelfItemAt = Map.Start(ObservationV8.SelfItem);
        private static readonly int SelfMovesAt = Map.Start(ObservationV8.SelfMoves);
        private static readonly int SelfMoveEstAt = Map.Start(ObservationV8.SelfMoveEstimates);
        private static readonly int OppAt = Map.Start(ObservationV8.Opponent);
        private static readonly int OppTypesAt = Map.Start(ObservationV8.OpponentTypes);
        private static readonly int OppEvidenceAt = Map.Start(ObservationV8.OpponentEvidence);
        private static readonly int OppMovesAt = Map.Start(ObservationV8.OpponentMoves);
        private static readonly int BeliefAt = Map.Start(ObservationV8.Belief);
        private static readonly int SelfTeamAt = Map.Start(ObservationV8.SelfTeam);
        private static readonly int OppTeamAt = Map.Start(ObservationV8.OpponentTeam);
        private static readonly int HazardsAt = Map.Start(ObservationV8.Hazards);
        private static readonly int FieldAt = Map.Start(ObservationV8.Field);
        private static readonly int HistoryAt = Map.Start(ObservationV8.History);
        private static readonly int BehaviourAt = Map.Start(ObservationV8.Behaviour);
        private static readonly int RiskAt = Map.Start(ObservationV8.Risk);

        private static readonly int SelfMoveStride = Map.Size(ObservationV8.SelfMoves) / ObservationV8.MoveSlots;
        private static readonly int SelfMoveEstStride = Map.Size(ObservationV8.SelfMoveEstimates) / ObservationV8.MoveSlots;
        private static readonly int OppMoveStride = Map.Size(ObservationV8.OpponentMoves) / ObservationV8.MoveSlots;
        private static readonly int SelfTeamStride = Map.Size(ObservationV8.SelfTeam) / ObservationV8.TeamSlots;
        private static readonly int OppTeamStride = Map.Size(ObservationV8.OpponentTeam) / ObservationV8.TeamSlots;
        private static readonly int HistoryStride = Map.Size(ObservationV8.History) / ObservationV8.HistoryTurns;

        public static int FeatureCount => Map.Count;

        /// <summary>
        /// The state the acting side can see. <paramref name="legalActions"/>
        /// is what the engine offered; null (a forced replacement) leaves
        /// every slot's legality at zero.
        /// </summary>
        public static float[] Encode(
            BattleState state,
            PlayerState actor,
            IReadOnlyList<BattleAction>? legalActions = null,
            float risk = ObservationSchema.NeutralRisk)
        {
            var f = new float[Map.Count];

            PokemonState self = actor.ActivePokemon;
            bool actorIsP1 = ReferenceEquals(actor, state.Player1);

            // Everything about them, and only this.
            SideKnowledge them = state.Knowledge.Against(state, actor);
            SideKnowledge us = state.Knowledge.For(state, actor);

            ObservedPokemon theirActive = them.Slots.FirstOrDefault(s => s.Active) ?? new ObservedPokemon();

            WriteSelf(f, SelfAt, self, state);
            WriteTypes(f, SelfTypesAt, self.Types);
            WriteStats(f, SelfStatsAt, self);
            ObserverEncoder.WriteAbilityShape(f, SelfAbilityAt, self);
            ObserverEncoder.WriteItemShape(f, SelfItemAt, self);

            float[] legality = legalActions == null
                ? new float[ObservationV8.MoveSlots]
                : ObserverEncoder.LegalMoveMask(legalActions);

            WriteOwnMoves(f, self, legality, theirActive, them);

            WriteOpponent(f, OppAt, theirActive);
            WriteTypes(f, OppTypesAt, theirActive.Types);
            WriteEvidence(f, OppEvidenceAt, theirActive);
            WriteKnownMoves(f, OppMovesAt, theirActive, state.TurnNumber);
            WriteBelief(f, BeliefAt, them, theirActive, state.TurnNumber);

            WriteOwnTeam(f, SelfTeamAt, actor, self);
            WriteTheirTeam(f, OppTeamAt, them);

            WriteHazards(f, HazardsAt, state, actorIsP1);
            WriteField(f, FieldAt, state, actor, actorIsP1);

            WriteHistory(f, HistoryAt, state, actorIsP1);
            WriteBehaviour(f, BehaviourAt, state, them, us, theirActive, actorIsP1);

            f[RiskAt] = Math.Clamp(risk, 0f, 1f);

            return Finite(f);
        }

        // ============================================================= self

        private static void WriteSelf(float[] f, int at, PokemonState mon, BattleState state)
        {
            f[at] = Fraction(mon.CurrentHP, mon.MaxHP);
            f[at + 1] = mon.Level / 100f;
            f[at + 2] = Grounding.IsGrounded(state, mon) ? 1f : 0f;

            f[at + 3] = Stage(mon.AttackStage);
            f[at + 4] = Stage(mon.DefenseStage);
            f[at + 5] = Stage(mon.SpAttackStage);
            f[at + 6] = Stage(mon.SpDefenseStage);
            f[at + 7] = Stage(mon.SpeedStage);
            f[at + 8] = Stage(mon.AccuracyStage);
            f[at + 9] = Stage(mon.EvasionStage);

            for (int i = 0; i < ObserverEncoder.StatusFlags.Length; i++)
                f[at + 10 + i] = mon.Status == ObserverEncoder.StatusFlags[i] ? 1f : 0f;

            f[at + 16] = mon.ConfusionTurns > 0 ? 1f : 0f;
            f[at + 17] = mon.LeechSeeded ? 1f : 0f;
            f[at + 18] = mon.SubstituteHP > 0 ? 1f : 0f;
            f[at + 19] = mon.TauntTurns > 0 ? 1f : 0f;
            f[at + 20] = mon.EncoreTurns > 0 ? 1f : 0f;
            f[at + 21] = mon.DisabledTurns > 0 ? 1f : 0f;
            f[at + 22] = mon.Trap != null ? 1f : 0f;
            f[at + 23] = mon.Charging ? 1f : 0f;
        }

        private static void WriteStats(float[] f, int at, PokemonState mon)
        {
            f[at] = Math.Clamp(mon.Stats.HP / 714f, 0f, 1f);
            f[at + 1] = Math.Clamp(mon.Stats.Attack / 600f, 0f, 1f);
            f[at + 2] = Math.Clamp(mon.Stats.Defense / 600f, 0f, 1f);
            f[at + 3] = Math.Clamp(mon.Stats.SpAttack / 600f, 0f, 1f);
            f[at + 4] = Math.Clamp(mon.Stats.SpDefense / 600f, 0f, 1f);
            f[at + 5] = Math.Clamp(mon.Stats.Speed / 600f, 0f, 1f);
        }

        /// <summary>
        /// The acting side's four moves, plus the three estimate columns.
        ///
        /// §319: the estimates are the delicate part. §311 answered them with
        /// Effectiveness() and DamageFraction(), both of which read the
        /// target's ability, item and defence stat. Here effectiveness comes
        /// off the type chart against the opponent's VISIBLE types - a
        /// species on the field is public, so that much is fair - and the
        /// ability correction is replaced by the one thing a player would
        /// have: whether this Pokemon has been SEEN to take nothing from that
        /// type. There is no damage estimate, because there is no honest one;
        /// what stands in its place is the hardest hit we have actually been
        /// seen to land on it.
        /// </summary>
        private static void WriteOwnMoves(
            float[] f, PokemonState self, float[] legality,
            ObservedPokemon theirActive, SideKnowledge them)
        {
            var slots = ObserverEncoder.SlotMoves(self);

            for (int slot = 0; slot < ObservationV8.MoveSlots; slot++)
            {
                int at = SelfMovesAt + slot * SelfMoveStride;
                int est = SelfMoveEstAt + slot * SelfMoveEstStride;

                f[at] = legality[slot];

                MoveState? move = slots[slot];

                if (move == null)
                    continue;

                f[at + 1] = Math.Min(move.Power, 225) / 150f;
                f[at + 2] = move.Accuracy <= 0 ? 1f : Math.Min(move.Accuracy, 100) / 100f;
                f[at + 3] = move.MaxPP <= 0 ? 1f : Math.Clamp(move.CurrentPP / (float)move.MaxPP, 0f, 1f);
                f[at + 4] = Math.Clamp(move.Priority / 5f, -1f, 1f);
                f[at + 5] = move.Category == MoveCategory.Physical ? 1f : 0f;
                f[at + 6] = move.Category == MoveCategory.Special ? 1f : 0f;
                f[at + 7] = move.Category == MoveCategory.Status ? 1f : 0f;
                f[at + 8] = !move.Typeless && self.Types.Contains(move.Type) ? 1f : 0f;

                if (!move.Typeless)
                    WriteTypes(f, at + 9, new[] { move.Type });

                f[at + 27] = ObserverEncoder.HasEffect(move, "Heal") || ObserverEncoder.HasEffect(move, "Drain") ? 1f : 0f;
                f[at + 28] = ObserverEncoder.HasStatChange(move, "self", raising: true) ? 1f : 0f;
                f[at + 29] = ObserverEncoder.HasStatChange(move, "opponent", raising: false) ? 1f : 0f;
                f[at + 30] = ObserverEncoder.StatusChance(move);
                f[at + 31] = (float)Math.Clamp(Math.Max(move.FlinchChance, move.SecondaryChance), 0.0, 1.0);
                f[at + 32] = Math.Clamp((move.MinHits + move.MaxHits) / 2f, 1f, 5f) / 5f;
                f[at + 33] = ObserverEncoder.HasEffect(move, "Protect") || ObserverEncoder.HasEffect(move, "Endure") ? 1f : 0f;

                bool damaging = move.Category != MoveCategory.Status && move.Power > 0;

                if (!damaging || move.Typeless || theirActive.Types.Count == 0)
                    continue;

                // The type chart against what is on the field. Nothing else.
                double chart = theirActive.Types
                    .Aggregate(1.0, (running, t) => running * TypeChart.GetMultiplier(move.Type, t));

                f[est] = (float)Math.Clamp(chart / 4.0, 0.0, 1.0);
                f[est + 1] = theirActive.ObservedImmunities.Contains(move.Type) ? 1f : 0f;
                f[est + 2] = BestHitSeen(them, move.Type);
            }
        }

        /// <summary>The hardest hit this side has been seen to land, as the
        /// honest stand-in for a damage roll it cannot compute.</summary>
        private static float BestHitSeen(SideKnowledge them, PokemonType type)
        {
            ObservedPokemon? active = them.Slots.FirstOrDefault(s => s.Active);

            return active?.WorstHitTaken ?? 0f;
        }

        // ========================================================= opponent

        private static void WriteOpponent(float[] f, int at, ObservedPokemon them)
        {
            if (!them.Seen)
                return;

            f[at] = them.HpFraction;
            f[at + 1] = them.Level / 100f;

            for (int i = 0; i < 7 && i < them.Stages.Length; i++)
                f[at + 2 + i] = Stage(them.Stages[i]);

            for (int i = 0; i < ObserverEncoder.StatusFlags.Length; i++)
                f[at + 9 + i] = them.Status == ObserverEncoder.StatusFlags[i] ? 1f : 0f;

            for (int i = 0; i < ObservationV8.VolatileSlots && i < them.Volatiles.Length; i++)
                f[at + 15 + i] = them.Volatiles[i] ? 1f : 0f;

            f[at + 23] = Math.Clamp(them.TurnsOnField / 16f, 0f, 1f);
            f[at + 24] = 1f;
            f[at + 25] = them.JustArrived ? 1f : 0f;
        }

        private static void WriteEvidence(float[] f, int at, ObservedPokemon them)
        {
            f[at] = them.WorstHitTaken;
            f[at + 1] = them.BestHitDealt;

            int turns = them.TimesMovedFirst + them.TimesMovedSecond;

            f[at + 2] = turns > 0 ? them.TimesMovedFirst / (float)turns : 0f;
            f[at + 3] = turns > 0 ? 1f : 0f;

            f[at + 4] = them.ItemSpent ? 1f : 0f;
            f[at + 5] = them.ItemName != null ? 1f : 0f;
            f[at + 6] = them.AbilityName != null ? 1f : 0f;
            f[at + 7] = Math.Clamp(them.MovesSeen.Count / 4f, 0f, 1f);
            f[at + 8] = them.TurnsOnField > 0
                ? Math.Clamp(them.TurnsBoosting / (float)them.TurnsOnField, 0f, 1f)
                : 0f;

            WriteTypes(f, at + 9, them.ObservedImmunities);
        }

        /// <summary>
        /// The opponent's four move slots - the ones it has REVEALED, and an
        /// explicit unknown for the rest.
        ///
        /// A revealed move is looked up in the move dex by the name it was
        /// seen to use, which is fair: the move was announced, and what
        /// Earthquake does is public knowledge. A slot nobody has seen sets
        /// its unknown column and leaves everything else at zero, so "not
        /// revealed" and "revealed, base power zero" are different rows.
        /// </summary>
        private static void WriteKnownMoves(float[] f, int at, ObservedPokemon them, int turn)
        {
            for (int slot = 0; slot < ObservationV8.MoveSlots; slot++)
            {
                int k = at + slot * OppMoveStride;

                if (slot >= them.MovesSeen.Count)
                {
                    f[k + 1] = 1f;                  // UNKNOWN, as a channel
                    continue;
                }

                string name = them.MovesSeen[slot];

                f[k] = 1f;

                MoveState? move = ObserverEncoder.LookUpMove(name);

                if (move != null)
                {
                    f[k + 2] = Math.Min(move.Power, 225) / 150f;
                    f[k + 3] = move.Accuracy <= 0 ? 1f : Math.Min(move.Accuracy, 100) / 100f;
                    f[k + 4] = Math.Clamp(move.Priority / 5f, -1f, 1f);
                    f[k + 5] = move.Category == MoveCategory.Physical ? 1f : 0f;
                    f[k + 6] = move.Category == MoveCategory.Special ? 1f : 0f;
                    f[k + 7] = move.Category == MoveCategory.Status ? 1f : 0f;

                    if (!move.Typeless)
                        WriteTypes(f, k + 8, new[] { move.Type });
                }

                int uses = them.MoveUses.TryGetValue(name, out int n) ? n : 0;

                f[k + 26] = Math.Clamp(uses / 8f, 0f, 1f);
                f[k + 27] = string.Equals(them.LastMove, name, StringComparison.OrdinalIgnoreCase) ? 1f : 0f;
            }
        }

        private static void WriteBelief(float[] f, int at, SideKnowledge them,
                                        ObservedPokemon active, int turn)
        {
            int revealed = Math.Min(active.MovesSeen.Count, ObservationV8.MoveSlots);

            f[at] = revealed / (float)ObservationV8.MoveSlots;
            f[at + 1] = (ObservationV8.MoveSlots - revealed) / (float)ObservationV8.MoveSlots;

            f[at + 2] = active.BestHitDealt > 0f ? 1f : 0f;
            f[at + 3] = them.StatusMovesUsed > 0 ? 1f : 0f;
            f[at + 4] = active.TurnsBoosting > 0 ? 1f : 0f;
            f[at + 5] = them.ProtectsUsed > 0 ? 1f : 0f;
            f[at + 6] = them.Switches > 0 ? 1f : 0f;

            f[at + 7] = turn > 0 ? Math.Clamp(them.Switches / (float)turn, 0f, 1f) : 0f;
            f[at + 8] = them.MovesUsed > 0
                ? Math.Clamp((active.LastMoveStreak - 1) / (float)them.MovesUsed, 0f, 1f)
                : 0f;
            f[at + 9] = Math.Clamp(active.LastMoveStreak / 8f, 0f, 1f);
            f[at + 10] = them.LastSwitchTurn > 0
                ? Math.Clamp((turn - them.LastSwitchTurn) / 16f, 0f, 1f)
                : 1f;
            f[at + 11] = Math.Clamp(them.SeenCount / (float)ObservationV8.TeamSlots, 0f, 1f);
        }

        // ============================================================ teams

        private static void WriteOwnTeam(float[] f, int at, PlayerState actor, PokemonState self)
        {
            for (int i = 0; i < ObservationV8.TeamSlots; i++)
            {
                int k = at + i * SelfTeamStride;

                if (i >= actor.Team.Count)
                    continue;

                PokemonState member = actor.Team[i];

                f[k] = 1f;
                f[k + 1] = member.Fainted ? 0f : 1f;
                f[k + 2] = Fraction(Math.Max(0, member.CurrentHP), member.MaxHP);
                f[k + 3] = ReferenceEquals(member, self) ? 1f : 0f;

                WriteTypes(f, k + 4, member.Types);
            }
        }

        /// <summary>
        /// Their six positions, as far as they have shown them.
        ///
        /// A position that exists is public - both players know a team is
        /// six - and everything else about it stays blank with its "unseen"
        /// column set until it has stood on the field. That column is the
        /// one that lets a network reason "three shown, two down, one still
        /// to come" instead of being handed the sixth Pokemon's name.
        /// </summary>
        private static void WriteTheirTeam(float[] f, int at, SideKnowledge them)
        {
            for (int i = 0; i < ObservationV8.TeamSlots; i++)
            {
                int k = at + i * OppTeamStride;

                if (i >= them.Slots.Count)
                    continue;

                ObservedPokemon slot = them.Slots[i];

                f[k] = 1f;

                if (!slot.Seen)
                {
                    f[k + 2] = 1f;                  // UNSEEN, as a channel
                    continue;
                }

                f[k + 1] = 1f;
                f[k + 3] = slot.Fainted ? 1f : 0f;
                f[k + 4] = slot.HpFraction;
                f[k + 5] = slot.Active ? 1f : 0f;
                f[k + 6] = slot.TurnsSinceSeen < 0 ? 1f : Math.Clamp(slot.TurnsSinceSeen / 16f, 0f, 1f);
                f[k + 7] = Math.Clamp(slot.MovesSeen.Count / 4f, 0f, 1f);

                WriteTypes(f, k + 8, slot.Types);
            }
        }

        // ============================================================ field

        private static void WriteHazards(float[] f, int at, BattleState s, bool actorIsP1)
        {
            f[at] = (actorIsP1 ? s.StealthRockP1 : s.StealthRockP2) ? 1f : 0f;
            f[at + 1] = Math.Clamp((actorIsP1 ? s.SpikesP1 : s.SpikesP2) / 3f, 0f, 1f);
            f[at + 2] = Math.Clamp((actorIsP1 ? s.ToxicSpikesP1 : s.ToxicSpikesP2) / 2f, 0f, 1f);
            f[at + 3] = (actorIsP1 ? s.StickyWebP1 : s.StickyWebP2) ? 1f : 0f;

            f[at + 4] = (actorIsP1 ? s.StealthRockP2 : s.StealthRockP1) ? 1f : 0f;
            f[at + 5] = Math.Clamp((actorIsP1 ? s.SpikesP2 : s.SpikesP1) / 3f, 0f, 1f);
            f[at + 6] = Math.Clamp((actorIsP1 ? s.ToxicSpikesP2 : s.ToxicSpikesP1) / 2f, 0f, 1f);
            f[at + 7] = (actorIsP1 ? s.StickyWebP2 : s.StickyWebP1) ? 1f : 0f;
        }

        private static void WriteField(float[] f, int at, BattleState s, PlayerState actor, bool actorIsP1)
        {
            f[at] = Turns(actorIsP1 ? s.ReflectTurnsP1 : s.ReflectTurnsP2);
            f[at + 1] = Turns(actorIsP1 ? s.LightScreenTurnsP1 : s.LightScreenTurnsP2);
            f[at + 2] = Turns(actorIsP1 ? s.MistTurnsP1 : s.MistTurnsP2);
            f[at + 3] = Turns(actorIsP1 ? s.SafeguardTurnsP1 : s.SafeguardTurnsP2);
            f[at + 4] = Turns(actorIsP1 ? s.TailwindTurnsP1 : s.TailwindTurnsP2);

            f[at + 5] = Turns(actorIsP1 ? s.ReflectTurnsP2 : s.ReflectTurnsP1);
            f[at + 6] = Turns(actorIsP1 ? s.LightScreenTurnsP2 : s.LightScreenTurnsP1);
            f[at + 7] = Turns(actorIsP1 ? s.MistTurnsP2 : s.MistTurnsP1);
            f[at + 8] = Turns(actorIsP1 ? s.SafeguardTurnsP2 : s.SafeguardTurnsP1);
            f[at + 9] = Turns(actorIsP1 ? s.TailwindTurnsP2 : s.TailwindTurnsP1);

            f[at + 10] = Turns(s.TrickRoomTurns);
            f[at + 11] = Turns(s.GravityTurns);
            f[at + 12] = Math.Clamp(s.Environment.WeatherTurns / 8f, 0f, 1f);
            f[at + 13] = Math.Clamp(s.Environment.TerrainTurns / 8f, 0f, 1f);

            int weather = (int)s.Environment.Weather;
            int terrain = (int)s.Environment.Terrain;

            if (weather >= 0 && weather < 5)
                f[at + 14 + weather] = 1f;

            if (terrain >= 0 && terrain < 5)
                f[at + 19 + terrain] = 1f;

            f[at + 24] = actor.UsedMegaEvolution ? 1f : 0f;
            f[at + 25] = actor.UsedZMove ? 1f : 0f;

            // Announced when it happens, so knowable without reading their
            // PlayerState - the battle says "it mega evolved!" out loud.
            f[at + 26] = (actorIsP1 ? s.Player2 : s.Player1).UsedMegaEvolution ? 1f : 0f;
            f[at + 27] = (actorIsP1 ? s.Player2 : s.Player1).UsedZMove ? 1f : 0f;
        }

        // ========================================================== history

        /// <summary>
        /// The last sixteen turns, newest first, mapped from the symmetric
        /// rows the knowledge layer keeps onto "mine" and "theirs".
        ///
        /// Newest first on purpose: the row a network should weight most is
        /// always at the same offset, whether the battle is four turns old or
        /// forty. Oldest-first would move it every turn.
        /// </summary>
        private static void WriteHistory(float[] f, int at, BattleState state, bool actorIsP1)
        {
            IReadOnlyList<TurnEvent> rows = state.Knowledge.History;

            for (int i = 0; i < ObservationV8.HistoryTurns; i++)
            {
                int index = rows.Count - 1 - i;

                if (index < 0)
                    break;

                TurnEvent row = rows[index];
                TurnEvent? older = index > 0 ? rows[index - 1] : null;

                int k = at + i * HistoryStride;

                f[k] = 1f;
                f[k + 1] = 1f - Math.Clamp(i / (float)ObservationV8.HistoryTurns, 0f, 1f);

                ObservedActionKind mine = actorIsP1 ? row.P1Kind : row.P2Kind;
                ObservedActionKind theirs = actorIsP1 ? row.P2Kind : row.P1Kind;

                f[k + 2 + (int)mine] = 1f;
                f[k + 6 + (int)theirs] = 1f;

                f[k + 10] = actorIsP1 ? row.P1DamageDealt : row.P2DamageDealt;
                f[k + 11] = actorIsP1 ? row.P2DamageDealt : row.P1DamageDealt;
                f[k + 12] = actorIsP1 ? row.P1HpAfter : row.P2HpAfter;
                f[k + 13] = actorIsP1 ? row.P2HpAfter : row.P1HpAfter;
                f[k + 14] = (actorIsP1 ? row.P1Fainted : row.P2Fainted) ? 1f : 0f;
                f[k + 15] = (actorIsP1 ? row.P2Fainted : row.P1Fainted) ? 1f : 0f;

                int myStages = actorIsP1 ? row.P1StageSum : row.P2StageSum;
                int theirStages = actorIsP1 ? row.P2StageSum : row.P1StageSum;

                f[k + 16] = Math.Clamp(myStages / 12f, -1f, 1f);
                f[k + 17] = Math.Clamp(theirStages / 12f, -1f, 1f);

                if (older != null)
                {
                    int myBefore = actorIsP1 ? older.P1StageSum : older.P2StageSum;
                    int theirBefore = actorIsP1 ? older.P2StageSum : older.P1StageSum;

                    f[k + 18] = myStages > myBefore ? 1f : 0f;
                    f[k + 19] = theirStages > theirBefore ? 1f : 0f;

                    string? myMove = actorIsP1 ? row.P1Move : row.P2Move;
                    string? theirMove = actorIsP1 ? row.P2Move : row.P1Move;
                    string? myOlder = actorIsP1 ? older.P1Move : older.P2Move;
                    string? theirOlder = actorIsP1 ? older.P2Move : older.P1Move;

                    f[k + 20] = Repeated(myMove, myOlder) ? 1f : 0f;
                    f[k + 21] = Repeated(theirMove, theirOlder) ? 1f : 0f;
                }

                f[k + 22] = (actorIsP1 ? row.P1Status : row.P2Status) != StatusCondition.None ? 1f : 0f;
                f[k + 23] = (actorIsP1 ? row.P2Status : row.P1Status) != StatusCondition.None ? 1f : 0f;

                if (older != null)
                {
                    string mineNow = actorIsP1 ? row.P2Species : row.P1Species;
                    string mineWas = actorIsP1 ? older.P2Species : older.P1Species;

                    f[k + 24] = string.Equals(mineNow, mineWas, StringComparison.Ordinal) ? 0f : 1f;
                }

                f[k + 25] = row.WeatherChanged ? 1f : 0f;
                f[k + 26] = row.TerrainChanged ? 1f : 0f;
                f[k + 27] = row.HazardsChanged ? 1f : 0f;
            }
        }

        private static bool Repeated(string? now, string? before) =>
            now != null && before != null
            && string.Equals(now, before, StringComparison.OrdinalIgnoreCase);

        // ======================================================== behaviour

        /// <summary>
        /// What has been OBSERVED about how each side plays. Statistics, never
        /// conclusions: "they have switched on four of eleven turns" and never
        /// "they are about to switch". The network is what turns one into the
        /// other, and that is the whole point of the section.
        /// </summary>
        private static void WriteBehaviour(
            float[] f, int at, BattleState state,
            SideKnowledge them, SideKnowledge us,
            ObservedPokemon theirActive, bool actorIsP1)
        {
            IReadOnlyList<TurnEvent> rows = state.Knowledge.History;
            int turn = Math.Max(1, state.TurnNumber);

            f[at] = Math.Clamp(them.Switches / (float)turn, 0f, 1f);

            int recentSwitches = 0;
            int swapAfterDamage = 0;
            int switchesCounted = 0;

            for (int i = rows.Count - 1; i >= 0 && i >= rows.Count - 5; i--)
            {
                ObservedActionKind kind = actorIsP1 ? rows[i].P2Kind : rows[i].P1Kind;

                if (kind == ObservedActionKind.Switch)
                    recentSwitches++;
            }

            for (int i = 1; i < rows.Count; i++)
            {
                ObservedActionKind kind = actorIsP1 ? rows[i].P2Kind : rows[i].P1Kind;

                if (kind != ObservedActionKind.Switch)
                    continue;

                switchesCounted++;

                float tookLastTurn = actorIsP1 ? rows[i - 1].P1DamageDealt : rows[i - 1].P2DamageDealt;

                if (tookLastTurn > 0f)
                    swapAfterDamage++;
            }

            f[at + 1] = Math.Clamp(recentSwitches / 5f, 0f, 1f);
            f[at + 2] = them.LastSwitchTurn > 0
                ? Math.Clamp((state.TurnNumber - them.LastSwitchTurn) / 16f, 0f, 1f)
                : 1f;

            f[at + 3] = Share(RepeatCount(rows, actorIsP1, theirs: true), rows.Count);
            f[at + 4] = Math.Clamp(theirActive.LastMoveStreak / 8f, 0f, 1f);

            f[at + 5] = Share(them.AttacksUsed, them.MovesUsed);
            f[at + 6] = Share(them.StatusMovesUsed, them.MovesUsed);
            f[at + 7] = Share(them.BoostMovesUsed, them.MovesUsed);
            f[at + 8] = Share(them.ProtectsUsed, them.MovesUsed);

            float theirMean = Mean(rows, actorIsP1, theirs: true, window: rows.Count);
            float theirRecent = Mean(rows, actorIsP1, theirs: true, window: 3);

            f[at + 9] = theirMean;
            f[at + 10] = theirRecent;
            f[at + 11] = Math.Clamp(theirRecent - theirMean, -1f, 1f);
            f[at + 12] = HpTrend(rows, actorIsP1, theirs: true);
            f[at + 13] = Share(swapAfterDamage, switchesCounted);

            f[at + 14] = Math.Clamp(us.Switches / (float)turn, 0f, 1f);
            f[at + 15] = Share(RepeatCount(rows, actorIsP1, theirs: false), rows.Count);

            ObservedPokemon ourActive = us.Slots.FirstOrDefault(s => s.Active) ?? new ObservedPokemon();

            f[at + 16] = Math.Clamp(ourActive.LastMoveStreak / 8f, 0f, 1f);
            f[at + 17] = Share(us.AttacksUsed, us.MovesUsed);

            float myMean = Mean(rows, actorIsP1, theirs: false, window: rows.Count);

            f[at + 18] = myMean;
            f[at + 19] = Mean(rows, actorIsP1, theirs: false, window: 3);
            f[at + 20] = HpTrend(rows, actorIsP1, theirs: false);

            f[at + 21] = Math.Clamp(state.TurnNumber / 50f, 0f, 1f);
            f[at + 22] = Math.Clamp(rows.Count / (float)ObservationV8.HistoryTurns, 0f, 1f);
            f[at + 23] = rows.Count < 3 ? 1f : 0f;
        }

        private static int RepeatCount(IReadOnlyList<TurnEvent> rows, bool actorIsP1, bool theirs)
        {
            int count = 0;

            for (int i = 1; i < rows.Count; i++)
            {
                bool wantP2 = actorIsP1 == theirs;

                string? now = wantP2 ? rows[i].P2Move : rows[i].P1Move;
                string? before = wantP2 ? rows[i - 1].P2Move : rows[i - 1].P1Move;

                if (Repeated(now, before))
                    count++;
            }

            return count;
        }

        private static float Mean(IReadOnlyList<TurnEvent> rows, bool actorIsP1, bool theirs, int window)
        {
            if (rows.Count == 0 || window <= 0)
                return 0f;

            int taken = 0;
            float total = 0f;
            bool wantP2 = actorIsP1 == theirs;

            for (int i = rows.Count - 1; i >= 0 && taken < window; i--, taken++)
                total += wantP2 ? rows[i].P2DamageDealt : rows[i].P1DamageDealt;

            return taken == 0 ? 0f : Math.Clamp(total / taken, 0f, 1f);
        }

        private static float HpTrend(IReadOnlyList<TurnEvent> rows, bool actorIsP1, bool theirs)
        {
            if (rows.Count < 2)
                return 0f;

            bool wantP2 = actorIsP1 == theirs;

            float first = wantP2 ? rows[0].P2HpAfter : rows[0].P1HpAfter;
            float last = wantP2 ? rows[^1].P2HpAfter : rows[^1].P1HpAfter;

            return Math.Clamp(last - first, -1f, 1f);
        }

        private static float Share(int part, int whole) =>
            whole <= 0 ? 0f : Math.Clamp(part / (float)whole, 0f, 1f);

        // =========================================================== shared

        private static void WriteTypes(float[] f, int at, IEnumerable<PokemonType> types)
        {
            foreach (PokemonType type in types)
            {
                int index = (int)type;

                if (index >= 0 && index < ObservationV8.TypeSlots)
                    f[at + index] = 1f;
            }
        }

        private static float Stage(int stage) => Math.Clamp(stage / 6f, -1f, 1f);

        private static float Turns(int remaining) => Math.Clamp(remaining / 5f, 0f, 1f);

        private static float Fraction(int part, int whole) =>
            whole <= 0 ? 0f : Math.Clamp(part / (float)whole, 0f, 1f);

        private static float[] Finite(float[] f)
        {
            for (int i = 0; i < f.Length; i++)
            {
                if (float.IsNaN(f[i]) || float.IsInfinity(f[i]))
                    f[i] = 0f;
            }

            return f;
        }
    }
}
