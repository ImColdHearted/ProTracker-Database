using Avalonia;
using Serilog;
using System.Runtime.InteropServices;

namespace Foot_Tracker;

internal static class Program
{
    // Coarse "how far did startup get" marker - written here and by App.axaml.cs
    // as startup works through its steps, read by the crash handlers below so a
    // crash log can say WHICH stage died. Exists because of a real macOS "the
    // app does not load" report that came with no visible error at all: the
    // only way to diagnose that kind of report remotely is a log that records
    // the machine, the stage, and the exception on its own. See
    // MIGRATION_GUIDE.md §86.
    internal static string StartupStage =
        "starting up (before the Avalonia framework initialized)";

    // Where the rolling log files actually ended up, for user-facing messages
    // (see App.axaml.cs's startup error window). Stays on the fallback text if
    // file logging never came up at all.
    internal static string LogFolderDisplayPath { get; private set; } =
        "(file logging could not be initialized - see the terminal output)";

    /// <summary>§190. The real log folder, or empty if one was never made.
    /// LogFolderDisplayPath above is a SENTENCE when logging failed, so it
    /// cannot be handed to Path.Combine or to a file manager.</summary>
    internal static string LogFolderPath { get; private set; } = string.Empty;

    /// <summary>§190. Where WriteStartupErrorFile put the crash, or empty if
    /// it could not be written anywhere at all. The startup error window
    /// names this exact file rather than telling the user to hunt for the
    /// newest protracker-*.log, because the user whose report prompted this
    /// had a log that was empty - the same unclean shutdown that emptied the
    /// file the app choked on had emptied the log too.</summary>
    internal static string StartupErrorFilePath { get; private set; } = string.Empty;

    /// <summary>§190. Removes a startup-error file left by a PREVIOUS run.
    /// Called once the app is up, because a file that says the app could not
    /// start is worse than useless while it plainly did - the next report
    /// would carry someone's month-old crash. Never throws.</summary>
    internal static void ClearStartupErrorFile()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(LogFolderPath))
                File.Delete(Path.Combine(LogFolderPath, "startup-error.txt"));
        }
        catch
        {
            // A file that will not delete is harmless; it is only stale text.
        }
    }

    /// <summary>§190. Writes the crash to one plain text file with one fixed
    /// name, using File.WriteAllText and nothing else. It deliberately does
    /// not go through Serilog: the whole point is to have a copy that does
    /// not depend on the logging pipeline being healthy, is not split across
    /// rolling daily files, and is not held open by the running process.
    /// Never throws - it is called from a catch block.</summary>
    internal static void WriteStartupErrorFile(string stage, Exception? ex)
    {
        string text = BuildStartupErrorText(stage, ex);

        foreach (string folder in new[]
                 {
                     LogFolderPath,
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     AppContext.BaseDirectory
                 })
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;

            try
            {
                Directory.CreateDirectory(folder);

                string path = Path.Combine(folder, "startup-error.txt");
                File.WriteAllText(path, text);

                StartupErrorFilePath = path;
                return;
            }
            catch
            {
                // Try the next place. A machine where none of the three work
                // still gets the window, which shows the same text on screen.
            }
        }
    }

    /// <summary>§190. The text of that file, and the same text the startup
    /// error window offers to copy - one builder so the two cannot drift.</summary>
    internal static string BuildStartupErrorText(string stage, Exception? ex)
    {
        var text = new System.Text.StringBuilder();

        text.AppendLine("Pro Tracker could not start.");
        text.AppendLine();
        text.AppendLine($"Written:    {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
        text.AppendLine($"Stage:      {stage}");
        text.AppendLine($"App:        {typeof(Program).Assembly.GetName().Version}");
        text.AppendLine($"OS:         {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        text.AppendLine($"Process:    {RuntimeInformation.ProcessArchitecture}");
        text.AppendLine($"Runtime:    {RuntimeInformation.FrameworkDescription}");
        text.AppendLine($"Base dir:   {AppContext.BaseDirectory}");
        text.AppendLine($"Log folder: {LogFolderDisplayPath}");
        text.AppendLine();
        text.AppendLine(ex?.ToString() ?? "(no exception details were available)");

        return text.ToString();
    }

    // Avalonia entry point. Ported 1:1 from the WinForms Program.Main - same log
    // folder, same rolling file policy - just swaps ApplicationConfiguration.Initialize()
    // + Application.Run(new Form()) for Avalonia's AppBuilder.
    [STAThread]
    public static void Main(string[] args)
    {
        // Failure-tolerant on purpose: a machine where the per-user app-data
        // folder can't be created or written (broken home-directory resolution,
        // a read-only profile - rare, but exactly the kind of environment a
        // "does not load, no error anywhere" report comes from) previously died
        // RIGHT HERE, before the try/catch below existed to record anything.
        // Now the app carries on with Serilog's silent default logger and the
        // failure itself goes to standard error, which is visible when the app
        // is launched from a terminal - the way the Linux/macOS READMEs tell
        // testers to run it.
        TryInitializeLogging();

        Log.Information("Pro Tracker starting.");

        // Not decoration: a "does not load" report is normally diagnosed from
        // this file alone, so it should say what machine it came from before
        // saying what went wrong on it. OSArchitecture vs ProcessArchitecture
        // matters on Apple Silicon specifically - an x64 process on an arm64 OS
        // means the app is running under Rosetta translation.
        Log.Information(
            "Environment: OS={OsDescription} ({OsArchitecture}), process={ProcessArchitecture}, runtime={FrameworkDescription}, app version={AppVersion}, base directory={BaseDirectory}",
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture,
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.FrameworkDescription,
            typeof(Program).Assembly.GetName().Version,
            AppContext.BaseDirectory);

        // Catches what the try/catch below can't see: exceptions that escape on
        // background threads (the tracking loops run on Task.Run, and their own
        // internal catch blocks are the first line of defense - this is the
        // last). The process is already doomed when this fires; the value is
        // that the log says so instead of the app just vanishing. Serilog's
        // file sink flushes each event as it's written (buffered is off by
        // default), so no explicit flush is needed for this to reach disk.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportFatal(
                "an unhandled exception on a background thread (stage: " + StartupStage + ")",
                e.ExceptionObject as Exception);

        // Faulted Tasks nothing ever awaited. These don't kill the process on
        // modern .NET, but they're exactly how a background feature dies
        // silently - worth a log line instead of nothing.
        //
        // Section 186: with a cap. One faulty background loop wrote 78,422
        // of these in a single day and turned the log into a fifty megabyte
        // file, which then had to be shipped in a support bundle to find out
        // what the fault was. The first few carry the stack trace, which is
        // all anyone needs; after that a periodic line keeps the running
        // total visible without the log becoming the bigger problem.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            int seen = Interlocked.Increment(ref unobservedFaults);

            if (seen <= UnobservedTraceLimit)
            {
                Log.Error(
                    e.Exception,
                    "Unobserved background task exception (stage: {StartupStage}). Occurrence {Seen}.",
                    StartupStage, seen);
            }
            else if (seen % UnobservedSummaryEvery == 0)
            {
                Log.Error(
                    "Unobserved background task exceptions: {Seen} so far (stage: {StartupStage}). " +
                    "Stack traces were logged for the first {Limit}; the rest are counted only.",
                    seen, StartupStage, UnobservedTraceLimit);
            }

            e.SetObserved();
        };

        try
        {
            StartupStage = "initializing the Avalonia framework " +
                "(platform windowing/rendering native libraries load here)";

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            ReportFatal(StartupStage, ex);
            throw;
        }
        finally
        {
            Log.Information("Pro Tracker shutting down.");
            Log.CloseAndFlush();
        }
    }

    private static void TryInitializeLogging()
    {
        try
        {
            string logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProTracker",
                "Logs");

            Directory.CreateDirectory(logFolder);

            string logPath = Path.Combine(logFolder, "protracker-.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            LogFolderDisplayPath = logFolder;
            LogFolderPath = logFolder;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "Pro Tracker: file logging could not be initialized - " +
                "continuing without a log file. " + ex);
        }
    }

    // One place that both records a fatal error and mirrors it to standard
    // error - the log file is what a tester sends in afterward, but stderr is
    // what they can see live in the terminal the READMEs tell them to launch
    // from, log-file location included so they know what to send.
    /// <summary>§186: how many unobserved faults get a full stack trace
    /// before the log starts counting instead of transcribing.</summary>
    private const int UnobservedTraceLimit = 20;

    /// <summary>§186: and how often it says so afterwards.</summary>
    private const int UnobservedSummaryEvery = 1000;

    private static int unobservedFaults;

    private static void ReportFatal(string stage, Exception? ex)
    {
        Log.Fatal(ex, "Pro Tracker crashed during: {Stage}.", stage);

        // §190: and to the fixed-name file too, so a crash that happens
        // before App.axaml.cs can show its window still leaves something a
        // user can find and send.
        WriteStartupErrorFile(stage, ex);

        Console.Error.WriteLine();
        Console.Error.WriteLine("Pro Tracker crashed during: " + stage);
        Console.Error.WriteLine(ex?.ToString() ?? "(no exception details were available)");
        Console.Error.WriteLine("Crash details were written to: " + LogFolderDisplayPath);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
