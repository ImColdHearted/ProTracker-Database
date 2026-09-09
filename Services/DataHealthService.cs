using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Foot_Tracker.Services;

/// <summary>
/// The read-only report behind the Admin Console's Data Health tab and the
/// support bundle's data-health category (MIGRATION_GUIDE.md §101): where
/// each store lives, whether its JSON still parses, when it last saved, and
/// whether the bundled assets (sprites, data files, the location dictionary)
/// are present. Strictly observational - this class opens files for reading
/// only and owns no repair or edit actions, deliberately: an admin screen is
/// not a license for destructive database editing.
/// </summary>
public static class DataHealthService
{
    private static readonly string DatabaseFolder =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker",
            "Database");

    public static string BuildReport()
    {
        var report = new StringBuilder();
        int client = SessionPersistenceService.ActiveClientNumber;

        report.AppendLine($"Data folder: {DatabaseFolder}");
        report.AppendLine($"Active client: {(client >= 1 ? client.ToString() : "none bound yet")}");
        report.AppendLine($"Admin Client mode: {(AdminModeService.IsActive ? "ACTIVE (all stores write-protected)" : "off")}");
        report.AppendLine();

        report.AppendLine("Per-client stores (existence / size / last write / parses):");

        if (client >= 1)
        {
            CheckJson(report, $"current-session-client{client}.json", "session counts");
            CheckJson(report, $"session-encounters-client{client}.json", "session encounter history");
            CheckJson(report, $"hunt-log-client{client}.json", "Catch Logs");
            CheckJson(report, $"pvp-opponents-client{client}.json", "PVP battle log");
            CheckJson(report, $"boss-cooldowns-client{client}.json", "boss cooldowns");
            CheckJson(report, $"ui-preferences-client{client}.json", "UI preferences");
        }
        else
        {
            report.AppendLine("  (no client bound - per-client files not checked)");
        }

        CheckJson(report, "lifetime-stats.json", "lifetime stats");

        report.AppendLine();
        report.AppendLine("Bundled assets:");

        string dictPath = Path.Combine(AppContext.BaseDirectory, "DataFiles", "pro-locations.json");
        report.AppendLine(File.Exists(dictPath)
            ? $"  location dictionary: present ({new FileInfo(dictPath).Length:N0} bytes)"
            : "  location dictionary: MISSING - route names will stop resolving");

        string tessPath = Path.Combine(AppContext.BaseDirectory, "tessdata", "eng.traineddata");
        report.AppendLine(File.Exists(tessPath)
            ? $"  OCR model: present ({new FileInfo(tessPath).Length:N0} bytes)"
            : "  OCR model: MISSING - all OCR will fail");

        try
        {
            int total = PokemonSpriteService.AllPokemon.Count;
            int missingSprites = PokemonSpriteService.AllPokemon
                .Count(p => PokemonSpriteService.GetSprite(p.Name) is null);

            report.AppendLine($"  Pokemon library: {total} species loaded, {missingSprites} without a resolvable sprite");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  Pokemon library: could not inspect ({ex.Message})");
        }

        return report.ToString();
    }

    private static void CheckJson(StringBuilder report, string fileName, string label)
    {
        string path = Path.Combine(DatabaseFolder, fileName);

        if (!File.Exists(path))
        {
            report.AppendLine($"  {label} ({fileName}): not created yet");
            return;
        }

        var info = new FileInfo(path);
        string parses;

        try
        {
            using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(path));
            parses = "parses OK";
        }
        catch (Exception ex)
        {
            parses = $"MALFORMED ({ex.Message})";
        }

        report.AppendLine(
            $"  {label} ({fileName}): {info.Length:N0} bytes, saved {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}, {parses}");
    }
}
