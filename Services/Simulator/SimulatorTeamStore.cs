using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PokemonSim.Simulation;
using Serilog;

namespace Foot_Tracker.Services.Simulator
{
    /// <summary>§203. One slot of the remembered team: which stored Pokemon
    /// it was, and what it was holding. Properties (not fields) so
    /// System.Text.Json round-trips it without options, the same way
    /// StoredPokemon does.</summary>
    public sealed class SavedTeamSlot
    {
        public string? GameId { get; set; }
        public string Fingerprint { get; set; } = "";
        public string? ItemName { get; set; }
    }

    /// <summary>
    /// §203. The team the builder had when the app last closed.
    ///
    /// It stores REFERENCES, not Pokemon: the same GameId-then-Fingerprint
    /// identity SimulatorPokemonStorage matches on, plus the held item,
    /// which is the one thing about a slot that is chosen here rather than
    /// read off a summary card. Everything else is looked back up out of
    /// storage on the way in.
    ///
    /// That is deliberate. Every Pokemon on a team is already in storage -
    /// section 198 made every successful import land there, and From Storage
    /// only offers what is there - so copying them would be a second copy of
    /// the same data, free to drift, and a Pokemon re-imported with better
    /// EVs would come back stale. A reference cannot go stale; it can only
    /// go missing, and a slot whose Pokemon was deleted from storage is
    /// simply dropped with a note rather than resurrecting something the
    /// player threw away.
    /// </summary>
    public static class SimulatorTeamStore
    {
        static readonly object Gate = new();

        static readonly string SaveFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker",
            "Database");

        static readonly string SavePath = Path.Combine(SaveFolder, "simulator-team.json");

        /// <summary>The remembered slots, in team order. Empty when nothing
        /// was saved, when the file will not read, or on the first run - all
        /// of which mean the same thing to the caller: start empty.</summary>
        public static List<SavedTeamSlot> Load()
        {
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(SavePath))
                        return new List<SavedTeamSlot>();

                    string json = File.ReadAllText(SavePath);

                    return JsonSerializer.Deserialize<List<SavedTeamSlot>>(json)
                           ?? new List<SavedTeamSlot>();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Simulator: the remembered team could not be read.");
                    return new List<SavedTeamSlot>();
                }
            }
        }

        public static void Save(IEnumerable<SavedTeamSlot> slots)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(SaveFolder);

                    string json = JsonSerializer.Serialize(
                        slots.ToList(), new JsonSerializerOptions { WriteIndented = true });

                    // §193: written through the temp-and-rename that actually
                    // reaches the disk, so a power cut cannot leave a
                    // zero-filled file where a team used to be.
                    DurableFile.WriteAllText(SavePath, json);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Simulator: the team could not be remembered.");
                }
            }
        }
    }
}
