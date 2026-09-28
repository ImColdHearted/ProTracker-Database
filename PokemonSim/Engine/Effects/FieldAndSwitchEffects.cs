using System;
using System.Linq;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// Section 158. Trick Room, Gravity, the dragging moves, Baton Pass,
    /// terrain/hazard cleanup, and the honest failure stubs for moves whose
    /// premise the Simulator does not carry (held items, allies,
    /// move-copying).
    /// </summary>
    public class TrickRoomEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (cancelled)
                return;

            if (state.TrickRoomTurns > 0)
            {
                state.TrickRoomTurns = 0;
                state.Log.Write("The twisted dimensions returned to normal!");
                return;
            }

            state.TrickRoomTurns = 5;
            state.Log.Write($"{attacker.Species} twisted the dimensions!");
        }
    }

    /// <summary>Gravity: five turns of everything grounded; active Magnet
    /// Rises collapse.</summary>
    public class GravityEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (cancelled)
                return;

            if (state.GravityTurns > 0)
            {
                state.Log.Write("But it failed!");
                return;
            }

            state.GravityTurns = 5;
            attacker.MagnetRiseTurns = 0;
            defender.MagnetRiseTurns = 0;

            state.Log.Write("Gravity intensified!");
        }
    }

    /// <summary>Roar / Whirlwind: drags a random healthy teammate of the
    /// target in. Ingrain's roots hold against it.</summary>
    public class ForcedSwitchEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (cancelled || defender.Fainted)
                return;

            if (defender.Rooted)
            {
                state.Log.Write($"{defender.Species} anchored itself with its roots!");
                return;
            }

            var owner = state.GetOwner(defender);

            var bench = owner.Team.Where(p => !p.Fainted && p != defender).ToList();

            if (bench.Count == 0)
            {
                state.Log.Write("But it failed!");
                return;
            }

            var dragged = bench[state.Rng.Next(bench.Count)];

            SwitchResolver.Resolve(state, owner, dragged, voluntary: false, slot: owner.SlotOf(defender));
            state.Log.Write($"{dragged.Species} was dragged out!");
        }
    }

    /// <summary>Baton Pass: switches to the next healthy teammate and
    /// hands over stat stages and any substitute.</summary>
    public class BatonPassEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (cancelled || attacker.Fainted)
                return;

            var player = state.GetOwner(attacker);
            var next = player.GetNextAvailablePokemon();

            if (next == null)
            {
                state.Log.Write("But it failed!");
                return;
            }

            int attack = attacker.AttackStage;
            int defense = attacker.DefenseStage;
            int spAttack = attacker.SpAttackStage;
            int spDefense = attacker.SpDefenseStage;
            int speed = attacker.SpeedStage;
            int accuracy = attacker.AccuracyStage;
            int evasion = attacker.EvasionStage;
            int substitute = attacker.SubstituteHP;

            SwitchResolver.Resolve(state, player, next, slot: player.SlotOf(attacker));

            // Hazards on entry can faint the recipient - nothing passes on.
            if (player.ActivePokemon != next || next.Fainted)
                return;

            next.AttackStage = attack;
            next.DefenseStage = defense;
            next.SpAttackStage = spAttack;
            next.SpDefenseStage = spDefense;
            next.SpeedStage = speed;
            next.AccuracyStage = accuracy;
            next.EvasionStage = evasion;
            next.SubstituteHP = substitute;

            state.Log.Write($"{next.Species} received the baton-passed boosts!");
        }
    }

    /// <summary>Ice Spinner: scrapes the terrain away after the hit.</summary>
    public class TerrainClearEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (cancelled || state.Environment.Terrain == TerrainType.None)
                return;

            state.Environment.Terrain = TerrainType.None;
            state.Environment.TerrainTurns = 0;
            state.Log.Write("The terrain was torn away!");
        }
    }

    /// <summary>Mortal Spin: clears the user's own hazards and traps (the
    /// poison rides in as the move's 100% status).</summary>
    public class ClearOwnHazardsEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            if (cancelled)
                return;

            HazardResolver.ClearSide(state, state.GetOwner(attacker));
            attacker.Trap = null;
            attacker.LeechSeeded = false;

            state.Log.Write($"{attacker.Species} spun away the hazards around it!");
        }
    }

    /// <summary>An honest failure: the move's premise (held items, an
    /// ally, another game's move pool) is not part of the Simulator, so it
    /// fails with a message saying why instead of pretending.</summary>
    public class FailEffect : BaseMoveEffect
    {
        readonly string message;

        public FailEffect(string message)
        {
            this.message = message;
        }

        public override MovePhase Phase => MovePhase.BeforeMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            state.Log.Write(message);
            cancelled = true;
        }
    }

    /// <summary>Splash-class: does nothing, says so.</summary>
    public class NoopEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        {
            state.Log.Write("But nothing happened!");
            cancelled = true;
        }
    }

    /// <summary>A registered name with no behaviour of its own - the
    /// engine reads it from move.Effects directly. GrassyGlidePriority is
    /// applied by LegalActions when the terrain is up; AteConverted tags a
    /// move Aerilate/Pixilate already converted. Registering them keeps
    /// MoveDex and the resolver from calling the names "not simulated".</summary>
    public class MarkerMoveEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterMove;

        public override void Apply(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            ref int damage,
            ref bool cancelled)
        { }
    }
}