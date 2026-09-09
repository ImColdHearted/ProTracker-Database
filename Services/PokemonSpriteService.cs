using System.Text.Json;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services
{
    public static class PokemonSpriteService
    {
        private static readonly Dictionary<string, string> spriteLookup =
            new(StringComparer.OrdinalIgnoreCase);

        // Name/OCR-alias -> that species' types, populated alongside spriteLookup
        // below (species entries only - see PokemonLibraryEntry.Types). Backs
        // GetTypes, which ViewModels expose as a small Types list next to a
        // Pokemon's name - TypeIconConverter (Converters/TypeIconConverter.cs)
        // turns each name into an icon at the XAML layer via GetTypeIcon below.
        private static readonly Dictionary<string, List<string>> typeLookup =
            new(StringComparer.OrdinalIgnoreCase);

        // Type name (e.g. "Fire") -> its loaded icon, cached after first use since
        // there are only 18 possible values and every row that shows a Pokemon's
        // name re-requests the same handful of icons.
        private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> typeIconCache =
            new(StringComparer.OrdinalIgnoreCase);

        // Sprite file name -> its loaded bitmap, cached after first use for the
        // same reason as typeIconCache above. MainWindowViewModel.UpdateTrackerDisplay
        // re-resolves every current/previous/target sprite on every HuntTimer_Tick
        // (once a second, for as long as a hunt is running) even though the
        // underlying Pokemon usually hasn't changed since the last tick - without
        // this cache, GetSprite/LoadSprite reopened and re-decoded the same image
        // file from disk every single second, on the UI thread. Keyed by sprite
        // file name rather than Pokemon name since several names/OCR aliases can
        // point at the same underlying file (see spriteLookup above).
        private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> spriteBitmapCache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, PokemonFormEntry> formLookup =
    new(StringComparer.OrdinalIgnoreCase);

        private static readonly List<PokemonLibraryEntry> pokemonEntries = new();

        public static IReadOnlyList<PokemonLibraryEntry> AllPokemon =>
            pokemonEntries;

        public static IReadOnlyList<PokemonFormEntry> AllForms =>
    formEntries;

        private static readonly List<PokemonFormEntry>
    formEntries = new();

        public static IReadOnlyList<PokemonFormEntry>
            GetHuntableRegionalForms()
        {
            return formEntries
                .Where(IsHuntableRegionalForm)
                .GroupBy(
                    f => f.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(f => f.DexNumber)
                .ThenBy(f => f.Name)
                .ToList();
        }

        private static bool IsHuntableRegionalForm(
    PokemonFormEntry form)
        {
            string name = form.Name;

            return
                name.Contains(
                    "Alolan",
                    StringComparison.OrdinalIgnoreCase) ||
                name.Contains(
                    "Alola",
                    StringComparison.OrdinalIgnoreCase) ||

                name.Contains(
                    "Galarian",
                    StringComparison.OrdinalIgnoreCase) ||
                name.Contains(
                    "Galar",
                    StringComparison.OrdinalIgnoreCase) ||

                name.Contains(
                    "Hisuian",
                    StringComparison.OrdinalIgnoreCase) ||
                name.Contains(
                    "Hisui",
                    StringComparison.OrdinalIgnoreCase);
        }

        public static void Load()
        {
            spriteLookup.Clear();
            typeLookup.Clear();
            typeIconCache.Clear();
            spriteBitmapCache.Clear();
            formLookup.Clear();
            pokemonEntries.Clear();
            formEntries.Clear();

            // §224.
            relaxedFormLookup.Clear();
            relaxedSpeciesLookup.Clear();
            relaxedAnchors.Clear();
            relaxedCache.Clear();

            // =========================================================
            // 1. LOAD NORMAL SPECIES
            // =========================================================

            string jsonPath = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Data",
                "Pokemon",
                "pokemon-species.json"
            );

            if (!File.Exists(jsonPath))
            {
                throw new FileNotFoundException(
                    "Pokemon library could not be found.",
                    jsonPath
                );
            }

            string json = File.ReadAllText(jsonPath);

            var entries =
                JsonSerializer.Deserialize<List<PokemonLibraryEntry>>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }
                ) ?? new List<PokemonLibraryEntry>();

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name) ||
                    string.IsNullOrWhiteSpace(entry.Sprite))
                {
                    continue;
                }

                pokemonEntries.Add(entry);

                spriteLookup[entry.Name] =
                    entry.Sprite;

                typeLookup[entry.Name] =
                    entry.Types;

                foreach (string alias in entry.OcrAliases)
                {
                    if (!string.IsNullOrWhiteSpace(alias))
                    {
                        spriteLookup[alias] =
                            entry.Sprite;

                        typeLookup[alias] =
                            entry.Types;
                    }
                }
            }

            // =========================================================
            // 2. LOAD ALTERNATE / REGIONAL FORMS
            // =========================================================

            string formsJsonPath = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Data",
                "Pokemon",
                "pokemon-forms.json"
            );

            if (!File.Exists(formsJsonPath))
                return;

            string formsJson =
                File.ReadAllText(formsJsonPath);

            var forms =
                JsonSerializer.Deserialize<List<PokemonFormEntry>>(
                    formsJson,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }
                ) ?? new List<PokemonFormEntry>();

            foreach (var form in forms)
            {
                if (string.IsNullOrWhiteSpace(form.Name) ||
                    string.IsNullOrWhiteSpace(form.Sprite))
                {
                    continue;
                }

                // Add exactly ONCE.
                formEntries.Add(form);

                // Canonical form name.
                formLookup[form.Name] = form;

                // OCR aliases.
                foreach (string alias in form.OcrAliases)
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        continue;

                    // Prevent "Rattata" from becoming
                    // "Rattata-Alolan", etc.
                    if (alias.Equals(
                            form.SpeciesName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    formLookup[alias] = form;
                }
            }

            // §224.
            BuildRelaxedIndex();
        }

        // =============================================================
        // §224. RELAXED NAME MATCHING
        // =============================================================
        //
        // The simulator's roster (PokemonSim/DataFiles/CompetitiveSets.json)
        // and the sprite library disagree about how to spell a form. The
        // roster puts the modifier FIRST and separates it with a colon -
        // "Mega Heracross", "Deoxys: Attack", "Hisuian Arcanine". The library
        // puts it LAST and separates it with a hyphen - "Heracross-Mega",
        // "Deoxys-Attack", "Arcanine-Hisui". Neither dictionary above has a
        // key in the roster's shape, so 174 of the roster's 565 names missed
        // both, returned before File.Exists was ever reached, and drew an
        // empty pad in the battle scene: every Mega, every Primal, and most
        // of the Alolan/Galarian/Hisuian/Paldean entries.
        //
        // The data was almost always already there. "Toxtricity: Amped" is
        // the plainest case - Amped is the DEFAULT form, so it lives in
        // pokemon-species.json, whose alias list already contains
        // "toxtricity amped". Only the colon stopped it matching.
        //
        // So this is a spelling problem, and it is fixed by spelling rather
        // than by 174 hand-written aliases that would have to be maintained
        // forever. Every key is folded once at Load time - lower case,
        // apostrophes dropped so "Sirfetch'd" and "Sirfetchd" agree, every
        // other mark and separator collapsed to a single space - and a name
        // that misses the exact dictionaries is folded the same way and
        // rebuilt against the folded index:
        //
        //   1. the folded name itself      "Deoxys: Attack" -> deoxys attack
        //   2. the species anchor, longest run first, with the leftover
        //      words moved in behind it    "Mega Heracross" -> heracross mega
        //   3. the regional synonyms       hisuian/paldean/alolan/galarian
        //   4. the words squashed together "Blood Moon"     -> bloodmoon
        //   5. progressively fewer words   "Ice Rider Calyrex" -> calyrex ice
        //   6. the unique form the key prefixes, when there is exactly one
        //      ("Paldean Tauros: Combat" -> Tauros-Paldea-Combat-Breed)
        //   7. the bare species, so a name this cannot place draws the base
        //      Pokemon rather than nothing
        //
        // Two names the shape of the string cannot reach are named below
        // rather than guessed at.
        //
        // This runs ONLY after the exact dictionaries miss, so every name
        // that resolves today still resolves to the identical file - "Type:
        // Null" keeps its own entry instead of being split at the colon. A
        // folded key two sources disagree about is dropped from the index
        // rather than resolved by whichever was read first: "Nidoran-Female"
        // and "Nidoran-Male" both fold to "nidoran", so a bare "Nidoran"
        // still resolves to nothing, exactly as it does today.
        //
        // Sprites only. Types are deliberately NOT relaxed - a Mega's types
        // are not always its species' types (Mega Charizard X is Fire/Dragon
        // where Charizard is Fire/Flying), so falling back there would print
        // a confident wrong answer instead of a blank one.

        private static readonly Dictionary<string, string> relaxedFormLookup =
            new(StringComparer.Ordinal);

        private static readonly Dictionary<string, string> relaxedSpeciesLookup =
            new(StringComparer.Ordinal);

        // Folded canonical species names only - the anchors step 2 searches
        // for, and the last-resort answer of step 7.
        private static readonly Dictionary<string, string> relaxedAnchors =
            new(StringComparer.Ordinal);

        // Longest anchor in words, measured rather than assumed: "Tapu Koko"
        // and "Iron Hands" are two, so a one-word scan would anchor "Iron
        // Hands" on nothing and a hard-coded 2 would break the day the
        // library gains a three-word species.
        private static int relaxedLongestAnchor = 1;

        // Folded name -> the file it resolved to, or null for a miss.
        // MainWindowViewModel re-resolves every visible sprite once a second
        // while a hunt runs, and the caches below key on the RESOLVED file,
        // so without this the whole walk would be repeated every tick for
        // every name that needs it.
        private static readonly Dictionary<string, string?> relaxedCache =
            new(StringComparer.Ordinal);

        private static readonly object relaxedGate = new();

        private static readonly Dictionary<string, string> RegionSynonyms =
            new(StringComparer.Ordinal)
            {
                ["hisuian"] = "hisui",
                ["paldean"] = "paldea",
                ["alolan"] = "alola",
                ["galarian"] = "galar",
            };

        /// <summary>§224. The two roster names no amount of rearranging
        /// reaches, mapped by hand to the folded library key. "Zygarde 100%"
        /// is Zygarde-Complete, which shares no word with "100"; "Galarian
        /// Darmanitan" prefixes both Darmanitan-Galarian-Standard and
        /// Darmanitan-Galarian-Zen, so step 6 finds two answers and declines
        /// to choose - Standard is the one a battle starts in.</summary>
        private static readonly Dictionary<string, string> RelaxedNameExceptions =
            new(StringComparer.Ordinal)
            {
                ["galarian darmanitan"] = "darmanitan galarian standard",
                ["zygarde 100"] = "zygarde complete",
            };

        /// <summary>§224. Lower case, apostrophes removed, every other
        /// non-alphanumeric run collapsed to one space, ends trimmed. The
        /// apostrophe is dropped rather than spaced so that "Sirfetch'd",
        /// "Sirfetch’d" and "sirfetchd" all fold together.</summary>
        private static string FoldName(string value)
        {
            var builder = new System.Text.StringBuilder(value.Length);
            bool pendingSpace = false;

            foreach (char raw in value)
            {
                if (raw == '\'' || raw == '\u2019')
                    continue;

                if (char.IsLetterOrDigit(raw))
                {
                    if (pendingSpace && builder.Length > 0)
                        builder.Append(' ');

                    pendingSpace = false;
                    builder.Append(char.ToLowerInvariant(raw));
                }
                else
                {
                    pendingSpace = true;
                }
            }

            return builder.ToString();
        }

        /// <summary>§224. Folds one source of names into an index, dropping
        /// any folded key whose sources disagree about the file - see the
        /// Nidoran note above.</summary>
        private static void FoldInto(
            Dictionary<string, string> index,
            string name,
            string spriteFile,
            HashSet<string> ambiguous)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(spriteFile))
            {
                return;
            }

            string folded = FoldName(name);

            if (folded.Length == 0)
                return;

            if (index.TryGetValue(folded, out string? existing))
            {
                if (!string.Equals(existing, spriteFile, StringComparison.Ordinal))
                    ambiguous.Add(folded);

                return;
            }

            index[folded] = spriteFile;
        }

        private static void DropAmbiguous(
            Dictionary<string, string> index,
            HashSet<string> ambiguous)
        {
            foreach (string key in ambiguous)
                index.Remove(key);
        }

        /// <summary>§224. Built at the end of Load from the same entries the
        /// exact dictionaries were built from, and following the same rules -
        /// in particular a form alias equal to its own species name is
        /// skipped here too, so "Rattata" does not fold into
        /// "Rattata-Alolan".</summary>
        private static void BuildRelaxedIndex()
        {
            var formAmbiguous = new HashSet<string>(StringComparer.Ordinal);
            var speciesAmbiguous = new HashSet<string>(StringComparer.Ordinal);
            var anchorAmbiguous = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in pokemonEntries)
            {
                FoldInto(relaxedAnchors, entry.Name, entry.Sprite, anchorAmbiguous);
                FoldInto(relaxedSpeciesLookup, entry.Name, entry.Sprite, speciesAmbiguous);

                foreach (string alias in entry.OcrAliases)
                    FoldInto(relaxedSpeciesLookup, alias, entry.Sprite, speciesAmbiguous);
            }

            foreach (var form in formEntries)
            {
                FoldInto(relaxedFormLookup, form.Name, form.Sprite, formAmbiguous);

                foreach (string alias in form.OcrAliases)
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        continue;

                    if (alias.Equals(
                            form.SpeciesName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    FoldInto(relaxedFormLookup, alias, form.Sprite, formAmbiguous);
                }
            }

            DropAmbiguous(relaxedFormLookup, formAmbiguous);
            DropAmbiguous(relaxedSpeciesLookup, speciesAmbiguous);
            DropAmbiguous(relaxedAnchors, anchorAmbiguous);

            relaxedLongestAnchor = 1;

            foreach (string anchor in relaxedAnchors.Keys)
            {
                int words = 1;

                foreach (char c in anchor)
                {
                    if (c == ' ')
                        words++;
                }

                if (words > relaxedLongestAnchor)
                    relaxedLongestAnchor = words;
            }
        }

        private static string? LookupRelaxed(string key) =>
            relaxedFormLookup.TryGetValue(key, out string? form) ? form
            : relaxedSpeciesLookup.TryGetValue(key, out string? species) ? species
            : null;

        /// <summary>§224. Step 6: the file of the one form this key is a
        /// prefix of, or null when it prefixes none or several.</summary>
        private static string? UniqueFormPrefix(string key)
        {
            string prefix = key + " ";
            string? found = null;

            foreach (var pair in relaxedFormLookup)
            {
                if (!string.Equals(pair.Key, key, StringComparison.Ordinal) &&
                    !pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (found is null)
                    found = pair.Value;
                else if (!string.Equals(found, pair.Value, StringComparison.Ordinal))
                    return null;
            }

            return found;
        }

        private static string ComposeRelaxed(
            string anchor,
            List<string> modifiers,
            int take)
        {
            if (take <= 0)
                return anchor;

            var builder = new System.Text.StringBuilder(anchor);

            for (int i = 0; i < take && i < modifiers.Count; i++)
            {
                builder.Append(' ');
                builder.Append(modifiers[i]);
            }

            return builder.ToString();
        }

        private static string? ResolveRelaxed(string folded)
        {
            if (RelaxedNameExceptions.TryGetValue(folded, out string? redirect))
                folded = redirect;

            string? direct = LookupRelaxed(folded);

            if (direct is not null)
                return direct;

            string[] words = folded.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

            int start = -1;
            int length = 0;

            for (int n = Math.Min(relaxedLongestAnchor, words.Length);
                 n >= 1 && start < 0;
                 n--)
            {
                for (int i = 0; i + n <= words.Length; i++)
                {
                    if (relaxedAnchors.ContainsKey(string.Join(' ', words, i, n)))
                    {
                        start = i;
                        length = n;
                        break;
                    }
                }
            }

            if (start < 0)
                return null;

            string anchor = string.Join(' ', words, start, length);

            var modifiers = new List<string>(words.Length - length);

            for (int i = 0; i < words.Length; i++)
            {
                if (i < start || i >= start + length)
                    modifiers.Add(words[i]);
            }

            var variants = new List<List<string>> { modifiers };

            var swapped = new List<string>(modifiers.Count);
            bool anySwapped = false;

            foreach (string modifier in modifiers)
            {
                if (RegionSynonyms.TryGetValue(modifier, out string? alternative))
                {
                    swapped.Add(alternative);
                    anySwapped = true;
                }
                else
                {
                    swapped.Add(modifier);
                }
            }

            if (anySwapped)
                variants.Add(swapped);

            if (modifiers.Count > 1)
                variants.Add(new List<string> { string.Concat(modifiers) });

            for (int cut = modifiers.Count; cut >= 1; cut--)
            {
                foreach (var variant in variants)
                {
                    string? hit = LookupRelaxed(
                        ComposeRelaxed(anchor, variant, Math.Min(cut, variant.Count)));

                    if (hit is not null)
                        return hit;
                }

                foreach (var variant in variants)
                {
                    string? hit = UniqueFormPrefix(
                        ComposeRelaxed(anchor, variant, Math.Min(cut, variant.Count)));

                    if (hit is not null)
                        return hit;
                }
            }

            return relaxedAnchors[anchor];
        }

        /// <summary>§224. The library file for a name the exact dictionaries
        /// could not place, or null when the shape of the name says nothing
        /// the library knows. Callers reach this through the opt-in on
        /// TryGetSpritePath, or automatically through the display helpers -
        /// never through the OCR path, which is left strict on purpose.</summary>
        private static string? TryRelaxedSpriteFile(string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return null;

            string folded = FoldName(pokemonName);

            if (folded.Length == 0)
                return null;

            lock (relaxedGate)
            {
                if (relaxedCache.TryGetValue(folded, out string? memo))
                    return memo;

                string? resolved = ResolveRelaxed(folded);

                relaxedCache[folded] = resolved;

                return resolved;
            }
        }

        public static IReadOnlyList<PokemonFormEntry>
            GetFormsForSpecies(string speciesName)
        {
            if (string.IsNullOrWhiteSpace(speciesName))
                return Array.Empty<PokemonFormEntry>();

            return formEntries
                .Where(f =>
                    f.SpeciesName.Equals(
                        speciesName,
                        StringComparison.OrdinalIgnoreCase))

                // Regional Pokémon are their own hunt targets.
                .Where(f =>
                    !IsHuntableRegionalForm(f))

                .GroupBy(
                    f => f.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(f => f.Name)
                .ToList();
        }

        public static Avalonia.Media.Imaging.Bitmap? GetSprite(string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return null;

            if (!spriteLookup.TryGetValue(
                    pokemonName.Trim(),
                    out string? spriteFile))
            {
                return null;
            }

            return LoadSprite(spriteFile);
        }

        /// <summary>§200. The sprite for an exact national-dex id, which is
        /// how the library names its files - 25.png, and 10000-and-up for
        /// the regional and alternate forms. Null for 0 or below, and for an
        /// id the library has no file for.
        ///
        /// This is the unambiguous way to ask, and the reason §200 exists: a
        /// NAME cannot tell a Hisuian Typhlosion from a Johto one, so a
        /// roster that asked by name always got the Johto one. The Boss
        /// Database's cards have resolved their pictures this way since they
        /// were written (see BossDetailViewModel); the battle had not.
        /// Misses are cached by LoadSprite like every other sprite.</summary>
        public static Avalonia.Media.Imaging.Bitmap? GetSpriteByDexNumber(int dexNumber) =>
            dexNumber <= 0 ? null : LoadSprite($"{dexNumber}.png");

        /// <summary>§138. The on-disk path of the sprite GetEncounterSprite
        /// would show for this name - a form's own file when the name is a
        /// form, the species file otherwise - or false when there is none.
        /// CounterpartMatcher reads the pixels itself (SkiaSharp, not an
        /// Avalonia bitmap), so it needs the file rather than the image.</summary>
        /// <param name="allowRelaxedMatch">§224. Opt in to the relaxed
        /// spelling walk when the exact dictionaries miss. Off by default:
        /// CounterpartMatcher decides which two screenshots are a pair from
        /// what it read off the screen, and a loose match there would turn a
        /// bad OCR read into a confident wrong pairing rather than the clean
        /// miss it produces today. The simulator opts in.</param>
        public static bool TryGetSpritePath(
            string pokemonName,
            out string? path,
            bool allowRelaxedMatch = false)
        {
            path = null;

            if (string.IsNullOrWhiteSpace(pokemonName))
                return false;

            string key = pokemonName.Trim();

            string? spriteFile =
                formLookup.TryGetValue(key, out var form) ? form.Sprite
                : spriteLookup.TryGetValue(key, out string? file) ? file
                : allowRelaxedMatch ? TryRelaxedSpriteFile(key)
                : null;

            if (string.IsNullOrWhiteSpace(spriteFile))
                return false;

            string candidate = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Sprites",
                spriteFile);

            if (!File.Exists(candidate))
                return false;

            path = candidate;
            return true;
        }

        public static Avalonia.Media.Imaging.Bitmap? GetEncounterSprite(string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return null;

            string key = pokemonName.Trim();

            if (formLookup.TryGetValue(key, out var form))
            {
                return LoadSprite(form.Sprite);
            }

            var sprite = GetSprite(key);

            if (sprite is not null)
                return sprite;

            // §224.
            string? relaxed = TryRelaxedSpriteFile(key);

            return relaxed is null ? null : LoadSprite(relaxed);
        }

        /// <summary>§139. Where the shiny sprites live, under Assets/Sprites:
        /// the same file names as the ordinary sprites (PokeAPI's pokemon
        /// ids - 278.png, 10001.png), which is exactly how the PokeAPI
        /// sprites repository lays out sprites/pokemon/shiny/ beside
        /// sprites/pokemon/. Dropping that folder in as Assets/Sprites/shiny
        /// is all it takes; the csproj's Assets glob ships it.</summary>
        public const string ShinySpriteFolder = "shiny";

        /// <summary>§139. The shiny sprite for the encounter cards, or the
        /// ordinary sprite when the library has no shiny for this name (or
        /// no shiny folder at all) - the card's label still says Shiny, so
        /// nothing is lost but the recolour. Misses are cached by LoadSprite
        /// like every other sprite, so a library without shinies costs one
        /// File.Exists per species, once.</summary>
        public static Avalonia.Media.Imaging.Bitmap? GetShinyEncounterSprite(string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return null;

            string key = pokemonName.Trim();

            string? spriteFile =
                formLookup.TryGetValue(key, out var form) ? form.Sprite
                : spriteLookup.TryGetValue(key, out string? file) ? file
                : TryRelaxedSpriteFile(key); // §224.

            if (string.IsNullOrWhiteSpace(spriteFile))
                return null;

            return LoadSprite(Path.Combine(ShinySpriteFolder, spriteFile))
                ?? LoadSprite(spriteFile);
        }

        private static Avalonia.Media.Imaging.Bitmap? LoadSprite(string spriteFile)
        {
            if (string.IsNullOrWhiteSpace(spriteFile))
                return null;

            if (spriteBitmapCache.TryGetValue(spriteFile, out Avalonia.Media.Imaging.Bitmap? cached))
            {
                return cached;
            }

            string path = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Sprites",
                spriteFile
            );

            Avalonia.Media.Imaging.Bitmap? bitmap = null;

            if (File.Exists(path))
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read
                );

                // System.Drawing.Image.FromStream + new Bitmap(source) -> Avalonia.Media.Imaging.Bitmap
                bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
            }

            // Cache even a miss (null) under this file name, same as
            // GetTypeIcon does above - a sprite file that doesn't exist on disk
            // isn't going to start existing mid-session, so there's no point
            // re-touching the filesystem for it on every future call.
            spriteBitmapCache[spriteFile] = bitmap;

            return bitmap;
        }

        public static IReadOnlyList<string> GetTypes(string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return Array.Empty<string>();

            return typeLookup.TryGetValue(
                    pokemonName.Trim(),
                    out List<string>? types)
                ? types
                : Array.Empty<string>();
        }

        public static Avalonia.Media.Imaging.Bitmap? GetTypeIcon(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            string key = typeName.Trim();

            if (typeIconCache.TryGetValue(
                    key,
                    out Avalonia.Media.Imaging.Bitmap? cached))
            {
                return cached;
            }

            string iconPath = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Typings",
                $"{key.ToLowerInvariant()}.png"
            );

            Avalonia.Media.Imaging.Bitmap? icon = File.Exists(iconPath)
                ? new Avalonia.Media.Imaging.Bitmap(iconPath)
                : null;

            // §186: say so, once per type, when the file is not there.
            //
            // This returned null silently for as long as the icons were
            // missing from the build, so every type-icon row in the app
            // rendered nothing and not one line of the log mentioned it -
            // the failure was invisible from the inside and only findable
            // by noticing the pictures had gone. The cache above means this
            // costs one line per type per session at most.
            if (icon == null)
            {
                Serilog.Log.Warning(
                    "Type icon missing: {Path}. Type icons will not render until " +
                    "SharedPokemonLibrary/Assets/Typings is present in the build output.",
                    iconPath);
            }

            typeIconCache[key] = icon;

            return icon;
        }

        public static string ResolveEncounterName(
            string detectedName)
        {
            if (string.IsNullOrWhiteSpace(detectedName))
                return string.Empty;

            string key = detectedName.Trim();

            // =========================================================
            // 1. EXACT NORMAL SPECIES MATCH ALWAYS WINS
            // =========================================================
            //
            // If OCR detected "Farfetch'd", "Rattata", "Meowth", etc.,
            // do not allow a regional/form alias to replace it.
            //
            var normalSpecies =
                pokemonEntries.FirstOrDefault(
                    p => p.Name.Equals(
                        key,
                        StringComparison.OrdinalIgnoreCase));

            if (normalSpecies != null)
            {
                return normalSpecies.Name;
            }

            // =========================================================
            // 2. CHECK ALTERNATE / REGIONAL FORM
            // =========================================================

            if (formLookup.TryGetValue(
                    key,
                    out var form))
            {
                return form.Name;
            }

            // =========================================================
            // 3. NOTHING SPECIAL FOUND
            // =========================================================

            return key;
        }
    }
}


    public class PokemonLibraryEntry
    {
        public int PokemonId { get; set; }

        public int DexNumber { get; set; }

        public string Name { get; set; } = string.Empty;

        public string SpeciesName { get; set; } = string.Empty;

        // Maps straight onto pokemon-species.json's "types" array (1-2 entries,
        // e.g. ["Grass", "Poison"]) added when every species got its typing
        // filled in - see MIGRATION_GUIDE.md. PropertyNameCaseInsensitive
        // (already used below) matches "types" -> Types with no extra
        // attribute needed, same as every other property here. Only species
        // entries carry this - pokemon-forms.json wasn't part of that pass, so
        // regional/alternate forms resolve to an empty list via GetTypes.
        public List<string> Types { get; set; } = new();

        public string Identifier { get; set; } = string.Empty;

        public string? FormIdentifier { get; set; }

        public bool IsDefaultForm { get; set; }

        public string Sprite { get; set; } = string.Empty;

        public List<string> OcrAliases { get; set; } = new();
    }