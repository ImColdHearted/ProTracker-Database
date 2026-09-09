using PokemonSim.Engine.Effects;
using PokemonSim.Models;
using System.Collections.Generic;
using System.Linq;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>
    /// Ability id -> implementation. Section 154: one namespace instead of
    /// three (the factory itself sat in a lowercase "pokemonsim" namespace
    /// nothing else used), ids are normalized ("Sand Stream" and
    /// "sandstream" both land), an UNKNOWN ability answers NoAbility plus a
    /// diagnostic through TryCreate instead of silently pretending, and
    /// Restore rebuilds ability wiring on cloned Pokemon.
    /// </summary>
    public static class AbilityFactory
    {
        public static string Normalize(string? id) =>
            (id ?? string.Empty).Replace(" ", "").Replace("-", "").Trim().ToLowerInvariant();

        /// <summary>Every id this factory implements. Section 158 grew
        /// this from 22 to the full ability universe the Simulator's
        /// species catalog and Boss Database roll from - behaviour-bearing
        /// classes, engine-checked ids, and the documented inert set (see
        /// MarkerAbility).</summary>
        public static IReadOnlyList<string> SupportedIds { get; } = new[]
        {
            "levitate", "intimidate", "technician", "chlorophyll", "drizzle",
            "sandstream", "snowwarning", "drought", "hugepower", "guts",
            "swiftswim", "flashfire", "regenerator", "torrent", "overgrow",
            "blaze", "voltabsorb", "moxie", "moldbreaker", "magicguard",
            "thickfat", "marvelscale",

            // ---- Section 158: classes with behaviour of their own. ----
            "swarm", "sandforce", "ironfist", "toughclaws", "megalauncher",
            "reckless", "analytic", "adaptability", "fairyaura", "aerilate",
            "pixilate", "libero", "protean", "multiscale", "purifyingsalt",
            "waterabsorb", "stormdrain", "sapsipper", "lightningrod",
            "dryskin", "goodasgold", "magicbounce", "static", "poisonpoint",
            // ---- §204 ----
            "disguise",
            "flamebody", "effectspore", "cursedbody", "ironbarbs",
            "roughskin", "aftermath", "mummy", "weakarmor", "justified",
            "toxicdebris", "poisontouch", "purepower", "sandrush",
            "slushrush", "quickfeet", "flareboost", "solarpower",
            "protosynthesis", "quarkdrive", "slowstart", "download",
            "trace", "airlock", "sheerforce", "turboblaze", "teravolt",

            // ---- Section 158: engine-checked ids (the behaviour lives in
            // MoveResolver, DamageCalculator, LegalActions, StatResolver,
            // TryInflictStatus, WeatherEffects, PoisonEffect, RecoilEffect,
            // SwitchResolver or AbilityEndOfTurn). ----
            "baddreams", "raindish", "hydration", "shedskin", "speedboost",
            "poisonheal", "naturalcure", "synchronize", "pressure",
            "serenegrace", "innerfocus", "steadfast", "contrary",
            "clearbody", "defiant", "competitive", "unaware", "scrappy",
            "shellarmor", "battlearmor", "superluck", "sniper", "skilllink",
            "noguard", "compoundeyes", "victorystar", "snowcloak",
            "tintedlens", "filter", "solidrock", "infiltrator", "truant",
            "shadowtag", "magnetpull", "rockhead", "leafguard", "insomnia",
            "vitalspirit", "sturdy", "owntempo",

            // ---- Section 158: deliberately inert - the premise (items,
            // allies, formes, out-of-battle life) is not simulated. ----
            "unnerve", "pickup", "runaway", "cheekpouch", "unburden",
            "minus", "multitype", "stancechange", "oblivious", "flowerveil",
            "soundproof"
        };

        public static bool IsSupported(string? id) =>
            SupportedIds.Contains(Normalize(id));

        public static IAbility Create(string id)
        {
            TryCreate(id, out IAbility ability);
            return ability;
        }

        /// <summary>False when the id is unknown - the caller gets a working
        /// NoAbility either way, and can tell the difference to say so
        /// ("this ability is not simulated yet") instead of silently
        /// treating it as implemented.</summary>
        public static bool TryCreate(string? id, out IAbility ability)
        {
            switch (Normalize(id))
            {
                case "levitate": ability = new LevitateAbility(); return true;
                case "intimidate": ability = new IntimidateAbility(); return true;
                case "technician": ability = new TechnicianAbility(); return true;
                case "chlorophyll": ability = new ChlorophyllAbility(); return true;
                case "drizzle": ability = new DrizzleAbility(); return true;
                case "sandstream": ability = new SandStreamAbility(); return true;
                case "snowwarning": ability = new SnowWarningAbility(); return true;
                case "drought": ability = new DroughtAbility(); return true;
                case "hugepower": ability = new HugePowerAbility(); return true;
                case "guts": ability = new GutsAbility(); return true;
                case "swiftswim": ability = new SwiftSwimAbility(); return true;
                case "flashfire": ability = new FlashFireAbility(); return true;
                case "regenerator": ability = new RegeneratorAbility(); return true;
                case "torrent": ability = new TorrentAbility(); return true;
                case "overgrow": ability = new OvergrowAbility(); return true;
                case "blaze": ability = new BlazeAbility(); return true;
                case "voltabsorb": ability = new VoltAbsorbAbility(); return true;
                case "moxie": ability = new MoxieAbility(); return true;
                case "moldbreaker": ability = new MoldBreakerAbility(); return true;
                case "magicguard": ability = new MagicGuardAbility(); return true;
                case "thickfat": ability = new ThickFatAbility(); return true;
                case "marvelscale": ability = new MarvelScaleAbility(); return true;

                // ---- Section 158: behaviour-bearing classes. ----
                case "swarm": ability = new SwarmAbility(); return true;
                case "sandforce": ability = new SandForceAbility(); return true;
                case "ironfist": ability = new IronFistAbility(); return true;
                case "toughclaws": ability = new ToughClawsAbility(); return true;
                case "megalauncher": ability = new MegaLauncherAbility(); return true;
                case "reckless": ability = new RecklessAbility(); return true;
                case "analytic": ability = new AnalyticAbility(); return true;
                case "adaptability": ability = new AdaptabilityAbility(); return true;
                case "fairyaura": ability = new FairyAuraAbility(); return true;
                case "aerilate": ability = new AteAbility("aerilate", PokemonType.Flying); return true;
                case "pixilate": ability = new AteAbility("pixilate", PokemonType.Fairy); return true;
                case "libero": ability = new LiberoAbility("libero"); return true;
                case "protean": ability = new LiberoAbility("protean"); return true;
                case "multiscale": ability = new MultiscaleAbility(); return true;
                case "purifyingsalt": ability = new PurifyingSaltAbility(); return true;
                case "waterabsorb": ability = new AbsorbAbility("waterabsorb", PokemonType.Water, heals: true); return true;
                case "stormdrain": ability = new AbsorbAbility("stormdrain", PokemonType.Water, heals: false, "SpAttack"); return true;
                case "sapsipper": ability = new AbsorbAbility("sapsipper", PokemonType.Grass, heals: false, "Attack"); return true;
                case "lightningrod": ability = new AbsorbAbility("lightningrod", PokemonType.Electric, heals: false, "SpAttack"); return true;
                case "dryskin": ability = new DrySkinAbility(); return true;

                // §204: Mimikyu's, which nothing had ever implemented - it
                // was a name in the card importer's form-change list and
                // nowhere else, so a Mimikyu built with it battled with no
                // ability at all.
                case "disguise": ability = new DisguiseAbility(); return true;
                case "goodasgold": ability = new GoodAsGoldAbility(); return true;
                case "magicbounce": ability = new MagicBounceAbility(); return true;
                case "static": ability = new ContactStatusAbility("static", StatusCondition.Paralysis); return true;
                case "poisonpoint": ability = new ContactStatusAbility("poisonpoint", StatusCondition.Poison); return true;
                case "flamebody": ability = new ContactStatusAbility("flamebody", StatusCondition.Burn); return true;
                case "effectspore": ability = new EffectSporeAbility(); return true;
                case "cursedbody": ability = new CursedBodyAbility(); return true;
                case "ironbarbs": ability = new BarbedBodyAbility("ironbarbs"); return true;
                case "roughskin": ability = new BarbedBodyAbility("roughskin"); return true;
                case "aftermath": ability = new AftermathAbility(); return true;
                case "mummy": ability = new MummyAbility(); return true;
                case "weakarmor": ability = new WeakArmorAbility(); return true;
                case "justified": ability = new JustifiedAbility(); return true;
                case "toxicdebris": ability = new ToxicDebrisAbility(); return true;
                case "poisontouch": ability = new PoisonTouchAbility(); return true;
                case "purepower": ability = new PurePowerAbility(); return true;
                case "sandrush": ability = new WeatherSpeedAbility("sandrush", WeatherType.Sandstorm); return true;
                case "slushrush": ability = new WeatherSpeedAbility("slushrush", WeatherType.Hail); return true;
                case "quickfeet": ability = new QuickFeetAbility(); return true;
                case "flareboost": ability = new FlareBoostAbility(); return true;
                case "solarpower": ability = new SolarPowerAbility(); return true;
                case "protosynthesis": ability = new ParadoxAbility("protosynthesis", sunDriven: true); return true;
                case "quarkdrive": ability = new ParadoxAbility("quarkdrive", sunDriven: false); return true;
                case "slowstart": ability = new SlowStartAbility(); return true;
                case "download": ability = new DownloadAbility(); return true;
                case "trace": ability = new TraceAbility(); return true;
                case "airlock": ability = new AirLockAbility(); return true;
                case "sheerforce": ability = new SheerForceAbility(); return true;
                case "turboblaze": ability = new MoldBreakerCloneAbility("turboblaze"); return true;
                case "teravolt": ability = new MoldBreakerCloneAbility("teravolt"); return true;

                // ---- Section 158: engine-checked and inert ids - see
                // SupportedIds and MarkerAbility for which is which. ----
                case "baddreams":
                case "raindish":
                case "hydration":
                case "shedskin":
                case "speedboost":
                case "poisonheal":
                case "naturalcure":
                case "synchronize":
                case "pressure":
                case "serenegrace":
                case "innerfocus":
                case "steadfast":
                case "contrary":
                case "clearbody":
                case "defiant":
                case "competitive":
                case "unaware":
                case "scrappy":
                case "shellarmor":
                case "battlearmor":
                case "superluck":
                case "sniper":
                case "skilllink":
                case "noguard":
                case "compoundeyes":
                case "victorystar":
                case "snowcloak":
                case "tintedlens":
                case "filter":
                case "solidrock":
                case "infiltrator":
                case "truant":
                case "shadowtag":
                case "magnetpull":
                case "rockhead":
                case "leafguard":
                case "insomnia":
                case "vitalspirit":
                case "sturdy":
                case "owntempo":
                case "unnerve":
                case "pickup":
                case "runaway":
                case "cheekpouch":
                case "unburden":
                case "minus":
                case "multitype":
                case "stancechange":
                case "oblivious":
                case "flowerveil":
                case "soundproof":
                    ability = new MarkerAbility(Normalize(id));
                    return true;

                default: ability = new NoAbility(); return string.IsNullOrWhiteSpace(id);
            }
        }

        /// <summary>Section 154. Rebuilds the ability object, its passive
        /// effects and its event subscriptions for a CLONED Pokemon on a
        /// CLONED battle, from the AbilityId the clone carried - without
        /// re-running switch-in side effects (no second Intimidate drop, no
        /// weather reset; the copied battle already contains their results).
        /// The subscribed flags copied in AbilityState are cleared first so
        /// the fresh EventManager really gets the listeners.</summary>
        public static void Restore(PokemonState pokemon, BattleState state)
        {
            pokemon.PassiveEffects.Clear();
            pokemon.Ability = null;

            if (string.IsNullOrWhiteSpace(pokemon.AbilityId))
                return;

            TryCreate(pokemon.AbilityId, out IAbility ability);

            pokemon.Ability = ability;
            pokemon.PassiveEffects.AddRange(ability.GetEffects());

            pokemon.AbilityState.Remove("regenerator.subscribed");
            pokemon.AbilityState.Remove("moxie.subscribed");

            switch (Normalize(pokemon.AbilityId))
            {
                case "regenerator":
                    RegeneratorAbility.Subscribe(pokemon, state);
                    break;

                case "moxie":
                    MoxieAbility.Subscribe(pokemon, state);
                    break;

                case "magicguard":
                    pokemon.HasMagicGuard = true;
                    break;
            }
        }
    }

    public class NoAbility : IAbility
    {
        public string Id => "none";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield break;
        }

        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }
}