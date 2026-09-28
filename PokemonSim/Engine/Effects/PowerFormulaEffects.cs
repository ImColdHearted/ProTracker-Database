using System;
using PokemonSim.Data;
using PokemonSim.Models;

namespace PokemonSim.Engine.Effects
{
    /// <summary>
    /// §303. The power-formula family.
    ///
    /// The audit found thirty-five moves whose power is not the number in
    /// moves.json - it is worked out from the battle. They had all been
    /// resolving at their listed power, which for Low Kick means a Pikachu
    /// and a Groudon take the same hit.
    ///
    /// Every one of them is the same shape: decide what the power really is
    /// this time, then deal damage as if the move had been listed at that.
    /// That second half is the whole reason this is a family rather than
    /// thirty-five classes. The engine computes damage before these effects
    /// run (they are BeforeDamage), so a formula does not need to reproduce
    /// the damage equation - it rescales what the equation already produced
    /// by the ratio of the real power to the listed one. One line, in one
    /// place, and a subclass only has to answer "how strong is it now".
    ///
    /// The ratio is taken against the move's OWN listed power rather than a
    /// hardcoded hundred. §158's power effects (FlailPower, ElectroBallPower
    /// and the rest) each divide by 100 and are correct only because their
    /// moves happen to be listed at 100; a formula here can be pointed at
    /// any move at any listed power. Those older ones are left alone - they
    /// work, they are covered, and converging them is a change to tested
    /// behaviour that belongs in its own section with a compiler to hand.
    ///
    /// WHAT IS NOT HERE. Nineteen of the thirty-five need something the
    /// engine cannot answer yet and are not faked: a "did my last move fail"
    /// flag (Stomping Tantrum, Temper Flare), a times-hit counter (Rage
    /// Fist), a consecutive-use counter (Fury Cutter, Rollout, Echoed
    /// Voice), the hit number inside a multi-hit (Triple Kick, Triple Axel),
    /// a switch that has been declared but not yet happened (Pursuit), or an
    /// ally (Beat Up, the three Pledges). Round cannot double in a
    /// one-on-one battle and Water Shuriken's bonus is one form of one
    /// species, so both are already right at their listed power.
    /// </summary>
    public abstract class PowerFormulaEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        /// <summary>How strong the move is on this use, in base-power
        /// points. Answer with the move's listed power to leave it alone -
        /// which is what every "not this time" branch does.</summary>
        protected abstract int PowerNow(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move);

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

            int listed = Math.Max(1, move.Power);
            int now = Math.Max(1, PowerNow(state, attacker, defender, move));

            if (now == listed)
                return;

            damage = Math.Max(1, (int)((long)damage * now / listed));
        }
    }

    /// <summary>The half of the family that only ever doubles: a condition,
    /// and twice the power when it holds.</summary>
    public abstract class PowerDoublerEffect : PowerFormulaEffect
    {
        protected abstract bool Doubles(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move);

        protected override int PowerNow(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move)
            => Doubles(state, attacker, defender, move) ? move.Power * 2 : move.Power;
    }

    // ---- the doublers -------------------------------------------------

    /// <summary>Acrobatics: twice as strong with empty hands.</summary>
    public class PowerIfNoItemEffect : PowerDoublerEffect
    {
        protected override bool Doubles(BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
            => string.IsNullOrWhiteSpace(attacker.HeldItemId) || attacker.LostItem;
    }

    /// <summary>Assurance: twice as strong against something that has
    /// already been hurt this turn. The counters are §158's Counter and
    /// Mirror Coat bookkeeping, cleared at the end of every turn, and they
    /// are damage TAKEN - which is the question Assurance asks.</summary>
    public class PowerIfTargetHurtEffect : PowerDoublerEffect
    {
        protected override bool Doubles(BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
            => defender.LastPhysicalDamageTaken + defender.LastSpecialDamageTaken > 0;
    }

    /// <summary>Avalanche and Revenge: twice as strong when the user has
    /// already been hurt this turn. Mainline asks whether the damage came
    /// from THIS target; in a one-on-one battle there is nobody else it
    /// could have come from.</summary>
    public class PowerIfHurtByTargetEffect : PowerDoublerEffect
    {
        protected override bool Doubles(BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
            => attacker.LastPhysicalDamageTaken + attacker.LastSpecialDamageTaken > 0;
    }

    /// <summary>Bolt Beak and Fishious Rend: twice as strong when the user
    /// gets there first. ActedThisTurn is set as a move resolves and
    /// cleared at end of turn, so a target that has not acted yet either
    /// moves later this turn or has just switched in - which is the same
    /// pair of cases mainline doubles for.</summary>
    public class PowerIfMovingFirstEffect : PowerDoublerEffect
    {
        protected override bool Doubles(BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
            => !defender.ActedThisTurn;
    }

    /// <summary>Payback: the mirror of the above - twice as strong when the
    /// target has already had its go.</summary>
    public class PowerIfMovingLastEffect : PowerDoublerEffect
    {
        protected override bool Doubles(BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
            => defender.ActedThisTurn;
    }

    /// <summary>Rising Voltage: twice as strong into a grounded target on
    /// Electric Terrain.</summary>
    public class PowerOnElectricTerrainEffect : PowerDoublerEffect
    {
        protected override bool Doubles(BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
            => state.Environment.Terrain == TerrainType.Electric &&
               Grounding.IsGrounded(state, defender);
    }

    /// <summary>
    /// Wake-Up Slap: twice as strong against a sleeping target, and it wakes
    /// the target up.
    ///
    /// The waking happens here, before the damage lands, rather than in a
    /// second AfterDamage effect. Nothing between the two points reads the
    /// defender's sleep for this move - the power question has already been
    /// answered on the line above, and the only other reader of a sleeping
    /// defender is Dream Eater's own gate, which is a different move - so
    /// the order is unobservable and one class is easier to keep honest
    /// than two that have to agree.
    /// </summary>
    public class PowerIfTargetAsleepEffect : PowerFormulaEffect
    {
        protected override int PowerNow(BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
        {
            if (defender.Status != StatusCondition.Sleep)
                return move.Power;

            defender.Status = StatusCondition.None;
            defender.SleepTurns = 0;

            state.Log.Write($"{defender.Species} was woken up!");

            return move.Power * 2;
        }
    }

    // ---- the formulas -------------------------------------------------

    /// <summary>Punishment: the listed power plus twenty for each stat
    /// stage the target has raised, up to 200.</summary>
    public class PowerFromTargetBoostsEffect : PowerFormulaEffect
    {
        protected override int PowerNow(BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
        {
            int stages =
                Math.Max(0, defender.AttackStage) +
                Math.Max(0, defender.DefenseStage) +
                Math.Max(0, defender.SpAttackStage) +
                Math.Max(0, defender.SpDefenseStage) +
                Math.Max(0, defender.SpeedStage) +
                Math.Max(0, defender.AccuracyStage) +
                Math.Max(0, defender.EvasionStage);

            return Math.Min(200, move.Power + 20 * stages);
        }
    }

    /// <summary>Gyro Ball: 25 times the target's Speed over the user's, plus
    /// one, capped at 150 - so the slower the user, the harder it hits. The
    /// stat is read through StatResolver, so stages, paralysis and the rest
    /// are already in the numbers.</summary>
    public class PowerFromSpeedRatioEffect : PowerFormulaEffect
    {
        protected override int PowerNow(BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
        {
            double mine = Math.Max(1.0, StatResolver.GetStat(state, attacker, "Speed"));
            double theirs = Math.Max(0.0, StatResolver.GetStat(state, defender, "Speed"));

            return Math.Min(150, (int)Math.Floor(25.0 * theirs / mine) + 1);
        }
    }

    /// <summary>Reversal: the classic 20..200 band by the user's remaining
    /// HP, in forty-eighths. Flail's own effect (§158) holds the same table
    /// against a hardcoded 100 - see the family's note.</summary>
    public class PowerFromUserHpBandsEffect : PowerFormulaEffect
    {
        protected override int PowerNow(BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
        {
            int ratio = Math.Max(1, attacker.CurrentHP * 48 / Math.Max(1, attacker.MaxHP));

            return ratio < 2 ? 200
                : ratio < 5 ? 150
                : ratio < 10 ? 100
                : ratio < 17 ? 80
                : ratio < 33 ? 40
                : 20;
        }
    }

    /// <summary>Low Kick and Grass Knot: 20 to 120 by how heavy the target
    /// is. A species with no weight in the dex leaves the move at its listed
    /// power rather than guessing a band.</summary>
    public class PowerFromTargetWeightEffect : PowerFormulaEffect
    {
        protected override int PowerNow(BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
        {
            double kg = PokemonDex.WeightOf(defender.Species);

            if (kg <= 0)
                return move.Power;

            return kg >= 200 ? 120
                : kg >= 100 ? 100
                : kg >= 50 ? 80
                : kg >= 25 ? 60
                : kg >= 10 ? 40
                : 20;
        }
    }

    /// <summary>Heavy Slam and Heat Crash: 40 to 120 by how many times
    /// heavier the user is than the target. Same rule as above when either
    /// weight is unknown.</summary>
    public class PowerFromWeightRatioEffect : PowerFormulaEffect
    {
        protected override int PowerNow(BattleState state, PokemonState attacker, PokemonState defender, MoveState move)
        {
            double mine = PokemonDex.WeightOf(attacker.Species);
            double theirs = PokemonDex.WeightOf(defender.Species);

            if (mine <= 0 || theirs <= 0)
                return move.Power;

            return mine >= theirs * 5 ? 120
                : mine >= theirs * 4 ? 100
                : mine >= theirs * 3 ? 80
                : mine >= theirs * 2 ? 60
                : 40;
        }
    }
}
