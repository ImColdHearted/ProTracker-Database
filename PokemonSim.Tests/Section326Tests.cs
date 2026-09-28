using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Evaluation;
using PokemonSim.Models;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// §326. Watching what a brain DOES, not only how it fares.
    ///
    /// §325's first real results said the installed model loses to random
    /// legal moves - 133-267 over 400 battles, 33.3%, interval 28.8 to
    /// 38.0. That number is the reason this file exists. A model that had
    /// learned nothing would sit at 50%, because that is what random scores
    /// against random; two thirds of the way to zero means it is choosing
    /// systematically WORSE than chance, and a win rate cannot say how.
    ///
    /// The cheapest thing that can is switching. §154's baseline never
    /// volunteers one, so every switch the other side makes is a turn it
    /// spends on damage and you do not.
    /// </summary>
    public class Section326Tests
    {
        /// <summary>A brain that does exactly what it is told, so the
        /// counter can be tested against a known sequence rather than
        /// against whatever a real brain happens to do.</summary>
        sealed class Scripted : IBattleStrategy
        {
            readonly bool switchWhenPossible;

            public Scripted(bool alwaysSwitch)
            {
                switchWhenPossible = alwaysSwitch;
            }

            public string Name => "Scripted";

            public BattleAction ChooseAction(
                BattleState state, PlayerState self, IReadOnlyList<BattleAction> legalActions)
            {
                if (switchWhenPossible)
                {
                    BattleAction? away = legalActions.FirstOrDefault(
                        a => a.Type == BattleActionType.Switch);

                    if (away != null)
                        return away;
                }

                return legalActions.First(a => a.Type == BattleActionType.Move);
            }

            public int ChooseReplacement(
                BattleState state, PlayerState self, IReadOnlyList<int> legalTeamIndexes) =>
                legalTeamIndexes[0];
        }

        static (BattleState State, BattleEngine Engine) Fight()
        {
            var mine = new List<PokemonState>
            {
                TestKit.Mon("Mine", PokemonType.Normal, moves: new[] { TestKit.Move("My Hit") }),
                TestKit.Mon("MyBench", PokemonType.Water, moves: new[] { TestKit.Move("Bench Hit") })
            };

            var theirs = new List<PokemonState>
            {
                TestKit.Mon("Theirs", PokemonType.Fire, moves: new[] { TestKit.Move("Their Hit") }),
                TestKit.Mon("TheirBench", PokemonType.Grass, moves: new[] { TestKit.Move("Their Bench") })
            };

            return TestKit.Battle(326, mine, theirs);
        }

        [Fact]
        public void TheCounterPassesTheChoiceStraightThrough()
        {
            // It must be invisible to the battle. A decorator that changed
            // what a brain picked would be measuring itself.
            (BattleState state, _) = Fight();

            var inner = new Scripted(alwaysSwitch: false);
            var counted = new CountingStrategy(inner);

            IReadOnlyList<BattleAction> legal = LegalActions.For(state, state.Player1);

            BattleAction direct = inner.ChooseAction(state, state.Player1, legal);
            BattleAction through = counted.ChooseAction(state, state.Player1, legal);

            Assert.Equal(direct.Type, through.Type);
            Assert.Same(direct.Move, through.Move);
        }

        [Fact]
        public void ItKeepsTheBrainsOwnName()
        {
            var counted = new CountingStrategy(new RandomMoveStrategy());

            Assert.Equal(new RandomMoveStrategy().Name, counted.Name);
        }

        [Fact]
        public void ABrainThatNeverSwitchesCountsNoSwitches()
        {
            (BattleState state, _) = Fight();

            var counted = new CountingStrategy(new Scripted(alwaysSwitch: false));

            for (int i = 0; i < 5; i++)
                counted.ChooseAction(state, state.Player1, LegalActions.For(state, state.Player1));

            Assert.Equal(5, counted.Decisions);
            Assert.Equal(0, counted.VoluntarySwitches);
            Assert.Equal(0, counted.Replacements);
        }

        [Fact]
        public void ABrainThatAlwaysSwitchesCountsEveryTurn()
        {
            (BattleState state, _) = Fight();

            var counted = new CountingStrategy(new Scripted(alwaysSwitch: true));

            for (int i = 0; i < 5; i++)
                counted.ChooseAction(state, state.Player1, LegalActions.For(state, state.Player1));

            Assert.Equal(5, counted.Decisions);
            Assert.Equal(5, counted.VoluntarySwitches);
        }

        [Fact]
        public void AForcedReplacementIsNotAVoluntarySwitch()
        {
            // The distinction the whole number depends on. Every brain that
            // loses a Pokemon makes a replacement; counting those as
            // switches would make every brain look like a switcher and the
            // comparison would say nothing.
            (BattleState state, _) = Fight();

            var counted = new CountingStrategy(new Scripted(alwaysSwitch: false));

            counted.ChooseReplacement(state, state.Player1, new[] { 1 });
            counted.ChooseReplacement(state, state.Player1, new[] { 1 });

            Assert.Equal(2, counted.Replacements);
            Assert.Equal(0, counted.VoluntarySwitches);
            Assert.Equal(0, counted.Decisions);
        }

        [Fact]
        public void TheReplacementChoiceIsPassedThroughUnchanged()
        {
            (BattleState state, _) = Fight();

            var counted = new CountingStrategy(new Scripted(alwaysSwitch: false));

            Assert.Equal(1, counted.ChooseReplacement(state, state.Player1, new[] { 1 }));
        }

        [Fact]
        public void TheShareIsSwitchesOverDecisions()
        {
            var side = new SideResult
            {
                Name = "test",
                Decisions = 200,
                VoluntarySwitches = 50
            };

            Assert.Equal(0.25, side.SwitchShare);
        }

        [Fact]
        public void ABrainThatNeverActedHasNoShareRatherThanADividedByZero()
        {
            var side = new SideResult { Name = "test" };

            Assert.Equal(0, side.SwitchShare);
        }
    }
}
