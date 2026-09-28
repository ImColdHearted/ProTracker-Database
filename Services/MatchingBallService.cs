using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>§398. The community's matching balls for one Pokémon: up to
    /// three for its ordinary colouring, up to three for its shiny.</summary>
    public sealed record MatchingBalls(
        string Name,
        int Dex,
        IReadOnlyList<string> Regular,
        IReadOnlyList<string> Shiny,
        bool ShinyUnavailable,
        string? Note)
    {
        public bool Any => Regular.Count > 0 || Shiny.Count > 0;

        /// <summary>"Poké Ball, Friend Ball, Nest Ball · shiny: Great Ball,
        /// Ultra Ball" - the row tooltip's line.</summary>
        public string Describe()
        {
            string regular = Regular.Count == 0 ? "none listed" : string.Join(", ", Regular);
            string shiny = ShinyUnavailable ? "not available"
                : Shiny.Count == 0 ? "none listed"
                : string.Join(", ", Shiny);

            return $"{regular} · shiny: {shiny}";
        }
    }

    /// <summary>
    /// §398. Which Poké Balls suit a Pokémon - the community's "Legal Matching
    /// Pokéballs 2.1" list (compiled by reddit.com/user/OracleLink, after a
    /// design by reddit.com/user/adamlutz), read from
    /// DataFiles/matching-pokeballs.json with the 27 ball pictures beside it
    /// in DataFiles/MatchingBalls. How the sheet became that file is in the
    /// file's own comment and MIGRATION_GUIDE.md §398.
    ///
    /// Names are matched forgivingly, because the sheet spells forms one way
    /// ("Raticate (Alola)") and the rest of this app another ("Alolan
    /// Raticate", "Raticate-Alola"): a name is folded to its words, the
    /// regional adjectives are folded to the sheet's nouns, and the words
    /// are matched as a set. A bare species name whose only entries carry a
    /// form ("Burmy", "Shaymin", "Giratina") answers with the sheet's first
    /// form for it - the base form, in the sheet's order.
    ///
    /// Loaded once, lazily. A missing or damaged file is an empty list and a
    /// warning - the pages that show balls show none, and nothing else
    /// notices - the same fail-soft rule as every DataFiles-backed catalog
    /// here. The pictures live under DataFiles rather than in the sprite
    /// library on purpose: the library ships as Assets.pak (§8), which has
    /// to be rebuilt by hand whenever a picture is added, while DataFiles are
    /// copied as they are.
    /// </summary>
    public static class MatchingBallService
    {
        private static readonly string DataPath =
            Path.Combine(AppContext.BaseDirectory, "DataFiles", "matching-pokeballs.json");

        private static readonly string SpriteFolder =
            Path.Combine(AppContext.BaseDirectory, "DataFiles", "MatchingBalls");

        private static readonly object Gate = new();

        private static Dictionary<string, MatchingBalls>? byName;
        private static Dictionary<string, string>? spriteFiles;
        private static readonly Dictionary<string, Bitmap?> spriteCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The sheet's form nouns for this app's regional adjectives,
        /// so "Alolan Raticate" finds "Raticate (Alola)".</summary>
        private static readonly Dictionary<string, string> RegionalWords = new(StringComparer.Ordinal)
        {
            ["alolan"] = "alola",
            ["galarian"] = "galar",
            ["hisuian"] = "hisui",
            ["paldean"] = "paldea",
        };

        /// <summary>Whether the list loaded at all - the page can leave its
        /// column out when it did not.</summary>
        public static bool IsAvailable
        {
            get
            {
                EnsureLoaded();
                return byName!.Count > 0;
            }
        }

        /// <summary>The balls listed for a Pokémon, or null when the sheet
        /// has no entry that matches its name.</summary>
        public static MatchingBalls? For(string? pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return null;

            EnsureLoaded();

            return byName!.TryGetValue(Fold(pokemonName), out MatchingBalls? found) ? found : null;
        }

        /// <summary>The picture for a ball named as the sheet names it
        /// ("Poké Ball"); null for a name it does not know or a picture that
        /// could not be read. Decoded once, misses included.</summary>
        public static Bitmap? Sprite(string? ballName)
        {
            if (string.IsNullOrWhiteSpace(ballName))
                return null;

            EnsureLoaded();

            lock (Gate)
            {
                if (spriteCache.TryGetValue(ballName, out Bitmap? cached))
                    return cached;

                Bitmap? bitmap = null;

                try
                {
                    if (spriteFiles!.TryGetValue(Fold(ballName), out string? file))
                    {
                        string path = Path.Combine(SpriteFolder, file);

                        if (File.Exists(path))
                            bitmap = new Bitmap(path);
                        else
                            Log.Warning("Matching balls: the picture for {Ball} is missing at {Path}.", ballName, path);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Matching balls: the picture for {Ball} could not be read.", ballName);
                }

                spriteCache[ballName] = bitmap;
                return bitmap;
            }
        }

        // ------------------------------------------------------------ loading

        private static void EnsureLoaded()
        {
            if (byName is not null)
                return;

            lock (Gate)
            {
                if (byName is not null)
                    return;

                var names = new Dictionary<string, MatchingBalls>(StringComparer.Ordinal);
                var files = new Dictionary<string, string>(StringComparer.Ordinal);

                try
                {
                    if (File.Exists(DataPath))
                    {
                        DataFile? data = JsonSerializer.Deserialize<DataFile>(
                            File.ReadAllText(DataPath),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (data is not null)
                        {
                            foreach (BallFile ball in data.Balls)
                            {
                                if (!string.IsNullOrWhiteSpace(ball.Name) && !string.IsNullOrWhiteSpace(ball.Sprite))
                                    files[Fold(ball.Name)] = ball.Sprite.Trim();
                            }

                            foreach (PokemonFile entry in data.Pokemon)
                                Index(names, entry);
                        }
                    }
                    else
                    {
                        Log.Warning("Matching balls: {Path} is missing - no balls will be shown.", DataPath);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Matching balls: {Path} could not be read - no balls will be shown.", DataPath);
                    names.Clear();
                    files.Clear();
                }

                spriteFiles = files;
                byName = names;
            }
        }

        /// <summary>Files one sheet entry under its folded name - which is
        /// what every spelling of a form folds to, parentheses, hyphens and
        /// adjectives included - and, when nothing plainer has claimed it
        /// yet, under the bare species name, so the sheet's first form
        /// ("Burmy (Plant)", "Shaymin (Land)") stands in for the species.</summary>
        private static void Index(Dictionary<string, MatchingBalls> names, PokemonFile entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                return;

            var balls = new MatchingBalls(
                entry.Name.Trim(),
                entry.Dex,
                (entry.Regular ?? new List<string>()).Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToList(),
                (entry.Shiny ?? new List<string>()).Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToList(),
                entry.ShinyUnavailable,
                string.IsNullOrWhiteSpace(entry.Note) ? null : entry.Note.Trim());

            // The name as written always wins for its own spelling; a
            // duplicate spelling (the sheet has a few) keeps the first.
            names.TryAdd(Fold(balls.Name), balls);

            Match form = Regex.Match(balls.Name, @"^(?<species>[^(]+)\(");

            if (form.Success)
            {
                string species = Fold(form.Groups["species"].Value);

                if (species.Length > 0)
                    names.TryAdd(species, balls);
            }
        }

        /// <summary>Lower-case words, letters and digits only, apostrophes
        /// dropped rather than split on ("Farfetch'd" and "Farfetchd" are
        /// one word), regional adjectives folded to the sheet's nouns, and
        /// the words sorted so their order never matters: "Alolan Raticate",
        /// "Raticate-Alolan", "Raticate-Alola" and "Raticate (Alola)" all
        /// fold to "alola raticate".</summary>
        private static string Fold(string name)
        {
            // §405: the sheet writes Nidoran♀/♂, the library Nidoran F/M -
            // the sign folds to the letter, so both come out "f nidoran".
            string lower = name.ToLowerInvariant().Replace("'", string.Empty).Replace("\u2019", string.Empty)
                .Replace("\u2640", " f").Replace("\u2642", " m");

            IEnumerable<string> words = Regex.Split(lower, "[^a-z0-9é]+")
                .Where(w => w.Length > 0)
                .Select(w => RegionalWords.TryGetValue(w, out string? noun) ? noun : w)
                .OrderBy(w => w, StringComparer.Ordinal);

            return string.Join(" ", words);
        }

        private sealed class DataFile
        {
            public List<BallFile> Balls { get; set; } = new();
            public List<PokemonFile> Pokemon { get; set; } = new();
        }

        private sealed class BallFile
        {
            public string? Name { get; set; }
            public string? Sprite { get; set; }
        }

        private sealed class PokemonFile
        {
            public int Dex { get; set; }
            public string? Name { get; set; }
            public List<string>? Regular { get; set; }
            public List<string>? Shiny { get; set; }
            public bool ShinyUnavailable { get; set; }
            public bool GenderDifferences { get; set; }
            public string? Note { get; set; }
        }
    }
}
