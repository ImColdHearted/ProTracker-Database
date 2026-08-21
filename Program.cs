using Avalonia;
using Serilog;

namespace Foot_Tracker;

internal static class Program
{
    // Avalonia entry point. Ported 1:1 from the WinForms Program.Main - same log
    // folder, same rolling file policy - just swaps ApplicationConfiguration.Initialize()
    // + Application.Run(new Form()) for Avalonia's AppBuilder.
    [STAThread]
    public static void Main(string[] args)
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

        try
        {
            Log.Information("Pro Tracker starting.");

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Encounter tracker encountered an unexpected error.");
            throw;
        }
        finally
        {
            Log.Information("Pro Tracker shutting down.");
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
