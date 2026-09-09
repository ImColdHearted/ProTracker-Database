using System;
using System.IO;
using PokemonSim.Engine.Strategies;
using Serilog;

namespace Foot_Tracker.Services.Simulator;

/// <summary>
/// §183. Where the opponent's book lives on disk.
///
/// BattleMemory itself has no opinion about paths - it is engine code and
/// the engine does not know this application exists. This is the tracker
/// half: one small json file beside the observation folder, loaded once
/// when the Simulator first needs a brain and written after each battle.
///
/// What goes in it is two species names, an action index and two counts per
/// matchup. That is a record of positions, not of a person: no teams, no
/// items, no timestamps, no hunt or session history, nothing that says who
/// played or when. It is separate from the observation corpus on purpose
/// and never reaches it, because the corpus is training data and this is
/// not - see section 183 for why a teacher must not be able to read it.
///
/// Every operation fails soft. A book that cannot be read or written costs
/// the opponent its memory of earlier battles and nothing else.
/// </summary>
public static class OpponentMemoryStore
{
    public static string Path => System.IO.Path.Combine(
        ObservationStore.Root, "opponent-memory.json");

    static BattleMemory? loaded;
    static readonly object gate = new();

    /// <summary>The one book this process uses, read from disk the first
    /// time it is asked for.</summary>
    public static BattleMemory Current
    {
        get
        {
            lock (gate)
            {
                if (loaded != null)
                    return loaded;

                try
                {
                    loaded = File.Exists(Path)
                        ? BattleMemory.FromJson(File.ReadAllText(Path))
                        : new BattleMemory();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Opponent memory: could not be read; starting empty.");
                    loaded = new BattleMemory();
                }

                return loaded;
            }
        }
    }

    public static void Save()
    {
        try
        {
            BattleMemory memory = Current;

            Directory.CreateDirectory(ObservationStore.Root);
            File.WriteAllText(Path, memory.ToJson());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opponent memory: could not be written.");
        }
    }

    /// <summary>Forget everything and remove the file. Returns how many
    /// matchups were dropped, so the panel can say what it did rather than
    /// claiming success over an empty book.</summary>
    public static int Clear()
    {
        lock (gate)
        {
            int had = Current.SituationCount;

            Current.Clear();

            try
            {
                if (File.Exists(Path))
                    File.Delete(Path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Opponent memory: the file could not be deleted.");
            }

            return had;
        }
    }

    /// <summary>One line for the admin panel.</summary>
    public static string Describe()
    {
        try
        {
            int situations = Current.SituationCount;

            if (situations == 0)
                return "Opponent memory: empty - the brain plays every matchup fresh.";

            long bytes = File.Exists(Path) ? new FileInfo(Path).Length : 0;

            return $"Opponent memory: {situations} matchup(s), {bytes / 1024.0:0.#} KB. " +
                   "The brain shades away from lines it has been repeating and toward ones that won.";
        }
        catch (Exception)
        {
            return "Opponent memory: unavailable.";
        }
    }
}
