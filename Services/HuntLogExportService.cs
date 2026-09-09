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
    /// Exports the Hunting Log (see HuntLogService.cs) to a standalone .csv or
    /// .json file - lets a player keep a copy of their encounter history
    /// outside the app, e.g. before the MaxSavedEntries cap eventually drops an
    /// old entry, or just to share/archive it. HuntLogViewModel and
    /// HuntLogSpeciesDetailViewModel.Export both pick which of ExportCsv/
    /// ExportJson to call based on the extension the user chose in the save
    /// dialog - same shape as PvpOpponentExportService.
    ///
    /// Read-only - unlike HuntDataExportService there is no matching import,
    /// since nothing in this app currently needs to load a previously exported
    /// hunting log back in.
    /// </summary>
    public static class HuntLogExportService
    {
        public static void ExportJson(
            IReadOnlyList<HuntLogEntry> entries,
            string filePath)
        {
            List<HuntLogEntry> ordered = entries
                .OrderByDescending(x => x.EncounteredAtUtc)
                .ToList();

            string json = JsonSerializer.Serialize(
                ordered,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(filePath, json);
        }

        public static void ExportCsv(
            IReadOnlyList<HuntLogEntry> entries,
            string filePath)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Pokemon,Level,Gender,RareType,Map,EncounteredAtUtc");

            foreach (HuntLogEntry entry in
                     entries.OrderByDescending(x => x.EncounteredAtUtc))
            {
                AddCsvRow(
                    sb,
                    entry.PokemonName,
                    entry.Level?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    entry.Gender ?? string.Empty,
                    entry.RareType ?? string.Empty,
                    entry.Map,
                    entry.EncounteredAtUtc.ToString("o", CultureInfo.InvariantCulture));
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        // ------------------------------------------------------------
        // CSV HELPERS - same escaping rules as HuntDataExportService/
        // PvpOpponentExportService's own (private there), kept as an
        // independent copy here rather than shared, same as those two already
        // do relative to each other.
        // ------------------------------------------------------------

        private static void AddCsvRow(
            StringBuilder sb,
            string pokemon,
            string level,
            string gender,
            string rareType,
            string map,
            string encounteredAtUtc)
        {
            sb.Append(EscapeCsv(pokemon));
            sb.Append(',');
            sb.Append(level);
            sb.Append(',');
            sb.Append(EscapeCsv(gender));
            sb.Append(',');
            sb.Append(EscapeCsv(rareType));
            sb.Append(',');
            sb.Append(EscapeCsv(map));
            sb.Append(',');
            sb.AppendLine(encounteredAtUtc);
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
