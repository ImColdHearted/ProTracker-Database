using System;
using System.Linq;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// Section 158. The volatile-condition batch: everything here sets or
    /// reads the new PokemonState volatiles, and BattleEngine's end-of-turn
    /// pass runs their clocks. Substitute blocking follows the §154 rule:
    /// a substitute stops conditions aimed THROUGH it (Infiltrator slips
    /// past), never the user's own self-targeted state.
    /// </summary>
    /// <summary>
    /// §301. Hyper Beam, Giga Impact, Blast Burn, Frenzy Plant and Hydro
    /// Cannon: the user spends its next turn recharging.
    ///
    /// AfterMove, which is the phase that answers "did this move go off" for
    /// free - a miss, a Protect and a semi-invulnerable target all return
    /// before it, so none of them costs a recharge. The damage test covers
    /// the one case that reaches this phase without connecting: a target the
    /// move had no effect on at all, which breaks out of the hit loop and
    /// falls through. Damage a Substitute soaked still counts, because the
    /// resolver adds it to the same total.
    /// </summary>
    public class RechargeEffect : BaseMoveEffect
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
            if (cancelled || damage <= 0)
                return;

            attacker.MustRecharge = true;
            attacker.RechargeMove = move;
        }
    }

    public class LeechSeedEffect : BaseMoveEffect
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

            if (defender.Types.Contains(PokemonType.Grass))
            {
                state.Log.Write($"It doesn't affect {defender.Species}...");
                return;
            }

            if (defender.LeechSeeded ||
                (defender.SubstituteHP > 0 &&
                 Abilities.AbilityFactory.Normalize(attacker.AbilityId) != "infiltrator"))
            {
                state.Log.Write("But it failed!");
                return;
            }

            defender.LeechSeeded = true;
            state.Log.Write($"{defender.Species} was seeded!");
        }
    }

    /// <summary>Section 158. Yawn: the target falls asleep at the end of
    /// the NEXT turn unless it is protected from sleep by then.</summary>
    public class YawnEffect : BaseMoveEffect
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

            if (defender.Status != StatusCondition.None || defender.YawnTurns > 0 ||
                (defender.SubstituteHP > 0 &&
                 Abilities.AbilityFactory.Normalize(attacker.AbilityId) != "infiltrator"))
            {
                state.Log.Write("But it failed!");
                return;
            }

            defender.YawnTurns = 2;
            state.Log.Write($"{defender.Species} grew drowsy!");
        }
    }

    /// <summary>Section 158. Confusion - registered plain (Confuse Ray,
    /// Swagger) and as chance variants (Confuse10/20/30/100) for the
    /// damaging moves whose secondary it is.</summary>
    public class InflictConfusionEffect : BaseMoveEffect
    {
        readonly double chance;

        public InflictConfusionEffect(double chance = 1.0)
        {
            this.chance = chance;
        }

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

            bool isStatusMove = move.Category == MoveCategory.Status;

            // A damaging carrier that never connected confuses nobody, and
            // its chance must actually roll.
            if (!isStatusMove && (damage <= 0 || !state.Rng.Chance(chance)))
                return;

            if (defender.SubstituteHP > 0 &&
                Abilities.AbilityFactory.Normalize(attacker.AbilityId) != "infiltrator")
            {
                if (isStatusMove)
                    state.Log.Write("But it failed!");
                return;
            }

            if (defender.ConfusionTurns > 0)
            {
                if (isStatusMove)
                    state.Log.Write($"{defender.Species} is already confused!");
                return;
            }

            if (!state.IgnoreDefenderAbilities &&
                Abilities.AbilityFactory.Normalize(defender.AbilityId) == "owntempo")
            {
                if (isStatusMove)
                    state.Log.Write($"{defender.Species}'s Own Tempo keeps it clear-headed!");
                return;
            }

            defender.ConfusionTurns = state.Rng.Next(2, 6);
            state.Log.Write($"{defender.Species} became confused!");
        }
    }

    /// <summary>Section 158. Perish Song: both actives faint in three
    /// turns unless they leave the field first.</summary>
    public class PerishSongEffect : BaseMoveEffect
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
            cancelled = true;

            bool sang = false;

            foreach (var pokemon in new[] { attacker, defender })
            {
                if (!pokemon.Fainted && pokemon.PerishCount < 0)
                {
                    pokemon.PerishCount = 3;
                    sang = true;
                }
            }

            state.Log.Write(sang
                ? "All Pokemon that heard the song will faint in three turns!"
                : "But it failed!");
        }
    }

    /// <summary>Section 158. Curse: a Ghost pays half its max HP to curse
    /// the opponent (a quarter per turn); anyone else trades Speed for
    /// Attack and Defense.</summary>
    public class CurseEffect : BaseMoveEffect
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
            cancelled = true;

            if (attacker.Types.Contains(PokemonType.Ghost))
            {
                if (defender.Fainted || defender.Cursed)
                {
                    state.Log.Write("But it failed!");
                    return;
                }

                attacker.CurrentHP = Math.Max(0, attacker.CurrentHP - attacker.MaxHP / 2);
                defender.Cursed = true;

                state.Log.Write($"{attacker.Species} cut its own HP and laid a curse on {defender.Species}!");

                if (attacker.Fainted)
                    state.Log.Write($"{attacker.Species} fainted!");

                return;
            }

            MoveResolver.ApplyStatChange(state, attacker, "Speed", -1);
            MoveResolver.ApplyStatChange(state, attacker, "Attack", 1);
            MoveResolver.ApplyStatChange(state, attacker, "Defense", 1);
        }
    }

    /// <summary>Section 158. Destiny Bond: if the user is knocked out by a
    /// direct hit before it next acts, the attacker goes down too (see the
    /// faint handling in MoveResolver).</summary>
    public class DestinyBondEffect : BaseMoveEffect
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
            cancelled = true;

            attacker.DestinyBondActive = true;
            state.Log.Write($"{attacker.Species} is hoping to take its attacker down with it!");
        }
    }

    /// <summary>Section 158. Ingrain: roots the user - a sixteenth of
    /// healing per turn, no switching, and it counts as grounded.</summary>
    public class IngrainEffect : BaseMoveEffect
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
            cancelled = true;

            if (attacker.Rooted)
            {
                state.Log.Write("But it failed!");
                return;
            }

            attacker.Rooted = true;
            state.Log.Write($"{attacker.Species} planted its roots!");
        }
    }

    /// <summary>Section 158. Aqua Ring: a sixteenth of healing per turn.</summary>
    public class AquaRingEffect : BaseMoveEffect
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
            cancelled = true;

            if (attacker.AquaRing)
            {
                state.Log.Write("But it failed!");
                return;
            }

            attacker.AquaRing = true;
            state.Log.Write($"{attacker.Species} surrounded itself with a veil of water!");
        }
    }

    /// <summary>Section 158. Magnet Rise: five turns of hovering (see
    /// Grounding).</summary>
    public class MagnetRiseEffect : BaseMoveEffect
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
            cancelled = true;

            if (attacker.MagnetRiseTurns > 0 || state.GravityTurns > 0)
            {
                state.Log.Write("But it failed!");
                return;
            }

            attacker.MagnetRiseTurns = 5;
            state.Log.Write($"{attacker.Species} levitated with electromagnetism!");
        }
    }

    /// <summary>Section 158. Stockpile: up to three charges, each raising
    /// Defense and Sp. Def (Spit Up and Swallow are not in any learnset the
    /// Simulator carries, so the counter's only battle meaning is these
    /// boosts).</summary>
    public class StockpileEffect : BaseMoveEffect
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
            cancelled = true;

            if (attacker.StockpileCount >= 3)
            {
                state.Log.Write("But it failed!");
                return;
            }

            attacker.StockpileCount++;
            state.Log.Write($"{attacker.Species} stockpiled {attacker.StockpileCount}!");

            MoveResolver.ApplyStatChange(state, attacker, "Defense", 1);
            MoveResolver.ApplyStatChange(state, attacker, "SpDefense", 1);
        }
    }

    /// <summary>Section 158. Encore: the target repeats its last move for
    /// the next three turns.</summary>
    public class EncoreEffect : BaseMoveEffect
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

            var lastMove = defender.LastMoveName == null
                ? null
                : defender.Moves.FirstOrDefault(m => m.Name == defender.LastMoveName);

            if (defender.EncoreTurns > 0 || lastMove == null || lastMove.CurrentPP <= 0)
            {
                state.Log.Write("But it failed!");
                return;
            }

            defender.EncoreMoveName = lastMove.Name;
            defender.EncoreTurns = 3;
            state.Log.Write($"{defender.Species} received an encore!");
        }
    }

    /// <summary>Section 158. Taunt: three turns of attacks only.</summary>
    public class TauntEffect : BaseMoveEffect
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

            if (defender.TauntTurns > 0)
            {
                state.Log.Write("But it failed!");
                return;
            }

            defender.TauntTurns = 3;
            state.Log.Write($"{defender.Species} fell for the taunt!");
        }
    }

    /// <summary>Section 158. Disable: the target's last move is sealed for
    /// four turns.</summary>
    public class DisableEffect : BaseMoveEffect
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

            if (defender.DisabledTurns > 0 || defender.LastMoveName == null ||
                !defender.Moves.Any(m => m.Name == defender.LastMoveName))
            {
                state.Log.Write("But it failed!");
                return;
            }

            defender.DisabledMoveName = defender.LastMoveName;
            defender.DisabledTurns = 4;
            state.Log.Write($"{defender.Species}'s {defender.DisabledMoveName} was disabled!");
        }
    }

    /// <summary>Section 158. Torment: the target may not repeat its last
    /// move (until it switches).</summary>
    public class TormentEffect : BaseMoveEffect
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

            if (defender.Tormented)
            {
                state.Log.Write("But it failed!");
                return;
            }

            defender.Tormented = true;
            state.Log.Write($"{defender.Species} was subjected to torment!");
        }
    }

    /// <summary>Section 158. Imprison: the opponent may not use moves the
    /// user also knows (see LegalActions).</summary>
    public class ImprisonEffect : BaseMoveEffect
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
            cancelled = true;

            if (attacker.ImprisonActive)
            {
                state.Log.Write("But it failed!");
                return;
            }

            attacker.ImprisonActive = true;
            state.Log.Write($"{attacker.Species} sealed the moves it shares with its opponent!");
        }
    }

    /// <summary>Section 158. Salt Cure: residual damage each turn, doubled
    /// against Water and Steel types.</summary>
    public class SaltCureEffect : BaseMoveEffect
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
            if (cancelled || damage <= 0 || defender.Fainted || defender.SaltCured)
                return;

            defender.SaltCured = true;
            state.Log.Write($"{defender.Species} is being salt cured!");
        }
    }

    /// <summary>Section 158. Lock-On: the user's next move skips its
    /// accuracy roll.</summary>
    public class LockOnEffect : BaseMoveEffect
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
            cancelled = true;

            attacker.LockOnActive = true;
            state.Log.Write($"{attacker.Species} took aim at {defender.Species}!");
        }
    }

    /// <summary>Section 158. Spite: the target's last move loses four PP.</summary>
    public class SpitePpEffect : BaseMoveEffect
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

            var lastMove = defender.LastMoveName == null
                ? null
                : defender.Moves.FirstOrDefault(m => m.Name == defender.LastMoveName);

            if (lastMove == null || lastMove.CurrentPP <= 0)
            {
                state.Log.Write("But it failed!");
                return;
            }

            int drained = Math.Min(4, lastMove.CurrentPP);
            lastMove.CurrentPP -= drained;

            state.Log.Write($"{defender.Species}'s {lastMove.Name} lost {drained} PP!");
        }
    }
}