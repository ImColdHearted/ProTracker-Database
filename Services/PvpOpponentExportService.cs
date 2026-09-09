using Foot_Tracker.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Exports the "Previously Battled Users" battle log (see
    /// PvpOpponentService.cs) to a standalone .csv or .json file - lets a
    /// player keep a copy of their PVP history outside the app, e.g. before the
    /// MaxSavedBattles cap eventually drops an old entry, or just to
    /// share/archive it. PreviouslyBattledUsersViewModel.Export picks which of
    /// ExportCsv/ExportJson to call based on the extension the user chose in
    /// the save dialog.
    ///
    /// Read-only - unlike HuntDataExportService there is no matching import,
    /// since nothing in this app currently needs to load a previously exported
    /// list back in.
    /// </summary>
    public static class PvpOpponentExportService
    {
        public static void ExportJson(
            IReadOnlyList<PvpOpponentEntry> opponents,
            string filePath)
        {
            List<PvpOpponentEntry> ordered = opponents
                .OrderByDescending(x => x.BattledAtUtc)
                .ToList();

            string json = JsonSerializer.Serialize(
                ordered,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(filePath, json);
        }

        public static void ExportCsv(
            IReadOnlyList<PvpOpponentEntry> opponents,
            string filePath)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Name,TimesBattled,BattledAtUtc");

            foreach (PvpOpponentEntry entry in
                     opponents.OrderByDescending(x => x.BattledAtUtc))
            {
                AddCsvRow(
                    sb,
                    entry.Name,
                    entry.TimesBattled.ToString(CultureInfo.InvariantCulture),
                    entry.BattledAtUtc.ToString("o", CultureInfo.InvariantCulture));
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        // ------------------------------------------------------------
        // CSV HELPERS - same escaping rules as HuntDataExportService's own
        // (private there), kept as an independent copy here rather than shared.
        // ------------------------------------------------------------

        private static void AddCsvRow(
            StringBuilder sb,
            string name,
            string timesBattled,
            string battledAtUtc)
        {
            sb.Append(EscapeCsv(name));
            sb.Append(',');
            sb.Append(timesBattled);
            sb.Append(',');
            sb.AppendLine(battledAtUtc);
        }

        private static string EscapeCsv(string value)
        {
            if (value.Contains(',') ||
                value.Contains('"') ||
                value.Contains('\n') ||
                value.Contains('\r'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }
    }
}
