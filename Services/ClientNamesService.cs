using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Optional per-client display names ("Main Account", "Alt Farming", etc.)
    /// so a player running more than one PRO client at once can actually tell
    /// them apart, instead of just "Client 1" / "Client 2" - edited in
    /// ClientSelectorWindow, and shown in the window title via
    /// MainWindowViewModel.UpdateTrackerDisplay (and in the "already being
    /// tracked" lock messages - see AssignTrackerClient).
    ///
    /// Deliberately NOT per-client the way BossCooldownService/
    /// PvpOpponentService are (see their GetSavePath remarks) - this file IS
    /// the lookup table covering every client slot at once, so unlike those
    /// it can't be keyed off "whichever client is currently active."
    ///
    /// A name here is purely cosmetic: it has no effect on which PRO window a
    /// client number actually points at (see ClientWindowInfo/
    /// IWindowCaptureService for that) or on any saved hunt data - renaming
    /// "Client 2" just changes the label shown for it, nothing else moves.
    /// </summary>
    public static class ClientNamesService
    {
        private static readonly string SaveFolder =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProTracker",
                "Database"
            );

        private static readonly string SavePath =
            Path.Combine(SaveFolder, "client-names.json");

        private static Dictionary<int, string> names = new();
        private static bool loaded;

        private static void EnsureLoaded()
        {
            if (loaded)
                return;

            loaded = true;

            if (!File.Exists(SavePath))
                return;

            try
            {
                string json = File.ReadAllText(SavePath);
                Dictionary<int, string>? saved = JsonSerializer.Deserialize<Dictionary<int, string>>(json);

                if (saved != null)
                    names = saved;
            }
            catch
            {
                // Keep whatever's already in `names` (empty, at this point) if
                // the file is damaged - same as every other local save this
                // app reads.
            }
        }

        /// <summary>The custom name for this client number, or null if one was never set (or was cleared).</summary>
        public static string? GetName(int clientNumber)
        {
            EnsureLoaded();

            return names.TryGetValue(clientNumber, out string? name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : null;
        }

        /// <summary>What to actually show for this client number - the custom name if one is set, else "Client N".</summary>
        public static string GetDisplayName(int clientNumber) =>
            GetName(clientNumber) ?? $"Client {clientNumber}";

        /// <summary>Sets the custom name for this client number, or clears it back to the "Client N" default (null/blank).</summary>
        public static void SetName(int clientNumber, string? name)
        {
            EnsureLoaded();

            if (string.IsNullOrWhiteSpace(name))
                names.Remove(clientNumber);
            else
                names[clientNumber] = name.Trim();

            Save();
        }

        private static void Save()
        {
            Directory.CreateDirectory(SaveFolder);

            string json = JsonSerializer.Serialize(
                names,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(SavePath, json);
        }
    }
}
