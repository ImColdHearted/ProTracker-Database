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
    /// </summary>
    public static class HeldItems
    {
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
            ["pixieplate"] = PokemonType.Fairy
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

        /// <summary>The Focus Sash check for MoveResolver's lethal-damage
        /// clamp: true (and consumes the sash) when it saves the holder.</summary>
        public static bool SashSaves(BattleState state, PokemonState defender, int hitDamage)
        {
            if (Item(defender) != "focussash" ||
                defender.CurrentHP != defender.MaxHP || hitDamage < defender.CurrentHP)
            {
                return false;
            }

            Consume(state, defender, $"{defender.Species} hung on using its Focus Sash!");
            return true;
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
        /// already traded the secondaries away - the classic pairing).</summary>
        public static void AfterMoveUsed(
            BattleState state,
            PokemonState attacker,
            MoveState move,
            int damageDealt)
        {
            if (HasChoiceItem(attacker) && attacker.ChoiceLockedMoveName == null &&
                move.Name != "Struggle")
            {
                attacker.ChoiceLockedMoveName = move.Name;
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

        /// <summary>DamageCalculator: Scope Lens sharpens the crit stage.</summary>
        public static int CritStageBonus(PokemonState attacker) =>
            Item(attacker) == "scopelens" ? 1 : 0;

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