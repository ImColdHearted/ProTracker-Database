using Serilog;
using System.Diagnostics;

namespace Foot_Tracker
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            string logFolder =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData
                    ),
                    "ProTracker",
                    "Logs"
                );

            Directory.CreateDirectory(
                logFolder
            );

            string logPath =
                Path.Combine(
                    logFolder,
                    "protracker-.log"
                );

            Log.Logger =
                new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.File(
                        logPath,
                        rollingInterval:
                            RollingInterval.Day,
                        retainedFileCountLimit: 14,
                        outputTemplate:
                            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} " +
                            "[{Level:u3}] " +
                            "{Message:lj}" +
                            "{NewLine}{Exception}"
                    )
                    .CreateLogger();

            try
            {
                Log.Information(
                    "Pro Tracker starting."
                );

                ApplicationConfiguration.Initialize();

                Application.Run(
                    new ProTrackerandDatabase()
                );
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);

                Log.Error(
                    ex,
                    "Encounter tracker encountered an unexpected error."
                );

            throw;
            }
            finally
            {
                Log.Information(
                    "Pro Tracker shutting down."
                );

                Log.CloseAndFlush();
            }
        }
    }
}