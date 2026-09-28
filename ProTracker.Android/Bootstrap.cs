using Foot_Tracker.Services;
using Serilog;

namespace ProTracker.Companion;

/// <summary>
/// §295. Everything that has to be true before the linked desktop services
/// can run on a phone, done once per launch before Avalonia starts.
///
/// THE BASE DIRECTORY. Every service reads its data as
/// Path.Combine(AppContext.BaseDirectory, "DataFiles", ...) - thirty files
/// share that habit and none of them is changed here. On Android an APK's
/// assets are not files, and AppContext.BaseDirectory is the app's install
/// path where no such folder exists. So the bundled data is unpacked once
/// into the app's private storage, and AppContext's base directory is then
/// pointed at that folder through the same AppContext data slot the runtime
/// itself reads it back from (APP_CONTEXT_BASE_DIRECTORY). After that the
/// desktop code cannot tell the difference.
///
/// THE UNPACK is versioned by the app's own version: a new build unpacks
/// again, an unchanged one does not, and a launch after the first is a file
/// read and a comparison.
///
/// THE USER'S OWN DATA lives where it does on the desktop -
/// Environment.SpecialFolder.LocalApplicationData, which on Android is the
/// app's private files directory - under the same ProTracker\Database
/// layout, so a zip of that folder made on the PC drops straight in (see
/// DataPage). Client 1 is claimed at start the way the desktop claims it,
/// because every per-client store names its file off the active client.
/// </summary>
public static class Bootstrap
{
    public const string BaseDirectoryKey = "APP_CONTEXT_BASE_DIRECTORY";

    private const string BundleAssetRoot = "bundle";

    public static string BundleDirectory { get; private set; } = string.Empty;

    public static string? StartupError { get; private set; }

    private static bool ran;

    /// <summary>Called from ProTrackerApplication, before Avalonia starts: the
    /// unpack and the base directory. Nothing Avalonia-typed runs here.</summary>
    public static void Run()
    {
        if (ran)
            return;

        ran = true;

        try
        {
            string files = global::Android.App.Application.Context.FilesDir!.AbsolutePath;

            BundleDirectory = Path.Combine(files, BundleAssetRoot);

            ConfigureLogging();

            // §316. The stamp is a fingerprint of WHAT IS IN THE APK, not the
            // app's version number, and that change is a bug fix rather than
            // a tidy-up. §313 added a whole new asset folder - the engine's
            // pokedex.json and moves.json under PokemonSimData - while
            // ApplicationDisplayVersion stayed 1.0.4. A phone that already had
            // the app therefore had a matching stamp, skipped the unpack, and
            // never got those files: the species list still worked (its data
            // came in with §295) while every Pokemon showed no abilities,
            // because PokemonDex had nothing to read. Nothing reported it.
            //
            // A fingerprint of the asset paths cannot miss that: a data file
            // added, removed or renamed changes it, so the unpack runs. The
            // assembly version is folded in as well, so a build that only
            // changes a file's CONTENTS still forces one.
            string version = typeof(Bootstrap).Assembly.GetName().Version?.ToString() ?? "0";
            string fingerprint = BundleFingerprint(version);
            string stamp = Path.Combine(BundleDirectory, "bundle-version.txt");

            if (!File.Exists(stamp) || File.ReadAllText(stamp).Trim() != fingerprint)
            {
                Log.Information("Unpacking the bundled data: {Fingerprint}.", fingerprint);
                UnpackAssets(BundleAssetRoot, BundleDirectory);
                File.WriteAllText(stamp, fingerprint);
            }

            // The runtime reads this slot back in AppContext.BaseDirectory.
            // Trailing separator, the way the runtime's own value carries one.
            AppContext.SetData(BaseDirectoryKey, BundleDirectory + Path.DirectorySeparatorChar);

            Log.Information("Companion base directory {Base}.", AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            StartupError = ex.ToString();

            try { Log.Error(ex, "Companion bootstrap failed."); } catch { }
        }
    }

    /// <summary>Called from App once Avalonia is up - the same loads the
    /// desktop App runs, in the same order, minus the ones that belong to
    /// the tracker itself.</summary>
    public static void LoadServices()
    {
        if (StartupError is not null)
            return;

        try
        {
            // Client 1, the way the desktop starts: the per-client stores
            // (boss records, PVP history, lifetime stats) name their files
            // off the active client and read nothing while none is active.
            SessionPersistenceService.SetActiveClient(1);

            PokemonSpriteService.Load();
            BossCooldownService.Load();
            BossRecordService.Load();

            Log.Information("Companion services loaded.");
        }
        catch (Exception ex)
        {
            StartupError = ex.ToString();
            Log.Error(ex, "Companion service load failed.");
        }
    }

    private static void ConfigureLogging()
    {
        string logs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker", "Logs");

        Directory.CreateDirectory(logs);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logs, "companion-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 5)
            .CreateLogger();
    }

    /// <summary>
    /// §316. Everything the APK carries under the bundle, as one short string:
    /// the app version, the number of files, and a hash of their sorted paths.
    /// Listing the asset tree is a handful of directory reads and costs a few
    /// milliseconds at launch - which is the price of never again shipping a
    /// data file that silently does not arrive.
    ///
    /// Paths only. The asset manager will not give a reliable uncompressed
    /// length for a compressed entry, so file CONTENTS are covered by the
    /// version instead, not pretended at here.
    /// </summary>
    private static string BundleFingerprint(string version)
    {
        var paths = new List<string>();
        CollectAssetPaths(BundleAssetRoot, paths);
        paths.Sort(StringComparer.Ordinal);

        ulong hash = 1469598103934665603UL;

        foreach (string p in paths)
        {
            foreach (char c in p)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
        }

        return $"{version}|{paths.Count}|{hash:x16}";
    }

    private static void CollectAssetPaths(string assetPath, List<string> into)
    {
        var assets = global::Android.App.Application.Context.Assets!;

        string[] names = assets.List(assetPath) ?? Array.Empty<string>();

        if (names.Length == 0)
        {
            into.Add(assetPath);
            return;
        }

        foreach (string name in names)
            CollectAssetPaths(assetPath + "/" + name, into);
    }

    /// <summary>Copies an asset folder out of the APK, recursively. The
    /// asset manager lists names only; a name that lists nothing is a file.</summary>
    private static void UnpackAssets(string assetPath, string destination)
    {
        var assets = global::Android.App.Application.Context.Assets!;

        string[] names = assets.List(assetPath) ?? Array.Empty<string>();

        Directory.CreateDirectory(destination);

        foreach (string name in names)
        {
            string childAsset = assetPath + "/" + name;
            string childDest = Path.Combine(destination, name);

            string[] grandchildren = assets.List(childAsset) ?? Array.Empty<string>();

            if (grandchildren.Length > 0)
            {
                UnpackAssets(childAsset, childDest);
                continue;
            }

            using Stream source = assets.Open(childAsset);
            using FileStream target = File.Create(childDest);
            source.CopyTo(target);
        }
    }
}
