using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// §221. Notices the app gives once - on the first run that reaches
    /// them - and never again.
    ///
    /// Deliberately NOT part of UiPreferencesService, which is keyed per
    /// client (ui-preferences-client{N}.json). A note that explained itself
    /// once per client profile would greet a four-client hunter four times,
    /// which is not what "the first time" means to the person reading it.
    /// This file is one per machine.
    ///
    /// A notice store that cannot be READ is treated as everything already
    /// seen. That is the deliberate direction to fail in: the other way
    /// round, an unreadable file would re-show every notice on every launch,
    /// turning a helpful note into a fault the player has no way to clear.
    /// A failed WRITE is only logged - the player has read the notice by
    /// then, and refusing to open the window they asked for would be a
    /// strange thing to do about a missing flag file.
    /// </summary>
    public static class FirstRunNoticeService
    {
        /// <summary>The Simulator's "teams come from your own client" note.</summary>
        public const string SimulatorImport = "simulator-import";

        private static readonly string NoticePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRO Tracker & Database",
            "first-run-notices.json");

        private static readonly object Gate = new();

        private static HashSet<string>? seen;

        public static bool HasSeen(string key)
        {
            lock (Gate)
                return Load().Contains(key);
        }

        public static void MarkSeen(string key)
        {
            lock (Gate)
            {
                HashSet<string> current = Load();

                if (!current.Add(key))
                    return;

                try
                {
                    string? folder = Path.GetDirectoryName(NoticePath);

                    if (!string.IsNullOrEmpty(folder))
                        Directory.CreateDirectory(folder);

                    File.WriteAllText(NoticePath, JsonSerializer.Serialize(current));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "First-run notice {Key} could not be recorded.", key);
                }
            }
        }

        private static HashSet<string> Load()
        {
            if (seen != null)
                return seen;

            try
            {
                if (File.Exists(NoticePath))
                {
                    seen = JsonSerializer.Deserialize<HashSet<string>>(
                               File.ReadAllText(NoticePath))
                           ?? new HashSet<string>(StringComparer.Ordinal);

                    return seen;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "First-run notices could not be read; none will be shown.");

                seen = new HashSet<string>(StringComparer.Ordinal) { SimulatorImport };
                return seen;
            }

            seen = new HashSet<string>(StringComparer.Ordinal);
            return seen;
        }
    }
}
