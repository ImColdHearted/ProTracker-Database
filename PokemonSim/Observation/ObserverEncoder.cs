using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Engine.Abilities;
using PokemonSim.Engine.Effects;
using PokemonSim.Engine.Items;
using PokemonSim.Models;

namespace PokemonSim.Observation
{
    /// <summary>
    /// §311. The feature encoder, version 7 - and the first one that breaks
    /// the prefix rule on purpose.
    ///
    /// WHAT WAS WRONG. The vector had grown by appending: 10 (§156) to 76
    /// (§175) to 160 (§176) to 202 (§177) to 203 (§182) to 205 (§184), every
    /// earlier width a valid prefix so no model ever broke. That rule was
    /// worth keeping while the additions were small. It stopped being worth
    /// keeping the moment the answer was "the state has no concept of an
    /// ability, an item, or a type".
    ///
    /// Two hundred and five features and NONE of them said what anything WAS.
    /// A Pokemon's types were nowhere in the vector; they reached the network
    /// only pre-chewed, as a type-chart product per move slot. An ability was
    /// nowhere at all. A held item was nowhere at all. Base or computed stats
    /// were nowhere, so a Blissey and a Deoxys were the same row with
    /// different hit points. Weather and terrain were fed as raw enum
    /// ORDINALS - Sun as 2, Sandstorm as 3 - so a network was being handed a
    /// number line on which Sandstorm sits between Rain and Hail, which it
    /// does not.
    ///
    /// And it was being taught things that are false. Effectiveness came off
    /// the type chart alone, so the state said an Earthquake does double to a
    /// Levitate Bronzong and that a Thunderbolt is worth using into a Volt
    /// Absorb Lanturn. The network could not learn its way around that,
    /// because nothing in its input mentioned the ability doing it.
    ///
    /// WHAT V7 IS. The same information, re-laid in blocks, plus the four
    /// things that were missing:
    ///
    ///     0- 39  POSITION (40). Both hit-point fractions, all seven stat
    ///            stages a side (§156 carried three and §177 appended two
    ///            more in a different block), both status conditions as
    ///            six-wide one-hots, levels, who acts first, the speed
    ///            ratio, how much team is left on each side, and - new -
    ///            whether each active is standing on the ground, and the
    ///            best effectiveness and best damage on offer this turn.
    ///    40- 75  TYPES (36). Eighteen per active, one-hot. This is the
    ///            concept the network never had: not "this move is doing
    ///            x2 right now" but "the thing in front of me is a Ghost".
    ///    76- 87  STATS (12). Six computed stats a side, so a wall reads
    ///            as a wall before it has taken a hit.
    ///    88-105  ABILITIES (18), nine a side, read straight off the
    ///            Pokemon's attached PassiveEffects: whether it has one at
    ///            all, whether this engine SIMULATES it (184 of the dex's
    ///            308 names have no rule - §307), which of the five move
    ///            phases it hooks, whether it only acts on defence, and
    ///            whether it cancels a whole type outright.
    ///   106-123  ITEMS (18), nine a side, from HeldItems' own predicates:
    ///            holding anything, simulated, can move a roll or a KO
    ///            count (§309), boosts damage, boosts a stat, Choice,
    ///            resist berry, heals each turn, saves its holder once.
    ///   124-279  MOVES (156), thirty-nine a slot. §175's eleven and
    ///            §176's nine effect flags, plus the move's own TYPE as an
    ///            eighteen-wide one-hot, plus one flag for "the thing in
    ///            front of me cancels this outright".
    ///   280-429  BENCH (150), twenty-five a position. §176's seven plus
    ///            that member's types. A switch head that cannot see what
    ///            its bench IS was the other half of why it never switched.
    ///   430-437  HAZARDS (8). Both sides' rocks, spikes, toxic spikes and
    ///            sticky web - the web now beside the other three rather
    ///            than stranded at the end of the vector by the prefix rule
    ///            that no longer applies.
    ///   438-465  FIELD (28). The screens and rooms as before, and weather
    ///            and terrain as FIVE-WIDE ONE-HOTS rather than as an
    ///            ordinal.
    ///   466-481  VOLATILES (16), eight an active: confused, seeded,
    ///            behind a substitute, taunted, encored, disabled, trapped,
    ///            charging.
    ///   482      RISK, the §182 dial.
    ///
    /// WHAT THIS COSTS. Every model trained on any earlier width is retired.
    /// AcceptedFeatureCounts is now one entry long, and OnnxShadowEvaluator
    /// refuses a model that asks for a retired width instead of feeding it
    /// the first N columns of a vector whose columns no longer mean what they
    /// did. Feeding 205 columns of this vector to a 205-input model would not
    /// fail - it would quietly score nonsense, which is the worse outcome and
    /// the reason the accepted list is short rather than forgiving.
    ///
    /// Perspective is the ACTING side's, as in §156, so both sides'
    /// decisions are training examples for one model.
    ///
    /// PURE READS. Encoding never mutates the battle and never draws from its
    /// rng - that is why the damage term below is an arithmetic estimate
    /// rather than a call to DamageCalculator, which rolls the 85-100 spread
    /// and the critical hit off state.Rng and would shift the visible
    /// battle's dice just by being watched. It is also why the ability and
    /// item blocks are READS of what is already attached rather than calls to
    /// §307's AbilityFactor or §309's ItemFactor: those measure by resolving
    /// the move against throwaway battles, sixteen and twenty-four times
    /// over, and this runs every turn of every training battle.
    /// </summary>
    public static class ObserverEncoder
    {
        // §311. The block map. Every start is the previous start plus the
        // previous size, written out that way so a block cannot be widened
        // without the ones after it moving - which is what the old
        // hand-numbered constants could not guarantee.

        /// <summary>Both actives' hit points, stages, status, levels, turn
        /// order, team strength and footing.</summary>
        public const int PositionBlockStart = 0;
        public const int PositionBlockSize = 40;

        /// <summary>§311. One-hot over PokemonType, which has eighteen
        /// members. Section311Tests pins that against the enum.</summary>
        public const int TypeSlots = 18;

        /// <summary>Eighteen a side, self then opponent.</summary>
        public const int TypeBlockStart = PositionBlockStart + PositionBlockSize;
        public const int TypeBlockSize = TypeSlots * 2;

        /// <summary>Six computed stats a side.</summary>
        public const int StatBlockStart = TypeBlockStart + TypeBlockSize;
        public const int StatBlockStride = 6;
        public const int StatBlockSize = StatBlockStride * 2;

        /// <summary>§311. Nine an ability, self then opponent.</summary>
        public const int AbilityBlockStart = StatBlockStart + StatBlockSize;
        public const int AbilityBlockStride = 9;
        public const int AbilityBlockSize = AbilityBlockStride * 2;

        /// <summary>§311. Nine an item, self then opponent.</summary>
        public const int ItemBlockStart = AbilityBlockStart + AbilityBlockSize;
        public const int ItemBlockStride = 9;
        public const int ItemBlockSize = ItemBlockStride * 2;

        /// <summary>Thirty-nine a move slot: §175's eleven, §176's nine,
        /// §311's cancelled flag, and the move's type.</summary>
        public const int MoveBlockStart = ItemBlockStart + ItemBlockSize;
        public const int MoveBlockStride = 21 + TypeSlots;
        public const int MoveBlockSize = MoveBlockStride * ObservationSchema.MoveSlots;

        /// <summary>Twenty-five a bench position: §176's seven and its
        /// types.</summary>
        public const int TeamBlockStart = MoveBlockStart + MoveBlockSize;
        public const int TeamBlockStride = 7 + TypeSlots;
        public const int TeamBlockSize = TeamBlockStride * ObservationSchema.TeamSlots;

        /// <summary>Four a side: rocks, spikes, toxic spikes, sticky web.
        /// §184 had to strand the web at the end of the vector to keep the
        /// prefix rule; §311 put it back where it belongs.</summary>
        public const int HazardBlockStart = TeamBlockStart + TeamBlockSize;
        public const int HazardBlockStride = 4;
        public const int HazardBlockSize = HazardBlockStride * 2;

        /// <summary>Screens, rooms, the two one-hots and the spent
        /// mega/Z.</summary>
        public const int FieldBlockStart = HazardBlockStart + HazardBlockSize;
        public const int FieldBlockSize = 28;

        /// <summary>§311. Weather and terrain each have five members
        /// counting None, one-hot rather than §156's ordinal.</summary>
        public const int WeatherSlots = 5;
        public const int TerrainSlots = 5;

        /// <summary>Eight volatiles an active. The four stat stages §177 put
        /// here moved into the position block, where the other three already
        /// were.</summary>
        public const int ActiveBlockStart = FieldBlockStart + FieldBlockSize;
        public const int ActiveBlockStride = 8;
        public const int ActiveBlockSize = ActiveBlockStride * 2;

        /// <summary>§182's dial, still last.</summary>
        public const int RiskBlockStart = ActiveBlockStart + ActiveBlockSize;
        public const int RiskBlockSize = 1;

        /// <summary>What the blocks above add up to. ObservationSchema's
        /// FeatureCount must equal it, and Section311Tests says so - two
        /// constants that have to agree is exactly the shape of bug this
        /// vector has had before.</summary>
        public const int TotalFeatures = RiskBlockStart + RiskBlockSize;

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

            // §319: its OWN width. ObservationSchema.FeatureCount is the
            // CURRENT schema's, which is now V8's - and this is V7, kept as
            // the omniscient comparison encoder.
            var features = new float[TotalFeatures];

            // ================================================== 0-39 position
            int p = PositionBlockStart;

            features[p] = Fraction(self.CurrentHP, self.MaxHP);
            features[p + 1] = Fraction(target.CurrentHP, target.MaxHP);

            WriteStages(features, p + 2, self);
            WriteStages(features, p + 9, target);

            for (int i = 0; i < StatusFlags.Length; i++)
            {
                features[p + 16 + i] = self.Status == StatusFlags[i] ? 1f : 0f;
                features[p + 22 + i] = target.Status == StatusFlags[i] ? 1f : 0f;
            }

            features[p + 28] = self.Level / 100f;
            features[p + 29] = target.Level / 100f;

            double selfSpeed = Speed(state, self);
            double targetSpeed = Speed(state, target);

            // §176: under Trick Room the slower side moves first, and
            // BattleEngine sorts its queue that way. "Who is faster" has to
            // mean "who acts first" or the feature is a lie for the whole
            // five turns the room is up.
            features[p + 30] = MovesFirst(state, selfSpeed, targetSpeed) ? 1f : 0f;
            features[p + 31] = targetSpeed <= 0
                ? 1f
                : (float)(Math.Min(selfSpeed / targetSpeed, 3.0) / 3.0);

            features[p + 32] = Alive(actor) / 6f;
            features[p + 33] = Alive(opponent) / 6f;
            features[p + 34] = TeamHpFraction(actor);
            features[p + 35] = TeamHpFraction(opponent);

            // §311: footing, which decides whether a terrain reaches you,
            // whether Spikes bite, and whether Ground touches you at all.
            features[p + 36] = Grounding.IsGrounded(state, self) ? 1f : 0f;
            features[p + 37] = Grounding.IsGrounded(state, target) ? 1f : 0f;

            // ===================================================== 40-75 types
            WriteTypes(features, TypeBlockStart, self.Types);
            WriteTypes(features, TypeBlockStart + TypeSlots, target.Types);

            // ===================================================== 76-87 stats
            WriteStats(features, StatBlockStart, self);
            WriteStats(features, StatBlockStart + StatBlockStride, target);

            // ================================================= 88-105 abilities
            WriteAbilityShape(features, AbilityBlockStart, self);
            WriteAbilityShape(features, AbilityBlockStart + AbilityBlockStride, target);

            // ===================================================== 106-123 items
            WriteItemShape(features, ItemBlockStart, self);
            WriteItemShape(features, ItemBlockStart + ItemBlockStride, target);

            // ===================================================== 124-279 moves
            float[] legality = legalActions == null
                ? new float[ObservationSchema.MoveSlots]
                : LegalMoveMask(legalActions);

            var slots = SlotMoves(self);

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

                // §311: ability- and item-aware. Before this it was the type
                // chart alone, which is how the state came to claim that an
                // Earthquake doubles into a Levitate Bronzong.
                float effectiveness = move.Typeless ? 1f : Effectiveness(state, move.Type, target);
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

                features[at + 11] = HasEffect(move, "Heal") || HasEffect(move, "Drain") ? 1f : 0f;
                features[at + 12] = HasStatChange(move, "self", raising: true) ? 1f : 0f;
                features[at + 13] = HasStatChange(move, "opponent", raising: false) ? 1f : 0f;
                features[at + 14] = StatusChance(move);
                features[at + 15] = (float)Math.Clamp(Math.Max(move.FlinchChance, move.SecondaryChance), 0.0, 1.0);
                features[at + 16] = Math.Clamp((move.MinHits + move.MaxHits) / 2f, 1f, 5f) / 5f;
                features[at + 17] = SetsField(move) ? 1f : 0f;
                features[at + 18] = HasEffect(move, "Protect") || HasEffect(move, "Endure") ? 1f : 0f;
                features[at + 19] = CostsTheUser(move) ? 1f : 0f;

                // §311: cancelled outright by what the target IS or is
                // holding, as opposed to merely resisted. A zero here and a
                // zero at +1 are the same number; this says which of the two
                // reasons produced it, which is the thing a network can learn
                // an ability from.
                features[at + 20] = damaging && !move.Typeless && Nullifies(state, target, move.Type) ? 1f : 0f;

                if (!move.Typeless)
                    WriteTypes(features, at + 21, new[] { move.Type });

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

            // §311. The two "best available" summaries §175 put in the
            // global block. They are written after the move loop because that
            // is where they are worked out - the block they belong to is the
            // position, and nothing says a block has to be filled in one
            // pass.
            features[PositionBlockStart + 38] = bestEffectiveness;
            features[PositionBlockStart + 39] = bestDamage;

            // ===================================================== 280-429 bench
            for (int i = 0; i < ObservationSchema.TeamSlots; i++)
            {
                int at = TeamBlockStart + i * TeamBlockStride;

                if (i >= actor.Team.Count)
                    continue;                       // absent: the whole block stays zero

                PokemonState member = actor.Team[i];

                features[at] = 1f;                  // present
                features[at + 1] = member.Fainted ? 0f : 1f;
                features[at + 2] = Fraction(Math.Max(0, member.CurrentHP), member.MaxHP);
                features[at + 3] = ReferenceEquals(member, self) ? 1f : 0f;
                features[at + 4] = BestOffence(state, member, target);
                features[at + 5] = WorstDefence(state, target, member);
                features[at + 6] = MovesFirst(state, Speed(state, member), targetSpeed) ? 1f : 0f;

                // §311: what it IS, which is the half of a switch decision
                // the bench block never carried.
                WriteTypes(features, at + 7, member.Types);
            }

            // =================================================== 430-437 hazards
            int h = HazardBlockStart;

            bool actorIsP1 = ReferenceEquals(actor, state.Player1);

            features[h] = (actorIsP1 ? state.StealthRockP1 : state.StealthRockP2) ? 1f : 0f;
            features[h + 1] = Math.Clamp((actorIsP1 ? state.SpikesP1 : state.SpikesP2) / 3f, 0f, 1f);
            features[h + 2] = Math.Clamp((actorIsP1 ? state.ToxicSpikesP1 : state.ToxicSpikesP2) / 2f, 0f, 1f);
            features[h + 3] = (actorIsP1 ? state.StickyWebP1 : state.StickyWebP2) ? 1f : 0f;

            features[h + 4] = (actorIsP1 ? state.StealthRockP2 : state.StealthRockP1) ? 1f : 0f;
            features[h + 5] = Math.Clamp((actorIsP1 ? state.SpikesP2 : state.SpikesP1) / 3f, 0f, 1f);
            features[h + 6] = Math.Clamp((actorIsP1 ? state.ToxicSpikesP2 : state.ToxicSpikesP1) / 2f, 0f, 1f);
            features[h + 7] = (actorIsP1 ? state.StickyWebP2 : state.StickyWebP1) ? 1f : 0f;

            // ===================================================== 438-465 field
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

            // §311: one-hot, not an ordinal. §156 wrote the enum's value
            // straight in, so a network was told Sandstorm sits between Rain
            // and Hail on a number line - the one encoding mistake that is
            // guaranteed to teach a relationship that does not exist.
            int weather = (int)state.Environment.Weather;
            int terrain = (int)state.Environment.Terrain;

            if (weather >= 0 && weather < WeatherSlots)
                features[d + 14 + weather] = 1f;

            if (terrain >= 0 && terrain < TerrainSlots)
                features[d + 19 + terrain] = 1f;

            // §161 gives every side one mega and one Z-Move per battle;
            // whether they are still in hand changes what a turn is worth.
            features[d + 24] = actor.UsedMegaEvolution ? 1f : 0f;
            features[d + 25] = actor.UsedZMove ? 1f : 0f;
            features[d + 26] = opponent.UsedMegaEvolution ? 1f : 0f;
            features[d + 27] = opponent.UsedZMove ? 1f : 0f;

            // ================================================= 466-481 volatiles
            WriteActive(features, ActiveBlockStart, self);
            WriteActive(features, ActiveBlockStart + ActiveBlockStride, target);

            // ======================================================== 482 risk
            features[RiskBlockStart] = Math.Clamp(risk, 0f, 1f);

            return Finite(features);
        }

        static float Fraction(int part, int whole) =>
            whole <= 0 ? 0f : Math.Clamp(part / (float)whole, 0f, 1f);

        /// <summary>§311. All seven stages in one place and one order.
        /// §156 carried Attack, Defense and Speed; §177 appended Sp. Atk,
        /// Sp. Def, accuracy and evasion into a different block because the
        /// prefix rule left nowhere else to put them. There is no prefix rule
        /// any more, so they are together.</summary>
        static void WriteStages(float[] features, int at, PokemonState mon)
        {
            features[at] = mon.AttackStage;
            features[at + 1] = mon.DefenseStage;
            features[at + 2] = mon.SpAttackStage;
            features[at + 3] = mon.SpDefenseStage;
            features[at + 4] = mon.SpeedStage;
            features[at + 5] = mon.AccuracyStage;
            features[at + 6] = mon.EvasionStage;
        }

        /// <summary>§311. One-hot over the eighteen types. A Pokemon sets
        /// one or two of them; a move sets exactly one. Written by the enum's
        /// own value, so adding a type to PokemonType cannot silently shift
        /// the meaning of the columns after it - it widens the block, and
        /// Section311Tests catches the width.</summary>
        static void WriteTypes(float[] features, int at, IEnumerable<PokemonType> types)
        {
            foreach (PokemonType type in types)
            {
                int index = (int)type;

                if (index >= 0 && index < TypeSlots)
                    features[at + index] = 1f;
            }
        }

        /// <summary>§311. The six computed stats, each against a 255 ceiling
        /// that a level-100 spread does not reach. Computed rather than base,
        /// because what decides a turn is what this Pokemon's Attack IS, not
        /// what its species line starts from.</summary>
        static void WriteStats(float[] features, int at, PokemonState mon)
        {
            features[at] = Math.Clamp(mon.Stats.HP / 714f, 0f, 1f);
            features[at + 1] = Math.Clamp(mon.Stats.Attack / 600f, 0f, 1f);
            features[at + 2] = Math.Clamp(mon.Stats.Defense / 600f, 0f, 1f);
            features[at + 3] = Math.Clamp(mon.Stats.SpAttack / 600f, 0f, 1f);
            features[at + 4] = Math.Clamp(mon.Stats.SpDefense / 600f, 0f, 1f);
            features[at + 5] = Math.Clamp(mon.Stats.Speed / 600f, 0f, 1f);
        }

        /// <summary>
        /// §311. What this Pokemon's ability IS, in the shapes a decision
        /// turns on - read off the effects the engine has already attached to
        /// it rather than off a list of names kept here.
        ///
        /// That is the whole design. A network given a 308-wide one-hot of
        /// ability names would have 308 columns that are zero in almost every
        /// row and one that is one, and it would have to learn each name from
        /// scratch. A network given "this ability hooks the BeforeMove phase
        /// on defence and cancels a whole type" has something it can
        /// generalise from Levitate to Water Absorb without ever having seen
        /// Water Absorb.
        ///
        /// SIMULATED is the load-bearing one. §307 found that 184 of the
        /// dex's 308 ability names have no rule in this engine at all, and a
        /// network told that a Pokemon "has Anticipation" when Anticipation
        /// does nothing here would be learning noise. This column says which.
        /// </summary>
        internal static void WriteAbilityShape(float[] features, int at, PokemonState mon)
        {
            bool named = !string.IsNullOrWhiteSpace(mon.AbilityId);

            features[at] = named ? 1f : 0f;
            features[at + 1] = named && AbilityFactory.IsSupported(mon.AbilityId) ? 1f : 0f;

            foreach (IMoveEffect effect in mon.PassiveEffects)
            {
                switch (effect.Phase)
                {
                    case MovePhase.BeforeMove: features[at + 2] = 1f; break;
                    case MovePhase.CalculateStat: features[at + 3] = 1f; break;
                    case MovePhase.BeforeDamage: features[at + 4] = 1f; break;
                    case MovePhase.AfterDamage: features[at + 5] = 1f; break;
                    case MovePhase.AfterMove: features[at + 6] = 1f; break;
                }

                if (effect.Side == EffectSide.DefenderOnly)
                    features[at + 7] = 1f;

                if (effect is ITypeNullifier)
                    features[at + 8] = 1f;
            }
        }

        /// <summary>
        /// §311. What this Pokemon is holding, in the same shapes and for the
        /// same reason - and every one of these questions is answered by
        /// HeldItems rather than here, because §309 decided the engine owns
        /// items and a second list would be a second list to keep in step.
        /// </summary>
        internal static void WriteItemShape(float[] features, int at, PokemonState mon)
        {
            string? id = mon.HeldItemId;
            bool held = !string.IsNullOrWhiteSpace(id);

            features[at] = held ? 1f : 0f;

            if (!held)
                return;

            features[at + 1] = HeldItems.IsSupported(id) ? 1f : 0f;
            features[at + 2] = HeldItems.AffectsDamageOrKo(id) ? 1f : 0f;
            features[at + 3] = HeldItems.BoostsDamage(id) ? 1f : 0f;
            features[at + 4] = HeldItems.BoostsAStat(id) ? 1f : 0f;
            features[at + 5] = HeldItems.IsChoiceItem(id) ? 1f : 0f;
            features[at + 6] = HeldItems.IsResistBerry(id) ? 1f : 0f;
            features[at + 7] = HeldItems.HealsEachTurn(mon) ? 1f : 0f;
            features[at + 8] = HeldItems.SavesItsHolderOnce(id) ? 1f : 0f;
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
        /// <summary>
        /// §319. A move by the name it was SEEN to be used under.
        ///
        /// This is fair. The move was announced out loud, and what
        /// Earthquake does is public knowledge - a player who watches one
        /// land knows its type, its power and its category without being
        /// told anything hidden. What the fair encoder may not do is read
        /// the opponent's move LIST; reading the dex entry for a move it
        /// has already used is a different act entirely.
        /// </summary>
        internal static MoveState? LookUpMove(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            try
            {
                return Data.MoveDex.TryGet(name, out MoveState move) ? move : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

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

        /// <summary>
        /// §311. What this type really does to that Pokemon: the type chart
        /// product, and then the two things the chart does not know.
        ///
        /// The chart alone is what the encoder used to report, and it is
        /// wrong in the two cases that matter most. An ability can eat a
        /// whole type - Levitate, Flash Fire, Volt Absorb, the four
        /// AbsorbAbility spellings, Dry Skin - and until §311 the state told
        /// the network an Earthquake does double damage to a Bronzong. An
        /// unpopped Air Balloon does the same for Ground, which §309 made a
        /// real immunity in the engine and which nothing here consulted.
        ///
        /// Asked of the effect rather than of a list kept here, so an absorb
        /// added later is understood without this file being edited. See
        /// ITypeNullifier.
        /// </summary>
        internal static float Effectiveness(BattleState? state, PokemonType attacking, PokemonState defender)
        {
            if (Nullifies(state, defender, attacking))
                return 0f;

            double multiplier = 1.0;

            foreach (PokemonType defending in defender.Types)
                multiplier *= TypeChart.GetMultiplier(attacking, defending);

            return (float)multiplier;
        }

        /// <summary>§311. Whether what this Pokemon IS or is HOLDING cancels
        /// that type outright, as opposed to merely resisting it.</summary>
        internal static bool Nullifies(BattleState? state, PokemonState defender, PokemonType attacking)
        {
            foreach (IMoveEffect effect in defender.PassiveEffects)
            {
                if (effect is ITypeNullifier nullifier && nullifier.NullifiedType == attacking)
                    return true;
            }

            // Ground is the one the chart shares with the FIELD: Flying and
            // Levitate are already zero above, and an Air Balloon is not.
            return attacking == PokemonType.Ground && !Grounding.IsGrounded(state, defender);
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
        /// §177's volatiles - the eight things that decide whether a move is
        /// even worth picking. §311 took the four stat stages out of this
        /// block and put them with the other three in the position block,
        /// where they always belonged; only the prefix rule had kept them
        /// apart.
        /// </summary>
        internal static void WriteActive(float[] features, int at, PokemonState mon)
        {
            features[at] = mon.ConfusionTurns > 0 ? 1f : 0f;
            features[at + 1] = mon.LeechSeeded ? 1f : 0f;
            features[at + 2] = mon.SubstituteHP > 0 ? 1f : 0f;
            features[at + 3] = mon.TauntTurns > 0 ? 1f : 0f;
            features[at + 4] = mon.EncoreTurns > 0 ? 1f : 0f;
            features[at + 5] = mon.DisabledTurns > 0 ? 1f : 0f;
            features[at + 6] = mon.Trap != null ? 1f : 0f;
            features[at + 7] = mon.Charging ? 1f : 0f;
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
        internal static float BestOffence(BattleState? state, PokemonState attacker, PokemonState target)
        {
            float best = 0f;

            foreach (MoveState move in attacker.Moves)
            {
                if (move.Category == MoveCategory.Status || move.Power <= 0 || move.Typeless)
                    continue;

                // §311: a bench member whose only Ground move meets a
                // Levitate used to read as its best answer to the thing in
                // front of it. It reads as zero now, which is what it is.
                float effectiveness = Effectiveness(state, move.Type, target);

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
        internal static float WorstDefence(BattleState? state, PokemonState attacker, PokemonState defender)
        {
            float worst = 0f;

            foreach (PokemonType type in attacker.Types)
            {
                float effectiveness = Effectiveness(state, type, defender);

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
            long current = 0;
            long max = 0;

            foreach (PokemonState pokemon in side.Team)
            {
                current += Math.Max(0, pokemon.CurrentHP);
                max += Math.Max(0, pokemon.MaxHP);
            }

            return max <= 0 ? 0f : (float)(current / (double)max);
        }
    }
}