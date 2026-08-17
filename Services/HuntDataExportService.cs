using Foot_Tracker.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    public static class HuntDataExportService
    {
        private const string FormatName = "ProTrackerHunt";
        private const int FormatVersion = 1;

        // ============================================================
        // JSON EXPORT
        // ============================================================

        public static void ExportJson(
            HuntSession session,
            string filePath)
        {
            var data = CreateSaveData(session);

            var export = new HuntJsonExport
            {
                Format = FormatName,
                Version = FormatVersion,
                Data = data
            };

            string json = JsonSerializer.Serialize(
                export,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            File.WriteAllText(filePath, json);
        }

        // ============================================================
        // JSON IMPORT
        // ============================================================

        public static HuntSessionSaveData ImportJson(
            string filePath)
        {
            string json = File.ReadAllText(filePath);

            var export =
                JsonSerializer.Deserialize<HuntJsonExport>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }
                );

            if (export == null ||
                export.Format != FormatName ||
                export.Data == null)
            {
                throw new InvalidDataException(
                    "This is not a valid Pro Tracker hunt file.");
            }

            if (export.Version > FormatVersion)
            {
                throw new InvalidDataException(
                    "This hunt file was created by a newer " +
                    "version of Pro Tracker.");
            }

            return export.Data;
        }

        // ============================================================
        // CSV EXPORT
        // ============================================================

        public static void ExportCsv(
            HuntSession session,
            string filePath)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Type,Name,Value");

            AddCsvRow(
                sb,
                "Session",
                "TargetPokemon",
                session.TargetPokemon);

            AddCsvRow(
                sb,
                "Session",
                "CurrentEncounter",
                session.CurrentEncounter);

            AddCsvRow(
                sb,
                "Session",
                "PreviousEncounter",
                session.PreviousEncounter);

            AddCsvRow(
                sb,
                "Session",
                "TotalEncounters",
                session.TotalEncounters.ToString(
                    CultureInfo.InvariantCulture));

            AddCsvRow(
                sb,
                "Session",
                "EncountersSinceShiny",
                session.EncountersSinceShiny.ToString(
                    CultureInfo.InvariantCulture));

            AddCsvRow(
                sb,
                "Session",
                "EncountersSinceForm",
                session.EncountersSinceForm.ToString(
                    CultureInfo.InvariantCulture));

            AddCsvRow(
                sb,
                "Session",
                "SuccessfulCatches",
                session.SuccessfulCatches.ToString(
                    CultureInfo.InvariantCulture));

            AddCsvRow(
                sb,
                "Session",
                "FailedCatches",
                session.FailedCatches.ToString(
                    CultureInfo.InvariantCulture));

            AddCsvRow(
                sb,
                "Session",
                "ElapsedSeconds",
                ((long)session.GetCurrentElapsedTime()
                    .TotalSeconds).ToString(
                        CultureInfo.InvariantCulture));

            foreach (var encounter in
                     session.EncounterCounts
                         .OrderByDescending(x => x.Value)
                         .ThenBy(x => x.Key))
            {
                AddCsvRow(
                    sb,
                    "Encounter",
                    encounter.Key,
                    encounter.Value.ToString(
                        CultureInfo.InvariantCulture));
            }

            File.WriteAllText(
                filePath,
                sb.ToString(),
                Encoding.UTF8);
        }

        // ============================================================
        // CSV IMPORT
        // ============================================================

        public static HuntSessionSaveData ImportCsv(
            string filePath)
        {
            var data = new HuntSessionSaveData();

            foreach (string rawLine in
                     File.ReadLines(filePath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                string[] fields = ParseCsvLine(rawLine);

                if (fields.Length != 3)
                    continue;

                string type = fields[0];
                string name = fields[1];
                string value = fields[2];

                if (type.Equals(
                    "Encounter",
                    StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(
                        value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int count))
                    {
                        data.EncounterCounts[name] = count;
                    }

                    continue;
                }

                if (!type.Equals(
                    "Session",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                switch (name)
                {
                    case "TargetPokemon":
                        data.TargetPokemon = value;
                        break;

                    case "CurrentEncounter":
                        data.CurrentEncounter = value;
                        break;

                    case "PreviousEncounter":
                        data.PreviousEncounter = value;
                        break;

                    case "TotalEncounters":
                        data.TotalEncounters =
                            ParseInt(value);
                        break;

                    case "EncountersSinceShiny":
                        data.EncountersSinceShiny =
                            ParseInt(value);
                        break;

                    case "EncountersSinceForm":
                        data.EncountersSinceForm =
                            ParseInt(value);
                        break;

                    case "SuccessfulCatches":
                        data.SuccessfulCatches =
                            ParseInt(value);
                        break;

                    case "FailedCatches":
                        data.FailedCatches =
                            ParseInt(value);
                        break;

                    case "ElapsedSeconds":

                        if (long.TryParse(
                            value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out long seconds))
                        {
                            data.ElapsedTime =
                                TimeSpan.FromSeconds(seconds);
                        }

                        break;
                }
            }

            return data;
        }

        // ============================================================
        // CREATE SAVE DATA
        // ============================================================

        private static HuntSessionSaveData CreateSaveData(
            HuntSession session)
        {
            var data = new HuntSessionSaveData
            {
                TargetPokemon =
                    session.TargetPokemon,

                CurrentEncounter =
                    session.CurrentEncounter,

                PreviousEncounter =
                    session.PreviousEncounter,

                TotalEncounters =
                    session.TotalEncounters,

                EncountersSinceShiny =
                    session.EncountersSinceShiny,

                EncountersSinceForm =
                    session.EncountersSinceForm,

                SuccessfulCatches =
                    session.SuccessfulCatches,

                FailedCatches =
                    session.FailedCatches,

                ElapsedTime =
                    session.GetCurrentElapsedTime()
            };

            foreach (var encounter in
                     session.EncounterCounts)
            {
                data.EncounterCounts[encounter.Key] =
                    encounter.Value;
            }

            return data;
        }

        // ============================================================
        // CSV HELPERS
        // ============================================================

        private static int ParseInt(string value)
        {
            return int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int result)
                    ? result
                    : 0;
        }

        private static void AddCsvRow(
            StringBuilder sb,
            string type,
            string name,
            string value)
        {
            sb.Append(EscapeCsv(type));
            sb.Append(',');
            sb.Append(EscapeCsv(name));
            sb.Append(',');
            sb.AppendLine(EscapeCsv(value));
        }

        private static string EscapeCsv(string value)
        {
            if (value.Contains(',') ||
                value.Contains('"') ||
                value.Contains('\n') ||
                value.Contains('\r'))
            {
                return "\"" +
                       value.Replace("\"", "\"\"") +
                       "\"";
            }

            return value;
        }

        private static string[] ParseCsvLine(
            string line)
        {
            var values = new List<string>();
            var current = new StringBuilder();

            bool insideQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    if (insideQuotes &&
                        i + 1 < line.Length &&
                        line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        insideQuotes = !insideQuotes;
                    }
                }
                else if (c == ',' && !insideQuotes)
                {
                    values.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            values.Add(current.ToString());

            return values.ToArray();
        }

        // ============================================================
        // JSON WRAPPER
        // ============================================================

        private sealed class HuntJsonExport
        {
            public string Format { get; set; } =
                FormatName;

            public int Version { get; set; }

            public HuntSessionSaveData? Data { get; set; }
        }
    }
}