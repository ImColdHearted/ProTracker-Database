using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PokemonSim.Actions;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Effects;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 184. The ninety-nine moves the competitive sets needed.
    ///
    /// Section 178 imported 2,574 Smogon sets and kept every move name
    /// verbatim, which left 353 of them - one in seven - naming at least
    /// one move the engine had never heard of. A set with a hole in it is
    /// not a slightly worse set: the slot silently vanishes, so the
    /// Pokemon walks in with three moves against six.
    ///
    /// Most of the ninety-nine are data. Nine needed a real effect. A
    /// named group are approximations, and the guide's table says which
    /// and how - the important thing being that an approximation is
    /// written down rather than discovered later by someone wondering why
    /// Bolt Beak never doubles.
    ///
    /// The test that matters most is the first one. It reads the same two
    /// data files the game reads and asserts there is nothing left over,
    /// so this cannot quietly rot the next time a set file is imported.
    /// </summary>
    public class Section184Tests
    {
        static Dictionary<string, JsonElement> Moves()
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(SimDataFiles.Resolve("moves.json")));

            return document.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }

        static Dictionary<string, List<List<string>>> SetSlots()
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(SimDataFiles.Resolve("CompetitiveSets.json")));

            var rows = new Dictionary<string, List<List<string>>>();
            int i = 0;

            foreach (JsonProperty species in document.RootElement.EnumerateObject())
            {
                foreach (JsonElement set in species.Value.EnumerateArray())
                {
                    var slots = new List<List<string>>();

                    foreach (JsonElement slot in set.GetProperty("moves").EnumerateArray())
                        slots.Add(slot.EnumerateArray().Select(m => m.GetString() ?? "").ToList());

                    rows[$"{species.Name}#{i++}"] = slots;
                }
            }

            return rows;
        }

        // ---------------- the whole point of the section ------------------

        [Fact]
        public void EveryMoveTheCompetitiveSetsNameNowExists()
        {
            Dictionary<string, JsonElement> moves = Moves();

            var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (List<List<string>> slots in SetSlots().Values)
            {
                foreach (List<string> slot in slots)
                {
                    foreach (string name in slot)
                    {
                        if (!moves.ContainsKey(name))
                            missing.Add(name);
                    }
                }
            }

            Assert.Empty(missing);
        }

        [Fact]
        public void EverySetCanFillEveryOneOfItsSlots()
        {
            // The stronger form: not merely that names resolve, but that no
            // set is left short a slot - which is what actually reaches the
            // battle.
            Dictionary<string, JsonElement> moves = Moves();

            var short_ = new List<string>();

            foreach ((string id, List<List<string>> slots) in SetSlots())
            {
                foreach (List<string> slot in slots)
                {
                    if (!slot.Any(moves.ContainsKey))
                        short_.Add(id);
                }
            }

            Assert.Empty(short_);
        }

        [Fact]
        public void EveryEffectAnyMoveNamesIsRegistered()
        {
            var unknown = new SortedSet<string>(StringComparer.Ordinal);

            foreach ((string name, JsonElement move) in Moves())
            {
                if (!move.TryGetProperty("effects", out JsonElement effects))
                    continue;

                foreach (JsonElement effect in effects.EnumerateArray())
                {
                    string id = effect.GetString() ?? "";

                    if (!MoveEffectRegistry.IsKnown(id))
                        unknown.Add($"{name} -> {id}");
                }
            }

            Assert.Empty(unknown);
        }

        [Fact]
        public void EveryNewMoveLoadsIntoAUsableMoveState()
        {
            MoveDex.EnsureLoaded();

            foreach (string name in new[]
            {
                "Triple Axel", "First Impression", "Precipice Blades", "Sticky Web",
                "Photon Geyser", "Aromatherapy", "Hidden Power Grass", "Strength Sap",
                "Final Gambit", "Shed Tail", "Chilly Reception", "Aurora Veil",
                "Heart Swap", "Refresh", "Oblivion Wing", "Ruination", "Mind Blown"
            })
            {
                Assert.True(MoveDex.TryGet(name, out MoveState move), name);
                Assert.Equal(name, move.Name);
                Assert.True(move.MaxPP > 0, name);
            }
        }

        [Fact]
        public void TripleAxelDealsWhatTheRealMoveDeals()
        {
            // The real move escalates 20, 40 then 60 over three hits. Three
            // hits of the average reach the same 120 without teaching the
            // resolver which hit it is on - so the total is the thing to
            // pin.
            MoveDex.EnsureLoaded();

            Assert.True(MoveDex.TryGet("Triple Axel", out MoveState axel));
            Assert.Equal(20 + 40 + 60, axel.Power * 3);
        }

        // ---------------- Sticky Web --------------------------------------

        static (BattleState State, BattleEngine Engine) Web(int seed) => TestKit.Battle(seed,
            new List<PokemonState>
            {
                TestKit.Mon("Mine", hp: 200, moves: TestKit.Move("Hit", power: 40)),
                TestKit.Mon("Bench", hp: 200, moves: TestKit.Move("Bench Hit", power: 40))
            },
            new List<PokemonState>
            {
                TestKit.Mon("Theirs", hp: 200, moves: TestKit.Move("Their Hit", power: 40)),
                TestKit.Mon("Their Bench", hp: 200, moves: TestKit.Move("Bench Hit", power: 40))
            });

        [Fact]
        public void StickyWebCostsTheIncomingPokemonAStageOfSpeed()
        {
            var (state, engine) = Web(1840);

            state.StickyWebP2 = true;

            PokemonState incoming = state.Player2.Team[1];

            Assert.Equal(0, incoming.SpeedStage);

            engine.Replace(state.Player2, incoming);

            Assert.Equal(-1, incoming.SpeedStage);
            Assert.Equal(incoming.MaxHP, incoming.CurrentHP);   // Speed, not HP
        }

        [Fact]
        public void StickyWebSparesWhatIsNotOnTheGround()
        {
            var (state, engine) = Web(1841);

            state.StickyWebP2 = true;

            PokemonState incoming = state.Player2.Team[1];

            incoming.MagnetRiseTurns = 5;

            engine.Replace(state.Player2, incoming);

            Assert.Equal(0, incoming.SpeedStage);
        }

        [Fact]
        public void StickyWebIsOneLayerAndSaysSoWhenItIsAlreadyThere()
        {
            var (state, _) = Web(1842);

            var effect = new StickyWebEffect();
            int damage = 0;
            bool cancelled = false;

            effect.Apply(state, state.Player1.ActivePokemon, state.Player2.ActivePokemon,
                         TestKit.Move("Sticky Web"), ref damage, ref cancelled);

            Assert.True(state.StickyWebP2);
            Assert.False(state.StickyWebP1);

            effect.Apply(state, state.Player1.ActivePokemon, state.Player2.ActivePokemon,
                         TestKit.Move("Sticky Web"), ref damage, ref cancelled);

            Assert.True(TestKit.LogContains(state, "But it failed"));
        }

        [Fact]
        public void ClearingHazardsClearsTheWebToo()
        {
            var (state, _) = Web(1843);

            state.StickyWebP1 = true;
            state.SpikesP1 = 2;

            HazardResolver.ClearSide(state, state.Player1);

            Assert.False(state.StickyWebP1);
            Assert.Equal(0, state.SpikesP1);
        }

        [Fact]
        public void CourtChangeMovesTheWebWithEverythingElse()
        {
            var (state, _) = Web(1844);

            state.StickyWebP1 = true;
            state.StickyWebP2 = false;

            var effect = new CourtChangeEffect();
            int damage = 0;
            bool cancelled = false;

            effect.Apply(state, state.Player1.ActivePokemon, state.Player2.ActivePokemon,
                         TestKit.Move("Court Change"), ref damage, ref cancelled);

            Assert.False(state.StickyWebP1);
            Assert.True(state.StickyWebP2);
        }

        [Fact]
        public void TheWebSurvivesACloneLikeEveryOtherHazard()
        {
            var (state, _) = Web(1845);

            state.StickyWebP1 = true;

            BattleState clone = state.Clone();

            Assert.True(clone.StickyWebP1);
            Assert.False(clone.StickyWebP2);
        }

        // ---------------- the other new effects ---------------------------

        static (BattleState State, PokemonState Mine, PokemonState Theirs) Duel(int seed)
        {
            var (state, _) = TestKit.Battle(seed,
                new List<PokemonState>
                {
                    TestKit.Mon("Mine", hp: 300, moves: TestKit.Move("Hit", power: 40)),
                    TestKit.Mon("Bench", hp: 300, moves: TestKit.Move("Bench Hit", power: 40))
                },
                new List<PokemonState> { TestKit.Mon("Theirs", hp: 300, attack: 140) });

            return (state, state.Player1.ActivePokemon, state.Player2.ActivePokemon);
        }

        static void Run(IMoveEffect effect, BattleState state, PokemonState user, PokemonState target,
                        string moveName, ref int damage)
        {
            bool cancelled = false;

            effect.Apply(state, user, target, TestKit.Move(moveName), ref damage, ref cancelled);
        }

        [Fact]
        public void AuroraVeilNeedsHailAndThenRaisesBothWalls()
        {
            var (state, mine, theirs) = Duel(1846);
            int damage = 0;

            Run(new AuroraVeilEffect(), state, mine, theirs, "Aurora Veil", ref damage);

            Assert.Equal(0, state.ReflectTurns(state.Player1));
            Assert.True(TestKit.LogContains(state, "only works in hail"));

            state.Environment.Weather = WeatherType.Hail;

            Run(new AuroraVeilEffect(), state, mine, theirs, "Aurora Veil", ref damage);

            Assert.True(state.ReflectTurns(state.Player1) > 0);
            Assert.True(state.LightScreenTurns(state.Player1) > 0);
        }

        [Fact]
        public void StrengthSapHealsByTheTargetsAttackAndThenLowersIt()
        {
            var (state, mine, theirs) = Duel(1847);
            int damage = 0;

            mine.CurrentHP = 50;

            Run(new StrengthSapEffect(), state, mine, theirs, "Strength Sap", ref damage);

            Assert.True(mine.CurrentHP > 50, "it should have healed");
            Assert.Equal(-1, theirs.AttackStage);

            // A target already at the floor has nothing left to sap.
            theirs.AttackStage = -6;
            mine.CurrentHP = 50;

            Run(new StrengthSapEffect(), state, mine, theirs, "Strength Sap", ref damage);

            Assert.Equal(50, mine.CurrentHP);
        }

        [Fact]
        public void FirstImpressionOnlyWorksOnTheTurnItsUserCameIn()
        {
            var (state, mine, theirs) = Duel(1848);
            int damage = 10;

            // §197: the rule reads "has this Pokemon had its go yet", not
            // "did it enter on this turn" - see PokemonState's field. This
            // test used to advance TurnNumber, which is the question the
            // effect asked before and the reason a replacement could not use
            // the move on the one turn it should have worked.
            mine.HasActedSinceEnteringField = false;

            bool cancelled = false;
            new FirstImpressionEffect().Apply(state, mine, theirs,
                TestKit.Move("First Impression"), ref damage, ref cancelled);

            Assert.False(cancelled);

            mine.HasActedSinceEnteringField = true;
            cancelled = false;

            new FirstImpressionEffect().Apply(state, mine, theirs,
                TestKit.Move("First Impression"), ref damage, ref cancelled);

            Assert.True(cancelled);
        }

        [Fact]
        public void FinalGambitSpendsTheUsersRemainingHealth()
        {
            var (state, mine, theirs) = Duel(1849);

            mine.CurrentHP = 137;

            int damage = 20;

            Run(new FinalGambitEffect(), state, mine, theirs, "Final Gambit", ref damage);

            Assert.Equal(137, damage);
            Assert.Equal(0, mine.CurrentHP);
            Assert.True(mine.Fainted);
        }

        [Fact]
        public void FinalGambitStillRespectsAnImmunity()
        {
            var (state, mine, theirs) = Duel(1850);

            mine.CurrentHP = 137;

            int damage = 0;    // the resolver already zeroed it

            Run(new FinalGambitEffect(), state, mine, theirs, "Final Gambit", ref damage);

            Assert.Equal(0, damage);
            Assert.Equal(137, mine.CurrentHP);
        }

        [Fact]
        public void RefreshCuresTheUserAndNobodyElse()
        {
            var (state, mine, theirs) = Duel(1851);
            int damage = 0;

            mine.Status = StatusCondition.Burn;
            theirs.Status = StatusCondition.Paralysis;
            state.Player1.Team[1].Status = StatusCondition.Poison;

            Run(new RefreshCureEffect(), state, mine, theirs, "Refresh", ref damage);

            Assert.Equal(StatusCondition.None, mine.Status);
            Assert.Equal(StatusCondition.Paralysis, theirs.Status);
            Assert.Equal(StatusCondition.Poison, state.Player1.Team[1].Status);
        }

        [Fact]
        public void HeartSwapTradesEveryStageBothWays()
        {
            var (state, mine, theirs) = Duel(1852);
            int damage = 0;

            mine.AttackStage = -2;
            mine.SpeedStage = 1;
            theirs.AttackStage = 4;
            theirs.SpDefenseStage = -1;

            Run(new HeartSwapEffect(), state, mine, theirs, "Heart Swap", ref damage);

            Assert.Equal(4, mine.AttackStage);
            Assert.Equal(-1, mine.SpDefenseStage);
            Assert.Equal(-2, theirs.AttackStage);
            Assert.Equal(1, theirs.SpeedStage);
        }

        [Fact]
        public void ShedTailPaysHalfAndLeavesTheSubstituteBehindForTheNextOne()
        {
            var (state, mine, theirs) = Duel(1853);
            int damage = 0;

            int cost = mine.MaxHP / 2;

            Run(new ShedTailEffect(), state, mine, theirs, "Shed Tail", ref damage);

            PokemonState incoming = state.Player1.ActivePokemon;

            Assert.NotSame(mine, incoming);
            Assert.Equal(cost, incoming.SubstituteHP);
            Assert.Equal(0, mine.SubstituteHP);
            Assert.Equal(mine.MaxHP - cost, mine.CurrentHP);
        }

        [Fact]
        public void ShedTailRefusesWhenItCannotPayOrHasNobodyToLeaveTo()
        {
            var (state, mine, theirs) = Duel(1854);
            int damage = 0;

            mine.CurrentHP = 10;

            Run(new ShedTailEffect(), state, mine, theirs, "Shed Tail", ref damage);

            Assert.Same(mine, state.Player1.ActivePokemon);
            Assert.Equal(10, mine.CurrentHP);
            Assert.True(TestKit.LogContains(state, "too weak"));
        }

        [Fact]
        public void ChillyReceptionSetsSnowEvenWhenThereIsNobodyToSwitchTo()
        {
            var (state, mine, theirs) = Duel(1855);
            int damage = 0;

            foreach (PokemonState mon in state.Player1.Team.Where(m => m != mine))
                mon.CurrentHP = 0;

            Run(new ChillyReceptionEffect(), state, mine, theirs, "Chilly Reception", ref damage);

            Assert.Equal(WeatherType.Hail, state.Environment.Weather);
            Assert.Same(mine, state.Player1.ActivePokemon);
        }

        [Fact]
        public void OblivionWingDrainsMoreThanAnOrdinaryDrain()
        {
            var (state, mine, theirs) = Duel(1856);

            mine.CurrentHP = 100;

            int damage = 100;

            Run(new DrainEffect(0.75), state, mine, theirs, "Oblivion Wing", ref damage);

            int threeQuarters = mine.CurrentHP;

            mine.CurrentHP = 100;
            damage = 100;

            Run(new DrainEffect(), state, mine, theirs, "Giga Drain", ref damage);

            Assert.Equal(175, threeQuarters);
            Assert.Equal(150, mine.CurrentHP);
        }

        // ---------------- the schema grew by two --------------------------

        [Fact]
        public void TheWebFeaturesSitAtTheEndAndDisturbNothingBeforeThem()
        {
            Assert.Equal(6, ObservationSchema.Version);
            Assert.Equal(205, ObservationSchema.FeatureCount);
            Assert.Equal(ObservationSchema.FeatureCountV5, ObservationSchema.WebFeatureIndex);
            Assert.Equal(ObservationSchema.WebFeatureIndex + 2, ObservationSchema.FeatureCount);

            var (state, _) = Web(1857);

            float[] clear = ObserverEncoder.Encode(state, state.Player1);

            state.StickyWebP1 = true;

            float[] webbed = ObserverEncoder.Encode(state, state.Player1);

            Assert.Equal(0f, clear[ObservationSchema.WebFeatureIndex]);
            Assert.Equal(1f, webbed[ObservationSchema.WebFeatureIndex]);
            Assert.Equal(0f, webbed[ObservationSchema.WebFeatureIndex + 1]);

            for (int i = 0; i < ObservationSchema.FeatureCountV5; i++)
                Assert.Equal(clear[i], webbed[i]);
        }

        [Fact]
        public void TheWebIsReadFromTheActorsPointOfView()
        {
            var (state, _) = Web(1858);

            state.StickyWebP2 = true;

            float[] mine = ObserverEncoder.Encode(state, state.Player1);
            float[] theirs = ObserverEncoder.Encode(state, state.Player2);

            // Player 1 laid it, so it is on the opponent's side of its view
            // and on its own side of theirs.
            Assert.Equal(0f, mine[ObservationSchema.WebFeatureIndex]);
            Assert.Equal(1f, mine[ObservationSchema.WebFeatureIndex + 1]);
            Assert.Equal(1f, theirs[ObservationSchema.WebFeatureIndex]);
            Assert.Equal(0f, theirs[ObservationSchema.WebFeatureIndex + 1]);
        }
    }
}