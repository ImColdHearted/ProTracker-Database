using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Pokédex descriptions for the boss detail window's Description panel.
    /// SharedPokemonLibrary/Data/pokemon-descriptions.json maps every species
    /// (PokeAPI identifiers, e.g. "mr-mime") to its flavor text from the
    /// NEWEST mainline game with an English entry - see MIGRATION_GUIDE.md
    /// §93 for how the file was built. Same lazy, never-throwing shape as
    /// MoveLookupService/CalculatorDataService: a missing or damaged file
    /// just means every lookup returns null, and the panel shows its own
    /// fallback line instead of anything breaking.
    ///
    /// Find is form-aware, because descriptions are species-level: boss-file
    /// names like "Alolan Muk", "Malamar-Mega" or "Necrozma-Dusk-Mane"
    /// resolve by stripping regional/mega prefixes and dash-suffixes down to
    /// the base species when the full name has no entry of its own. Names
    /// that hit directly (Porygon-Z, Ho-oh, Mr. Mime, Jangmo-o) match BEFORE
    /// any stripping, so real dashed species never lose their tails.
    /// </summary>
    public static class PokemonDescriptionService
    {
        private static readonly Dictionary<string, string> descriptionsByNormalizedName = new();

        private static bool loaded;

        private static readonly string[] FormPrefixes =
            { "Alolan ", "Galarian ", "Hisuian ", "Paldean ", "Mega " };

        public static string? Find(string? pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return null;

            EnsureLoaded();

            if (descriptionsByNormalizedName.Count == 0)
                return null;

            // 1) The full name exactly as the boss file wrote it.
            string? found = Lookup(pokemonName);
            if (found is not null)
                return found;

            // 2) Without a ": Forme" tail (e.g. "Palkia: Origin").
            string head = pokemonName.Split(':')[0].Trim();
            found = Lookup(head);
            if (found is not null)
                return found;

            // 3) Without a regional/mega prefix ("Alolan Muk" -> "Muk").
            found = LookupWithoutPrefix(head);
            if (found is not null)
                return found;

            // 4) Dropping dash-suffixes one at a time ("Necrozma-Dusk-Mane" ->
            //    "Necrozma-Dusk" -> "Necrozma"), re-trying prefixes each step.
            string work = head;
            while (work.Contains('-'))
            {
                work = work[..work.LastIndexOf('-')];
                found = Lookup(work) ?? LookupWithoutPrefix(work);
                if (found is not null)
                    return found;
            }

            return null;
        }

        private static string? LookupWithoutPrefix(string name)
        {
            foreach (string prefix in FormPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    name.Length > prefix.Length)
                {
                    string? found = Lookup(name[prefix.Length..]);
                    if (found is not null)
                        return found;
                }
            }

            return null;
        }

        private static string? Lookup(string name)
        {
            return descriptionsByNormalizedName.TryGetValue(Normalize(name), out string? text)
                ? text
                : null;
        }

        /// <summary>Lowercase letters/digits only, accents folded - "Mr. Mime",
        /// "mr-mime" and "MR MIME" all become "mrmime", "Flabébé" becomes
        /// "flabebe".</summary>
        private static string Normalize(string name)
        {
            string decomposed = name.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);

            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;

                if (char.IsLetterOrDigit(c))
                    builder.Append(char.ToLowerInvariant(c));
            }

            return builder.ToString();
        }

        private static void EnsureLoaded()
        {
            if (loaded)
                return;

            loaded = true;

            try
            {
                string path = Path.Combine(
                    AppContext.BaseDirectory,
                    "SharedPokemonLibrary",
                    "Data",
                    "pokemon-descriptions.json"
                );

                if (!File.Exists(path))
                    return;

                Dictionary<string, string>? data =
                    JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));

                if (data == null)
                    return;

                foreach (KeyValuePair<string, string> entry in data)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
                        descriptionsByNormalizedName[Normalize(entry.Key)] = entry.Value;
                }
            }
            catch
            {
                // See the class remarks - no descriptions is a display
                // downgrade, never an error.
                descriptionsByNormalizedName.Clear();
            }
        }
    }
}
