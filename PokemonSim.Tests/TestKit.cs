using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Engine;
using PokemonSim.Models;

namespace PokemonSim.Tests
{
    /// <summary>Section 154. Hand-built Pokemon and moves so each test pins
    /// exactly the numbers it needs - no data files, no hidden state.</summary>
    public static class TestKit
    {
        public static MoveState Move(
            string name,
            PokemonType type = PokemonType.Normal,
            MoveCategory category = MoveCategory.Physical,
            int power = 50,
            int accuracy = 0,
            int pp = 16,
            int priority = 0)
        {
            return new MoveState
            {
                Name = name,
                Type = type,
                Category = category,
                Power = power,
                Accuracy = accuracy,
                MaxPP = pp,
                CurrentPP = pp,
                Priority = priority
            };
        }

        /// <summary>Section 155: the single-move convenience the suite uses
        /// everywhere. C# does not accept a bare element as a NAMED params
        /// argument ("moves: Move(...)" needs an array), so this non-params
        /// overload is what those calls actually bind to; multi-move calls
        /// pass "moves: new[] { ... }" into the params overload below.</summary>
        public static PokemonState Mon(
            string species,
            PokemonType type = PokemonType.Normal,
            int hp = 200,
            int attack = 100,
            int defense = 100,
            int speed = 100,
            MoveState? moves = null)
        {
            return Mon(species, type, hp, attack, defense, speed,
                moves == null ? Array.Empty<MoveState>() : new[] { moves });
        }

        public static PokemonState Mon(
            string species,
            PokemonType type = PokemonType.Normal,
            int hp = 200,
            int attack = 100,
            int defense = 100,
            int speed = 100,
            params MoveState[] moves)
        {
            var moveList = moves.Length > 0
                ? moves.ToList()
                : new List<MoveState> { Move("Test Hit") };

            for (int i = 0; i < moveList.Count; i++)
                moveList[i].Index = i;

            return new PokemonState
            {
                Species = species,
                Types = new List<PokemonType> { type },
                Level = 50,
                MaxHP = hp,
                CurrentHP = hp,
                Stats = new Stats
                {
                    HP = hp,
                    Attack = attack,
                    Defense = defense,
                    SpAttack = attack,
                    SpDefense = defense,
                    Speed = speed
                },
                Moves = moveList
            };
        }

        public static (BattleState State, BattleEngine Engine) Battle(
            int seed,
            List<PokemonState> team1,
            List<PokemonState> team2)
        {
            var state = new BattleState
            {
                Player1 = new PlayerState { Name = "P1", Team = team1, ActivePokemon = team1[0] },
                Player2 = new PlayerState { Name = "P2", Team = team2, ActivePokemon = team2[0] },
                Rng = new BattleRng(seed)
            };

            BattleInitializer.Initialize(state);

            return (state, new BattleEngine(state));
        }

        public static (BattleState State, BattleEngine Engine) Duel(int seed, PokemonState a, PokemonState b) =>
            Battle(seed, new List<PokemonState> { a }, new List<PokemonState> { b });

        public static BattleAction MoveAction(BattleState state, PokemonState user, MoveState move) =>
            new BattleAction
            {
                Type = BattleActionType.Move,
                User = user,
                Move = move,
                Priority = move.Priority,
                Speed = (int)StatResolver.GetStat(state, user, "Speed")
            };

        public static BattleAction SwitchAction(BattleState state, PokemonState user, PokemonState target) =>
            new BattleAction
            {
                Type = BattleActionType.Switch,
                User = user,
                SwitchTarget = target,
                Priority = 10,
                Speed = (int)StatResolver.GetStat(state, user, "Speed")
            };

        /// <summary>Runs one turn where each side uses its first move.</summary>
        public static void Clash(BattleState state, BattleEngine engine)
        {
            engine.RunTurn(
                MoveAction(state, state.Player1.ActivePokemon, state.Player1.ActivePokemon.Moves[0]),
                MoveAction(state, state.Player2.ActivePokemon, state.Player2.ActivePokemon.Moves[0]));
        }

        public static bool LogContains(BattleState state, string fragment) =>
            state.Log.Lines.Any(line => line.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}