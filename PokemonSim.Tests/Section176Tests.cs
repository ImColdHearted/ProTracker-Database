using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using PokemonSim.Shadow;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 176. The switch head, and the three feature blocks that make
    /// switching decidable.
    ///
    /// Section 175 fixed the move head by telling the model what the four
    /// move slots contain. A switch head without the same treatment would
    /// have repeated the mistake exactly: nothing in the state said what
    /// was ON the bench, so "send in team slot 3" would have been as
    /// unpredictable as "use move slot 3" once was. These tests pin the
    /// bench, the hazards and the per-move effect flags alongside the
    /// output itself, and pin that everything section 175 wrote is still
    /// the untouched front of the vector.
    /// </summary>
    public class Section176Tests : IDisposable
    {
        readonly string dir = Path.Combine(
            Path.GetTempPath(), "protracker-176-tests", Guid.NewGuid().ToString("N"));

        readonly List<string> temporaryFiles = new();

        public void Dispose()
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }

            foreach (string file in temporaryFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); }
                catch { /* best effort */ }
            }
        }

        // ---------------- helpers ----------------

        static PokemonState Typed(string species, PokemonType[] types, int hp = 200,
                                  int attack = 100, int defense = 100, int speed = 100,
                                  params MoveState[] moves)
        {
            PokemonState mon = TestKit.Mon(species, types[0], hp, attack, defense, speed, moves);
            mon.Types = types.ToList();
            return mon;
        }

        static int Effect(int slot, int offset) =>
            ObserverEncoder.EffectBlockStart + slot * ObserverEncoder.EffectBlockStride + offset;

        static int Team(int slot, int offset) =>
            ObserverEncoder.TeamBlockStart + slot * ObserverEncoder.TeamBlockStride + offset;

        static int Hazard(int offset) => ObserverEncoder.HazardBlockStart + offset;

        static int Switch(int teamIndex) => ObservationSchema.MoveSlots + teamIndex;

        // ---------------- layout ----------------

        [Fact]
        public void TheThreeNewBlocksSitBehindTheSection175Vector()
        {
            // Everything section 175 wrote is untouched, and the new blocks
            // start exactly where it stopped. That is the whole reason a
            // 76-input model still loads and still behaves.
            Assert.Equal(ObservationSchema.FeatureCountV2, ObserverEncoder.EffectBlockStart);

            Assert.Equal(
                ObserverEncoder.EffectBlockStart +
                ObservationSchema.MoveSlots * ObserverEncoder.EffectBlockStride,
                ObserverEncoder.TeamBlockStart);

            Assert.Equal(
                ObserverEncoder.TeamBlockStart +
                ObservationSchema.TeamSlots * ObserverEncoder.TeamBlockStride,
                ObserverEncoder.HazardBlockStart);

            // Section 177 appended behind this, so what the hazard block
            // now ends is the section 176 vector - which is exactly the
            // prefix an older model is fed.
            Assert.Equal(ObservationSchema.FeatureCountV3,
                         ObserverEncoder.HazardBlockStart + ObserverEncoder.HazardBlockSize);

            Assert.Equal(ObservationSchema.MoveSlots + ObservationSchema.TeamSlots,
                         ObservationSchema.ActionSlots);

            Assert.Equal(new[] { ObservationSchema.ActionSlots, ObservationSchema.MoveSlots },
                         ObservationSchema.AcceptedActionCounts);

            // Newest first, and every earlier width really is smaller - the
            // prefix rule the evaluator leans on.
            Assert.Equal(
                ObservationSchema.AcceptedFeatureCounts.OrderByDescending(w => w).ToArray(),
                ObservationSchema.AcceptedFeatureCounts);
        }

        // ---------------- the effect flags ----------------

        static MoveState WithEffects(MoveState move, params string[] effects)
        {
            move.Effects = effects.ToList();
            return move;
        }

        [Fact]
        public void EachSlotSaysWhatItsMoveActuallyDoes()
        {
            MoveState recover = WithEffects(
                TestKit.Move("Recover", PokemonType.Normal, MoveCategory.Status, power: 0), "Heal");

            MoveState swordsDance = TestKit.Move("Swords Dance", PokemonType.Normal, MoveCategory.Status, power: 0);
            swordsDance.StatChanges = new List<StatChange>
            {
                new StatChange { Stat = "Attack", Stages = 2, Target = "self" }
            };

            MoveState growl = TestKit.Move("Growl", PokemonType.Normal, MoveCategory.Status, power: 0);
            growl.StatChanges = new List<StatChange>
            {
                new StatChange { Stat = "Attack", Stages = -1, Target = "opponent" }
            };

            MoveState wisp = TestKit.Move("Will-O-Wisp", PokemonType.Fire, MoveCategory.Status, power: 0);
            wisp.InflictStatus = StatusCondition.Burn;

            var attacker = Typed("Attacker", new[] { PokemonType.Normal },
                moves: new[] { recover, swordsDance, growl, wisp });

            var target = Typed("Target", new[] { PokemonType.Normal });

            (BattleState state, _) = TestKit.Duel(760, attacker, target);

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            Assert.Equal(1f, f[Effect(0, 0)]);       // heals
            Assert.Equal(0f, f[Effect(1, 0)]);

            Assert.Equal(1f, f[Effect(1, 1)]);       // raises its own stats
            Assert.Equal(0f, f[Effect(0, 1)]);

            Assert.Equal(1f, f[Effect(2, 2)]);       // lowers the target's
            Assert.Equal(0f, f[Effect(1, 2)]);

            // A move that names a status but gives no chance always applies.
            Assert.Equal(1f, f[Effect(3, 3)]);
            Assert.Equal(0f, f[Effect(0, 3)]);
        }

        [Fact]
        public void ChancesHitsFieldSettersProtectAndSelfCostAreEncoded()
        {
            MoveState flinchy = TestKit.Move("Rock Slide", PokemonType.Rock, power: 75);
            flinchy.FlinchChance = 0.3;

            MoveState multi = TestKit.Move("Bullet Seed", PokemonType.Grass, power: 25);
            multi.MinHits = 2;
            multi.MaxHits = 5;

            MoveState rocks = WithEffects(
                TestKit.Move("Stealth Rock", PokemonType.Rock, MoveCategory.Status, power: 0), "StealthRock");

            MoveState reckless = WithEffects(
                TestKit.Move("Double-Edge", PokemonType.Normal, power: 120), "Recoil33");

            var attacker = Typed("Attacker", new[] { PokemonType.Normal },
                moves: new[] { flinchy, multi, rocks, reckless });

            var target = Typed("Target", new[] { PokemonType.Normal });

            (BattleState state, _) = TestKit.Duel(761, attacker, target);

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            Assert.Equal(0.3f, f[Effect(0, 4)], 4);          // flinch chance
            Assert.Equal(1f / 5f, f[Effect(0, 5)], 4);       // a single hit

            Assert.Equal(3.5f / 5f, f[Effect(1, 5)], 4);     // two to five hits
            Assert.Equal(1f, f[Effect(2, 6)]);               // sets hazards
            Assert.Equal(0f, f[Effect(0, 6)]);
            Assert.Equal(1f, f[Effect(3, 8)]);               // recoil costs the user
            Assert.Equal(0f, f[Effect(1, 8)]);
        }

        [Fact]
        public void WeatherSettersAndProtectCount()
        {
            MoveState rainDance = TestKit.Move("Rain Dance", PokemonType.Water, MoveCategory.Status, power: 0);
            rainDance.SetWeather = WeatherType.Rain;

            MoveState protect = WithEffects(
                TestKit.Move("Protect", PokemonType.Normal, MoveCategory.Status, power: 0), "Protect");

            var attacker = Typed("Attacker", new[] { PokemonType.Water },
                moves: new[] { rainDance, protect });

            var target = Typed("Target", new[] { PokemonType.Normal });

            (BattleState state, _) = TestKit.Duel(762, attacker, target);

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            Assert.Equal(1f, f[Effect(0, 6)]);
            Assert.Equal(1f, f[Effect(1, 7)]);
            Assert.Equal(0f, f[Effect(0, 7)]);
        }

        // ---------------- the bench ----------------

        [Fact]
        public void EveryTeamPositionDescribesItselfAgainstTheOpposingActive()
        {
            var active = Typed("Active", new[] { PokemonType.Normal }, hp: 200, speed: 100,
                moves: new[] { TestKit.Move("Tackle", PokemonType.Normal, power: 50) });

            // A Water attacker that outspeeds and resists nothing special.
            var bench = Typed("Bench", new[] { PokemonType.Water }, hp: 200, speed: 200,
                moves: new[] { TestKit.Move("Surf", PokemonType.Water, power: 90) });

            var fainted = Typed("Fainted", new[] { PokemonType.Normal }, hp: 200,
                moves: new[] { TestKit.Move("Tackle", PokemonType.Normal, power: 50) });

            var target = Typed("Target", new[] { PokemonType.Fire }, hp: 200, speed: 150,
                moves: new[] { TestKit.Move("Ember", PokemonType.Fire, power: 60) });

            (BattleState state, _) = TestKit.Battle(763,
                new List<PokemonState> { active, bench, fainted },
                new List<PokemonState> { target });

            fainted.CurrentHP = 0;
            bench.CurrentHP = 100;      // half

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            // slot 0 - the one that is out
            Assert.Equal(1f, f[Team(0, 0)]);       // present
            Assert.Equal(1f, f[Team(0, 1)]);       // alive
            Assert.Equal(1f, f[Team(0, 2)]);       // full hp
            Assert.Equal(1f, f[Team(0, 3)]);       // is the active
            Assert.Equal(0f, f[Team(0, 6)]);       // 100 does not outspeed 150

            // slot 1 - the healthy bench Water type
            Assert.Equal(1f, f[Team(1, 0)]);
            Assert.Equal(1f, f[Team(1, 1)]);
            Assert.Equal(0.5f, f[Team(1, 2)]);
            Assert.Equal(0f, f[Team(1, 3)]);
            Assert.Equal(2f, f[Team(1, 4)]);       // Water into Fire is 2x
            Assert.Equal(0.5f, f[Team(1, 5)]);     // Fire into Water is 0.5x
            Assert.Equal(1f, f[Team(1, 6)]);       // 200 outspeeds 150

            // slot 2 - present but down
            Assert.Equal(1f, f[Team(2, 0)]);
            Assert.Equal(0f, f[Team(2, 1)]);
            Assert.Equal(0f, f[Team(2, 2)]);

            // slots 3 to 5 - nobody there, so the whole block is zero
            for (int slot = 3; slot < ObservationSchema.TeamSlots; slot++)
            {
                for (int offset = 0; offset < ObserverEncoder.TeamBlockStride; offset++)
                    Assert.Equal(0f, f[Team(slot, offset)]);
            }
        }

        [Fact]
        public void TheBenchIsJudgedOnTypesNotOnTheOpponentsHiddenMoveset()
        {
            // A player cannot see an opponent's moves. The defensive
            // reading must therefore come from the opposing active's TYPES,
            // which are on the screen - not from the moves it happens to
            // be carrying, which would be the model learning to cheat.
            var active = Typed("Active", new[] { PokemonType.Normal });

            var bench = Typed("Bench", new[] { PokemonType.Grass }, moves: new[]
            {
                TestKit.Move("Vine Whip", PokemonType.Grass, power: 45)
            });

            // Water type, but its only move is Ice - which would read 2x
            // into Grass if the encoder were peeking at movesets.
            var target = Typed("Target", new[] { PokemonType.Water }, moves: new[]
            {
                TestKit.Move("Ice Beam", PokemonType.Ice, MoveCategory.Special, power: 90)
            });

            (BattleState state, _) = TestKit.Battle(764,
                new List<PokemonState> { active, bench },
                new List<PokemonState> { target });

            float[] f = ObserverEncoder.Encode(state, state.Player1);

            // Water into Grass is 0.5x. Ice into Grass would be 2x.
            Assert.Equal(0.5f, f[Team(1, 5)]);
            Assert.Equal(2f, f[Team(1, 4)]);       // Grass into Water is 2x
        }

        // ---------------- hazards ----------------

        [Fact]
        public void HazardsReadMineThenTheirsFromEitherSeat()
        {
            var a = TestKit.Mon("Alpha", moves: TestKit.Move("Hit"));
            var b = TestKit.Mon("Beta", moves: TestKit.Move("Hit"));

            (BattleState state, _) = TestKit.Duel(765, a, b);

            state.StealthRockP1 = true;
            state.SpikesP1 = 3;
            state.ToxicSpikesP2 = 1;

            float[] p1 = ObserverEncoder.Encode(state, state.Player1);
            float[] p2 = ObserverEncoder.Encode(state, state.Player2);

            Assert.Equal(1f, p1[Hazard(0)]);          // rocks on my side
            Assert.Equal(1f, p1[Hazard(1)]);          // three layers of spikes
            Assert.Equal(0f, p1[Hazard(2)]);
            Assert.Equal(0f, p1[Hazard(3)]);          // nothing on theirs
            Assert.Equal(0.5f, p1[Hazard(5)]);        // one toxic spike layer

            // The other seat sees the same field the other way round.
            Assert.Equal(p1[Hazard(0)], p2[Hazard(3)]);
            Assert.Equal(p1[Hazard(1)], p2[Hazard(4)]);
            Assert.Equal(p1[Hazard(5)], p2[Hazard(2)]);
        }

        [Fact]
        public void TrickRoomFlipsWhoActsFirst()
        {
            var slow = TestKit.Mon("Slow", speed: 40, moves: TestKit.Move("Hit"));
            var fast = TestKit.Mon("Fast", speed: 300, moves: TestKit.Move("Hit"));

            (BattleState state, _) = TestKit.Duel(766, slow, fast);

            int outspeeds = ObserverEncoder.GlobalBlockStart;

            Assert.Equal(0f, ObserverEncoder.Encode(state, state.Player1)[outspeeds]);

            // BattleEngine sorts its queue the other way round while the
            // room is up, so the feature has to as well.
            state.TrickRoomTurns = 5;

            Assert.Equal(1f, ObserverEncoder.Encode(state, state.Player1)[outspeeds]);
            Assert.Equal(0f, ObserverEncoder.Encode(state, state.Player2)[outspeeds]);
        }

        [Fact]
        public void EveryFeatureIsStillFinite()
        {
            var a = Typed("Alpha", new[] { PokemonType.Ghost }, hp: 1, speed: 1,
                moves: new[] { TestKit.Move("Nothing", PokemonType.Normal, MoveCategory.Status, power: 0, pp: 0) });

            var spare = Typed("Spare", new[] { PokemonType.Normal }, hp: 1, speed: 1);
            var b = Typed("Beta", new[] { PokemonType.Flying }, hp: 1, defense: 1, speed: 1);

            (BattleState state, BattleEngine engine) = TestKit.Battle(767,
                new List<PokemonState> { a, spare },
                new List<PokemonState> { b });

            a.CurrentHP = 0;
            b.CurrentHP = 0;
            state.TrickRoomTurns = 3;
            state.SpikesP1 = 3;

            foreach (PlayerState side in new[] { state.Player1, state.Player2 })
            {
                float[] f = ObserverEncoder.Encode(state, side, engine.GetLegalActions(side));

                Assert.Equal(ObservationSchema.FeatureCount, f.Length);
                Assert.All(f, value => Assert.True(float.IsFinite(value), $"non-finite feature {value}"));
            }
        }

        // ---------------- the masks ----------------

        [Fact]
        public void TheActionMaskCoversMovesThenTeamPositions()
        {
            var active = TestKit.Mon("Active", moves: new[] { TestKit.Move("One"), TestKit.Move("Two") });
            var bench = TestKit.Mon("Bench", moves: TestKit.Move("Hit"));
            var down = TestKit.Mon("Down", moves: TestKit.Move("Hit"));
            var foe = TestKit.Mon("Foe", moves: TestKit.Move("Hit"));

            (BattleState state, BattleEngine engine) = TestKit.Battle(768,
                new List<PokemonState> { active, bench, down },
                new List<PokemonState> { foe });

            down.CurrentHP = 0;

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);

            float[] moves = ObserverEncoder.LegalMoveMask(legal);
            float[] actions = ObserverEncoder.LegalActionMask(legal, state.Player1);

            Assert.Equal(ObservationSchema.ActionSlots, actions.Length);

            // Its first four entries ARE the move mask, which is what lets
            // a four-output model read the front of it unchanged.
            for (int i = 0; i < ObservationSchema.MoveSlots; i++)
                Assert.Equal(moves[i], actions[i]);

            Assert.Equal(0f, actions[Switch(0)]);      // cannot switch to itself
            Assert.Equal(1f, actions[Switch(1)]);      // the healthy bench
            Assert.Equal(0f, actions[Switch(2)]);      // fainted

            // And the mask agrees with what the engine actually offered.
            foreach (BattleAction action in legal.Where(a => a.Type == BattleActionType.Switch))
                Assert.Equal(1f, actions[Switch(state.Player1.Team.IndexOf(action.SwitchTarget!))]);
        }

        [Fact]
        public void AReplacementMaskOffersOnlyTeamPositions()
        {
            float[] mask = ObserverEncoder.ReplacementMask(new[] { 1, 3 });

            Assert.Equal(ObservationSchema.ActionSlots, mask.Length);

            for (int i = 0; i < ObservationSchema.MoveSlots; i++)
                Assert.Equal(0f, mask[i]);

            Assert.Equal(1f, mask[Switch(1)]);
            Assert.Equal(1f, mask[Switch(3)]);
            Assert.Equal(0f, mask[Switch(0)]);
            Assert.Equal(0f, mask[Switch(2)]);
        }

        // ---------------- the teacher ----------------

        [Fact]
        public void MonteCarloRanksSwitchesAlongsideMoves()
        {
            var active = TestKit.Mon("Active", speed: 120, moves: new[]
            {
                TestKit.Move("Weak", power: 10), TestKit.Move("Strong", power: 110)
            });

            var bench = TestKit.Mon("Bench", moves: TestKit.Move("Hit", power: 60));
            var foe = TestKit.Mon("Foe", hp: 240, moves: TestKit.Move("Hit", power: 30));

            (BattleState state, BattleEngine engine) = TestKit.Battle(769,
                new List<PokemonState> { active, bench },
                new List<PokemonState> { foe });

            var strategy = new MonteCarloStrategy(
                new MonteCarloConfig { SimulationsPerAction = 3, MaxRolloutTurns = 5 }, seed: 176);

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);

            Assert.Contains(legal, a => a.Type == BattleActionType.Switch);

            BattleAction chosen = strategy.ChooseAction(state, state.Player1, legal);

            IReadOnlyList<float>? scores = strategy.LastMoveScores;

            Assert.NotNull(scores);
            Assert.Equal(ObservationSchema.ActionSlots, scores!.Count);

            // The bench position it could switch to was scored; the ones
            // that do not exist were not.
            Assert.False(float.IsNaN(scores[Switch(1)]));
            Assert.True(float.IsNaN(scores[Switch(0)]));
            Assert.True(float.IsNaN(scores[Switch(4)]));

            // Whatever it chose is the top of its own ranking.
            int index = chosen.Type == BattleActionType.Switch
                ? Switch(state.Player1.Team.IndexOf(chosen.SwitchTarget!))
                : chosen.Move!.Index;

            Assert.Equal(scores.Where(s => !float.IsNaN(s)).Max(), scores[index]);
        }

        [Fact]
        public async Task TheObserverRecordsTheWholeActionSpace()
        {
            var a = TestKit.Mon("Alpha", hp: 200, moves: new[]
            {
                TestKit.Move("Hit A", power: 60), TestKit.Move("Hit B", power: 40)
            });

            var spare = TestKit.Mon("Spare", hp: 200, moves: TestKit.Move("Hit C", power: 50));
            var b = TestKit.Mon("Beta", hp: 200, moves: TestKit.Move("Hit D", power: 50));

            (BattleState state, BattleEngine engine) = TestKit.Battle(770,
                new List<PokemonState> { a, spare },
                new List<PokemonState> { b });

            var observer = new BattleObserver(dir, "Lab", 770);

            engine.RunBattle(
                new MonteCarloStrategy(
                    new MonteCarloConfig { SimulationsPerAction = 2, MaxRolloutTurns = 4 }, seed: 176),
                new RandomMoveStrategy(),
                observer: observer);

            await observer.DisposeAsync();

            List<JsonElement> records = await ReadAllRecords();

            List<JsonElement> decisions = records
                .Where(r => r.GetProperty("Record").GetString() == "decision")
                .Where(r => r.GetProperty("Action").GetProperty("Kind").GetString() != "Replacement")
                .ToList();

            Assert.NotEmpty(decisions);

            Assert.All(decisions, r =>
            {
                Assert.Equal(ObservationSchema.FeatureCount, r.GetProperty("State").GetArrayLength());
                Assert.Equal(ObservationSchema.MoveSlots, r.GetProperty("LegalMoves").GetArrayLength());
                Assert.Equal(ObservationSchema.ActionSlots, r.GetProperty("LegalActions").GetArrayLength());

                // LegalActions really does open with LegalMoves.
                List<JsonElement> moves = r.GetProperty("LegalMoves").EnumerateArray().ToList();
                List<JsonElement> actions = r.GetProperty("LegalActions").EnumerateArray().ToList();

                for (int i = 0; i < moves.Count; i++)
                    Assert.Equal(moves[i].GetSingle(), actions[i].GetSingle());
            });

            var ranked = decisions
                .Where(r => r.TryGetProperty("TeacherScores", out JsonElement s) &&
                            s.ValueKind == JsonValueKind.Array)
                .ToList();

            Assert.NotEmpty(ranked);
            Assert.All(ranked, r =>
                Assert.Equal(ObservationSchema.ActionSlots,
                             r.GetProperty("TeacherScores").GetArrayLength()));

            // A replacement is written after the engine already sent the
            // Pokemon in, so its state contains the answer - it is marked
            // as offering nothing rather than posing as a switch example.
            foreach (JsonElement r in records.Where(x =>
                x.GetProperty("Record").GetString() == "decision" &&
                x.GetProperty("Action").GetProperty("Kind").GetString() == "Replacement"))
            {
                Assert.All(r.GetProperty("LegalActions").EnumerateArray().ToList(),
                    v => Assert.Equal(0f, v.GetSingle()));
            }
        }

        async Task<List<JsonElement>> ReadAllRecords()
        {
            var records = new List<JsonElement>();

            if (!Directory.Exists(dir))
                return records;

            foreach (string file in Directory.GetFiles(dir, "obs-*.jsonl"))
            {
                foreach (string line in await File.ReadAllLinesAsync(file))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        records.Add(JsonDocument.Parse(line).RootElement.Clone());
                }
            }

            return records;
        }

        // ---------------- the evaluator and the player ----------------

        const string SwitchProbe =
            "CAgSFnByb3RyYWNrZXItYWktdHJhaW5pbmc6rTMKIAoBeAoCVzAKAkIwEgZvdXRwdXQaBWdlbW0wIgRHZW1tEgpwb2tlbW9uX2Fp" +
            "Ko4yCKABCAoQAUICVzBKgDIAAIA/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgD8AAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AIA/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgD8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgD8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAPwAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAKjIIChABQgJCMEooAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAFoZCgF4EhQKEggBEg4KBxIFYmF0Y2gKAwigAWIdCgZvdXRwdXQSEwoRCAESDQoHEgViYXRjaAoC" +
            "CApCBAoAEA0=";

        const string MoveOnlyProbe =
            "CAgSFnByb3RyYWNrZXItYWktdHJhaW5pbmc6swIKIAoBeAoCVzAKAkIwEgZvdXRwdXQaBWdlbW0wIgRHZW1tEgpwb2tlbW9uX2Fp" +
            "Kq0BCAoIBBABQgJXMEqgAQAAgD8AAAAAAAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAAAA" +
            "AAAAgD8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAqGggEEAFCAkIwShAAAAAAAAAAAAAAAAAAAAAAWhgKAXgSEwoRCAESDQoHEgViYXRj" +
            "aAoCCApiHQoGb3V0cHV0EhMKEQgBEg0KBxIFYmF0Y2gKAggEQgQKABAN";

        string WriteProbe(string base64)
        {
            string path = Path.Combine(Path.GetTempPath(), $"protracker-176-{Guid.NewGuid():N}.onnx");
            File.WriteAllBytes(path, Convert.FromBase64String(base64));
            temporaryFiles.Add(path);
            return path;
        }

        [Fact]
        public void TheEvaluatorReportsWhetherAModelCanSwitch()
        {
            using var withHead = new OnnxShadowEvaluator(WriteProbe(SwitchProbe));
            using var moveOnly = new OnnxShadowEvaluator(WriteProbe(MoveOnlyProbe));

            Assert.True(withHead.Status.Available, withHead.Status.Description);
            Assert.True(moveOnly.Status.Available, moveOnly.Status.Description);

            Assert.Equal(ObservationSchema.ActionSlots, withHead.Status.OutputWidth);
            Assert.True(withHead.Status.CanChooseSwitches);
            Assert.DoesNotContain("no switch head", withHead.Status.Description);

            // §177: this probe is the section 176 width, and says so.
            Assert.Equal(ObservationSchema.FeatureCountV3, withHead.Status.InputWidth);
            Assert.Contains("pre-177", withHead.Status.Description);

            Assert.Equal(ObservationSchema.MoveSlots, moveOnly.Status.OutputWidth);
            Assert.False(moveOnly.Status.CanChooseSwitches);
            Assert.Contains("no switch head", moveOnly.Status.Description);
        }

        [Fact]
        public void AModelWithASwitchHeadCanPreferATeamPosition()
        {
            using var evaluator = new OnnxShadowEvaluator(WriteProbe(SwitchProbe));

            if (!evaluator.Status.Available)
                return;

            // The probe copies inputs 0-9 to its ten outputs, so the state
            // decides the answer and the test can name it exactly.
            var state = new float[ObservationSchema.FeatureCount];
            state[Switch(1)] = 1f;         // output 5 is the biggest

            var open = new float[ObservationSchema.ActionSlots];
            for (int i = 0; i < open.Length; i++) open[i] = 1f;

            ShadowPrediction? said = evaluator.Predict(state, open);

            Assert.NotNull(said);
            Assert.Equal(Switch(1), said!.PreferredActionIndex);
            Assert.Equal(1, said.PreferredTeamIndex);
            Assert.Equal(-1, said.PreferredMoveIndex);      // it wanted a switch, not a move

            // Masking still beats the model's own opinion.
            var onlyMoveTwo = new float[ObservationSchema.ActionSlots];
            onlyMoveTwo[2] = 1f;

            ShadowPrediction? masked = evaluator.Predict(state, onlyMoveTwo);

            Assert.NotNull(masked);
            Assert.Equal(2, masked!.PreferredMoveIndex);
            Assert.Equal(-1, masked.PreferredTeamIndex);
        }

        [Fact]
        public void AMoveOnlyModelIsNeverAskedAboutSwitches()
        {
            using var evaluator = new OnnxShadowEvaluator(WriteProbe(MoveOnlyProbe));

            if (!evaluator.Status.Available)
                return;

            var state = new float[ObservationSchema.FeatureCount];
            state[Switch(1)] = 9f;         // a switch would win, if it could see it
            state[3] = 1f;

            var open = new float[ObservationSchema.ActionSlots];
            for (int i = 0; i < open.Length; i++) open[i] = 1f;

            ShadowPrediction? said = evaluator.Predict(state, open);

            Assert.NotNull(said);
            Assert.Equal(3, said!.PreferredMoveIndex);
            Assert.Equal(-1, said.PreferredTeamIndex);
            Assert.Equal(ObservationSchema.MoveSlots, said.Scores.Length);
        }

        sealed class FixedEvaluator : IShadowEvaluator
        {
            readonly int preferred;

            public FixedEvaluator(int outputWidth, int preferred)
            {
                this.preferred = preferred;
                Status = new ShadowEvaluatorStatus
                {
                    Available = true,
                    InputWidth = ObservationSchema.FeatureCount,
                    OutputWidth = outputWidth,
                    Description = "test"
                };
            }

            public ShadowEvaluatorStatus Status { get; }
            public float[]? LastMask { get; private set; }

            public ShadowPrediction? Predict(float[] state, float[] legalActionMask)
            {
                LastMask = legalActionMask;

                if (preferred < 0 || preferred >= legalActionMask.Length || legalActionMask[preferred] <= 0)
                    return null;

                return new ShadowPrediction { PreferredActionIndex = preferred, Score = 1f };
            }
        }

        [Fact]
        public void TheNeuralPlayerCanFinallyLeaveABadMatchup()
        {
            var active = TestKit.Mon("Active", moves: new[] { TestKit.Move("One"), TestKit.Move("Two") });
            var bench = TestKit.Mon("Bench", moves: TestKit.Move("Hit"));
            var foe = TestKit.Mon("Foe", moves: TestKit.Move("Hit"));

            (BattleState state, BattleEngine engine) = TestKit.Battle(771,
                new List<PokemonState> { active, bench },
                new List<PokemonState> { foe });

            var evaluator = new FixedEvaluator(ObservationSchema.ActionSlots, Switch(1));
            var strategy = new NeuralStrategy(evaluator);

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);

            BattleAction chosen = strategy.ChooseAction(state, state.Player1, legal);

            Assert.Equal(BattleActionType.Switch, chosen.Type);
            Assert.Same(bench, chosen.SwitchTarget);
            Assert.Equal(1, strategy.SwitchChoiceCount);
            Assert.Equal(1, strategy.ModelChoiceCount);

            // It was masked over the whole action space, not just the moves.
            Assert.Equal(ObservationSchema.ActionSlots, evaluator.LastMask!.Length);

            // A switch is not a turn you also mega evolve on.
            Assert.False(chosen.MegaEvolve);
        }

        [Fact]
        public void TheSwitchHeadAlsoPicksWhoComesInAfterAFaint()
        {
            var down = TestKit.Mon("Down", moves: TestKit.Move("Hit"));
            var first = TestKit.Mon("First", moves: TestKit.Move("Hit"));
            var second = TestKit.Mon("Second", moves: TestKit.Move("Hit"));
            var foe = TestKit.Mon("Foe", moves: TestKit.Move("Hit"));

            (BattleState state, _) = TestKit.Battle(772,
                new List<PokemonState> { down, first, second },
                new List<PokemonState> { foe });

            down.CurrentHP = 0;

            var strategy = new NeuralStrategy(
                new FixedEvaluator(ObservationSchema.ActionSlots, Switch(2)));

            Assert.Equal(2, strategy.ChooseReplacement(state, state.Player1, new[] { 1, 2 }));

            // A model without a head cannot answer, so the baseline does -
            // and it still returns something legal.
            var moveOnly = new NeuralStrategy(
                new FixedEvaluator(ObservationSchema.MoveSlots, 0));

            Assert.Contains(moveOnly.ChooseReplacement(state, state.Player1, new[] { 1, 2 }),
                            new[] { 1, 2 });
            Assert.Equal(0, moveOnly.SwitchChoiceCount);
        }

        [Fact]
        public void AnImpossiblePreferenceFallsBackToSomethingLegal()
        {
            var active = TestKit.Mon("Active", moves: new[] { TestKit.Move("One") });
            var bench = TestKit.Mon("Bench", moves: TestKit.Move("Hit"));
            var foe = TestKit.Mon("Foe", moves: TestKit.Move("Hit"));

            (BattleState state, BattleEngine engine) = TestKit.Battle(773,
                new List<PokemonState> { active, bench },
                new List<PokemonState> { foe });

            // Team position 5 does not exist on a team of two.
            var strategy = new NeuralStrategy(
                new FixedEvaluator(ObservationSchema.ActionSlots, Switch(5)));

            IReadOnlyList<BattleAction> legal = engine.GetLegalActions(state.Player1);
            BattleAction chosen = strategy.ChooseAction(state, state.Player1, legal);

            Assert.Contains(chosen, legal);
            Assert.Equal(0, strategy.ModelChoiceCount);
            Assert.Equal(1, strategy.DecisionCount);
        }
    }
}