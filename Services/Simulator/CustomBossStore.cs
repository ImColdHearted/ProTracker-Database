using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PokemonSim.Simulation;
using Serilog;

namespace Foot_Tracker.Services.Simulator;

/// <summary>§173. One custom opponent as the Simulator lists it: the team
/// itself, the file it came from, and whether that file is one this app
/// may write to.</summary>
public sealed class CustomBossEntry
{
    public required string Id { get; init; }
    public required CustomOpponentTeam Team { get; init; }
    public required string FilePath { get; init; }
    public bool Editable { get; init; }

    public string Title => string.IsNullOrWhiteSpace(Team.Name) ? Id : Team.Name;

    public string TeamSummary => Team.Team.Count(p => !string.IsNullOrWhiteSpace(p.Species)) is var n && n == 0
        ? "(empty)"
        : string.Join(", ", Team.Team.Where(p => !string.IsNullOrWhiteSpace(p.Species)).Select(p => p.Species));
}

/// <summary>
/// §173. Where custom opponents live. Two folders, read in this order:
///
///   1. the shipped one - DataFiles/Bosses/CustomBosses beside the app,
///      which is what a file committed to the repo becomes, so custom
///      opponents can travel with a build;
///   2. the writable one - %LOCALAPPDATA%/ProTracker/CustomBosses, which
///      is where this app SAVES, because the shipped folder lives in the
///      build output and a rebuild would wipe anything written there.
///
/// A file present in both is taken from the writable copy, the same
/// "your copy wins" rule §156 uses for the shadow model. The format
/// itself is the engine's CustomOpponents; this class only knows about
/// folders and files, and one unreadable file costs exactly itself.
/// </summary>
public static class CustomBossStore
{
    public static string ShippedFolder => Path.Combine(
        AppContext.BaseDirectory, "DataFiles", "Bosses", "CustomBosses");

    public static string WritableFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProTracker",
        "CustomBosses");

    /// <summary>Every custom opponent, writable copies winning, plus the
    /// per-file problems that kept one out of the list.</summary>
    public static (List<CustomBossEntry> Entries, List<string> Problems) LoadAll()
    {
        var byId = new Dictionary<string, CustomBossEntry>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        foreach ((string folder, bool editable) in new[] { (ShippedFolder, false), (WritableFolder, true) })
        {
            if (!Directory.Exists(folder))
                continue;

            foreach (string file in Directory.GetFiles(folder, "*.json")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string id = Path.GetFileNameWithoutExtension(file);

                try
                {
                    CustomOpponentTeam team = CustomOpponents.Parse(File.ReadAllText(file));

                    byId[id] = new CustomBossEntry
                    {
                        Id = id,
                        Team = team,
                        FilePath = file,
                        Editable = editable
                    };
                }
                catch (Exception ex)
                {
                    problems.Add($"{id}.json could not be read - {FirstLine(ex.Message)}");
                }
            }
        }

        return (byId.Values.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList(), problems);
    }

    /// <summary>Writes a custom opponent into the writable folder and
    /// returns its path. The id is derived from the name so the file is
    /// recognisable on disk; an existing id is overwritten.</summary>
    public static string Save(string id, CustomOpponentTeam team)
    {
        Directory.CreateDirectory(WritableFolder);

        string path = Path.Combine(WritableFolder, SafeId(id) + ".json");

        File.WriteAllText(path, CustomOpponents.Serialize(team));

        return path;
    }

    /// <summary>Deletes a custom opponent's writable copy. A shipped file
    /// is never touched - false says so rather than pretending.</summary>
    public static bool Delete(string id)
    {
        string path = Path.Combine(WritableFolder, SafeId(id) + ".json");

        try
        {
            if (!File.Exists(path))
                return false;

            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Custom opponents: {File} could not be deleted.", path);
            return false;
        }
    }

    /// <summary>A file name from a display name: letters and digits only,
    /// so nothing a user types can escape the folder. Empty names get a
    /// timestamp rather than a blank file name.</summary>
    public static string SafeId(string name)
    {
        string cleaned = Regex.Replace(name ?? string.Empty, "[^A-Za-z0-9]", "");

        if (cleaned.Length > 60)
            cleaned = cleaned.Substring(0, 60);

        return cleaned.Length > 0 ? cleaned : "Custom" + DateTime.Now.ToString("yyyyMMddHHmmss");
    }

    static string FirstLine(string text)
    {
        int cut = text.IndexOfAny(new[] { '\r', '\n' });
        return cut < 0 ? text : text.Substring(0, cut);
    }
}
