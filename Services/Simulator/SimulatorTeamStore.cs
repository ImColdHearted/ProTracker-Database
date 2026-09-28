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

        /// <summary>§317. The Pokemon itself, for a slot whose reference
        /// resolves to nothing - which is every slot on a phone, where the
        /// team is built out of the Pokedex and storage does not exist.
        /// Null in a file written before §317, and that is a legal state:
        /// the reader falls back to it only when the lookup fails, so an old
        /// file behaves exactly as it always did. StoredPokemon rather than
        /// ImportedPokemon because ImportedPokemon is public FIELDS, which
        /// System.Text.Json does not serialize without options - and this
        /// type was already the serializable shape of one.</summary>
        public StoredPokemon? Pokemon { get; set; }
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
    /// That is deliberate, and it is still how a slot is READ: every Pokemon
    /// on a desktop team is already in storage - section 198 made every
    /// successful import land there, and From Storage only offers what is
    /// there - so treating the copy as authoritative would let a Pokemon
    /// re-imported with better EVs come back stale. A reference cannot go
    /// stale; it can only go missing.
    ///
    /// §317: it can also go missing FOREVER, which §203 did not have to think
    /// about. §314 gave the phone a builder that makes Pokemon out of the
    /// Pokedex, and a phone has no storage for them to land in - so every
    /// reference missed, every slot was dropped, and a team built on a phone
    /// simply was not there the next morning. So each slot now carries the
    /// Pokemon as well. The lookup still wins; the copy is read only when the
    /// lookup finds nothing.
    ///
    /// And the copy is written for, and only for, a slot storage does not
    /// hold at save time. That is what keeps this from quietly becoming a
    /// second storage: a Pokemon that IS in storage saves no copy, so deleting
    /// it there still drops the slot rather than resurrecting something the
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
