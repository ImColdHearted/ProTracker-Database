using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Models;

namespace PokemonSim.Observation
{
    /// <summary>
    /// Section 175. The feature encoder, rebuilt.
    ///
    /// Section 156 recorded the author's original ten floats - HP
    /// fractions, six stat stages, weather and terrain - because that is
    /// what ai-lab/BattleStateEncoder produced and what the shipped
    /// pokemon_ai.onnx expected. Those ten say nothing about WHAT the four
    /// move slots contain, and RandomTeams shuffles a team's moves into its
    /// slots, so slot 2 is a different move in every battle. A model asked
    /// to pick a slot from those ten features is being asked to predict a
    /// randomly permuted label from inputs that do not mention it: measured
    /// on a task with the same shape, a 2,916-parameter network scores
    /// exactly chance, and so does a 137,988-parameter one. Capacity was
    /// never the constraint - the state was.
    ///
    /// So the vector now describes the decision. Layout (76 floats):
    ///
    ///   0-9    the section 156 ten, IN THE SAME ORDER AND MEANING, so a
    ///          10-input model still loads and is fed this prefix
    ///          (OnnxShadowEvaluator slices to the width the model asks
    ///          for). Nothing about a v1 model breaks.
    ///   10-53  eleven numbers for each of the four move slots: whether it
    ///          is legal this turn, its type effectiveness against the
    ///          opposing active, power, category, STAB, accuracy, PP left,
    ///          priority, and a damage estimate as a fraction of the
    ///          target's CURRENT hp.
    ///   54-75  the rest of the position: who is faster, both sides'
    ///          status, how much team is left on each side, the best
    ///          effectiveness and best damage available, and the levels.
    ///
    /// Section 176 appended three more blocks, and gave the model a switch
    /// output to go with them. A switch head without them would have been
    /// blind in exactly the way the move head used to be: nothing in the
    /// state said what was ON the bench, so "send in team slot 3" was as
    /// unpredictable as "use move slot 3" once was.
    ///
    ///   76-111  nine effect flags for each move slot: whether it heals,
    ///           raises its own stats, lowers the target's, its chance of
    ///           leaving a status, its flinch or secondary chance, how
    ///           many times it expects to hit, whether it sets weather,
    ///           terrain or hazards, whether it protects, and whether it
    ///           costs the user something (recoil, self-faint, a charge
    ///           turn). Turns "this is a status move" into "this is a
    ///           recovery move".
    ///  112-153  seven numbers for each of the six team positions, in the
    ///           order PlayerState.Team holds them, so team slot i lines
    ///           up with switch output MoveSlots + i: present, alive, hp
    ///           fraction, whether it is the one out right now, how well
    ///           its moves hit the opposing active, how hard that active's
    ///           types hit back, and whether it would outspeed.
    ///  154-159  entry hazards on both sides - Stealth Rock, Spikes and
    ///           Toxic Spikes - which are most of what makes a switch
    ///           cost something.
    ///
    /// Section 177 appended the last two blocks, and closed a hole that had
    /// been open since section 156: the SPECIAL attack and defense stages
    /// were never recorded at all. Only Attack, Defense and Speed were, so
    /// a Nasty Plot or a Calm Mind was invisible to every model this
    /// project has ever trained - half of competitive play, missing from
    /// the state.
    ///
    ///  160-177  the field: Reflect, Light Screen, Mist, Safeguard and
    ///           Tailwind on each side, then Trick Room and Gravity, the
    ///           turns left on the weather and the terrain, and whether
    ///           each side has spent its mega evolution and its Z-Move.
    ///  178-201  twelve numbers for each active, self then opponent: the
    ///           four stat stages the section 156 ten left out (SpAttack,
    ///           SpDefense, accuracy, evasion), then the volatiles that
    ///           change what works - confused, leech seeded, behind a
    ///           substitute, taunted, encored, disabled, trapped, and
    ///           charging a two-turn move.
    ///
    /// Perspective is the ACTING side's, as in section 156, so both sides'
    /// decisions are training examples for one model.
    ///
    /// PURE READS. Encoding never mutates the battle and never draws from
    /// its rng - that is why the damage term below is an arithmetic
    /// estimate rather than a call to DamageCalculator, which rolls the
    /// 85-100 spread and the critical hit off state.Rng and would shift
    /// the visible battle's dice just by being watched. Section 175's
    /// tests fingerprint the rng across an Encode call to keep it that way.
    /// </summary>
    public static class ObserverEncoder
    {
        /// <summary>Where each move slot's block starts.</summary>
        public const int MoveBlockStart = ObservationSchema.LegacyFeatureCount;

        /// <summary>Floats per move slot.</summary>
        public const int MoveBlockStride = 11;

        /// <summary>Where the whole-position block starts.</summary>
        public const int GlobalBlockStart =
            MoveBlockStart + ObservationSchema.MoveSlots * MoveBlockStride;

        /// <summary>Floats in the whole-position block.</summary>
        public const int GlobalBlockSize = 22;

        /// <summary>Section 176. Where the per-move effect flags start -
        /// the end of the section 175 vector, which is why everything
        /// before it is still bit-for-bit what a v2 model was trained on.</summary>
        public const int EffectBlockStart = GlobalBlockStart + GlobalBlockSize;

        /// <summary>Effect flags per move slot.</summary>
        public const int EffectBlockStride = 9;

        /// <summary>Section 176. Where the six team positions start.</summary>
        public const int TeamBlockStart =
            EffectBlockStart + ObservationSchema.MoveSlots * EffectBlockStride;

        /// <summary>Floats per team position.</summary>
        public const int TeamBlockStride = 7;

        /// <summary>Section 176. Where the entry hazards start: three for
        /// the acting side's own field, then three for the opponent's.</summary>
        public const int HazardBlockStart =
            TeamBlockStart + ObservationSchema.TeamSlots * TeamBlockStride;

        /// <summary>Floats in the hazard block.</summary>
        public const int HazardBlockSize = 6;

        /// <summary>Section 177. Where the field conditions start.</summary>
        public const int FieldBlockStart = HazardBlockStart + HazardBlockSize;

        /// <summary>Floats in the field block.</summary>
        public const int FieldBlockSize = 18;

        /// <summary>Section 177. Where the two actives' remaining stages
        /// and volatiles start: self first, then the opponent.</summary>
        public const int ActiveBlockStart = FieldBlockStart + FieldBlockSize;

        /// <summary>Floats per active.</summary>
        public const int ActiveBlockStride = 12;

        /// <summary>The six real status conditions, in the order they are
        /// encoded as flags (StatusCondition.None is the absence of all
        /// six rather than a seventh flag).</summary>
        public static readonly StatusCondition[] StatusFlags =
        {
            StatusCondition.Burn,
            StatusCondition.Paralysis,
            StatusCondition.Freeze,
            StatusCondition.Sleep,
            StatusCondition.Poison,
            StatusCondition.Toxic
        };

        /// <summary>
        /// The state the acting side sees. <paramref name="legalActions"/>
        /// is what the engine offered this turn; pass null (a forced
        /// replacement, where no move is being chosen) and every slot's
        /// legality reads zero.
        /// </summary>
        /// <param name="risk">Section 182. The risk appetite this state
        /// is being judged under: 0 is plain expected value, 1 is the
        /// gambler that ranks an action by how good it is when it goes
        /// well. It is written into the last feature so ONE model can hold
        /// a whole difficulty range - collect at several values, and at
        /// play time the number you feed in selects which teacher the
        /// model imitates. It says nothing about the battle, which is why
        /// it sits at the end where it disturbs no earlier index.</param>
        public static float[] Encode(
            BattleState state,
            PlayerState actor,
            IReadOnlyList<BattleAction>? legalActions = null,
            float risk = ObservationSchema.NeutralRisk)
        {
            PokemonState self = actor.ActivePokemon;
            PlayerState opponent = state.GetOpponentOf(actor);
            PokemonState target = opponent.ActivePokemon;

            var features = new float[ObservationSchema.FeatureCount];

            // ---- 0-9: the section 156 ten, unchanged ----
            features[0] = self.MaxHP <= 0 ? 0f : self.CurrentHP / (float)self.MaxHP;
            features[1] = target.MaxHP <= 0 ? 0f : target.CurrentHP / (float)target.MaxHP;

            features[2] = self.AttackStage;
            features[3] = self.DefenseStage;
            features[4] = self.SpeedStage;

            features[5] = target.AttackStage;
            features[6] = target.DefenseStage;
            features[7] = target.SpeedStage;

            features[8] = (float)state.Environment.Weather;
            features[9] = (float)state.Environment.Terrain;

            // ---- 10-53: four move slots ----
            float[] legality = legalActions == null
                ? new float[ObservationSchema.MoveSlots]
                : LegalMoveMask(legalActions);

            MoveState?[] slots = SlotMoves(self);

            float bestEffectiveness = 0f;
            float bestDamage = 0f;

            for (int slot = 0; slot < ObservationSchema.MoveSlots; slot++)
            {
                int at = MoveBlockStart + slot * MoveBlockStride;
                MoveState? move = slots[slot];

                features[at] = legality[slot];

                if (move == null)
                    continue;

                bool damaging = move.Category != MoveCategory.Status && move.Power > 0;
                float effectiveness = move.Typeless ? 1f : Effectiveness(move.Type, target);
                float damage = damaging ? DamageFraction(state, self, target, move, effectiveness) : 0f;

                features[at + 1] = damaging ? effectiveness : 0f;
                features[at + 2] = Math.Min(move.Power, 225) / 150f;
                features[at + 3] = move.Category == MoveCategory.Physical ? 1f : 0f;
                features[at + 4] = move.Category == MoveCategory.Special ? 1f : 0f;
                features[at + 5] = move.Category == MoveCategory.Status ? 1f : 0f;
                features[at + 6] = !move.Typeless && self.Types.Contains(move.Type) ? 1f : 0f;
                features[at + 7] = move.Accuracy <= 0 ? 1f : Math.Min(move.Accuracy, 100) / 100f;
                features[at + 8] = move.MaxPP <= 0 ? 1f : Math.Clamp(move.CurrentPP / (float)move.MaxPP, 0f, 1f);
                features[at + 9] = Math.Clamp(move.Priority / 5f, -1f, 1f);
                features[at + 10] = damage;

                // The two "best available" summaries only count slots the
                // engine is actually offering this turn.
                if (legality[slot] > 0)
                {
                    if (damaging && effectiveness > bestEffectiveness)
                        bestEffectiveness = effectiveness;

                    if (damage > bestDamage)
                        bestDamage = damage;
                }
            }

            // ---- 54-75: the rest of the position ----
            int g = GlobalBlockStart;

            double selfSpeed = Speed(state, self);
            double targetSpeed = Speed(state, target);

            // Section 176: under Trick Room the slower side moves first,
            // and BattleEngine sorts its queue that way. "Who is faster"
            // has to mean "who acts first" or the feature is a lie for the
            // whole five turns the room is up.
            features[g] = MovesFirst(state, selfSpeed, targetSpeed) ? 1f : 0f;
            features[g + 1] = targetSpeed <= 0
                ? 1f
                : (float)(Math.Min(selfSpeed / targetSpeed, 3.0) / 3.0);

            for (int i = 0; i < StatusFlags.Length; i++)
            {
                features[g + 2 + i] = self.Status == StatusFlags[i] ? 1f : 0f;
                features[g + 8 + i] = target.Status == StatusFlags[i] ? 1f : 0f;
            }

            features[g + 14] = Alive(actor) / 6f;
            features[g + 15] = Alive(opponent) / 6f;
            features[g + 16] = TeamHpFraction(actor);
            features[g + 17] = TeamHpFraction(opponent);
            features[g + 18] = bestEffectiveness;
            features[g + 19] = bestDamage;
            features[g + 20] = self.Level / 100f;
            features[g + 21] = target.Level / 100f;

            // ---- 76-111: what each move slot actually DOES ----
            for (int slot = 0; slot < ObservationSchema.MoveSlots; slot++)
            {
                MoveState? move = slots[slot];

                if (move == null)
                    continue;

                int at = EffectBlockStart + slot * EffectBlockStride;

                features[at] = HasEffect(move, "Heal") || HasEffect(move, "Drain") ? 1f : 0f;
                features[at + 1] = HasStatChange(move, "self", raising: true) ? 1f : 0f;
                features[at + 2] = HasStatChange(move, "opponent", raising: false) ? 1f : 0f;
                features[at + 3] = StatusChance(move);
                features[at + 4] = (float)Math.Clamp(Math.Max(move.FlinchChance, move.SecondaryChance), 0.0, 1.0);
                features[at + 5] = Math.Clamp((move.MinHits + move.MaxHits) / 2f, 1f, 5f) / 5f;
                features[at + 6] = SetsField(move) ? 1f : 0f;
                features[at + 7] = HasEffect(move, "Protect") || HasEffect(move, "Endure") ? 1f : 0f;
                features[at + 8] = CostsTheUser(move) ? 1f : 0f;
            }

            // ---- 112-153: the six team positions ----
            for (int i = 0; i < ObservationSchema.TeamSlots; i++)
            {
                int at = TeamBlockStart + i * TeamBlockStride;

                if (i >= actor.Team.Count)
                    continue;                       // absent: the whole block stays zero

                PokemonState member = actor.Team[i];

                features[at] = 1f;                  // present
                features[at + 1] = member.Fainted ? 0f : 1f;
                features[at + 2] = member.MaxHP <= 0 ? 0f : Math.Max(0, member.CurrentHP) / (float)member.MaxHP;
                features[at + 3] = ReferenceEquals(member, self) ? 1f : 0f;
                features[at + 4] = BestOffence(member, target);
                features[at + 5] = WorstDefence(target, member);
                features[at + 6] = MovesFirst(state, Speed(state, member), targetSpeed) ? 1f : 0f;
            }

            // ---- 154-159: entry hazards, mine then theirs ----
            int h = HazardBlockStart;

            bool actorIsP1 = ReferenceEquals(actor, state.Player1);

            features[h] = (actorIsP1 ? state.StealthRockP1 : state.StealthRockP2) ? 1f : 0f;
            features[h + 1] = Math.Clamp((actorIsP1 ? state.SpikesP1 : state.SpikesP2) / 3f, 0f, 1f);
            features[h + 2] = Math.Clamp((actorIsP1 ? state.ToxicSpikesP1 : state.ToxicSpikesP2) / 2f, 0f, 1f);
            features[h + 3] = (actorIsP1 ? state.StealthRockP2 : state.StealthRockP1) ? 1f : 0f;
            features[h + 4] = Math.Clamp((actorIsP1 ? state.SpikesP2 : state.SpikesP1) / 3f, 0f, 1f);
            features[h + 5] = Math.Clamp((actorIsP1 ? state.ToxicSpikesP2 : state.ToxicSpikesP1) / 2f, 0f, 1f);

            // ---- 160-177: the field ----
            int d = FieldBlockStart;

            features[d] = Turns(state.ReflectTurns(actor));
            features[d + 1] = Turns(state.LightScreenTurns(actor));
            features[d + 2] = Turns(state.MistTurns(actor));
            features[d + 3] = Turns(state.SafeguardTurns(actor));
            features[d + 4] = Turns(state.TailwindTurns(actor));

            features[d + 5] = Turns(state.ReflectTurns(opponent));
            features[d + 6] = Turns(state.LightScreenTurns(opponent));
            features[d + 7] = Turns(state.MistTurns(opponent));
            features[d + 8] = Turns(state.SafeguardTurns(opponent));
            features[d + 9] = Turns(state.TailwindTurns(opponent));

            features[d + 10] = Turns(state.TrickRoomTurns);
            features[d + 11] = Turns(state.GravityTurns);
            features[d + 12] = Math.Clamp(state.Environment.WeatherTurns / 8f, 0f, 1f);
            features[d + 13] = Math.Clamp(state.Environment.TerrainTurns / 8f, 0f, 1f);

            // Section 161 gives every side one mega and one Z-Move per
            // battle; whether they are still in hand changes what a turn
            // is worth.
            features[d + 14] = actor.UsedMegaEvolution ? 1f : 0f;
            features[d + 15] = actor.UsedZMove ? 1f : 0f;
            features[d + 16] = opponent.UsedMegaEvolution ? 1f : 0f;
            features[d + 17] = opponent.UsedZMove ? 1f : 0f;

            // ---- 178-201: the two actives, mine then theirs ----
            WriteActive(features, ActiveBlockStart, self);
            WriteActive(features, ActiveBlockStart + ActiveBlockStride, target);

            // ---- 202: which teacher this state is being judged by ----
            features[ObservationSchema.RiskFeatureIndex] = Math.Clamp(risk, 0f, 1f);

            // ---- 203-204: Sticky Web, mine then theirs ----
            //
            // Section 184 put these here rather than in the hazard block at
            // 154. Growing that block in place would push the field and
            // active blocks along by two and make every model trained
            // before today read the wrong columns; appending keeps every
            // earlier width a prefix, which is the one rule this whole
            // vector is built on.
            int w = ObservationSchema.WebFeatureIndex;

            features[w] = (actorIsP1 ? state.StickyWebP1 : state.StickyWebP2) ? 1f : 0f;
            features[w + 1] = (actorIsP1 ? state.StickyWebP2 : state.StickyWebP1) ? 1f : 0f;

            return Finite(features);
        }

        /// <summary>
        /// Every feature guaranteed to be a real number. Each term above
        /// guards its own divisor, so this should never change anything -
        /// but the record writer uses System.Text.Json's DEFAULT options,
        /// which throw on NaN and infinity, and BattleObserver catches its
        /// own exceptions. One stray non-finite feature from some future
        /// ability effect would therefore not crash anything; it would
        /// quietly stop recording, which is far worse. So the encoder
        /// promises finiteness at the boundary rather than hoping for it.
        /// </summary>
        static float[] Finite(float[] features)
        {
            for (int i = 0; i < features.Length; i++)
            {
                if (float.IsNaN(features[i]) || float.IsInfinity(features[i]))
                    features[i] = 0f;
            }

            return features;
        }

        /// <summary>
        /// Section 182. Where one action sits in the action space: its
        /// move slot, or MoveSlots plus the team position it switches to.
        /// -1 for anything with no slot to name - Struggle, or a switch to
        /// a Pokemon that is not on this side's team.
        ///
        /// The reverse of NeuralStrategy's Match. It exists because under
        /// DAgger the label is an action the teacher only recommended, so
        /// there is no chosen BattleAction to read the index off.
        /// </summary>
        public static int ActionIndex(PlayerState self, BattleAction action)
        {
            if (action.Type == BattleActionType.Move)
            {
                if (action.Move == null || action.Move.Name == "Struggle")
                    return -1;

                int slot = action.Move.Index;

                return slot >= 0 && slot < ObservationSchema.MoveSlots ? slot : -1;
            }

            if (action.Type == BattleActionType.Switch && action.SwitchTarget != null)
            {
                int index = self.Team.IndexOf(action.SwitchTarget);

                if (index >= 0 && index < ObservationSchema.TeamSlots)
                    return ObservationSchema.MoveSlots + index;
            }

            return -1;
        }

        /// <summary>
        /// Section 176. The mask over the FULL action space: the four move
        /// slots as LegalMoveMask gives them, then one entry per team
        /// position this side may legally switch to. Its first MoveSlots
        /// entries are exactly LegalMoveMask, so a four-output model reads
        /// the front of it and sees no difference.
        ///
        /// Team positions are indexed as PlayerState.Team holds them, which
        /// is the same index BattleObserver records in
        /// ObservedAction.SwitchTeamIndex - so the label a decision writes
        /// and the output the model produces are the same number.
        /// </summary>
        public static float[] LegalActionMask(
            IReadOnlyList<BattleAction> legalActions, PlayerState actor)
        {
            var mask = new float[ObservationSchema.ActionSlots];
            float[] moves = LegalMoveMask(legalActions);

            for (int i = 0; i < moves.Length; i++)
                mask[i] = moves[i];

            foreach (BattleAction action in legalActions)
            {
                if (action.Type != BattleActionType.Switch || action.SwitchTarget == null)
                    continue;

                int index = actor.Team.IndexOf(action.SwitchTarget);

                if (index >= 0 && index < ObservationSchema.TeamSlots)
                    mask[ObservationSchema.MoveSlots + index] = 1f;
            }

            return mask;
        }

        /// <summary>The mask over just the team positions, for a forced
        /// replacement - there is no move to make, only somebody to send
        /// in. The move half is left at zero.</summary>
        public static float[] ReplacementMask(IReadOnlyList<int> legalTeamIndexes)
        {
            var mask = new float[ObservationSchema.ActionSlots];

            foreach (int index in legalTeamIndexes)
            {
                if (index >= 0 && index < ObservationSchema.TeamSlots)
                    mask[ObservationSchema.MoveSlots + index] = 1f;
            }

            return mask;
        }

        /// <summary>1 per legal move slot (0-3), matching the first four of
        /// the model's outputs. All zero when the only "move" is
        /// Struggle.</summary>
        public static float[] LegalMoveMask(IReadOnlyList<BattleAction> legalActions)
        {
            var mask = new float[ObservationSchema.MoveSlots];

            foreach (BattleAction action in legalActions)
            {
                if (action.Type != BattleActionType.Move || action.Move == null)
                    continue;

                if (action.Move.Name == "Struggle")
                    continue;

                int index = action.Move.Index;

                if (index >= 0 && index < mask.Length)
                    mask[index] = 1f;
            }

            return mask;
        }

        /// <summary>The four slots as the mask indexes them: by
        /// MoveState.Index, which is what LegalMoveMask reads, falling back
        /// to list position for data that never set one.</summary>
        internal static MoveState?[] SlotMoves(PokemonState pokemon)
        {
            var slots = new MoveState?[ObservationSchema.MoveSlots];

            for (int i = 0; i < pokemon.Moves.Count; i++)
            {
                MoveState move = pokemon.Moves[i];
                int slot = move.Index >= 0 && move.Index < slots.Length ? move.Index : i;

                if (slot >= 0 && slot < slots.Length && slots[slot] == null)
                    slots[slot] = move;
            }

            return slots;
        }

        /// <summary>The type chart product against the defender's types -
        /// 0, 0.25, 0.5, 1, 2 or 4.</summary>
        internal static float Effectiveness(PokemonType attacking, PokemonState defender)
        {
            double multiplier = 1.0;

            foreach (PokemonType defending in defender.Types)
                multiplier *= TypeChart.GetMultiplier(attacking, defending);

            return (float)multiplier;
        }

        /// <summary>
        /// A deterministic damage estimate as a fraction of the target's
        /// CURRENT hp: 1 means "this looks lethal". The main-series formula
        /// with the two things that are not knowable in advance left out -
        /// the 85-100 random spread and the critical hit - because both
        /// come off state.Rng and an encoder may not roll the battle's
        /// dice. Weather, screens, abilities that scale damage rather than
        /// stats, and multi-hit counts are also left out; this is a ranking
        /// feature, not a damage calculator.
        /// </summary>
        internal static float DamageFraction(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            float effectiveness)
        {
            if (move.Power <= 0 || effectiveness <= 0f)
                return 0f;

            bool physical = move.Category == MoveCategory.Physical;

            // Section 158's three re-pointing flags, honored so the estimate
            // does not rank Body Press and Foul Play off the wrong stat.
            string offenseStat = move.UsesDefenseAsOffense ? "Defense" : physical ? "Attack" : "SpAttack";
            string defenseStat = move.UsesTargetDefense || physical ? "Defense" : "SpDefense";

            PokemonState offenseSource = move.UsesTargetAttack ? defender : attacker;

            double offense = StatResolver.GetStat(state, offenseSource, offenseStat);
            double defense = StatResolver.GetStat(state, defender, defenseStat);

            if (defense <= 0)
                return 0f;

            double raw = (2.0 * attacker.Level / 5.0 + 2.0) * move.Power * offense / defense / 50.0 + 2.0;

            if (!move.Typeless && attacker.Types.Contains(move.Type))
                raw *= 1.5;

            raw *= effectiveness;

            int hp = Math.Max(1, defender.CurrentHP);
            double fraction = raw / hp;

            return double.IsFinite(fraction) ? (float)Math.Clamp(fraction, 0.0, 2.0) : 0f;
        }

        /// <summary>A side condition's remaining turns as a fraction of
        /// the five they normally start with, so "just set" and "about to
        /// drop" are different numbers rather than the same flag.</summary>
        static float Turns(int remaining) => Math.Clamp(remaining / 5f, 0f, 1f);

        /// <summary>
        /// Section 177. One active's twelve: the four stat stages the
        /// section 156 ten never carried, then the volatiles that decide
        /// whether a move is even worth picking. Stages stay raw, on the
        /// same minus-six-to-six scale as the three that were already in
        /// the vector, so the whole set reads alike.
        /// </summary>
        internal static void WriteActive(float[] features, int at, PokemonState mon)
        {
            features[at] = mon.SpAttackStage;
            features[at + 1] = mon.SpDefenseStage;
            features[at + 2] = mon.AccuracyStage;
            features[at + 3] = mon.EvasionStage;

            features[at + 4] = mon.ConfusionTurns > 0 ? 1f : 0f;
            features[at + 5] = mon.LeechSeeded ? 1f : 0f;
            features[at + 6] = mon.SubstituteHP > 0 ? 1f : 0f;
            features[at + 7] = mon.TauntTurns > 0 ? 1f : 0f;
            features[at + 8] = mon.EncoreTurns > 0 ? 1f : 0f;
            features[at + 9] = mon.DisabledTurns > 0 ? 1f : 0f;
            features[at + 10] = mon.Trap != null ? 1f : 0f;
            features[at + 11] = mon.Charging ? 1f : 0f;
        }

        /// <summary>Who acts first. Section 176: under Trick Room the
        /// engine sorts its queue the other way round (BattleEngine passes
        /// TrickRoomTurns > 0 into queue.Sort), so a raw speed comparison
        /// would be wrong for every turn the room is up. A tie reads as
        /// "not first", which is what a coin flip is worth as a feature.</summary>
        internal static bool MovesFirst(BattleState state, double mine, double theirs) =>
            state.TrickRoomTurns > 0 ? mine < theirs : mine > theirs;

        /// <summary>The best type multiplier this Pokemon's own damaging
        /// moves get against a target - "can it hit that thing".</summary>
        internal static float BestOffence(PokemonState attacker, PokemonState target)
        {
            float best = 0f;

            foreach (MoveState move in attacker.Moves)
            {
                if (move.Category == MoveCategory.Status || move.Power <= 0 || move.Typeless)
                    continue;

                float effectiveness = Effectiveness(move.Type, target);

                if (effectiveness > best)
                    best = effectiveness;
            }

            return best;
        }

        /// <summary>How hard the opposing active's TYPES hit into this
        /// Pokemon - "how badly would it get hurt coming in". Deliberately
        /// its types and not its moves: a player cannot see an opponent's
        /// moveset, and a model trained on information the game does not
        /// show would be learning to cheat. Types are on the screen.</summary>
        internal static float WorstDefence(PokemonState attacker, PokemonState defender)
        {
            float worst = 0f;

            foreach (PokemonType type in attacker.Types)
            {
                float effectiveness = Effectiveness(type, defender);

                if (effectiveness > worst)
                    worst = effectiveness;
            }

            return worst;
        }

        internal static bool HasEffect(MoveState move, string effect) =>
            move.Effects != null &&
            move.Effects.Any(name => string.Equals(name, effect, StringComparison.OrdinalIgnoreCase));

        internal static bool HasStatChange(MoveState move, string target, bool raising)
        {
            if (move.StatChanges == null)
                return false;

            foreach (StatChange change in move.StatChanges)
            {
                if (!string.Equals(change.Target, target, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (raising ? change.Stages > 0 : change.Stages < 0)
                    return true;
            }

            return false;
        }

        /// <summary>How likely this move is to leave a status condition.
        /// A move that names one but gives no chance is a dedicated status
        /// move (Toxic, Will-O-Wisp) and always applies when it lands.</summary>
        internal static float StatusChance(MoveState move)
        {
            bool inflicts =
                move.InflictStatus != StatusCondition.None ||
                (move.RandomStatus != null && move.RandomStatus.Count > 0);

            if (!inflicts)
                return 0f;

            return move.StatusChance > 0
                ? (float)Math.Clamp(move.StatusChance, 0.0, 1.0)
                : 1f;
        }

        internal static bool SetsField(MoveState move) =>
            move.SetWeather != WeatherType.None ||
            move.SetTerrain != TerrainType.None ||
            HasEffect(move, "StealthRock") ||
            HasEffect(move, "Spikes") ||
            HasEffect(move, "ToxicSpikes") ||
            HasEffect(move, "Gravity") ||
            HasEffect(move, "TrickRoom");

        /// <summary>Recoil, a self-faint, or a turn spent charging - the
        /// moves whose downside is paid by the user.</summary>
        internal static bool CostsTheUser(MoveState move) =>
            HasEffect(move, "TwoTurn") ||
            HasEffect(move, "SelfFaint") ||
            (move.Effects != null &&
             move.Effects.Any(name => name.StartsWith("Recoil", StringComparison.OrdinalIgnoreCase)));

        static double Speed(BattleState state, PokemonState pokemon) =>
            StatResolver.GetStat(state, pokemon, "Speed");

        static float Alive(PlayerState side)
        {
            int alive = 0;

            foreach (PokemonState pokemon in side.Team)
            {
                if (!pokemon.Fainted)
                    alive++;
            }

            return alive;
        }

        static float TeamHpFraction(PlayerState side)
        {
            long current = 0, max = 0;

            foreach (PokemonState pokemon in side.Team)
            {
                current += Math.Max(0, pokemon.CurrentHP);
                max += Math.Max(0, pokemon.MaxHP);
            }

            return max <= 0 ? 0f : (float)(current / (double)max);
        }
    }
}