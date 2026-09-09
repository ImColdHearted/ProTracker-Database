using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Foot_Tracker.Tracking;
using Serilog;

namespace Foot_Tracker.Services;

/// <summary>
/// Builds the sanitized support bundle behind the Admin Console's Support
/// Bundle tab (MIGRATION_GUIDE.md §101): one zip in the user's Downloads
/// folder containing exactly the categories the admin ticked - environment
/// facts, recent logs, explicitly-recorded OCR diagnostics, the data-health
/// report, and (only when its own box is ticked) the hunting-data JSON
/// files.
///
/// Sanitization is structural, not best-effort scrubbing: the bundle only
/// ever copies files from the app's own known directories, never scans the
/// filesystem; and no credential exists anywhere those directories reach -
/// the Report a Problem mail credential is a compiled-in constant
/// (ReportEmailSettings) that never touches any settings file,
/// AdminAuthService stores only a salted hash inside the binary, and the
/// log pipeline never receives what the user types (verified §101).
/// Screenshots are never included; the only images are the small OCR crops
/// the admin explicitly recorded in diagnostic mode.
/// </summary>
public static class SupportBundleService
{
    public sealed class BundleOptions
    {
        public bool IncludeEnvironment { get; set; } = true;
        public bool IncludeLogs { get; set; } = true;
        public bool IncludeDiagnostics { get; set; } = true;
        public bool IncludeDataHealth { get; set; } = true;
        public bool IncludeSettings { get; set; } = true;

        /// <summary>Off by default and clearly labeled in the console -
        /// hunting data is personal progress, not debugging material, so it
        /// only travels when deliberately selected.</summary>
        public bool IncludeHuntingData { get; set; }
    }

    private static readonly string DataFolder =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker");

    /// <summary>Creates the zip on a background thread and returns its full
    /// path. Never overwrites: the file name carries a timestamp.</summary>
    public static Task<string> CreateAsync(BundleOptions options) => Task.Run(() =>
    {
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);

        string zipPath = Path.Combine(
            downloads, $"ProTracker-Support-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            if (options.IncludeEnvironment)
                AddText(zip, "environment.txt", BuildEnvironmentReport());

            if (options.IncludeLogs)
            {
                // The two most recent daily logs, capped so a bundle stays
                // mail-sized even after a very chatty day.
                string logsFolder = Path.Combine(DataFolder, "Logs");

                if (Directory.Exists(logsFolder))
                {
                    foreach (FileInfo log in new DirectoryInfo(logsFolder)
                                 .GetFiles("protracker-*.log")
                                 .OrderByDescending(f => f.LastWriteTimeUtc)
                                 .Take(2))
                    {
                        AddFileTail(zip, "logs/" + log.Name, log.FullName, maxBytes: 4 * 1024 * 1024);
                    }
                }
            }

            if (options.IncludeDiagnostics)
            {
                int cropIndex = 0;
                var index = new StringBuilder();

                foreach (TrackerDiagnostics.DiagnosticEntry entry in TrackerDiagnostics.GetRecordingSnapshot())
                {
                    string cropNote = string.Empty;

                    if (entry.CropPng is { Length: > 0 })
                    {
                        string cropName = $"diagnostics/crop-{++cropIndex:D3}.png";
                        AddBytes(zip, cropName, entry.CropPng);
                        cropNote = " [" + cropName + "]";
                    }

                    index.AppendLine(
                        $"{entry.TimeUtc:HH:mm:ss.fff} {entry.Detector}: {entry.Text.Replace("\n", " / ")}{cropNote}");
                }

                AddText(zip, "diagnostics/ocr-diagnostics.txt",
                    index.Length > 0
                        ? index.ToString()
                        : "No diagnostic recording captured - enable it on the Diagnostics tab first.");
            }

            if (options.IncludeDataHealth)
                AddText(zip, "data-health.txt", DataHealthService.BuildReport());

            if (options.IncludeSettings)
            {
                // Per-client display/preference JSONs only. No credential can
                // appear here - the mail credential is a compiled-in constant,
                // never a file (see the class doc); session/hunt data files
                // are the IncludeHuntingData category, not this one.
                string dbFolder = Path.Combine(DataFolder, "Database");

                if (Directory.Exists(dbFolder))
                {
                    foreach (string file in Directory.GetFiles(dbFolder, "*.json"))
                    {
                        string name = Path.GetFileName(file);
                        bool isHuntingData =
                            name.StartsWith("current-session", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("session-encounters", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("hunt-log", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("pvp-", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("lifetime", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("boss-", StringComparison.OrdinalIgnoreCase);

                        if (isHuntingData && !options.IncludeHuntingData)
                            continue;

                        AddFileTail(zip, (isHuntingData ? "hunting-data/" : "settings/") + name,
                            file, maxBytes: 2 * 1024 * 1024);
                    }
                }
            }
        }

        Log.Information("Support bundle created at {Path}", zipPath);
        return zipPath;
    });

    /// <summary>The category list the console shows BEFORE creating anything -
    /// what each checkbox will actually put in the zip.</summary>
    public static string DescribeCategories() =>
        "Environment: app/OS/.NET/architecture versions and the location dictionary's metadata.\n" +
        "Logs: the two most recent daily log files (tail-capped at 4MB each). Logs never contain credentials.\n" +
        "Diagnostics: only what diagnostic recording explicitly captured - small OCR crops and raw text, never full screenshots.\n" +
        "Data health: the same read-only report shown on the Data Health tab.\n" +
        "Settings: per-client display/preference JSONs. No credentials - none are ever stored in any settings file.\n" +
        "Hunting data (off by default): the per-client session, history, catch-log, lifetime, boss and PVP JSONs.";

    private static string BuildEnvironmentReport()
    {
        var report = new StringBuilder();

        report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
        report.AppendLine($"App version: {typeof(SupportBundleService).Assembly.GetName().Version}");
        report.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        report.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        report.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        report.AppendLine($"Avalonia: {typeof(Avalonia.Application).Assembly.GetName().Version}");
        report.AppendLine($"Active client number: {SessionPersistenceService.ActiveClientNumber}");
        report.AppendLine($"Admin Client active: {AdminModeService.IsActive}");
        report.AppendLine($"Tracker state: {TrackerDiagnostics.StateText}");

        try
        {
            string dictPath = Path.Combine(AppContext.BaseDirectory, "DataFiles", "pro-locations.json");

            report.AppendLine(File.Exists(dictPath)
                ? $"Location dictionary: {new FileInfo(dictPath).Length:N0} bytes, modified {File.GetLastWriteTime(dictPath):yyyy-MM-dd HH:mm}"
                : "Location dictionary: MISSING");
        }
        catch (Exception ex)
        {
            report.AppendLine($"Location dictionary: could not inspect ({ex.Message})");
        }

        return report.ToString();
    }

    private static void AddText(ZipArchive zip, string entryName, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(text);
    }

    private static void AddBytes(ZipArchive zip, string entryName, byte[] bytes)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName);
        using Stream stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Adds a file, keeping only its last <paramref name="maxBytes"/>
    /// when it is larger - for logs, the tail is the useful end.</summary>
    private static void AddFileTail(ZipArchive zip, string entryName, string path, long maxBytes)
    {
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (source.Length > maxBytes)
                source.Seek(-maxBytes, SeekOrigin.End);

            ZipArchiveEntry entry = zip.CreateEntry(entryName);
            using Stream target = entry.Open();
            source.CopyTo(target);
        }
        catch (Exception ex)
        {
            AddText(zip, entryName + ".error.txt", $"Could not include this file: {ex.Message}");
        }
    }
}
