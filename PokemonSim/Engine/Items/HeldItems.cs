using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Models;

namespace PokemonSim.Engine.Items
{
    /// <summary>
    /// Section 159. Held items - the whole system in one id-driven static
    /// class, deliberately shaped like the §158 "engine-checked" abilities
    /// rather than the PassiveEffects pipeline: item behaviour must NOT be
    /// suppressed by Mold Breaker's IgnoreDefenderAbilities, so items get
    /// their own hooks at the same engine seams (StatResolver,
    /// MoveResolver's hit loop, the end-of-turn pass, Grounding,
    /// LegalActions). A Pokemon carries at most one normalized
    /// HeldItemId; consumed or removed items set LostItem, which is what
    /// Unburden and the theft moves read.
    ///
    /// The catalog is the battle-relevant set the Boss Database and the
    /// team builder actually use. Section 161: the mega stones now power
    /// real mega evolutions (MegaEvolutions has the stone-to-forme table),
    /// and the 18 type Z-Crystals power Z-Moves (ZMoves) - both are
    /// otherwise inert here, and both are "sticky": IsSticky below keeps
    /// Trick, Fling, Knock Off and Thief from parting them from their
    /// owner. An id outside the catalog warns at team build, exactly like
    /// an unknown ability.
    ///
    /// §375: forty-two more. The sixteen plates Pixie Plate was missing are
    /// type boosters like the Charcoal set; the eighteen Gems are their
    /// one-shot cousins (gems, GemMultiplier); and eight items with effects
    /// of their own - Focus Band, Shell Bell, Shed Shell, Quick Claw, Razor
    /// Fang, Razor Claw, Eject Button, Bright Powder - each at the engine
    /// seam its effect lives at (SavesFromLethalHit, AfterMoveUsed,
    /// LetsHolderEscape, QuickClawTriggers, FlinchChance, CritStageBonus,
    /// AfterMoveTaken, AccuracyAgainstMultiplier). The picker's rule has
    /// not moved: everything it offers does something here.
    /// </summary>
    public static class HeldItems
    {
        /// <summary>§375. A Gem's boost - the Gen 6 value, as chosen.</summary>
        public const double GemMultiplier = 1.3;

        /// <summary>§375. How often a Focus Band holds its holder at 1 HP.</summary>
        public const double FocusBandChance = 0.10;

        /// <summary>§375. How often a Quick Claw moves its holder to the
        /// front of its priority bracket.</summary>
        public const double QuickClawChance = 0.20;

        /// <summary>§375. A Razor Fang's flinch, on a damaging move that has
        /// no flinch of its own.</summary>
        public const double RazorFangFlinchChance = 0.10;

        /// <summary>§375. What Bright Powder does to the accuracy of every
        /// move aimed at its holder.</summary>
        public const double BrightPowderAccuracy = 0.9;

        public static string Normalize(string? id) =>
            (id ?? string.Empty).Replace(" ", "").Replace("-", "").Replace("'", "").Trim().ToLowerInvariant();

        // ---- the catalog ----

        static readonly Dictionary<string, string> displayNames = new()
        {
            ["leftovers"] = "Leftovers",
            ["blacksludge"] = "Black Sludge",
            ["choiceband"] = "Choice Band",
            ["choicespecs"] = "Choice Specs",
            ["choicescarf"] = "Choice Scarf",
            ["lifeorb"] = "Life Orb",
            ["focussash"] = "Focus Sash",
            ["assaultvest"] = "Assault Vest",
            ["eviolite"] = "Eviolite",
            ["rockyhelmet"] = "Rocky Helmet",
            ["weaknesspolicy"] = "Weakness Policy",
            ["expertbelt"] = "Expert Belt",
            ["muscleband"] = "Muscle Band",
            ["wiseglasses"] = "Wise Glasses",
            ["airballoon"] = "Air Balloon",
            ["lightball"] = "Light Ball",
            ["sitrusberry"] = "Sitrus Berry",
            ["lumberry"] = "Lum Berry",
            ["flameorb"] = "Flame Orb",
            ["toxicorb"] = "Toxic Orb",
            ["icyrock"] = "Icy Rock",
            ["damprock"] = "Damp Rock",
            ["heatrock"] = "Heat Rock",
            ["smoothrock"] = "Smooth Rock",
            ["lightclay"] = "Light Clay",
            ["scopelens"] = "Scope Lens",
            ["widelens"] = "Wide Lens",
            ["silkscarf"] = "Silk Scarf",
            ["charcoal"] = "Charcoal",
            ["mysticwater"] = "Mystic Water",
            ["magnet"] = "Magnet",
            ["miracleseed"] = "Miracle Seed",
            ["nevermeltice"] = "Never-Melt Ice",
            ["blackbelt"] = "Black Belt",
            ["poisonbarb"] = "Poison Barb",
            ["softsand"] = "Soft Sand",
            ["sharpbeak"] = "Sharp Beak",
            ["twistedspoon"] = "Twisted Spoon",
            ["silverpowder"] = "Silver Powder",
            ["hardstone"] = "Hard Stone",
            ["spelltag"] = "Spell Tag",
            ["dragonfang"] = "Dragon Fang",
            ["blackglasses"] = "Black Glasses",
            ["metalcoat"] = "Metal Coat",
            ["pixieplate"] = "Pixie Plate",

            // §375: the other sixteen plates - boosters, like Pixie Plate.
            ["fistplate"] = "Fist Plate",
            ["skyplate"] = "Sky Plate",
            ["toxicplate"] = "Toxic Plate",
            ["earthplate"] = "Earth Plate",
            ["stoneplate"] = "Stone Plate",
            ["insectplate"] = "Insect Plate",
            ["spookyplate"] = "Spooky Plate",
            ["ironplate"] = "Iron Plate",
            ["flameplate"] = "Flame Plate",
            ["splashplate"] = "Splash Plate",
            ["meadowplate"] = "Meadow Plate",
            ["zapplate"] = "Zap Plate",
            ["mindplate"] = "Mind Plate",
            ["icicleplate"] = "Icicle Plate",
            ["dracoplate"] = "Draco Plate",
            ["dreadplate"] = "Dread Plate",

            // §375: the eighteen Gems - one boosted move each, then gone.
            ["normalgem"] = "Normal Gem",
            ["firegem"] = "Fire Gem",
            ["watergem"] = "Water Gem",
            ["electricgem"] = "Electric Gem",
            ["grassgem"] = "Grass Gem",
            ["icegem"] = "Ice Gem",
            ["fightinggem"] = "Fighting Gem",
            ["poisongem"] = "Poison Gem",
            ["groundgem"] = "Ground Gem",
            ["flyinggem"] = "Flying Gem",
            ["psychicgem"] = "Psychic Gem",
            ["buggem"] = "Bug Gem",
            ["rockgem"] = "Rock Gem",
            ["ghostgem"] = "Ghost Gem",
            ["dragongem"] = "Dragon Gem",
            ["darkgem"] = "Dark Gem",
            ["steelgem"] = "Steel Gem",
            ["fairygem"] = "Fairy Gem",

            // §375: eight with effects of their own.
            ["focusband"] = "Focus Band",
            ["shellbell"] = "Shell Bell",
            ["shedshell"] = "Shed Shell",
            ["quickclaw"] = "Quick Claw",
            ["razorfang"] = "Razor Fang",
            ["razorclaw"] = "Razor Claw",
            ["ejectbutton"] = "Eject Button",
            ["brightpowder"] = "Bright Powder",

            ["occaberry"] = "Occa Berry",
            ["passhoberry"] = "Passho Berry",
            ["wacanberry"] = "Wacan Berry",
            ["rindoberry"] = "Rindo Berry",
            ["yacheberry"] = "Yache Berry",
            ["chopleberry"] = "Chople Berry",
            ["kebiaberry"] = "Kebia Berry",
            ["shucaberry"] = "Shuca Berry",
            ["cobaberry"] = "Coba Berry",
            ["payapaberry"] = "Payapa Berry",
            ["tangaberry"] = "Tanga Berry",
            ["chartiberry"] = "Charti Berry",
            ["kasibberry"] = "Kasib Berry",
            ["habanberry"] = "Haban Berry",
            ["colburberry"] = "Colbur Berry",
            ["babiriberry"] = "Babiri Berry",
            ["chilanberry"] = "Chilan Berry",
            ["roseliberry"] = "Roseli Berry",

            // Mega stones - §161: each unlocks its mega evolution (see
            // MegaEvolutions); otherwise inert, and sticky.
            ["tyranitarite"] = "Tyranitarite",
            ["garchompite"] = "Garchompite",
            ["diancite"] = "Diancite",
            ["salamencite"] = "Salamencite",
            ["metagrossite"] = "Metagrossite",
            ["slowbronite"] = "Slowbronite",
            ["swampertite"] = "Swampertite",
            ["galladite"] = "Galladite",
            ["aerodactylite"] = "Aerodactylite",
            ["charizarditex"] = "Charizardite X",
            ["charizarditey"] = "Charizardite Y",
            ["latiosite"] = "Latiosite",
            ["latiasite"] = "Latiasite",
            ["heracronite"] = "Heracronite",
            ["gengarite"] = "Gengarite",
            ["lucarionite"] = "Lucarionite",
            ["alakazite"] = "Alakazite",
            ["blastoisinite"] = "Blastoisinite",
            ["blazikenite"] = "Blazikenite",
            ["gardevoirite"] = "Gardevoirite",
            ["gyaradosite"] = "Gyaradosite",
            ["mewtwonitex"] = "Mewtwonite X",
            ["mewtwonitey"] = "Mewtwonite Y",
            ["steelixite"] = "Steelixite",

            // Section 161: the 18 type Z-Crystals - one Z-Move per side
            // per battle (see ZMoves). Sticky, like the stones.
            ["normaliumz"] = "Normalium Z",
            ["firiumz"] = "Firium Z",
            ["wateriumz"] = "Waterium Z",
            ["electriumz"] = "Electrium Z",
            ["grassiumz"] = "Grassium Z",
            ["iciumz"] = "Icium Z",
            ["fightiniumz"] = "Fightinium Z",
            ["poisoniumz"] = "Poisonium Z",
            ["groundiumz"] = "Groundium Z",
            ["flyiniumz"] = "Flyinium Z",
            ["psychiumz"] = "Psychium Z",
            ["buginiumz"] = "Buginium Z",
            ["rockiumz"] = "Rockium Z",
            ["ghostiumz"] = "Ghostium Z",
            ["dragoniumz"] = "Dragonium Z",
            ["darkiniumz"] = "Darkinium Z",
            ["steeliumz"] = "Steelium Z",
            ["fairiumz"] = "Fairium Z"
        };

        static readonly HashSet<string> megaStones = new()
        {
            "tyranitarite", "garchompite", "diancite", "salamencite",
            "metagrossite", "slowbronite", "swampertite", "galladite",
            "aerodactylite", "charizarditex", "charizarditey", "latiosite",
            "latiasite", "heracronite", "gengarite", "lucarionite",
            "alakazite", "blastoisinite", "blazikenite", "gardevoirite",
            "gyaradosite", "mewtwonitex", "mewtwonitey", "steelixite"
        };

        /// <summary>Section 161: the type each Z-Crystal answers to.</summary>
        static readonly Dictionary<string, PokemonType> zCrystals = new()
        {
            ["normaliumz"] = PokemonType.Normal,
            ["firiumz"] = PokemonType.Fire,
            ["wateriumz"] = PokemonType.Water,
            ["electriumz"] = PokemonType.Electric,
            ["grassiumz"] = PokemonType.Grass,
            ["iciumz"] = PokemonType.Ice,
            ["fightiniumz"] = PokemonType.Fighting,
            ["poisoniumz"] = PokemonType.Poison,
            ["groundiumz"] = PokemonType.Ground,
            ["flyiniumz"] = PokemonType.Flying,
            ["psychiumz"] = PokemonType.Psychic,
            ["buginiumz"] = PokemonType.Bug,
            ["rockiumz"] = PokemonType.Rock,
            ["ghostiumz"] = PokemonType.Ghost,
            ["dragoniumz"] = PokemonType.Dragon,
            ["darkiniumz"] = PokemonType.Dark,
            ["steeliumz"] = PokemonType.Steel,
            ["fairiumz"] = PokemonType.Fairy
        };

        /// <summary>The Fire/Water/... damage boosters: id, boosted type.</summary>
        static readonly Dictionary<string, PokemonType> typeBoosters = new()
        {
            ["silkscarf"] = PokemonType.Normal,
            ["charcoal"] = PokemonType.Fire,
            ["mysticwater"] = PokemonType.Water,
            ["magnet"] = PokemonType.Electric,
            ["miracleseed"] = PokemonType.Grass,
            ["nevermeltice"] = PokemonType.Ice,
            ["blackbelt"] = PokemonType.Fighting,
            ["poisonbarb"] = PokemonType.Poison,
            ["softsand"] = PokemonType.Ground,
            ["sharpbeak"] = PokemonType.Flying,
            ["twistedspoon"] = PokemonType.Psychic,
            ["silverpowder"] = PokemonType.Bug,
            ["hardstone"] = PokemonType.Rock,
            ["spelltag"] = PokemonType.Ghost,
            ["dragonfang"] = PokemonType.Dragon,
            ["blackglasses"] = PokemonType.Dark,
            ["metalcoat"] = PokemonType.Steel,
            ["pixieplate"] = PokemonType.Fairy,

            // §375: a plate is a booster with a different name. Pixie Plate
            // was already here because Fairy has no other; these are the
            // sixteen types that have both.
            ["fistplate"] = PokemonType.Fighting,
            ["skyplate"] = PokemonType.Flying,
            ["toxicplate"] = PokemonType.Poison,
            ["earthplate"] = PokemonType.Ground,
            ["stoneplate"] = PokemonType.Rock,
            ["insectplate"] = PokemonType.Bug,
            ["spookyplate"] = PokemonType.Ghost,
            ["ironplate"] = PokemonType.Steel,
            ["flameplate"] = PokemonType.Fire,
            ["splashplate"] = PokemonType.Water,
            ["meadowplate"] = PokemonType.Grass,
            ["zapplate"] = PokemonType.Electric,
            ["mindplate"] = PokemonType.Psychic,
            ["icicleplate"] = PokemonType.Ice,
            ["dracoplate"] = PokemonType.Dragon,
            ["dreadplate"] = PokemonType.Dark
        };

        /// <summary>§375. The type each Gem answers to. A Gem is the one-shot
        /// cousin of the boosters above: the first damaging move of its type
        /// its holder uses is GemMultiplier stronger and the Gem is gone.
        /// Every hit of a multi-hit move gets the boost, because the move is
        /// what the Gem was spent on (PokemonState.GemBoosting carries that
        /// from the first hit to the last). Fairy Gem never left the games'
        /// own code, but its sprite is in the drop and it makes the set
        /// eighteen.</summary>
        static readonly Dictionary<string, PokemonType> gems = new()
        {
            ["normalgem"] = PokemonType.Normal,
            ["firegem"] = PokemonType.Fire,
            ["watergem"] = PokemonType.Water,
            ["electricgem"] = PokemonType.Electric,
            ["grassgem"] = PokemonType.Grass,
            ["icegem"] = PokemonType.Ice,
            ["fightinggem"] = PokemonType.Fighting,
            ["poisongem"] = PokemonType.Poison,
            ["groundgem"] = PokemonType.Ground,
            ["flyinggem"] = PokemonType.Flying,
            ["psychicgem"] = PokemonType.Psychic,
            ["buggem"] = PokemonType.Bug,
            ["rockgem"] = PokemonType.Rock,
            ["ghostgem"] = PokemonType.Ghost,
            ["dragongem"] = PokemonType.Dragon,
            ["darkgem"] = PokemonType.Dark,
            ["steelgem"] = PokemonType.Steel,
            ["fairygem"] = PokemonType.Fairy
        };

        /// <summary>The super-effective-hit berries: id, resisted type.
        /// Chilan is the odd one out - it halves ANY Normal hit.</summary>
        static readonly Dictionary<string, PokemonType> resistBerries = new()
        {
            ["occaberry"] = PokemonType.Fire,
            ["passhoberry"] = PokemonType.Water,
            ["wacanberry"] = PokemonType.Electric,
            ["rindoberry"] = PokemonType.Grass,
            ["yacheberry"] = PokemonType.Ice,
            ["chopleberry"] = PokemonType.Fighting,
            ["kebiaberry"] = PokemonType.Poison,
            ["shucaberry"] = PokemonType.Ground,
            ["cobaberry"] = PokemonType.Flying,
            ["payapaberry"] = PokemonType.Psychic,
            ["tangaberry"] = PokemonType.Bug,
            ["chartiberry"] = PokemonType.Rock,
            ["kasibberry"] = PokemonType.Ghost,
            ["habanberry"] = PokemonType.Dragon,
            ["colburberry"] = PokemonType.Dark,
            ["babiriberry"] = PokemonType.Steel,
            ["chilanberry"] = PokemonType.Normal,
            ["roseliberry"] = PokemonType.Fairy
        };

        static readonly Dictionary<string, WeatherType> weatherRocks = new()
        {
            ["icyrock"] = WeatherType.Hail,
            ["damprock"] = WeatherType.Rain,
            ["heatrock"] = WeatherType.Sun,
            ["smoothrock"] = WeatherType.Sandstorm
        };

        public static IReadOnlyList<string> SupportedIds { get; } =
            displayNames.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        /// <summary>§309. Every supported item by its pretty name, sorted -
        /// what a picker offers. The Damage Calculator's two Item boxes were
        /// built from a hand-written list of seven until §309; this is every
        /// one the engine actually carries (a hundred and five then, a
        /// hundred and forty-seven since §375).</summary>
        public static IReadOnlyList<string> DisplayNamesInOrder { get; } =
            displayNames.Values.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// §309. Items that cannot move a damage roll OR a hits-to-KO count
        /// in a one-on-one calculation, whatever else they do in a battle.
        ///
        /// This is not a list of things the engine cannot simulate - it
        /// simulates every one of them. It is a list of things whose effect
        /// has nowhere to show up in a window that asks "what does this hit
        /// do, and how many of them does it take". A Heat Rock makes the sun
        /// last eight turns instead of five, and the sun's multiplier is the
        /// same either way. A Light Clay does the same for a screen. A Lum
        /// Berry cures a status the window is being TOLD about rather than
        /// watching arrive. A Wide Lens moves accuracy, which this window
        /// does not model, and a Scope Lens moves the crit stage, whose
        /// damage this window computes and does not print.
        ///
        /// The mega stones and Z-Crystals are the large half. A stone does
        /// nothing at all unless its holder has already mega evolved - and a
        /// calculator picks the mega forme as a SPECIES - so the stone is
        /// inert in every row this window can draw. PRO has no Z-Moves.
        ///
        /// They are all still offered, because a picker that quietly omits
        /// the thing someone is holding is worse than one that says so. The
        /// banner says so.
        /// </summary>
        public static bool AffectsDamageOrKo(string? id)
        {
            string key = Normalize(id);

            if (!displayNames.ContainsKey(key))
                return false;

            return !inertInACalculation.Contains(key)
                && !megaStones.Contains(key)
                && !zCrystals.ContainsKey(key);
        }

        /// <summary>
        /// §311. What an item DOES, in the handful of shapes a decision
        /// turns on. The observer's feature encoder needs these: a hundred and
        /// five one-hot columns would be a hundred and five columns that are
        /// zero in almost every training row, and what a network can actually
        /// learn from is "the thing in front of me is Choice-locked" rather
        /// than "the thing in front of me is holding item number 61".
        ///
        /// They live here rather than in the encoder for §309's reason: the
        /// engine owns items. An encoder carrying its own list of which items
        /// boost damage would be a second list to keep in step with this one.
        /// </summary>
        public static bool BoostsDamage(string? id)
        {
            string key = Normalize(id);

            return typeBoosters.ContainsKey(key)
                || gems.ContainsKey(key)
                || key is "lifeorb" or "expertbelt" or "muscleband" or "wiseglasses";
        }

        /// <summary>§311. Half again (or double) on one of its holder's stats -
        /// the Choice three, Assault Vest, Eviolite, Light Ball.</summary>
        public static bool BoostsAStat(string? id) =>
            Normalize(id) is "choiceband" or "choicespecs" or "choicescarf"
                or "assaultvest" or "eviolite" or "lightball";

        /// <summary>§311. A Choice item, which locks its holder into the first
        /// move it picks - the single most important thing to know about an
        /// opposing Pokemon that has already attacked once.</summary>
        public static bool IsChoiceItem(string? id) =>
            Normalize(id) is "choiceband" or "choicespecs" or "choicescarf";

        /// <summary>§311. One of the eighteen berries that halve a qualifying
        /// hit, Chilan included.</summary>
        public static bool IsResistBerry(string? id) =>
            resistBerries.ContainsKey(Normalize(id));

        /// <summary>§311. Which type that berry answers, or null.</summary>
        public static PokemonType? ResistedType(string? id) =>
            resistBerries.TryGetValue(Normalize(id), out PokemonType type) ? type : null;

        /// <summary>§311. Gives its holder HP back at the end of every turn.
        /// A Black Sludge only heals a Poison type, and hurts anything else -
        /// which is why this takes the holder rather than just the id.</summary>
        public static bool HealsEachTurn(PokemonState holder)
        {
            string key = Item(holder);

            if (key == "leftovers")
                return true;

            return key == "blacksludge" && holder.Types.Contains(PokemonType.Poison);
        }

        /// <summary>§311. Spends itself to keep its holder standing: a Focus
        /// Sash on a full bar, or a Sitrus Berry once half of one is gone.
        /// </summary>
        public static bool SavesItsHolderOnce(string? id) =>
            Normalize(id) is "focussash" or "sitrusberry";

        static readonly HashSet<string> inertInACalculation = new()
        {
            "icyrock", "damprock", "heatrock", "smoothrock",   // weather duration
            "lightclay",                                       // screen duration
            "lumberry",                                        // cures a status
            "widelens",                                        // accuracy
            "scopelens",                                       // the crit stage
            // §375: the same reasoning for the eight. A Focus Band is a
            // one-in-ten, a Quick Claw and an Eject Button move turns
            // around, a Shed Shell opens a door, a Razor Fang and a Bright
            // Powder move odds, a Shell Bell heals the wrong side, and a
            // Razor Claw is a Scope Lens.
            "focusband", "shellbell", "shedshell", "quickclaw",
            "razorfang", "razorclaw", "ejectbutton", "brightpowder",
        };

        public static bool IsSupported(string? id) =>
            displayNames.ContainsKey(Normalize(id));

        public static bool IsMegaStone(string? id) =>
            megaStones.Contains(Normalize(id));

        /// <summary>Section 161.</summary>
        public static bool IsZCrystal(string? id) =>
            zCrystals.ContainsKey(Normalize(id));

        /// <summary>Section 161: the crystal's type, or null for anything
        /// that is not a Z-Crystal.</summary>
        public static PokemonType? ZCrystalType(string? id) =>
            zCrystals.TryGetValue(Normalize(id), out PokemonType type) ? type : null;

        /// <summary>§375.</summary>
        public static bool IsGem(string? id) =>
            gems.ContainsKey(Normalize(id));

        /// <summary>§375: the Gem's type, or null for anything that is not
        /// a Gem.</summary>
        public static PokemonType? GemType(string? id) =>
            gems.TryGetValue(Normalize(id), out PokemonType type) ? type : null;

        /// <summary>§375: the type a booster or a plate strengthens, or
        /// null. Both tables are the same table; this just reads it.</summary>
        public static PokemonType? BoostedType(string? id) =>
            typeBoosters.TryGetValue(Normalize(id), out PokemonType type) ? type : null;

        /// <summary>§375. LegalActions: a Shed Shell lets its holder leave
        /// past a trapping move or a trapping ability. Not past Ingrain -
        /// roots are the holder's own doing, and the games leave them.</summary>
        public static bool LetsHolderEscape(PokemonState pokemon) =>
            Item(pokemon) == "shedshell";

        /// <summary>§375. BattleEngine, as the turn's actions are queued: a
        /// Quick Claw's one-in-five to go first in its priority bracket.
        /// Rolled once per action, off the battle's own RNG, so a seeded
        /// battle replays the same.</summary>
        public static bool QuickClawTriggers(BattleState state, PokemonState pokemon) =>
            Item(pokemon) == "quickclaw" && state.Rng.Chance(QuickClawChance);

        /// <summary>§375. ApplySecondaryEffects: the flinch a Razor Fang
        /// lends a damaging move that has none of its own. Serene Grace
        /// doubles it there like any other; Inner Focus refuses it there
        /// like any other.</summary>
        public static double FlinchChance(PokemonState attacker) =>
            Item(attacker) == "razorfang" ? RazorFangFlinchChance : 0.0;

        /// <summary>§375. MoveResolver's accuracy roll, defender side: Bright
        /// Powder takes a tenth off every move aimed at its holder. An item,
        /// so Mold Breaker does not see through it.</summary>
        public static double AccuracyAgainstMultiplier(PokemonState defender) =>
            Item(defender) == "brightpowder" ? BrightPowderAccuracy : 1.0;

        /// <summary>Section 161: items that never leave their holder. A
        /// Z-Crystal is sticky on anyone; a mega stone is sticky on the
        /// Pokemon it belongs to (base or mega'd), and only rides loose on
        /// a mismatched holder. Trick, Fling, Knock Off and Thief all
        /// check here.</summary>
        public static bool IsSticky(PokemonState holder)
        {
            if (holder.HeldItemId == null)
                return false;

            string id = Normalize(holder.HeldItemId);

            if (zCrystals.ContainsKey(id))
                return true;

            return megaStones.Contains(id) &&
                MegaEvolutions.StoneMatches(holder.Species, id);
        }

        public static bool IsBerry(string? id) =>
            Normalize(id).EndsWith("berry");

        /// <summary>The pretty name for a normalized id ("choiceband" ->
        /// "Choice Band"); falls back to the id itself.</summary>
        public static string DisplayName(string? id) =>
            displayNames.TryGetValue(Normalize(id), out string? name) ? name : id ?? string.Empty;

        static string Item(PokemonState pokemon) => Normalize(pokemon.HeldItemId);

        static string Ability(PokemonState pokemon) =>
            Abilities.AbilityFactory.Normalize(pokemon.AbilityId);

        static bool HasChoiceItem(PokemonState pokemon)
        {
            string item = Item(pokemon);
            return item == "choiceband" || item == "choicespecs" || item == "choicescarf";
        }

        /// <summary>LegalActions: a Choice item locks its holder into the
        /// first move it picked; an Assault Vest bars status moves.</summary>
        public static bool AllowsMove(PokemonState pokemon, MoveState move)
        {
            if (pokemon.ChoiceLockedMoveName != null && HasChoiceItem(pokemon) &&
                move.Name != pokemon.ChoiceLockedMoveName)
            {
                return false;
            }

            if (Item(pokemon) == "assaultvest" && move.Category == MoveCategory.Status)
                return false;

            return true;
        }

        /// <summary>StatResolver: the stat-multiplying items, plus
        /// Unburden's doubled Speed after an item is lost.</summary>
        public static double StatMultiplier(BattleState state, PokemonState pokemon, string stat)
        {
            double multiplier = 1.0;

            switch (Item(pokemon))
            {
                case "choiceband" when stat == "Attack": multiplier = 1.5; break;
                case "choicespecs" when stat == "SpAttack": multiplier = 1.5; break;
                case "choicescarf" when stat == "Speed": multiplier = 1.5; break;
                case "assaultvest" when stat == "SpDefense": multiplier = 1.5; break;

                // Eviolite: the engine has no evolution data, so the boss
                // file (or the player) giving it to a not-fully-evolved
                // Pokemon is trusted - see the §159 guide note.
                case "eviolite" when stat == "Defense" || stat == "SpDefense": multiplier = 1.5; break;

                case "lightball" when pokemon.Species.Contains("Pikachu", StringComparison.OrdinalIgnoreCase) &&
                                      (stat == "Attack" || stat == "SpAttack"):
                    multiplier = 2.0; break;
            }

            if (stat == "Speed" && pokemon.LostItem && pokemon.HeldItemId == null &&
                Ability(pokemon) == "unburden")
            {
                multiplier *= 2.0;
            }

            return multiplier;
        }

        /// <summary>MoveResolver's hit loop, attacker side: type boosters,
        /// Life Orb, Expert Belt, Muscle Band, Wise Glasses.</summary>
        public static void ModifyOutgoingDamage(
            BattleState state,
            PokemonState attacker,
            MoveState move,
            double effectiveness,
            ref int hitDamage)
        {
            if (hitDamage <= 0)
                return;

            string item = Item(attacker);

            // §375: a Gem spent on an earlier hit of this same move keeps
            // boosting to the last hit.
            if (attacker.GemBoosting)
            {
                hitDamage = (int)(hitDamage * GemMultiplier);
                return;
            }

            if (gems.TryGetValue(item, out PokemonType gemType) &&
                move.Type == gemType && !move.Typeless)
            {
                string spent = DisplayName(attacker.HeldItemId);

                attacker.GemBoosting = true;
                Consume(state, attacker, $"The {spent} strengthened {move.Name}'s power!");

                hitDamage = (int)(hitDamage * GemMultiplier);
                return;
            }

            if (typeBoosters.TryGetValue(item, out PokemonType boosted) &&
                move.Type == boosted && !move.Typeless)
            {
                hitDamage = (int)(hitDamage * 1.2);
                return;
            }

            switch (item)
            {
                case "lifeorb":
                    hitDamage = (int)(hitDamage * 1.3);
                    break;

                case "expertbelt":
                    if (effectiveness > 1.0)
                        hitDamage = (int)(hitDamage * 1.2);
                    break;

                case "muscleband":
                    if (move.Category == MoveCategory.Physical)
                        hitDamage = (int)(hitDamage * 1.1);
                    break;

                case "wiseglasses":
                    if (move.Category == MoveCategory.Special)
                        hitDamage = (int)(hitDamage * 1.1);
                    break;
            }
        }

        /// <summary>MoveResolver's hit loop, defender side: the resist
        /// berries eat themselves to halve the qualifying hit. Unnerve
        /// keeps every berry uneaten.</summary>
        public static void ModifyIncomingDamage(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            double effectiveness,
            ref int hitDamage)
        {
            if (hitDamage <= 0 || move.Typeless)
                return;

            if (!resistBerries.TryGetValue(Item(defender), out PokemonType resisted) ||
                move.Type != resisted)
            {
                return;
            }

            // Chilan halves any Normal hit; the rest need super-effective.
            if (resisted != PokemonType.Normal && effectiveness <= 1.0)
                return;

            if (Ability(attacker) == "unnerve")
                return;

            string eaten = DisplayName(defender.HeldItemId);
            hitDamage /= 2;
            Consume(state, defender, $"{defender.Species}'s {eaten} weakened the attack!");
        }

        /// <summary>The item check in MoveResolver's lethal-damage clamp: a
        /// Focus Sash holds its holder at 1 HP from full health and is eaten
        /// doing it; §375: a Focus Band does the same from any health, one
        /// time in ten, and keeps itself. True when the holder is saved.</summary>
        public static bool SavesFromLethalHit(BattleState state, PokemonState defender, int hitDamage)
        {
            if (hitDamage < defender.CurrentHP)
                return false;

            switch (Item(defender))
            {
                case "focussash":
                    if (defender.CurrentHP != defender.MaxHP)
                        return false;

                    Consume(state, defender, $"{defender.Species} hung on using its Focus Sash!");
                    return true;

                case "focusband":
                    if (!state.Rng.Chance(FocusBandChance))
                        return false;

                    state.Log.Write($"{defender.Species} hung on using its Focus Band!");
                    return true;
            }

            return false;
        }

        /// <summary>After a hit lands on the defender: Rocky Helmet,
        /// Weakness Policy, the Air Balloon pop, and a Sitrus check.</summary>
        public static void AfterHitTaken(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            double effectiveness,
            int hitDamage)
        {
            if (hitDamage <= 0)
                return;

            switch (Item(defender))
            {
                case "rockyhelmet":
                    if (move.IsContact && !attacker.Fainted && !attacker.HasMagicGuard)
                    {
                        attacker.CurrentHP = Math.Max(0, attacker.CurrentHP - Math.Max(1, attacker.MaxHP / 6));
                        state.Log.Write($"{attacker.Species} was hurt by {defender.Species}'s Rocky Helmet!");

                        if (attacker.Fainted)
                            state.Log.Write($"{attacker.Species} fainted!");
                    }
                    break;

                case "weaknesspolicy":
                    if (effectiveness > 1.0 && !defender.Fainted)
                    {
                        Consume(state, defender, $"{defender.Species}'s Weakness Policy activated!");
                        MoveResolver.ApplyStatChange(state, defender, "Attack", 2);
                        MoveResolver.ApplyStatChange(state, defender, "SpAttack", 2);
                    }
                    break;

                case "airballoon":
                    Consume(state, defender, $"{defender.Species}'s Air Balloon popped!");
                    break;
            }

            TrySitrus(state, defender);
        }

        /// <summary>After the attacker's move fully resolves: the Choice
        /// lock latches, and Life Orb takes its tithe (unless Sheer Force
        /// already traded the secondaries away - the classic pairing).
        /// §375: a Shell Bell gives an eighth of the damage back, and a
        /// Gem's boost ends with the move it was spent on.</summary>
        public static void AfterMoveUsed(
            BattleState state,
            PokemonState attacker,
            MoveState move,
            int damageDealt)
        {
            attacker.GemBoosting = false;

            if (HasChoiceItem(attacker) && attacker.ChoiceLockedMoveName == null &&
                move.Name != "Struggle")
            {
                attacker.ChoiceLockedMoveName = move.Name;
            }

            if (Item(attacker) == "shellbell" && damageDealt > 0 &&
                !attacker.Fainted && attacker.CurrentHP < attacker.MaxHP)
            {
                attacker.CurrentHP = Math.Min(attacker.MaxHP,
                    attacker.CurrentHP + Math.Max(1, damageDealt / 8));
                state.Log.Write($"{attacker.Species} restored a little HP using its Shell Bell!");
            }

            if (Item(attacker) == "lifeorb" && damageDealt > 0 &&
                !attacker.Fainted && !attacker.HasMagicGuard &&
                !(Abilities.AbilityFactory.Normalize(attacker.AbilityId) == "sheerforce" &&
                  (move.SecondaryChance > 0 || move.FlinchChance > 0 || move.StatusChance > 0)))
            {
                attacker.CurrentHP = Math.Max(0, attacker.CurrentHP - Math.Max(1, attacker.MaxHP / 10));
                state.Log.Write($"{attacker.Species} lost some of its HP because of the Life Orb!");

                if (attacker.Fainted)
                    state.Log.Write($"{attacker.Species} fainted!");
            }
        }

        /// <summary>§375. The end of MoveResolver.Resolve, defender side: an
        /// Eject Button sends its holder to the bench once the move that hit
        /// it is over - after the hit's own consequences, before the next
        /// action - and is spent doing it. The replacement is the first
        /// healthy teammate, as Baton Pass and U-turn choose theirs; a
        /// holder with no bench, or one that fainted, keeps the button.</summary>
        public static void AfterMoveTaken(
            BattleState state,
            PokemonState attacker,
            PokemonState defender,
            MoveState move,
            int damageDealt)
        {
            if (Item(defender) != "ejectbutton" || damageDealt <= 0 || defender.Fainted)
                return;

            PlayerState owner = state.GetOwner(defender);
            int slot = owner.SlotOf(defender);

            if (slot < 0)
                return;

            PokemonState? next = owner.GetNextAvailablePokemon();

            if (next == null)
                return;

            Consume(state, defender, $"{defender.Species} is switched out with the Eject Button!");
            SwitchResolver.Resolve(state, owner, next, voluntary: false, slot: slot);
        }

        /// <summary>TryInflictStatus tail: a Lum Berry cures the fresh
        /// status on the spot.</summary>
        public static void OnStatusInflicted(BattleState state, PokemonState pokemon)
        {
            if (Item(pokemon) != "lumberry" || pokemon.Status == StatusCondition.None)
                return;

            var opponent = state.GetOpponents(pokemon).FirstOrDefault();

            if (opponent != null && Ability(opponent) == "unnerve")
                return;

            pokemon.Status = StatusCondition.None;
            pokemon.SleepTurns = 0;
            pokemon.ToxicCounter = 0;

            Consume(state, pokemon, $"{pokemon.Species}'s Lum Berry cured its status!");
        }

        /// <summary>MoveResolver's weather setter: the weather rocks
        /// stretch their holder's weather to eight turns.</summary>
        public static int WeatherDuration(PokemonState setter, WeatherType weather)
        {
            return weatherRocks.TryGetValue(Item(setter), out WeatherType boosted) && boosted == weather
                ? 8
                : 5;
        }

        /// <summary>ScreenEffect: Light Clay stretches Reflect and Light
        /// Screen to eight turns.</summary>
        public static int ScreenDuration(PokemonState setter) =>
            Item(setter) == "lightclay" ? 8 : 5;

        /// <summary>DamageCalculator: Scope Lens sharpens the crit stage;
        /// §375: so does a Razor Claw, by the same one stage.</summary>
        public static int CritStageBonus(PokemonState attacker) =>
            Item(attacker) is "scopelens" or "razorclaw" ? 1 : 0;

        /// <summary>MoveResolver's accuracy roll: Wide Lens loosens it.</summary>
        public static double AccuracyMultiplier(PokemonState attacker) =>
            Item(attacker) == "widelens" ? 1.1 : 1.0;

        /// <summary>BattleEngine's end-of-turn pass: Leftovers, Black
        /// Sludge, the status orbs, and a Sitrus safety check after
        /// residual damage.</summary>
        public static void EndOfTurn(BattleState state)
        {
            foreach (var pokemon in new[] { state.Player1.ActivePokemon, state.Player2.ActivePokemon })
            {
                if (pokemon.Fainted)
                    continue;

                switch (Item(pokemon))
                {
                    case "leftovers":
                        if (pokemon.CurrentHP < pokemon.MaxHP)
                        {
                            pokemon.CurrentHP = Math.Min(pokemon.MaxHP,
                                pokemon.CurrentHP + Math.Max(1, pokemon.MaxHP / 16));
                            state.Log.Write($"{pokemon.Species} restored a little HP using its Leftovers!");
                        }
                        break;

                    case "blacksludge":
                        if (pokemon.Types.Contains(PokemonType.Poison))
                        {
                            if (pokemon.CurrentHP < pokemon.MaxHP)
                            {
                                pokemon.CurrentHP = Math.Min(pokemon.MaxHP,
                                    pokemon.CurrentHP + Math.Max(1, pokemon.MaxHP / 16));
                                state.Log.Write($"{pokemon.Species} restored a little HP using its Black Sludge!");
                            }
                        }
                        else if (!pokemon.HasMagicGuard)
                        {
                            pokemon.CurrentHP = Math.Max(0, pokemon.CurrentHP - Math.Max(1, pokemon.MaxHP / 8));
                            state.Log.Write($"{pokemon.Species} is hurt by its Black Sludge!");

                            if (pokemon.Fainted)
                                state.Log.Write($"{pokemon.Species} fainted!");
                        }
                        break;

                    case "flameorb":
                        if (pokemon.Status == StatusCondition.None)
                            MoveResolver.TryInflictStatus(state, pokemon, StatusCondition.Burn,
                                announceFailure: false, substituteBlocks: false);
                        break;

                    case "toxicorb":
                        if (pokemon.Status == StatusCondition.None)
                            MoveResolver.TryInflictStatus(state, pokemon, StatusCondition.Toxic,
                                announceFailure: false, substituteBlocks: false);
                        break;
                }

                TrySitrus(state, pokemon);
            }
        }

        static void TrySitrus(BattleState state, PokemonState pokemon)
        {
            if (Item(pokemon) != "sitrusberry" || pokemon.Fainted ||
                pokemon.CurrentHP * 2 > pokemon.MaxHP)
            {
                return;
            }

            var opponent = state.GetOpponents(pokemon).FirstOrDefault();

            if (opponent != null && Ability(opponent) == "unnerve")
                return;

            pokemon.CurrentHP = Math.Min(pokemon.MaxHP,
                pokemon.CurrentHP + Math.Max(1, pokemon.MaxHP / 4));

            Consume(state, pokemon, $"{pokemon.Species} restored HP with its Sitrus Berry!");
        }

        /// <summary>An item leaves its holder for good: berries feed Cheek
        /// Pouch an extra third of max HP on the way down.</summary>
        public static void Consume(BattleState state, PokemonState pokemon, string message)
        {
            bool wasBerry = IsBerry(pokemon.HeldItemId);

            pokemon.HeldItemId = null;
            pokemon.LostItem = true;

            state.Log.Write(message);

            if (wasBerry && Ability(pokemon) == "cheekpouch" && !pokemon.Fainted &&
                pokemon.CurrentHP < pokemon.MaxHP)
            {
                pokemon.CurrentHP = Math.Min(pokemon.MaxHP,
                    pokemon.CurrentHP + Math.Max(1, pokemon.MaxHP / 3));
                state.Log.Write($"{pokemon.Species}'s Cheek Pouch restored its HP!");
            }
        }

        /// <summary>Knock Off / Thief style removal - no eating, no Cheek
        /// Pouch, the lock releases if a Choice item leaves.</summary>
        public static void Remove(BattleState state, PokemonState pokemon)
        {
            pokemon.HeldItemId = null;
            pokemon.LostItem = true;
            pokemon.ChoiceLockedMoveName = null;
        }
    }
}