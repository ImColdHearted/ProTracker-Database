using System;
using System.Collections.Generic;
using System.IO;

namespace PokemonSim.Data
{
    /// <summary>
    /// Section 154. Finds the engine's data files wherever the process
    /// runs: the tracker's output copies them under PokemonSimData/ (see
    /// pokemonsim.csproj's TargetPath), the library's own output keeps the
    /// same layout for the dev console and the tests, and a source
    /// checkout's DataFiles/ still works when running next to the sources.
    /// Case-correct names throughout - Linux and macOS care.
    /// </summary>
    public static class SimDataFiles
    {
        public static string Resolve(string fileName)
        {
            foreach (string candidate in Candidates(fileName))
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            // The first candidate is the canonical location - callers get a
            // clear FileNotFoundException from their own read.
            return Path.Combine(AppContext.BaseDirectory, "PokemonSimData", fileName);
        }

        static IEnumerable<string> Candidates(string fileName)
        {
            string baseDir = AppContext.BaseDirectory;

            yield return Path.Combine(baseDir, "PokemonSimData", fileName);
            yield return Path.Combine(baseDir, "DataFiles", fileName);

            // Walking up from bin/Debug/... to a source checkout.
            var dir = new DirectoryInfo(baseDir);

            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                yield return Path.Combine(dir.FullName, "DataFiles", fileName);
                yield return Path.Combine(dir.FullName, "PokemonSim", "DataFiles", fileName);
            }
        }

        public static string MovesPath => Resolve("moves.json");
        public static string PokedexPath => Resolve("pokedex.json");

        /// <summary>Section 178: the imported competitive sets the Battle
        /// Lab builds random opponents from. Optional - the simulator runs
        /// without it, sampling learnsets as it did before.</summary>
        public static string CompetitiveSetsPath => Resolve("CompetitiveSets.json");
    }
}