using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Serilog;

namespace Foot_Tracker.Services.Simulator;

public sealed class ObserverSettings
{
    public bool Enabled { get; set; }
    public bool RecordLabRuns { get; set; }
}

/// <summary>
/// §156. Where the Simulator's observation data lives on this machine:
/// %LOCALAPPDATA%/ProTracker/SimulatorObservations - its OWN directory,
/// deliberately apart from the hunting database, logs and settings, so
/// nothing about hunting ever mixes into training data and clearing one
/// can never touch the other. This class only manages that folder (the
/// observer's tiny settings file, counting and clearing obs-*.jsonl
/// files, finding the shadow model); the recording itself is the
/// engine-side BattleObserver.
/// </summary>
public static class ObservationStore
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProTracker",
        "SimulatorObservations");

    static string SettingsPath => Path.Combine(Root, "observer-settings.json");

    public static ObserverSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<ObserverSettings>(File.ReadAllText(SettingsPath))
                       ?? new ObserverSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Simulator observer: settings failed to load.");
        }

        return new ObserverSettings();
    }

    public static void SaveSettings(ObserverSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Simulator observer: settings failed to save.");
        }
    }

    /// <summary>
    /// (battle files, decision records) currently stored. Call it off the
    /// UI thread.
    ///
    /// Section 179 stopped it reading the whole corpus. It used to open
    /// every file and count the lines containing a decision marker, which
    /// at eighty thousand battles meant reading four gigabytes off the
    /// disk every time the Battle Lab panel was opened or a run finished.
    /// Each battle's LAST line is its FinalRecord, and that already
    /// carries DecisionRecords - the count the observer kept while writing
    /// it - so the same exact number comes from the tail of each file.
    /// A battle still in progress, or one whose run was interrupted before
    /// its final line, has no such record; those fall back to counting
    /// that one file's lines rather than guessing.
    /// </summary>
    public static (int Files, long Records) CountStored()
    {
        try
        {
            if (!Directory.Exists(Root))
                return (0, 0);

            string[] files = Directory.GetFiles(Root, "obs-*.jsonl");
            long records = 0;

            foreach (string file in files)
            {
                try
                {
                    records += RecordsIn(file);
                }
                catch (IOException)
                {
                    // A battle currently writing - skip its count this pass.
                }
            }

            return (files.Length, records);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Simulator observer: stats failed.");
            return (0, 0);
        }
    }

    /// <summary>How many decisions one battle file holds, from its final
    /// line rather than all of them.</summary>
    static long RecordsIn(string file)
    {
        string? last = LastLine(file);

        if (last != null)
        {
            const string marker = "\"DecisionRecords\":";
            int at = last.IndexOf(marker, StringComparison.Ordinal);

            if (at >= 0)
            {
                int start = at + marker.Length;
                int end = start;

                while (end < last.Length && (char.IsDigit(last[end]) || last[end] == ' '))
                    end++;

                if (long.TryParse(last.AsSpan(start, end - start).Trim(), out long counted))
                    return counted;
            }
        }

        // No final record - an interrupted battle. Count this one the slow
        // way; there are only ever a handful.
        return File.ReadLines(file).Count(line =>
            line.Contains("\"Record\":\"decision\"", StringComparison.Ordinal));
    }

    /// <summary>The last non-empty line of a file, read from its tail.
    /// One observation line is comfortably under this window even at
    /// section 177's width; anything longer simply falls back.</summary>
    static string? LastLine(string file)
    {
        const int window = 8192;

        using var stream = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (stream.Length == 0)
            return null;

        int take = (int)Math.Min(window, stream.Length);
        stream.Seek(-take, SeekOrigin.End);

        var buffer = new byte[take];
        int read = stream.Read(buffer, 0, take);

        string tail = Encoding.UTF8.GetString(buffer, 0, read);

        string[] lines = tail.Split('\n');

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();

            // The first line of the window may be a fragment; only trust a
            // line that begins where a record begins.
            if (line.Length > 0 && line[0] == '{')
                return line;
        }

        return null;
    }

    /// <summary>
    /// Deletes every observation file (obs-*.jsonl) and NOTHING else - the
    /// settings file stays, and so does a trained pokemon_ai.onnx dropped
    /// here by Install Trained Model, which lives in this same folder and
    /// which ResolveModelPath prefers over the shipped copy. Deleting the
    /// folder wholesale would take that model with it, so this never does.
    ///
    /// Section 179 also returns the bytes reclaimed, because that is the
    /// number worth showing after clearing a four-gigabyte corpus. File
    /// deletion does not go through the Windows recycle bin, so there is
    /// no second pass to empty afterwards.
    /// </summary>
    public static (int Files, long Bytes) ClearAll()
    {
        int cleared = 0;
        long bytes = 0;

        try
        {
            if (!Directory.Exists(Root))
                return (0, 0);

            foreach (string file in Directory.GetFiles(Root, "obs-*.jsonl"))
            {
                try
                {
                    long size = new FileInfo(file).Length;

                    File.Delete(file);

                    cleared++;
                    bytes += size;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Simulator observer: could not delete {File}.", file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Simulator observer: clear failed.");
        }

        return (cleared, bytes);
    }

    /// <summary>The shadow model, if any: a user-dropped copy in the
    /// observation folder wins (so a newly trained model needs no
    /// rebuild), else the copy shipped beside the engine's data files;
    /// null when neither exists - the observer then runs observation-only.</summary>
    public static string? ResolveModelPath()
    {
        string dropped = Path.Combine(Root, "pokemon_ai.onnx");

        if (File.Exists(dropped))
            return dropped;

        string shipped = Path.Combine(AppContext.BaseDirectory, "PokemonSimData", "pokemon_ai.onnx");

        return File.Exists(shipped) ? shipped : null;
    }
}
