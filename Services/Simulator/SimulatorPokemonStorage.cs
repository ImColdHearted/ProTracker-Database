using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PokemonSim.Simulation;
using Serilog;

namespace Foot_Tracker.Services.Simulator
{
    /// <summary>One Pokemon the player imported from their game, as the
    /// storage file keeps it. Properties (not fields) so System.Text.Json
    /// round-trips it without options.</summary>
    public sealed class StoredPokemon
    {
        public string SpeciesName { get; set; } = "";
        public int Level { get; set; } = 100;
        public string NatureName { get; set; } = "Hardy";
        public string? AbilityName { get; set; }
        public List<string> MoveNames { get; set; } = new();

        /// <summary>HP, Attack, Defense, SpAttack, SpDefense, Speed.</summary>
        public int[] Ivs { get; set; } = { 31, 31, 31, 31, 31, 31 };
        public int[] Evs { get; set; } = new int[6];

        public string? GameId { get; set; }

        /// <summary>§164: the card's golden S badge.</summary>
        public bool IsShiny { get; set; }

        public DateTime AddedUtc { get; set; }

        public string Fingerprint =>
            $"{SpeciesName}|{Level}|{NatureName}|{string.Join(",", Ivs)}|{string.Join(",", Evs)}";

        public static StoredPokemon From(ImportedPokemon imported) => new()
        {
            SpeciesName = imported.SpeciesName,
            Level = imported.Level,
            NatureName = imported.NatureName,
            AbilityName = imported.AbilityName,
            MoveNames = imported.MoveNames.ToList(),
            Ivs = imported.Ivs.ToArray(),
            Evs = imported.Evs.ToArray(),
            GameId = imported.GameId,
            IsShiny = imported.IsShiny,
            AddedUtc = DateTime.UtcNow
        };

        public ImportedPokemon ToImported() => new()
        {
            SpeciesName = SpeciesName,
            Level = Level,
            NatureName = NatureName,
            AbilityName = AbilityName,
            MoveNames = MoveNames.ToList(),
            Ivs = Ivs.Length == 6 ? Ivs.ToArray() : new[] { 31, 31, 31, 31, 31, 31 },
            Evs = Evs.Length == 6 ? Evs.ToArray() : new int[6],
            GameId = GameId,
            IsShiny = IsShiny
        };
    }

    /// <summary>
    /// §162. The Pokemon storage behind the team builder: every Pokemon
    /// imported from a screenshot lands here automatically, and the "From
    /// Storage" button re-offers them so a team rebuild never needs the
    /// game re-scanned. One JSON file in the same LocalApplicationData
    /// folder every other local save uses; a damaged file logs and starts
    /// empty rather than breaking the Simulator. Entries dedupe on the
    /// card's ID number when one was read, else on the exact build
    /// fingerprint - re-importing the same Pokemon updates its entry
    /// instead of stacking copies.
    /// </summary>
    public static class SimulatorPokemonStorage
    {
        static readonly object Gate = new();

        static readonly string SaveFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker",
            "Database");

        static readonly string SavePath = Path.Combine(SaveFolder, "simulator-pokemon.json");

        static List<StoredPokemon>? entries;

        public static IReadOnlyList<StoredPokemon> All()
        {
            lock (Gate)
            {
                EnsureLoaded();
                return entries!.ToList();
            }
        }

        /// <summary>Adds or refreshes one imported Pokemon; returns the
        /// stored entry and whether it replaced an older one.</summary>
        public static (StoredPokemon Entry, bool Replaced) Upsert(ImportedPokemon imported)
        {
            lock (Gate)
            {
                EnsureLoaded();

                StoredPokemon entry = StoredPokemon.From(imported);

                int existing = entries!.FindIndex(e =>
                    (entry.GameId != null && e.GameId == entry.GameId) ||
                    e.Fingerprint == entry.Fingerprint);

                bool replaced = existing >= 0;

                if (replaced)
                    entries[existing] = entry;
                else
                    entries.Add(entry);

                Save();

                return (entry, replaced);
            }
        }

        public static void Remove(StoredPokemon entry)
        {
            lock (Gate)
            {
                EnsureLoaded();

                entries!.RemoveAll(e =>
                    ReferenceEquals(e, entry) ||
                    (entry.GameId != null && e.GameId == entry.GameId) ||
                    e.Fingerprint == entry.Fingerprint);

                Save();
            }
        }

        static void EnsureLoaded()
        {
            if (entries != null)
                return;

            entries = new List<StoredPokemon>();

            try
            {
                if (!File.Exists(SavePath))
                    return;

                List<StoredPokemon>? saved = JsonSerializer.Deserialize<List<StoredPokemon>>(
                    File.ReadAllText(SavePath));

                if (saved != null)
                    entries = saved;
            }
            catch (Exception ex)
            {
                // Same "damaged file starts empty instead of breaking the
                // feature" stance as every other local save in this app.
                Log.Warning(ex, "Simulator storage: could not read {Path} - starting empty.", SavePath);
            }
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(SaveFolder);

                File.WriteAllText(SavePath, JsonSerializer.Serialize(
                    entries, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Simulator storage: could not save {Path}.", SavePath);
            }
        }
    }
}
