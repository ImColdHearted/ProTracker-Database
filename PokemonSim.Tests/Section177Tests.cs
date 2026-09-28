using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Engine;
using PokemonSim.Models;
using PokemonSim.Observation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 177. The field conditions, and the stat stages that were
    /// never there.
    ///
    /// The second half of that is the more interesting one. Section 156's
    /// ten floats carried the Attack, Defense and Speed stages and stopped.
    /// SpAttack and SpDefense were simply absent, so every model this
    /// project has trained has been blind to Nasty Plot, Calm Mind, and
    /// every special wall that ever set up in front of it. These tests pin
    /// them in, alongside the volatiles that decide whether a move is worth
    /// picking at all, and pin that everything before entry 160 is still
    /// exactly what section 176 wrote.
    /// </summary>
    public class Section177Tests
    {
        static int Field(int offset) => ObserverEncoder.FieldBlockStart + offset;

        /// <summary>§311. §177's per-active block was four stat stages then
        /// eight volatiles. The stages moved into the position block, beside
        /// the three §156 already had; the volatiles stayed. These two keep
        /// §177's own offsets - 0-3 a stage, 4-11 a volatile - and send each
        /// to wherever §311 put it, so every fact below still asserts the
        /// thing it was written to assert.</summary>
        static int Mine(int offset) => offset < 4
            ? ObserverEncoder.PositionBlockStart + 2 + StageOffset(offset)
            : ObserverEncoder.ActiveBlockStart + (offset - 4);

        static int Theirs(int offset) => offset < 4
            ? ObserverEncoder.PositionBlockStart + 9 + StageOffset(offset)
            : ObserverEncoder.ActiveBlockStart + ObserverEncoder.ActiveBlockStride + (offset - 4);

        /// <summary>§177 wrote SpAtk, SpDef, accuracy, evasion; §311's seven
        /// are Atk, Def, SpAtk, SpDef, Speed, accuracy, evasion.</summary>
        static int StageOffset(int section177Offset) => section177Offset switch
        {
            0 => 2,   // SpAttack
            1 => 3,   // SpDefense
            2 => 5,   // accuracy
            3 => 6,   // evasion
            _ => throw new ArgumentOutOfRangeException(nameof(section177Offset)),
        };

        static (BattleState State, PokemonState Self, PokemonState Foe) Setup(int seed = 777)
        {
            var a = TestKit.Mon("Alpha", hp: 200, moves: TestKit.Move("Hit"));
            var b = TestKit.Mon("Beta", hp: 200, moves: TestKit.Move("Hit"));
            (BattleState state, _) = TestKit.Duel(seed, a, b);
            return (state, a, b);
        }

        // ---------------- layout ----------------

        [Fact]
        public void TheTwoNewBlocksSitBehindTheSection176Vector()
        {
            Assert.Equal(ObserverEncoder.HazardBlockStart + ObserverEncoder.HazardBlockSize,
                         ObserverEncoder.FieldBlockStart);

            Assert.Equal(ObserverEncoder.FieldBlockStart + ObserverEncoder.FieldBlockSize,
                         ObserverEncoder.ActiveBlockStart);

            // Two actives, self then opponent, and then section 177's
            // vector is full. Section 182 appended one feature after it,
            // so this is now the width of V4 rather than of the whole
            // thing - which is exactly what "every earlier width is a
            // prefix" has to mean once something is added.
            Assert.Equal(ObserverEncoder.RiskBlockStart,
                         ObserverEncoder.ActiveBlockStart + 2 * ObserverEncoder.ActiveBlockStride);

            Assert.Equal(6, ObservationSchema.Version);
            Assert.Equal(205, ObservationSchema.FeatureCount);
            // §311: the retired widths are history rather than compatibility
            // now, and this is where they are written down.
            Assert.Equal(new[] { 10, 76, 160, 202, 203, 205 },
                         ObservationSchema.RetiredFeatureCounts);
            Assert.Equal(new[] { ObservationSchema.FeatureCount },
                         ObservationSchema.AcceptedFeatureCounts);

            // Newest first, and every earlier width is genuinely smaller -
            // the prefix rule the evaluator leans on.
            Assert.Equal(
                ObservationSchema.AcceptedFeatureCounts.OrderByDescending(w => w).ToArray(),
                ObservationSchema.AcceptedFeatureCounts);

            Assert.Equal(new[] { 205, 203, 202, 160, 76, 10 }, ObservationSchema.AcceptedFeatureCounts);
        }

        [Fact]
        public void EverythingSection176WroteIsStillWhereItWas()
        {
            (BattleState state, PokemonState self, PokemonState foe) = Setup(778);

            self.CurrentHP = 100;                    // of 200
            self.AttackStage = 2;
            foe.SpeedStage = -1;
            state.Environment.Weather = WeatherType.Rain;
            state.StealthRockP1 = true;

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            Assert.Equal(ObservationSchema.FeatureCount, f.Length);

            // §311: the landmarks are still all there, in the blocks they
            // were re-laid into. Weather is the one that changed SHAPE as
            // well as position - an ordinal at f[8] became a one-hot, which
            // is the whole reason §311 was willing to break the layout.
            Assert.Equal(0.5f, f[0]);                                    // hp fraction
            Assert.Equal(2f, f[ObserverEncoder.PositionBlockStart + 2]); // my Attack stage
            Assert.Equal(-1f, f[ObserverEncoder.PositionBlockStart + 10]); // their Defense stage
            Assert.Equal(1f, f[Field(14) + (int)WeatherType.Rain]);
            Assert.Equal(0.5f, f[ObserverEncoder.PositionBlockStart + 28]); // §175's level, re-laid
            Assert.Equal(1f, f[ObserverEncoder.TeamBlockStart]);          // section 176, present
            Assert.Equal(1f, f[ObserverEncoder.HazardBlockStart]);        // section 176, rocks
        }

        // ---------------- the stages that were missing ----------------

        [Fact]
        public void TheSpecialStagesAreFinallyInTheVector()
        {
            (BattleState state, PokemonState self, PokemonState foe) = Setup(779);

            // Nasty Plot on my side, Calm Mind on theirs - neither of which
            // any model before section 177 could see at all.
            self.SpAttackStage = 2;
            self.SpDefenseStage = -1;
            self.AccuracyStage = 1;
            self.EvasionStage = -2;

            foe.SpAttackStage = 1;
            foe.SpDefenseStage = 1;

            float[] p1 = ObserverEncoder.Encode(state, state.Player1);
            float[] p2 = ObserverEncoder.Encode(state, state.Player2);

            Assert.Equal(2f, p1[Mine(0)]);
            Assert.Equal(-1f, p1[Mine(1)]);
            Assert.Equal(1f, p1[Mine(2)]);
            Assert.Equal(-2f, p1[Mine(3)]);

            Assert.Equal(1f, p1[Theirs(0)]);
            Assert.Equal(1f, p1[Theirs(1)]);

            // Perspective mirrors, exactly as the section 156 stages do.
            Assert.Equal(p1[Mine(0)], p2[Theirs(0)]);
            Assert.Equal(p1[Theirs(1)], p2[Mine(1)]);
        }

        [Fact]
        public void TheVolatilesThatDecideWhetherAMoveIsWorthPickingAreThere()
        {
            (BattleState state, PokemonState self, PokemonState foe) = Setup(780);

            self.ConfusionTurns = 2;
            self.LeechSeeded = true;
            self.TauntTurns = 3;
            self.Charging = true;

            foe.SubstituteHP = 50;
            foe.EncoreTurns = 2;
            foe.DisabledTurns = 4;

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            Assert.Equal(1f, f[Mine(4)]);        // confused
            Assert.Equal(1f, f[Mine(5)]);        // leech seeded
            Assert.Equal(0f, f[Mine(6)]);        // no substitute
            Assert.Equal(1f, f[Mine(7)]);        // taunted
            Assert.Equal(0f, f[Mine(8)]);
            Assert.Equal(0f, f[Mine(9)]);
            Assert.Equal(0f, f[Mine(10)]);       // not trapped
            Assert.Equal(1f, f[Mine(11)]);       // charging

            Assert.Equal(1f, f[Theirs(6)]);      // behind a substitute
            Assert.Equal(1f, f[Theirs(8)]);      // encored
            Assert.Equal(1f, f[Theirs(9)]);      // a move disabled
            Assert.Equal(0f, f[Theirs(4)]);
        }

        [Fact]
        public void BeingTrappedIsVisibleToTheSwitchHead()
        {
            (BattleState state, PokemonState self, _) = Setup(781);

            Assert.Equal(0f, ObserverEncoder.Encode(state, state.Player1)[Mine(10)]);

            self.Trap = new PokemonState.TrapEffect
            {
                TurnsRemaining = 3,
                DamageFraction = 0.125,
                SourceMove = "Whirlpool"
            };

            Assert.Equal(1f, ObserverEncoder.Encode(state, state.Player1)[Mine(10)]);
        }

        // ---------------- the field ----------------

        [Fact]
        public void ScreensAndSideConditionsReadMineThenTheirs()
        {
            (BattleState state, _, _) = Setup(782);

            state.ReflectTurnsP1 = 5;
            state.LightScreenTurnsP1 = 1;
            state.SafeguardTurnsP2 = 5;
            state.TailwindTurnsP2 = 2;

            float[] p1 = ObserverEncoder.Encode(state, state.Player1);
            float[] p2 = ObserverEncoder.Encode(state, state.Player2);

            Assert.Equal(1f, p1[Field(0)]);            // my Reflect, just set
            Assert.Equal(0.2f, p1[Field(1)], 4);       // my Light Screen, one turn left
            Assert.Equal(0f, p1[Field(2)]);
            Assert.Equal(0f, p1[Field(3)]);

            Assert.Equal(1f, p1[Field(8)]);            // their Safeguard
            Assert.Equal(0.4f, p1[Field(9)], 4);       // their Tailwind

            // The other seat sees the same field the other way round.
            Assert.Equal(p1[Field(0)], p2[Field(5)]);
            Assert.Equal(p1[Field(8)], p2[Field(3)]);
            Assert.Equal(p1[Field(9)], p2[Field(4)]);
        }

        [Fact]
        public void TrickRoomGravityAndTheWeatherClockAreEncoded()
        {
            (BattleState state, _, _) = Setup(783);

            state.TrickRoomTurns = 5;
            state.GravityTurns = 2;
            state.Environment.Weather = WeatherType.Sandstorm;
            state.Environment.WeatherTurns = 8;
            state.Environment.Terrain = TerrainType.Grassy;
            state.Environment.TerrainTurns = 4;

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            Assert.Equal(1f, f[Field(10)]);
            Assert.Equal(0.4f, f[Field(11)], 4);
            Assert.Equal(1f, f[Field(12)]);
            Assert.Equal(0.5f, f[Field(13)], 4);

            // §311: the weather itself is a one-hot now, not §156's ordinal.
            // Exactly one of the five is lit, which is the property an
            // ordinal could never have.
            Assert.Equal(1f, f[Field(14) + (int)WeatherType.Sandstorm]);
            Assert.Equal(1f, f[Field(19) + (int)TerrainType.Grassy]);

            Assert.Equal(1, Enumerable.Range(0, ObserverEncoder.WeatherSlots)
                             .Count(i => f[Field(14) + i] > 0f));
            Assert.Equal(1, Enumerable.Range(0, ObserverEncoder.TerrainSlots)
                             .Count(i => f[Field(19) + i] > 0f));
        }

        [Fact]
        public void ASideConditionsClockRunsDownRatherThanSwitchingOff()
        {
            (BattleState state, _, _) = Setup(784);

            var seen = new List<float>();

            for (int turns = 5; turns >= 0; turns--)
            {
                state.ReflectTurnsP1 = turns;
                seen.Add(ObserverEncoder.Encode(state, state.Player1)[Field(0)]);
            }

            // Six distinct readings, falling - "just set" and "about to
            // drop" are different numbers, not the same flag.
            Assert.Equal(seen.Count, seen.Distinct().Count());
            Assert.Equal(seen.OrderByDescending(v => v).ToList(), seen);
            Assert.Equal(1f, seen.First());
            Assert.Equal(0f, seen.Last());
        }

        [Fact]
        public void SpentMegasAndZMovesAreVisibleToBothSides()
        {
            (BattleState state, _, _) = Setup(785);

            float[] before = ObserverEncoder.Encode(state, state.Player1);

            Assert.Equal(0f, before[Field(14)]);
            Assert.Equal(0f, before[Field(17)]);

            state.Player1.UsedMegaEvolution = true;
            state.Player2.UsedZMove = true;

            float[] p1 = ObserverEncoder.Encode(state, state.Player1);
            float[] p2 = ObserverEncoder.Encode(state, state.Player2);

            Assert.Equal(1f, p1[Field(14)]);       // I spent my mega
            Assert.Equal(0f, p1[Field(15)]);
            Assert.Equal(0f, p1[Field(16)]);
            Assert.Equal(1f, p1[Field(17)]);       // they spent their Z

            Assert.Equal(p1[Field(14)], p2[Field(16)]);
            Assert.Equal(p1[Field(17)], p2[Field(15)]);
        }

        // ---------------- the invariants that must survive ----------------

        [Fact]
        public void TheWholeVectorStaysFiniteUnderAbsurdInputs()
        {
            var a = TestKit.Mon("Alpha", hp: 1, speed: 1, moves: TestKit.Move("Hit", power: 0, pp: 0));
            var b = TestKit.Mon("Beta", hp: 1, defense: 1, speed: 1, moves: TestKit.Move("Hit"));

            (BattleState state, BattleEngine engine) = TestKit.Duel(786, a, b);

            a.CurrentHP = 0;
            b.CurrentHP = 0;
            a.SpAttackStage = 6;
            b.EvasionStage = -6;
            a.ConfusionTurns = 99;
            state.TrickRoomTurns = 99;
            state.ReflectTurnsP1 = 99;
            state.Environment.WeatherTurns = 99;

            foreach (PlayerState side in new[] { state.Player1, state.Player2 })
            {
                float[] f = ObserverEncoder.Encode(state, side, engine.GetLegalActions(side));

                Assert.Equal(ObservationSchema.FeatureCount, f.Length);
                Assert.All(f, v => Assert.True(float.IsFinite(v), $"non-finite feature {v}"));
            }
        }

        [Fact]
        public void EveryClockedFieldFeatureStaysInsideZeroToOne()
        {
            (BattleState state, _, _) = Setup(787);

            state.ReflectTurnsP1 = 500;
            state.TailwindTurnsP2 = 500;
            state.GravityTurns = 500;
            state.Environment.TerrainTurns = 500;

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            for (int i = 0; i < ObserverEncoder.FieldBlockSize; i++)
                Assert.InRange(f[Field(i)], 0f, 1f);
        }

        [Fact]
        public void EncodingStillNeverRollsTheBattlesDice()
        {
            (BattleState state, PokemonState self, _) = Setup(788);

            self.SpAttackStage = 3;
            state.ReflectTurnsP1 = 4;
            state.TrickRoomTurns = 2;

            BattleRng before = state.Rng.Clone();

            ObserverEncoder.Encode(state, state.Player1);
            ObserverEncoder.Encode(state, state.Player2);

            BattleRng after = state.Rng.Clone();

            for (int i = 0; i < 25; i++)
                Assert.Equal(before.Next(1000000), after.Next(1000000));
        }

        [Fact]
        public void AnUntouchedBattleLeavesTheWholeFieldBlockAtZero()
        {
            (BattleState state, _, _) = Setup(789);

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            for (int i = 0; i < ObserverEncoder.FieldBlockSize; i++)
                Assert.Equal(0f, f[Field(i)]);

            // And every volatile flag on both actives, which is what makes
            // a set flag mean something. §177's offsets 4-11 are §311's
            // whole block, which Mine and Theirs map for us.
            for (int offset = 4; offset < 4 + ObserverEncoder.ActiveBlockStride; offset++)
            {
                Assert.Equal(0f, f[Mine(offset)]);
                Assert.Equal(0f, f[Theirs(offset)]);
            }
        }

        [Fact]
        public void WriteActiveFillsExactlyItsOwnStride()
        {
            var mon = TestKit.Mon("Solo");
            mon.ConfusionTurns = 2;
            mon.LeechSeeded = true;

            var features = new float[ObserverEncoder.ActiveBlockStride * 3];

            ObserverEncoder.WriteActive(features, ObserverEncoder.ActiveBlockStride, mon);

            // Its own stride is written; nothing either side of it is.
            for (int i = 0; i < ObserverEncoder.ActiveBlockStride; i++)
            {
                Assert.Equal(0f, features[i]);
                Assert.Equal(0f, features[ObserverEncoder.ActiveBlockStride * 2 + i]);
            }

            // §311: the stat stages left this block, so confusion is its
            // first entry and the seed its second.
            Assert.Equal(1f, features[ObserverEncoder.ActiveBlockStride]);
            Assert.Equal(1f, features[ObserverEncoder.ActiveBlockStride + 1]);
        }
    }
}