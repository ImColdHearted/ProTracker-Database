using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine
{
    /// <summary>
    /// One Pokemon uses one move. Section 154 rebuilt the middle of this
    /// file while keeping its shape: rolls come from the battle's seeded
    /// rng; PP is actually spent; Protect actually blocks; a Substitute
    /// actually absorbs; two-turn moves charge and release through the
    /// charge lock; damage-modifying abilities run against each hit's real
    /// number (they used to multiply a damage of zero, before any hit was
    /// rolled); the duplicated AfterMove pass is gone; secondary effects
    /// respect their chances (Tri Attack's random status ignored its roll);
    /// status-category moves apply their own status and stat changes (Swords
    /// Dance did nothing - its stat change sat behind a secondary-chance
    /// gate no status move passed); statuses respect type immunities; sleep
    /// gets a real 1-3 turn counter; and an effect name the registry does
    /// not know logs a diagnostic instead of silently doing nothing.
    /// </summary>
    public static class MoveResolver
    {
        static string Ability(PokemonState pokemon) =>
            Abilities.AbilityFactory.Normalize(pokemon.AbilityId);

        /// <summary>Section 158: viaSleepTalk marks the inner call Sleep
        /// Talk makes - the called move skips the status gates and costs no
        /// PP of its own.</summary>
        public static void Resolve(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            bool viaSleepTalk = false)
        {
            var log = state.Log;

            if (attacker.Fainted)
                return;

            // Section 158: Truant loafs every other turn.
            if (!viaSleepTalk && Ability(attacker) == "truant")
            {
                if (attacker.Loafing)
                {
                    attacker.Loafing = false;
                    log.Write($"{attacker.Species} is loafing around!");
                    return;
                }

                attacker.Loafing = true;
            }

            // Flinch
            if (attacker.Flinched)
            {
                log.Write($"{attacker.Species} flinched and couldn't move!");
                attacker.Flinched = false;
                return;
            }

            // Sleep. Section 158: Sleep Talk and Snore act from inside it.
            if (attacker.Status == StatusCondition.Sleep && !viaSleepTalk)
            {
                if (attacker.SleepTurns > 0)
                {
                    attacker.SleepTurns--;
                    log.Write($"{attacker.Species} is fast asleep!");

                    if (move.Effects != null && move.Effects.Contains("SleepTalk"))
                    {
                        if (move.CurrentPP > 0)
                            move.CurrentPP--;

                        log.Write($"{attacker.Species} used {move.Name}!");

                        var callable = attacker.Moves
                            .Where(m => m != move &&
                                        (m.Effects == null ||
                                         (!m.Effects.Contains("SleepTalk") &&
                                          !m.Effects.Contains("TwoTurn"))))
                            .ToList();

                        if (callable.Count == 0)
                        {
                            log.Write("But it failed!");
                            return;
                        }

                        Resolve(state, attacker, defender,
                            callable[state.Rng.Next(callable.Count)], viaSleepTalk: true);
                        return;
                    }

                    if (move.Effects == null || !move.Effects.Contains("SnoreOnlyAsleep"))
                        return;
                }
                else
                {
                    attacker.Status = StatusCondition.None;
                    log.Write($"{attacker.Species} woke up!");
                }
            }

            // Freeze - 20% thaw chance per turn.
            if (attacker.Status == StatusCondition.Freeze)
            {
                if (state.Rng.Next(5) != 0)
                {
                    log.Write($"{attacker.Species} is frozen solid!");
                    return;
                }

                attacker.Status = StatusCondition.None;
                log.Write($"{attacker.Species} thawed out!");
            }

            // Section 158: confusion - a volatile that ticks per action; a
            // third of actions hit the user instead (a 40-power typeless
            // physical hit against its own Defense).
            if (attacker.ConfusionTurns > 0 && !viaSleepTalk)
            {
                attacker.ConfusionTurns--;

                if (attacker.ConfusionTurns == 0)
                {
                    log.Write($"{attacker.Species} snapped out of its confusion!");
                }
                else
                {
                    log.Write($"{attacker.Species} is confused!");

                    if (state.Rng.Next(3) == 0)
                    {
                        double selfAttack = StatResolver.GetStat(state, attacker, "Attack");
                        double selfDefense = StatResolver.GetStat(state, attacker, "Defense");

                        int selfHit = (int)((((2 * attacker.Level / 5 + 2) * 40 * selfAttack / selfDefense) / 50 + 2)
                            * (state.Rng.Next(85, 101) / 100.0));

                        attacker.CurrentHP = Math.Max(0, attacker.CurrentHP - Math.Max(1, selfHit));

                        log.Write($"{attacker.Species} hurt itself in its confusion!");

                        if (attacker.Fainted)
                        {
                            log.Write($"{attacker.Species} fainted!");

                            state.Events.Dispatch(
                                new BattleEvent
                                {
                                    Type = BattleEventType.PokemonFainted,
                                    Target = attacker
                                },
                                state);
                        }

                        return;
                    }
                }
            }

            // Paralysis - 25% full paralysis.
            if (attacker.Status == StatusCondition.Paralysis)
            {
                if (state.Rng.Next(4) == 0)
                {
                    log.Write($"{attacker.Species} is paralyzed! It can't move!");
                    return;
                }
            }

            bool targetsOpponent = TargetsOpponent(move);

            // PP (section 154): spent when the move is pressed - which for a
            // two-turn move is the charging turn, not the release. The legal-
            // action layer keeps 0-PP moves off the menu; reaching this line
            // without PP anyway is a bug worth seeing, not hiding.
            // Section 158: a Sleep Talk call is free, and Pressure drains
            // one extra PP from moves aimed at its owner.
            if (move.MaxPP > 0 && !attacker.Charging && !viaSleepTalk)
            {
                if (move.CurrentPP <= 0)
                {
                    log.Write($"(engine: {attacker.Species} tried {move.Name} with no PP left - refused.)");
                    return;
                }

                move.CurrentPP--;

                if (targetsOpponent && !defender.Fainted &&
                    Ability(defender) == "pressure" && move.CurrentPP > 0)
                {
                    move.CurrentPP--;
                }
            }

            // Section 158: Destiny Bond lapses when its user next acts (the
            // move's own effect re-arms it below), and the last-move memory
            // feeds Encore, Disable, Torment, Spite and Analytic.
            attacker.DestinyBondActive = false;
            attacker.LastMoveName = move.Name;
            attacker.ActedThisTurn = true;

            log.Write($"{attacker.Species} used {move.Name}!");

            // Section 217. The move is committed: every gate above has
            // passed, the PP is spent, and the announcement is written.
            // This is the one point at which a move use is certain, which
            // is what a watching view needs in order to animate it.
            //
            // BattleEventType.BeforeMove has been in the enum since the
            // start and nothing ever dispatched it, so a status move left
            // no trace an observer could see - only damage did. Nothing in
            // the engine subscribes; this is a report, not a hook, and the
            // resolver does not read anything back from it.
            state.Events.Dispatch(
                new BattleEvent
                {
                    Type = BattleEventType.BeforeMove,
                    Source = attacker,
                    Target = defender,
                    Move = move
                },
                state
            );

            int damage = 0;
            bool cancelled = false;

            RunPhase(MovePhase.BeforeMove, ref damage, ref cancelled);

            if (cancelled)
            {
                state.IgnoreDefenderAbilities = false;
                return;
            }

            // Protect (section 154): the flag existed but nothing ever read
            // it. A protected defender blocks anything aimed at it.
            // Section 158: King's Shield lets status moves through and
            // punishes contact; Quick Guard stops priority moves; Wide
            // Guard stops spread moves. Section 161: a Z-Move is not
            // stopped - a quarter of its damage gets through.
            bool zBrokeProtection = false;

            if (targetsOpponent && !defender.Fainted)
            {
                bool blocked = false;

                if (defender.Protected &&
                    (move.Category != MoveCategory.Status || !defender.KingsShieldUp))
                {
                    blocked = true;

                    if (defender.KingsShieldUp && move.IsContact &&
                        move.Category != MoveCategory.Status)
                    {
                        log.Write($"{defender.Species} protected itself!");
                        ApplyStatChangeAgainst(state, defender, attacker, "Attack", -2);
                        state.IgnoreDefenderAbilities = false;
                        return;
                    }
                }
                else if (defender.QuickGuardUp && move.Priority > 0 &&
                         move.Category != MoveCategory.Status)
                {
                    blocked = true;
                }
                else if (defender.WideGuardUp && move.IsSpread)
                {
                    blocked = true;
                }

                if (blocked)
                {
                    if (move.IsZMove && move.Category != MoveCategory.Status)
                    {
                        zBrokeProtection = true;
                        log.Write($"{defender.Species} couldn't fully protect itself!");
                    }
                    else
                    {
                        log.Write($"{defender.Species} protected itself!");

                        // Section 162: Baneful Bunker poisons on contact.
                        if (defender.BanefulBunkerUp && move.IsContact &&
                            move.Category != MoveCategory.Status)
                        {
                            TryInflictStatus(state, attacker, StatusCondition.Poison,
                                announceFailure: false, substituteBlocks: false, source: defender);
                        }

                        state.IgnoreDefenderAbilities = false;
                        return;
                    }
                }
            }

            // Semi-invulnerable turn of Fly and friends.
            if (targetsOpponent && defender.Charging)
            {
                log.Write($"{defender.Species} avoided the attack!");
                state.IgnoreDefenderAbilities = false;
                return;
            }

            // Accuracy. 0-or-less accuracy means the move cannot miss - the
            // data files omit accuracy for sure-hit and self-targeted moves.
            // Section 158: Lock-On and No Guard skip the roll entirely;
            // Compound Eyes, Victory Star and Snow Cloak bend it.
            if (move.Accuracy > 0 && targetsOpponent && !attacker.LockOnActive &&
                Ability(attacker) != "noguard" && Ability(defender) != "noguard")
            {
                double accuracy = move.Accuracy;

                accuracy *= AccuracyStageCalculator.GetMultiplier(attacker.AccuracyStage);
                accuracy *= 1.0 / AccuracyStageCalculator.GetMultiplier(defender.EvasionStage);

                if (Ability(attacker) == "compoundeyes")
                    accuracy *= 1.3;

                if (Ability(attacker) == "victorystar")
                    accuracy *= 1.1;

                // Section 159: Wide Lens.
                accuracy *= Items.HeldItems.AccuracyMultiplier(attacker);

                if (!state.IgnoreDefenderAbilities &&
                    Ability(defender) == "snowcloak" &&
                    state.Environment.Weather == WeatherType.Hail)
                {
                    accuracy *= 0.8;
                }

                if (state.Rng.Next(1, 101) > accuracy)
                {
                    log.Write("The attack missed!");
                    state.IgnoreDefenderAbilities = false;
                    return;
                }
            }

            attacker.LockOnActive = false;

            double effectiveness = 1.0;
            bool announcedEffectiveness = false;
            bool brokeSubstitute = false;
            int hitsLanded = 0;

            if (move.Category != MoveCategory.Status)
            {
                // Section 158: Skill Link always rolls the full count.
                int hits = move.MaxHits > move.MinHits
                    ? Ability(attacker) == "skilllink"
                        ? move.MaxHits
                        : state.Rng.Next(move.MinHits, move.MaxHits + 1)
                    : move.MinHits;

                for (int i = 0; i < hits && !defender.Fainted; i++)
                {
                    DamageResult result = DamageCalculator.CalculateDamage(state, attacker, defender, move);

                    effectiveness = result.Effectiveness;

                    if (result.Effectiveness <= 0)
                    {
                        log.Write($"It doesn't affect {defender.Species}...");
                        break;
                    }

                    int hitDamage = result.Damage;

                    // Section 161: the quarter that leaks through Protect.
                    if (zBrokeProtection && hitDamage > 0)
                        hitDamage = Math.Max(1, hitDamage / 4);

                    // Section 158: Reflect / Light Screen halve their
                    // category - crits and Infiltrator punch through.
                    if (hitDamage > 0 && !result.CriticalHit &&
                        Ability(attacker) != "infiltrator")
                    {
                        var defenderSide = state.GetOwner(defender);

                        bool screened = move.Category == MoveCategory.Physical
                            ? state.ReflectTurns(defenderSide) > 0
                            : state.LightScreenTurns(defenderSide) > 0;

                        if (screened)
                            hitDamage /= 2;
                    }

                    // Section 158: the effectiveness- and crit-reading
                    // abilities live here - phase hooks only see a number.
                    if (hitDamage > 0 && result.Effectiveness < 1.0 &&
                        Ability(attacker) == "tintedlens")
                    {
                        hitDamage *= 2;
                    }

                    if (hitDamage > 0 && result.Effectiveness > 1.0 &&
                        !state.IgnoreDefenderAbilities &&
                        (Ability(defender) == "filter" || Ability(defender) == "solidrock"))
                    {
                        hitDamage = hitDamage * 3 / 4;
                    }

                    if (result.CriticalHit && Ability(attacker) == "sniper")
                        hitDamage = hitDamage * 3 / 2;

                    // Section 159: held items on both ends of the hit -
                    // type boosters, Life Orb, Expert Belt on the way out;
                    // the resist berries on the way in.
                    Items.HeldItems.ModifyOutgoingDamage(state, attacker, move, result.Effectiveness, ref hitDamage);
                    Items.HeldItems.ModifyIncomingDamage(state, attacker, defender, move, result.Effectiveness, ref hitDamage);

                    // Damage-modifying passives (Technician, Blaze, Thick
                    // Fat, Flash Fire's boost...) against this hit's number.
                    RunPhase(MovePhase.BeforeDamage, ref hitDamage, ref cancelled);

                    if (cancelled)
                        break;

                    if (result.CriticalHit)
                        log.Write("A critical hit!");

                    if (!announcedEffectiveness)
                    {
                        if (result.Effectiveness > 1.0)
                            log.Write("It's super effective!");
                        else if (result.Effectiveness < 1.0)
                            log.Write("It's not very effective...");

                        announcedEffectiveness = true;
                    }

                    // Substitute takes the hit first (section 154 - the HP
                    // pool existed, nothing ever routed damage into it).
                    // Section 158: Infiltrator slips past it.
                    if (defender.SubstituteHP > 0 && Ability(attacker) != "infiltrator")
                    {
                        int soaked = Math.Min(defender.SubstituteHP, hitDamage);
                        defender.SubstituteHP -= soaked;
                        damage += soaked;

                        log.Write($"The substitute took the hit for {defender.Species}!");

                        if (defender.SubstituteHP <= 0)
                        {
                            brokeSubstitute = true;
                            log.Write($"{defender.Species}'s substitute faded!");
                        }
                    }
                    else
                    {
                        // Section 158: Endure hangs on at 1 HP; Sturdy does
                        // the same from full health. Section 159: so does a
                        // Focus Sash, eating itself (SashSaves logs).
                        if (hitDamage >= defender.CurrentHP)
                        {
                            bool endures = defender.Enduring;

                            bool sturdy = !endures &&
                                defender.CurrentHP == defender.MaxHP &&
                                !state.IgnoreDefenderAbilities &&
                                Ability(defender) == "sturdy";

                            bool sash = !endures && !sturdy &&
                                Items.HeldItems.SashSaves(state, defender, hitDamage);

                            if (endures || sturdy || sash)
                            {
                                hitDamage = defender.CurrentHP - 1;

                                if (endures || sturdy)
                                {
                                    log.Write(endures
                                        ? $"{defender.Species} endured the hit!"
                                        : $"{defender.Species} hung on with Sturdy!");
                                }
                            }
                        }

                        defender.CurrentHP -= hitDamage;

                        if (defender.CurrentHP < 0)
                            defender.CurrentHP = 0;

                        damage += hitDamage;

                        // Section 158: Counter / Mirror Coat / Metal Burst
                        // bookkeeping, cleared at end of turn.
                        if (move.Category == MoveCategory.Physical)
                            defender.LastPhysicalDamageTaken += hitDamage;
                        else if (move.Category == MoveCategory.Special)
                            defender.LastSpecialDamageTaken += hitDamage;

                        // Section 159: Rocky Helmet, Weakness Policy, the
                        // Air Balloon pop, a Sitrus Berry check.
                        Items.HeldItems.AfterHitTaken(state, attacker, defender, move, result.Effectiveness, hitDamage);
                    }

                    hitsLanded++;
                }

                if (hits > 1 && hitsLanded > 1)
                    log.Write($"Hit {hitsLanded} times!");
            }

            RunPhase(MovePhase.AfterDamage, ref damage, ref cancelled);
            RunPhase(MovePhase.AfterMove, ref damage, ref cancelled);

            // Section 159: the Choice lock latches and Life Orb collects.
            Items.HeldItems.AfterMoveUsed(state, attacker, move, damage);

            state.IgnoreDefenderAbilities = false;

            if (damage > 0 && defender.SubstituteHP <= 0 && !brokeSubstitute)
            {
                log.Write(
                    $"{defender.Species} lost {damage} HP. ({defender.CurrentHP}/{defender.MaxHP})"
                );
            }

            ApplySecondaryEffects(state, move, attacker, defender, damage, hitsLanded, brokeSubstitute);

            ApplyEnvironmentEffects(state, attacker, move);

            state.Events.Dispatch(
                new BattleEvent
                {
                    Type = BattleEventType.DamageDealt,
                    Source = attacker,
                    Target = defender,
                    Value = damage
                },
                state
            );

            if (defender.Fainted)
            {
                log.Write($"{defender.Species} fainted!");

                // Section 158: Destiny Bond drags the attacker down too.
                if (defender.DestinyBondActive && damage > 0 && !attacker.Fainted)
                {
                    attacker.CurrentHP = 0;
                    log.Write($"{defender.Species} took {attacker.Species} down with it!");
                }

                state.Events.Dispatch(
                    new BattleEvent
                    {
                        Type = BattleEventType.PokemonFainted,
                        Target = defender,
                        Source = attacker
                    },
                    state
                );
            }

            if (attacker.Fainted)
            {
                log.Write($"{attacker.Species} fainted!");

                state.Events.Dispatch(
                    new BattleEvent
                    {
                        Type = BattleEventType.PokemonFainted,
                        Target = attacker
                    },
                    state
                );
            }

            // -------- LOCAL FUNCTIONS --------

            void RunPhase(MovePhase phase, ref int damageValue, ref bool cancelledValue)
            {
                if (move.Effects != null)
                {
                    foreach (var name in move.Effects)
                    {
                        var effect = MoveEffectRegistry.Get(name);

                        if (effect == null)
                        {
                            if (phase == MovePhase.BeforeMove)
                                log.Write($"({move.Name}'s effect \"{name}\" is not simulated yet - the move's other numbers still apply.)");

                            continue;
                        }

                        if (effect.Phase == phase)
                        {
                            effect.Apply(state, attacker, defender, move, ref damageValue, ref cancelledValue);

                            if (cancelledValue)
                                return;
                        }
                    }
                }

                // §197: an effect only speaks for the slot it belongs to.
                // Without this the attacker ran the defender's abilities and
                // the defender ran the attacker's - see EffectSide.
                foreach (var effect in attacker.PassiveEffects)
                {
                    if (effect.Phase == phase && effect.Side != EffectSide.DefenderOnly)
                    {
                        effect.Apply(state, attacker, defender, move, ref damageValue, ref cancelledValue);

                        if (cancelledValue)
                            return;
                    }
                }

                if (!state.IgnoreDefenderAbilities)
                {
                    foreach (var effect in defender.PassiveEffects)
                    {
                        if (effect.Phase == phase && effect.Side != EffectSide.AttackerOnly)
                        {
                            effect.Apply(state, attacker, defender, move, ref damageValue, ref cancelledValue);

                            if (cancelledValue)
                                return;
                        }
                    }
                }
            }
        }

        /// <summary>Section 158: whether a move is aimed at the opponent -
        /// the one answer Protect, Pressure, Good as Gold and the accuracy
        /// roll all share. Effect-only status moves (Leech Seed, Taunt,
        /// Confuse Ray...) now count via the opponent-effect table.</summary>
        internal static bool TargetsOpponent(MoveState move) =>
            move.Category != MoveCategory.Status ||
            move.InflictStatus != StatusCondition.None ||
            move.RandomStatus != null ||
            HasOpponentStatChange(move) ||
            HasOpponentEffect(move);

        static readonly HashSet<string> OpponentEffects = new(StringComparer.OrdinalIgnoreCase)
        {
            "BindTrap", "TrapNoDamage", "InflictConfusion", "LeechSeed",
            "Yawn", "Taunt", "Encore", "Disable", "Torment", "ForcedSwitch",
            "SkillSwap", "WorrySeed", "SoakWater", "MagicPowderPsychic",
            "SpitePp", "TrickItem"
        };

        static bool HasOpponentEffect(MoveState move) =>
            move.Effects != null && move.Effects.Any(OpponentEffects.Contains);

        static bool HasOpponentStatChange(MoveState move)
        {
            if (move.StatChanges == null)
                return false;

            foreach (var change in move.StatChanges)
            {
                if (change.Target != "self")
                    return true;
            }

            return false;
        }

        static void ApplyEnvironmentEffects(BattleState state, PokemonState attacker, MoveState move)
        {
            if (move.SetWeather != WeatherType.None)
            {
                if (state.Environment.Weather == move.SetWeather)
                {
                    state.Log.Write("But it failed!");
                }
                else
                {
                    state.Environment.Weather = move.SetWeather;

                    // Section 159: a matching weather rock stretches the
                    // holder's weather to eight turns.
                    state.Environment.WeatherTurns = Items.HeldItems.WeatherDuration(attacker, move.SetWeather);

                    state.Log.Write($"The weather became {move.SetWeather}!");
                }
            }

            if (move.SetTerrain != TerrainType.None)
            {
                if (state.Environment.Terrain == move.SetTerrain)
                {
                    state.Log.Write("But it failed!");
                }
                else
                {
                    state.Environment.Terrain = move.SetTerrain;
                    state.Environment.TerrainTurns = 5;

                    state.Log.Write($"{move.SetTerrain} Terrain was created!");
                }
            }
        }

        static void ApplySecondaryEffects(
            BattleState state,
            MoveState move,
            PokemonState attacker,
            PokemonState defender,
            int damageDealt,
            int hitsLanded,
            bool brokeSubstitute)
        {
            bool isStatusMove = move.Category == MoveCategory.Status;

            // A damaging move that never connected has no secondaries.
            if (!isStatusMove && hitsLanded == 0)
                return;

            bool substituteBlocks = defender.SubstituteHP > 0 && !brokeSubstitute;

            // Section 158: Sheer Force trades every secondary of a damaging
            // move for its damage boost; Serene Grace doubles their odds.
            bool sheerForce = !isStatusMove &&
                (move.SecondaryChance > 0 || move.FlinchChance > 0 || move.StatusChance > 0) &&
                Ability(attacker) == "sheerforce";

            double chanceScale = !isStatusMove && Ability(attacker) == "serenegrace" ? 2.0 : 1.0;

            // ---- Status conditions ----
            double statusChance = move.StatusChance > 0 ? move.StatusChance
                : move.SecondaryChance > 0 ? move.SecondaryChance
                : isStatusMove ? 1.0
                : 0.0;

            if (sheerForce)
                statusChance = 0;
            else if (!isStatusMove)
                statusChance = Math.Min(1.0, statusChance * chanceScale);

            StatusCondition toInflict = StatusCondition.None;

            if (move.RandomStatus != null && move.RandomStatus.Count > 0)
                toInflict = move.RandomStatus[state.Rng.Next(move.RandomStatus.Count)];
            else if (move.InflictStatus != StatusCondition.None)
                toInflict = move.InflictStatus;

            if (toInflict != StatusCondition.None && statusChance > 0 && state.Rng.Chance(statusChance))
            {
                TryInflictStatus(state, defender, toInflict, announceFailure: isStatusMove, substituteBlocks, attacker);
            }

            // ---- Flinch ----
            // Section 158: Inner Focus refuses it; Steadfast answers it.
            if (!sheerForce && move.FlinchChance > 0 && !defender.Fainted && !substituteBlocks &&
                state.Rng.Chance(Math.Min(1.0, move.FlinchChance * chanceScale)))
            {
                if (state.IgnoreDefenderAbilities || Ability(defender) != "innerfocus")
                {
                    defender.Flinched = true;
                    state.Log.Write($"{defender.Species} flinched!");

                    if (Ability(defender) == "steadfast")
                        ApplyStatChange(state, defender, "Speed", 1);
                }
            }

            // ---- Stat changes ----
            if (move.StatChanges != null)
            {
                foreach (var change in move.StatChanges)
                {
                    bool self = change.Target == "self";
                    var target = self ? attacker : defender;

                    if (!self)
                    {
                        // Opponent-aimed stat drops on a damaging move are a
                        // secondary: they need their chance roll. Self
                        // changes (Swords Dance, Overheat's drop) always
                        // happen. Section 158: Sheer Force suppresses them,
                        // and drops route through the guarded path (Mist,
                        // Clear Body, Defiant/Competitive).
                        if (sheerForce)
                            continue;

                        double chance = isStatusMove ? 1.0
                            : move.SecondaryChance > 0 ? Math.Min(1.0, move.SecondaryChance * chanceScale)
                            : 1.0;

                        if (target.Fainted || substituteBlocks || !state.Rng.Chance(chance))
                            continue;

                        ApplyStatChangeAgainst(state, attacker, target, change.Stat, change.Stages);
                        continue;
                    }

                    ApplyStatChange(state, target, change.Stat, change.Stages);
                }
            }
        }

        /// <summary>Section 154: one place for "can this status land" - type
        /// immunities (Fire/burn, Electric/paralysis, Ice/freeze,
        /// Poison+Steel/poison), one status at a time, and a fresh sleep
        /// counter when sleep lands.</summary>
        internal static void TryInflictStatus(
            BattleState state,
            PokemonState target,
            StatusCondition status,
            bool announceFailure,
            bool substituteBlocks,
            PokemonState? source = null)
        {
            if (target.Fainted)
                return;

            if (substituteBlocks)
            {
                if (announceFailure)
                    state.Log.Write("But it failed!");
                return;
            }

            if (target.Status != StatusCondition.None)
            {
                if (announceFailure)
                    state.Log.Write($"{target.Species} is already {target.Status}!");
                return;
            }

            bool immune = status switch
            {
                StatusCondition.Burn => target.Types.Contains(PokemonType.Fire),
                StatusCondition.Paralysis => target.Types.Contains(PokemonType.Electric),
                StatusCondition.Freeze => target.Types.Contains(PokemonType.Ice),
                StatusCondition.Poison or StatusCondition.Toxic =>
                    target.Types.Contains(PokemonType.Poison) || target.Types.Contains(PokemonType.Steel),
                _ => false
            };

            // Section 158: ability immunities.
            string targetAbility = Ability(target);

            if (!immune)
            {
                immune =
                    targetAbility == "purifyingsalt" ||
                    (status == StatusCondition.Sleep &&
                     (targetAbility == "insomnia" || targetAbility == "vitalspirit")) ||
                    (targetAbility == "leafguard" &&
                     state.Environment.Weather == WeatherType.Sun);
            }

            if (immune)
            {
                if (announceFailure)
                    state.Log.Write($"It doesn't affect {target.Species}...");
                return;
            }

            // Section 158: field protections - Safeguard's side and Misty
            // Terrain for the grounded.
            if (source != null && !ReferenceEquals(source, target) &&
                state.SafeguardTurns(state.GetOwner(target)) > 0)
            {
                if (announceFailure)
                    state.Log.Write($"{target.Species} is protected by Safeguard!");
                return;
            }

            if (state.Environment.Terrain == TerrainType.Misty &&
                Grounding.IsGrounded(state, target))
            {
                if (announceFailure)
                    state.Log.Write($"{target.Species} is protected by the Misty Terrain!");
                return;
            }

            target.Status = status;

            if (status == StatusCondition.Sleep)
                target.SleepTurns = state.Rng.Next(1, 4);

            if (status == StatusCondition.Toxic)
                target.ToxicCounter = 0;

            state.Log.Write($"{target.Species} is {status}!");

            // Section 159: a held Lum Berry cures the fresh status on the
            // spot.
            Items.HeldItems.OnStatusInflicted(state, target);

            // Section 158: Synchronize mirrors poison, burn and paralysis
            // back at whoever inflicted them.
            if (source != null && !ReferenceEquals(source, target) && !source.Fainted &&
                targetAbility == "synchronize" &&
                (status == StatusCondition.Burn ||
                 status == StatusCondition.Poison ||
                 status == StatusCondition.Toxic ||
                 status == StatusCondition.Paralysis))
            {
                TryInflictStatus(state, source, status, announceFailure: false, substituteBlocks: false);
            }
        }

        /// <summary>Section 158: a stat change with a known opponent
        /// source. Mist and Clear Body can refuse a drop; Defiant and
        /// Competitive answer one with plus two. Self changes go straight
        /// to ApplyStatChange.</summary>
        internal static void ApplyStatChangeAgainst(
            BattleState state,
            PokemonState source,
            PokemonState target,
            string stat,
            int stages)
        {
            if (ReferenceEquals(source, target) || stages >= 0)
            {
                ApplyStatChange(state, target, stat, stages);
                return;
            }

            if (state.MistTurns(state.GetOwner(target)) > 0)
            {
                state.Log.Write($"{target.Species} is protected by the mist!");
                return;
            }

            if (!state.IgnoreDefenderAbilities && Ability(target) == "clearbody")
            {
                state.Log.Write($"{target.Species}'s Clear Body keeps its stats up!");
                return;
            }

            ApplyStatChange(state, target, stat, stages);

            if (target.Fainted)
                return;

            string targetAbility = Ability(target);

            if (targetAbility == "defiant")
                ApplyStatChange(state, target, "Attack", 2);
            else if (targetAbility == "competitive")
                ApplyStatChange(state, target, "SpAttack", 2);
        }

        internal static void ApplyStatChange(
            BattleState state,
            PokemonState pokemon,
            string stat,
            int stages)
        {
            // Section 158: Contrary flips every stage change it receives.
            if (Ability(pokemon) == "contrary")
                stages = -stages;

            int before;
            int after;

            switch (stat)
            {
                case "Attack":
                    before = pokemon.AttackStage;
                    pokemon.AttackStage = after = Clamp(before + stages);
                    break;

                case "Defense":
                    before = pokemon.DefenseStage;
                    pokemon.DefenseStage = after = Clamp(before + stages);
                    break;

                case "SpAttack":
                    before = pokemon.SpAttackStage;
                    pokemon.SpAttackStage = after = Clamp(before + stages);
                    break;

                case "SpDefense":
                    before = pokemon.SpDefenseStage;
                    pokemon.SpDefenseStage = after = Clamp(before + stages);
                    break;

                case "Speed":
                    before = pokemon.SpeedStage;
                    pokemon.SpeedStage = after = Clamp(before + stages);
                    break;

                case "Accuracy":
                    before = pokemon.AccuracyStage;
                    pokemon.AccuracyStage = after = Clamp(before + stages);
                    break;

                case "Evasion":
                    before = pokemon.EvasionStage;
                    pokemon.EvasionStage = after = Clamp(before + stages);
                    break;

                default:
                    state.Log.Write($"(stat \"{stat}\" is not simulated yet.)");
                    return;
            }

            if (after == before)
            {
                state.Log.Write(
                    $"{pokemon.Species}'s {stat} won't go any {(stages > 0 ? "higher" : "lower")}!");
                return;
            }

            state.Log.Write(
                $"{pokemon.Species}'s {stat} {(stages > 0 ? "rose" : "fell")}!"
            );
        }

        static int Clamp(int stage)
        {
            if (stage > 6) return 6;
            if (stage < -6) return -6;
            return stage;
        }
    }
}