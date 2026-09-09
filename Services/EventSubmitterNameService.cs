using System;
using System.IO;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Remembers the name used on the last Events-board submission
    /// (MIGRATION_GUIDE.md §106) so the submit window pre-fills it - the same
    /// small convenience the compose form gets by keeping its own name field
    /// between posts. One line of text next to the other local databases;
    /// deliberately not per-client, since the person at the keyboard is the
    /// same whichever PRO client this tracker window is following.
    /// </summary>
    public static class EventSubmitterNameService
    {
        private static readonly string SavePath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProTracker",
                "Database",
                "event-submitter-name.txt"
            );

        public static string GetLastName()
        {
            try
            {
                return File.Exists(SavePath)
                    ? File.ReadAllText(SavePath).Trim()
                    : string.Empty;
            }
            catch
            {
                // A pre-filled name is a nicety - never worth surfacing.
                return string.Empty;
            }
        }

        public static void SetLastName(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;

                Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
                DurableFile.WriteAllText(SavePath, name.Trim());
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Submitter name could not be saved");
            }
        }
    }
}
