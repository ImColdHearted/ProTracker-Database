using System;
using System.Collections.Generic;

namespace PokemonSim.Engine.Effects
{
    /// <summary>Effect names as DataFiles/moves.json spells them. Get
    /// answers null for a name it does not know, and MoveResolver turns
    /// that null into a visible "(not simulated yet)" log line - an
    /// unsupported effect is a diagnostic, never a silent success.
    /// Section 154 adds StruggleRecoil and drops the constructor's
    /// Console.WriteLine (a library should not talk to a console).</summary>
    public static class MoveEffectRegistry
    {
        static Dictionary<string, Func<IMoveEffect>> effects =
            new Dictionary<string, Func<IMoveEffect>>();

        static MoveEffectRegistry()
        {
            Register("Protect", () => new ProtectEffect());
            Register("Drain", () => new DrainEffect());
            Register("Heal", () => new HealEffect());
            Register("SwitchAfter", () => new SwitchAfterEffect());
            Register("Substitute", () => new SubstituteEffect());
            Register("StealthRock", () => new StealthRockEffect());
            Register("Spikes", () => new SpikesEffect());
            Register("ToxicSpikes", () => new ToxicSpikesEffect());
            Register("TwoTurn", () => new TwoTurnMoveEffect());

            // §301: the mirror of TwoTurn - the turn is spent after the
            // damage instead of before it.
            Register("Recharge", () => new RechargeEffect());
            Register("RapidSpin", () => new RapidSpinEffect());
            Register("Defog", () => new DefogEffect());
            Register("StruggleRecoil", () => new StruggleRecoilEffect());

            // Section 155: the boss-move batch.
            Register("Recoil25", () => new RecoilEffect(0.25));
            Register("Recoil33", () => new RecoilEffect(1.0 / 3.0));
            Register("Recoil50", () => new RecoilEffect(0.5));
            Register("FacadeBoost", () => new StatusPowerEffect(checksAttacker: true));
            Register("HexBoost", () => new StatusPowerEffect(checksAttacker: false));
            Register("LevelDamage", () => new LevelDamageEffect());
            Register("SelfFaint", () => new SelfFaintEffect());
            Register("BindTrap", () => new BindTrapEffect(dealsDamage: true));
            Register("TrapNoDamage", () => new BindTrapEffect(dealsDamage: false));

            // ---- Section 158: the move-completion batch. ----

            // Side conditions and field states.
            Register("Reflect", () => new ScreenEffect(physical: true));
            Register("LightScreen", () => new ScreenEffect(physical: false));
            Register("ScreenBreak", () => new ScreenBreakEffect());
            Register("Mist", () => new MistEffect());
            Register("Safeguard", () => new SafeguardEffect());
            Register("Tailwind", () => new TailwindEffect());
            Register("CourtChange", () => new CourtChangeEffect());
            Register("TrickRoom", () => new TrickRoomEffect());
            Register("Gravity", () => new GravityEffect());
            Register("TerrainClear", () => new TerrainClearEffect());
            Register("ClearOwnHazards", () => new ClearOwnHazardsEffect());

            // Volatile conditions.
            Register("LeechSeed", () => new LeechSeedEffect());
            Register("Yawn", () => new YawnEffect());
            Register("InflictConfusion", () => new InflictConfusionEffect());
            Register("Confuse10", () => new InflictConfusionEffect(0.10));
            Register("Confuse20", () => new InflictConfusionEffect(0.20));
            Register("Confuse30", () => new InflictConfusionEffect(0.30));
            Register("Confuse100", () => new InflictConfusionEffect(1.0));
            Register("PerishSong", () => new PerishSongEffect());
            Register("Curse", () => new CurseEffect());
            Register("DestinyBond", () => new DestinyBondEffect());
            Register("Ingrain", () => new IngrainEffect());
            Register("AquaRing", () => new AquaRingEffect());
            Register("MagnetRise", () => new MagnetRiseEffect());
            Register("Stockpile", () => new StockpileEffect());
            Register("Encore", () => new EncoreEffect());
            Register("Taunt", () => new TauntEffect());
            Register("Disable", () => new DisableEffect());
            Register("Torment", () => new TormentEffect());
            Register("Imprison", () => new ImprisonEffect());
            Register("SaltCure", () => new SaltCureEffect());
            Register("LockOn", () => new LockOnEffect());
            Register("SpitePp", () => new SpitePpEffect());

            // Damage arithmetic.
            Register("SuperFang", () => new SuperFangEffect());
            Register("Endeavor", () => new EndeavorEffect());
            Register("Psywave", () => new PsywaveEffect());
            Register("Ohko", () => new OhkoEffect());
            Register("Counter", () => new CounterEffect(physical: true));
            Register("MirrorCoat", () => new CounterEffect(physical: false));
            Register("MetalBurst", () => new MetalBurstEffect());
            Register("PainSplit", () => new PainSplitEffect());
            Register("FalseSwipe", () => new FalseSwipeEffect());
            Register("StoredPower", () => new StoredPowerEffect());
            Register("FlailPower", () => new FlailPowerEffect());
            Register("ElectroBallPower", () => new ElectroBallPowerEffect());
            Register("TargetHpPower", () => new TargetHpPowerEffect());
            Register("UserHpPower", () => new UserHpPowerEffect());
            Register("BrineBoost", () => new BrineBoostEffect());
            Register("VenoshockBoost", () => new StatusPowerEffect(checksAttacker: false, requiresPoison: true));
            Register("ExpandingForceBoost", () => new ExpandingForceEffect());
            Register("WeatherBall", () => new WeatherBallEffect());
            Register("MeteorCharge", () => new MeteorChargeEffect());
            Register("DreamEaterGate", () => new DreamEaterGateEffect());
            Register("HalfHpCost", () => new HalfHpCostEffect());
            Register("LastResort", () => new LastResortEffect());

            // The Protect family.
            Register("Endure", () => new EndureEffect());
            Register("KingsShield", () => new KingsShieldEffect());
            Register("BanefulBunker", () => new BanefulBunkerEffect());
            Register("QuickGuard", () => new QuickGuardEffect());
            Register("WideGuard", () => new WideGuardEffect());
            Register("Feint", () => new FeintEffect());

            // Rest, cures, copies and identity benders.
            Register("Rest", () => new RestEffect());
            Register("HealBell", () => new HealBellEffect());
            Register("HealQuarter", () => new HealEffect(0.25));
            Register("SleepTalk", () => new SleepTalkEffect());
            Register("SnoreOnlyAsleep", () => new SnoreGateEffect());
            Register("BellyDrum", () => new BellyDrumEffect());
            Register("PsychUp", () => new PsychUpEffect());
            Register("Haze", () => new HazeEffect());
            Register("SkillSwap", () => new SkillSwapEffect());
            Register("WorrySeed", () => new WorrySeedEffect());
            Register("SoakWater", () => new SoakWaterEffect());
            Register("MagicPowderPsychic", () => new MagicPowderEffect());
            Register("HealingWish", () => new HealingWishEffect());

            // Switching.
            Register("ForcedSwitch", () => new ForcedSwitchEffect());
            Register("BatonPass", () => new BatonPassEffect());

            // Markers the engine reads by name.
            Register("GrassyGlidePriority", () => new MarkerMoveEffect());
            Register("AteConverted", () => new MarkerMoveEffect());

            // Section 159: the item-premise moves become real.
            Register("TrickItem", () => new TrickItemEffect());
            Register("FlingItem", () => new FlingItemEffect());
            Register("FlingThrow", () => new FlingThrowEffect());
            Register("PoltergeistGate", () => new PoltergeistGateEffect());
            Register("KnockOff", () => new KnockOffEffect());
            Register("KnockOffRemove", () => new KnockOffRemoveEffect());
            Register("StealItem", () => new StealItemEffect());

            // ---- Section 184: the competitive-set batch. ----
            Register("StickyWeb", () => new StickyWebEffect());
            Register("AuroraVeil", () => new AuroraVeilEffect());
            Register("StrengthSap", () => new StrengthSapEffect());
            Register("FirstImpression", () => new FirstImpressionEffect());

            // §197: the same rule under a name that says what it does rather
            // than which move asked for it first. Fake Out uses this one.
            Register("FirstTurnOnly", () => new FirstImpressionEffect());
            Register("FinalGambit", () => new FinalGambitEffect());
            Register("RefreshCure", () => new RefreshCureEffect());
            Register("HeartSwap", () => new HeartSwapEffect());
            Register("ShedTail", () => new ShedTailEffect());
            Register("ChillyReception", () => new ChillyReceptionEffect());
            Register("Drain75", () => new DrainEffect(0.75));

            // ---- §303: the power-formula family. ----
            Register("PowerIfNoItem", () => new PowerIfNoItemEffect());
            Register("PowerIfTargetHurt", () => new PowerIfTargetHurtEffect());
            Register("PowerIfHurtByTarget", () => new PowerIfHurtByTargetEffect());
            Register("PowerIfMovingFirst", () => new PowerIfMovingFirstEffect());
            Register("PowerIfMovingLast", () => new PowerIfMovingLastEffect());
            Register("PowerIfTargetAsleep", () => new PowerIfTargetAsleepEffect());
            Register("PowerOnElectricTerrain", () => new PowerOnElectricTerrainEffect());
            Register("PowerFromTargetBoosts", () => new PowerFromTargetBoostsEffect());
            Register("PowerFromSpeedRatio", () => new PowerFromSpeedRatioEffect());
            Register("PowerFromUserHpBands", () => new PowerFromUserHpBandsEffect());
            Register("PowerFromTargetWeight", () => new PowerFromTargetWeightEffect());
            Register("PowerFromWeightRatio", () => new PowerFromWeightRatioEffect());

            // ---- §304: the lock-ins, the counters and the leftovers. ----

            // The multi-turn lock. Three numbers, one class: how few turns,
            // how many, and whether running out confuses the user.
            Register("LockIn2to3Confuse", () => new LockInEffect(2, 3, confuses: true));
            Register("LockIn3", () => new LockInEffect(3, 3, confuses: false));
            Register("LockIn5", () => new LockInEffect(5, 5, confuses: false));
            Register("UproarWake", () => new UproarWakeEffect());

            // The consecutive-use ramps.
            Register("PowerFromFuryCutter", () => new PowerFromConsecutiveUsesEffect(doubles: true, maxSteps: 3));
            Register("PowerFromEchoedVoice", () => new PowerFromConsecutiveUsesEffect(doubles: false, maxSteps: 5));
            Register("PowerFromRollout", () => new PowerFromConsecutiveUsesEffect(doubles: true, maxSteps: 5, defenseCurlDoubles: true));
            Register("DefenseCurl", () => new DefenseCurlEffect());
            Register("Minimize", () => new MinimizeEffect());

            // The counters §303 had to leave behind.
            Register("PowerFromTimesAttacked", () => new PowerFromTimesAttackedEffect());
            Register("PowerIfLastMoveFailed", () => new PowerIfLastMoveFailedEffect());
            Register("PowerFromHitNumber", () => new PowerFromHitNumberEffect());
            Register("PowerIfTargetSwitching", () => new PowerIfTargetSwitchingEffect());
            Register("PowerFromHappiness", () => new PowerFromHappinessEffect(inverted: false));
            Register("PowerFromLowHappiness", () => new PowerFromHappinessEffect(inverted: true));
            Register("PowerIfAshGreninja", () => new PowerIfAshGreninjaEffect());
            Register("BeatUp", () => new BeatUpEffect());
            Register("PowerFromBeatUpAlly", () => new PowerFromBeatUpAllyEffect());

            // The leftovers.
            Register(CrashDamageEffect.Name, () => new CrashDamageEffect());
            Register("SmackDown", () => new SmackDownEffect());
            Register("HealBlock", () => new HealBlockEffect());
            Register("CureTargetBurn", () => new CureTargetBurnEffect());
            Register("GlaiveRush", () => new GlaiveRushEffect());
            Register("Rage", () => new RageEffect());
            Register("Charge", () => new ChargeEffect());
            Register("Roost", () => new RoostEffect());
            Register("IonDeluge", () => new IonDelugeEffect());
            Register("SpectralThief", () => new SpectralThiefEffect());
            Register("Wish", () => new WishEffect());
            Register("RevivalBlessing", () => new RevivalBlessingEffect());
            Register("UpperHandGate", () => new UpperHandGateEffect());

            // Honest failures - the premise is outside the Simulator.
            Register("FailNoItems", () => new FailEffect("But it failed! (Held items are not simulated.)"));
            Register("FailNoAlly", () => new FailEffect("But it failed! (There is no ally in a one-on-one battle.)"));
            Register("FailNotSimulated", () => new FailEffect("But it failed! (This move's mechanic is not simulated.)"));
            Register("Noop", () => new NoopEffect());
        }

        public static void Register(string name, Func<IMoveEffect> factory)
        {
            effects[name.ToLowerInvariant()] = factory;
        }

        public static IMoveEffect? Get(string name)
        {
            if (effects.TryGetValue(name.ToLowerInvariant(), out var factory))
                return factory();

            return null;
        }

        public static bool IsKnown(string name) =>
            effects.ContainsKey(name.ToLowerInvariant());
    }
}